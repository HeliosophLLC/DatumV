using System;
using Heliosoph.DatumV.Execution;

namespace Heliosoph.DatumV.Export;

/// <summary>
/// Thrown by an <see cref="IExportSink"/> mid-stream when a row's contents
/// violate a runtime constraint the planner could not detect — an oversized
/// blob, an out-of-range numeric value, an I/O failure surfaced as a typed
/// error.
/// </summary>
public class ExportRuntimeException : ExecutionException
{
    /// <inheritdoc />
    public ExportRuntimeException(string message) : base(message) { }

    /// <inheritdoc />
    public ExportRuntimeException(string message, Exception? innerException)
        : base(message, innerException) { }
}
