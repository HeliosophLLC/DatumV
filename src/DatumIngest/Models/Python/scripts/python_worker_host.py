"""
NDJSON worker host for Heliosoph.DatumV's Python-backed model bridge.

Per-model worker scripts import this module and call ``run(infer_func)``
where ``infer_func(inputs, overrides)`` returns one output value per input
row. The host handles the wire protocol (NDJSON over stdin/stdout), the
ready handshake, and exception reporting — the model author writes only
the inference function.

Wire protocol
-------------
- Every line on stdout is one complete JSON object.
- Startup: emit ``{"ready": true}`` once initialisation finishes.
- Per request line on stdin:
    ``{"id": <int>, "inputs": [[..row0_cols..], ..], "overrides": [[..], ..]}``
  Reply on stdout with one of:
    success  -> ``{"id": <int>, "outputs": [v0, v1, ...]}``
    error    -> ``{"id": <int>, "error": "<msg>", "traceback": "<py tb>"}``

Value encoding
--------------
- ``None``                          -> JSON ``null``
- ``str``                           -> JSON string
- ``bool``                          -> JSON ``true``/``false``
- ``int`` / ``float``               -> JSON number
- ``bytes``                         -> ``{"_bytes": "<base64>"}``  (encoded by helpers below)
- Anything else                     -> raises ``TypeError`` from ``json``

Stdin EOF terminates the worker cleanly.
"""

import base64
import json
import sys
import traceback
from typing import Any, Callable, List


def encode_value(v: Any) -> Any:
    """Convert a Python value into something json.dumps can serialise."""
    if isinstance(v, (bytes, bytearray, memoryview)):
        return {"_bytes": base64.b64encode(bytes(v)).decode("ascii")}
    return v


def decode_value(v: Any) -> Any:
    """Inverse of encode_value."""
    if isinstance(v, dict) and "_bytes" in v:
        return base64.b64decode(v["_bytes"])
    return v


def _decode_row(row: List[Any]) -> List[Any]:
    return [decode_value(c) for c in row]


def _send(obj: Any) -> None:
    sys.stdout.write(json.dumps(obj) + "\n")
    sys.stdout.flush()


def run(infer_func: Callable[[List[List[Any]], List[List[Any]]], List[Any]]) -> None:
    """Main loop. Call this from a per-model worker script after loading the model.

    ``infer_func(inputs, overrides)`` receives:
      * ``inputs``   - rows x columns of decoded values (bytes already base64-decoded)
      * ``overrides``- rows x columns of decoded override values (catalog order)

    Should return a list of values, one per input row. Each output is encoded
    automatically (bytes -> {"_bytes": ...}; everything else passes through).
    """
    # Ready handshake — must be the first stdout line.
    _send({"ready": True})

    for line in sys.stdin:
        line = line.strip()
        if not line:
            continue

        request_id = None
        try:
            req = json.loads(line)
            request_id = req.get("id")
            raw_inputs = req.get("inputs", [])
            raw_overrides = req.get("overrides", [])

            decoded_inputs = [_decode_row(r) for r in raw_inputs]
            decoded_overrides = [_decode_row(r) for r in raw_overrides]

            outputs = infer_func(decoded_inputs, decoded_overrides)
            if not isinstance(outputs, list):
                raise TypeError(
                    f"infer() must return a list; got {type(outputs).__name__}"
                )
            if len(outputs) != len(decoded_inputs):
                raise ValueError(
                    f"infer() returned {len(outputs)} outputs for "
                    f"{len(decoded_inputs)} input rows"
                )

            encoded_outputs = [encode_value(v) for v in outputs]
            _send({"id": request_id, "outputs": encoded_outputs})
        except Exception as exc:  # noqa: BLE001 - blanket catch is intentional here
            _send(
                {
                    "id": request_id,
                    "error": f"{type(exc).__name__}: {exc}",
                    "traceback": traceback.format_exc(),
                }
            )
