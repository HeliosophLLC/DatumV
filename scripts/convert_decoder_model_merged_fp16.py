"""
Patches an optimum-cli merged fp16 decoder ONNX so ORT will load it.

The bug: the export declares each If-subgraph output with a name like
`logits` / `present.0.decoder.key`, but no node *inside* the subgraph
produces those values — they only exist in the OUTER graph (as Cast
outputs that consume the If's outer output). ORT first reports this
as:

    Subgraph output (logits) is an outer scope value being returned
    directly. Please update the model to add an Identity node ...

Wrapping in Identity (the obvious "fix") makes it worse: the Identity
references the outer-scope `logits`, which is itself produced by a
Cast that consumes the If's output, creating a cycle:

    If --> outer Cast (logits) --> Identity-in-If's-subgraph --> If

ORT then rejects the patched file with "the graph is not acyclic".

The actual fix is structural: each subgraph's INTERNAL nodes already
produce values named `graph_output_cast_0..N` (matching the If node's
outer output names positionally). The fix is to rename each
subgraph output declaration to point at the corresponding internal
value — no Identity needed, no cycle, just correct wiring.

Verify the output by loading it with onnxruntime:

    .venv/Scripts/python.exe -c "import onnxruntime as ort; \\
        ort.InferenceSession('decoder_model_merged_fp16.onnx', \\
        providers=['CPUExecutionProvider'])"
A second bug in the same export: the If's outer outputs are typed as
fp16, but the subgraph's internal `graph_output_cast_<i>` values are
computed in fp32 (the model's compute boundary). ORT's type inference
flags the mismatch:

    Type Error: Type (tensor(float)) of output arg (graph_output_cast_<i>)
    of node ... does not match expected type (tensor(float16)).

The fix is local: per subgraph output, insert a Cast node converting
the internal fp32 value to fp16, and route the subgraph output through
the Cast.
"""

import onnx
from onnx import helper, TensorProto

src = r"E:\Models\trocr-base-printed\decoder_model_merged_fp16.onnx"
dst = r"E:\Models\trocr-base-printed\decoder_model_merged_fp16_converted.onnx"

m = onnx.load(src)


def patch_subgraph(g, if_outer_outputs):
    """Rewire subgraph output declarations to match internal node outputs.

    The If's outer output[i] is positionally tied to subgraph output[i].
    The subgraph's internal nodes produce a value named
    ``if_outer_outputs[i]`` (e.g. ``graph_output_cast_<i>``); we just
    rename the subgraph's output declaration to that name. If a previous
    patch attempt added bogus Identity nodes (with `_id`-suffixed names),
    strip them out as well.
    """
    # Drop any leftover patch artefacts so re-running this script is
    # idempotent: the old Identity-with-_id-suffix nodes from the broken
    # first attempt, and any Cast-with-_fp16-suffix nodes from a prior
    # run of this script.
    keep = [
        n for n in g.node
        if not (n.op_type == "Identity"
                and len(n.output) == 1
                and n.output[0].endswith("_id"))
        and not (n.op_type == "Cast"
                and len(n.output) == 1
                and n.output[0].endswith("_fp16"))
    ]
    del g.node[:]
    g.node.extend(keep)

    if len(g.output) != len(if_outer_outputs):
        raise RuntimeError(
            f"subgraph output count {len(g.output)} != if outer count {len(if_outer_outputs)}")

    # Map each subgraph output to the corresponding internal value name,
    # routing through a Cast(to=fp16) since the subgraph's internal
    # values are fp32 but the If's outer outputs expect fp16.
    produced = {o for n in g.node for o in n.output}
    cast_nodes = []
    for i, out in enumerate(g.output):
        target = if_outer_outputs[i]
        if target not in produced:
            raise RuntimeError(
                f"subgraph has no internal node producing {target!r}; "
                f"pattern doesn't match expected optimum-cli export")
        casted_name = f"{target}_fp16"
        cast = helper.make_node(
            "Cast",
            inputs=[target],
            outputs=[casted_name],
            name=f"cast_subgraph_out_{i}",
            to=TensorProto.FLOAT16)
        cast_nodes.append(cast)
        out.name = casted_name
        out.type.tensor_type.elem_type = TensorProto.FLOAT16
    g.node.extend(cast_nodes)


patched_branches = 0
for node in m.graph.node:
    if node.op_type != "If":
        continue
    outer_outputs = list(node.output)
    for branch_attr in ("then_branch", "else_branch"):
        g = next(a for a in node.attribute if a.name == branch_attr).g
        patch_subgraph(g, outer_outputs)
        patched_branches += 1

onnx.save(m, dst)
print(f"Patched {patched_branches} branches; wrote {dst}")
