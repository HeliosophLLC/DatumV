using System;
using Heliosoph.DatumV.Execution;

namespace Heliosoph.DatumV.Export;

/// <summary>
/// Thrown at plan time when a COPY statement cannot be turned into a runnable
/// export — unknown FORMAT, missing FORMAT and unrecognised extension,
/// directory-only format pointed at a file path, typed-media column the
/// format cannot represent, etc. Subclass of
/// <see cref="ExecutionException"/> so it surfaces to clients through the
/// shared user-actionable error channel.
/// </summary>
public class ExportPlanException : ExecutionException
{
    /// <inheritdoc />
    public ExportPlanException(string message) : base(message) { }

    /// <inheritdoc />
    public ExportPlanException(string message, Exception? innerException)
        : base(message, innerException) { }
}
