---
title: Tokenization Functions
category: tokenization
---

# Tokenization Functions

The `tokenizer.*` family converts text to and from the integer token id
sequences that transformer ONNX exports consume. Each function loads its
vocabulary / merges artifacts from disk on first use and caches the
in-memory tokenizer process-wide — subsequent calls reuse the parsed
state, so the only per-row cost is the encode itself.

These functions are not body-scoped: they can be called from any
`SELECT`, but they appear almost exclusively inside `CREATE MODEL`
bodies as the first step of a transformer pipeline. The vocab/merges
paths follow the same resolution rules as
[`USING` paths](../sql/create-model.md#the-using-clause) — `file://`
prefixes short-circuit; bare relative paths resolve against the
[models directory](../sql/create-model.md#models-directory).

## WordPiece (BERT family)

### tokenizer.encode_bert

`tokenizer.encode_bert(text, vocab_path [, max_length])` → `Struct<input_ids: Int64[], attention_mask: Int64[], token_type_ids: Int64[]>`

Bidirectional WordPiece tokenize with `[CLS]` prepended and `[SEP]`
appended. The returned struct uses the canonical BERT ONNX input names
so it pipes straight into the `(Struct, Struct)` form of
[`infer()`](../sql/create-model.md#multi-input-models) without
re-keying.

`vocab_path` points at the model's `vocab.txt` (one token per line —
the artifact every BERT-style export ships alongside `model.onnx`).

The optional `max_length` caps the total emitted token count
(including `[CLS]` and `[SEP]`). Long inputs are clipped from the
tail and the trailing `[SEP]` is reapplied. Omit (or pass `NULL`)
for no truncation. Every BERT-family encoder has a fixed
position-embedding table — commonly 512 for BERT-base — and inputs
beyond that index out of range and abort inside the ONNX embeddings
layer, so a model body driving such a model should pass
`max_length => 512` (or whatever the model's documented cap is).

```sql
DECLARE encoded Struct = tokenizer.encode_bert(text, 'vocab.txt', max_length => 512);
DECLARE n Int32 = cardinality(encoded['input_ids']);
DECLARE last_hidden_state Float32[] = infer(encoded, {
  input_ids:      [1::Int32, n],
  attention_mask: [1::Int32, n],
  token_type_ids: [1::Int32, n]
});
```

Canonical use: `models.all_minilm_l6_v2`.

### tokenizer.encode_bert_pair

`tokenizer.encode_bert_pair(query, passage, vocab_path [, max_length])` → `Struct<input_ids, attention_mask, token_type_ids>`

Two-segment WordPiece tokenize for sentence-pair tasks (reranking,
NLI, paraphrase detection). The output layout is `[CLS] query [SEP]
passage [SEP]`; `token_type_ids` is zero across the query segment and
one across the passage segment — the discriminator BERT cross-encoders
were trained to read.

The optional `max_length` caps the assembled sequence length.
Truncation is longest-first — the longer of the two sides loses one
token per step until both fit under the cap, matching HuggingFace
tokenizers' default sentence-pair truncation strategy. Pass it
whenever the model has a fixed position-embedding cap (e.g.
`max_length => 512` for BERT-base cross-encoders).

Canonical use: `models.bge_reranker_base`.

## Byte-Pair Encoding (GPT-2 family)

### tokenizer.encode_bpe

`tokenizer.encode_bpe(text, vocab_path, merges_path)` → `Int64[]`

GPT-2-style byte-level BPE: every Unicode codepoint maps to a
printable proxy character via the standard byte-encoder table, then
greedy merges apply per the `merges.txt` ranking. Produces a flat
sequence of token ids with no special-token markers — the model body
adds whatever framing tokens it needs.

Pair with `tokenizer.decode_bpe` + `tokenizer.byte_level_decode` to
round-trip generated ids back to text.

```sql
DECLARE text_ids Int64[] = tokenizer.encode_bpe(
    prompt, 'vocab.json', 'merges.txt');
```

Canonical use: `models.moondream2`.

### tokenizer.encode_clip

`tokenizer.encode_clip(text, vocab_path, merges_path)` → `Int64[77]`

CLIP byte-level BPE with `<|startoftext|>` / `<|endoftext|>` framing
and fixed length 77 — the exact tokenizer OpenAI/OpenCLIP shipped in
`tokenizer/vocab.json` + `tokenizer/merges.txt`. Length is padded with
`<|endoftext|>` or right-truncated as needed so the text-encoder ONNX
input shape `[1, 77]` never has to be reshaped per row.

Canonical use: `models.sd_turbo` (prompt embedding for the SD text encoder).

### tokenizer.encode_roberta

`tokenizer.encode_roberta(text, tokenizer_json_path)` → `Struct<input_ids: Int64[], attention_mask: Int64[]>`

RoBERTa byte-level BPE with `<s>` / `</s>` framing. Reads HuggingFace
`tokenizer.json` directly (the JSON dump RoBERTa exports ship), so
there is no separate `vocab.json` + `merges.txt` pair.

Returns the two-field input bundle RoBERTa expects (no
`token_type_ids` — RoBERTa drops the segment-embedding head).

Canonical use: `models.twitter_roberta_sentiment`.

### tokenizer.decode_bpe

`tokenizer.decode_bpe(ids, vocab_path, merges_path)` → `String`

Inverse of `tokenizer.encode_bpe` — token ids → the raw byte-level BPE
text representation. The output still carries the byte-encoder proxy
characters (`Ġ` instead of space, `Ċ` instead of newline, etc.); pipe
through `tokenizer.byte_level_decode` to recover the user-facing UTF-8.

### tokenizer.byte_level_decode

`tokenizer.byte_level_decode(text)` → `String`

Inverts the GPT-2 byte-encoder mapping. Each proxy character in the
input is mapped back to its underlying byte and the resulting byte
sequence is interpreted as UTF-8. The standard final step of any LLM
or seq2seq decode loop whose tokenizer is byte-level BPE (Whisper,
Phi, Llama-via-BPE-export, Florence-2 text head, ...).

```sql
DECLARE raw String = tokenizer.decode_bpe(token_ids, 'vocab.json', 'merges.txt');
RETURN tokenizer.byte_level_decode(raw)
```

Canonical uses: `models.whisper_base`,
`models.moondream2`.

## See Also

- [CREATE MODEL](../sql/create-model.md) — the DDL surface these functions wire into.
- [Inference Functions](inference.md) — preprocess / postprocess helpers paired with the encoders fed by these tokenizers.
- [Models](../models.md) — how `models.X(...)` dispatches and what's in the built-in catalog.
