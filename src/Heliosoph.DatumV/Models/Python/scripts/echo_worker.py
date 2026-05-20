"""
Echo worker — proof-of-concept for the Python-backed model bridge.

Returns each row's first column unchanged. Validates the IPC protocol end
to end (handshake -> request -> response -> shutdown) without depending on
any ML library, so the framework's plumbing can be tested in isolation
from any specific model integration.

Run via the C# ``PythonBackedModel`` helper or directly:

    python python_worker_host.py
    # ... no, run THIS file; it imports the host:
    python echo_worker.py
"""

import os
import sys
from typing import Any, List

# Make python_worker_host importable when this file is run directly from
# any working directory.
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from python_worker_host import run  # noqa: E402  - import after sys.path edit


def infer(inputs: List[List[Any]], overrides: List[List[Any]]) -> List[Any]:
    _ = overrides
    return [row[0] if row else None for row in inputs]


if __name__ == "__main__":
    run(infer)
