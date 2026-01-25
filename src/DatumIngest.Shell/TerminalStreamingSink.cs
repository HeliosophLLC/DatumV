using DatumIngest.Execution;
using DatumIngest.Functions;
using DatumIngest.Model;

using Spectre.Console;

namespace DatumIngest.Shell;

/// <summary>
/// <see cref="IModelStreamingSink"/> implementation that prints model output
/// to the terminal as it arrives. Wired up by <see cref="InteractiveShell"/>
/// for <c>EXEC &lt;model-call&gt;</c> statements so LLM tokens render live
/// rather than after the full response collects.
/// </summary>
/// <remarks>
/// <para>
/// String chunks are written directly to <see cref="Console.Out"/>, bypassing
/// <see cref="AnsiConsole"/>'s markup parser — model output frequently
/// contains <c>[</c> / <c>]</c> literals (code, JSON, math notation) that
/// markup would misinterpret. Other <see cref="DataKind"/>s collapse to a
/// short debug representation; today this only fires if a non-LLM model is
/// streaming, which has no production path yet.
/// </para>
/// <para>
/// <strong>Tracking chunk receipt.</strong> The shell consults
/// <see cref="ChunksReceived"/> after the streaming run to decide whether the
/// EXEC target actually streamed (in which case the sink already printed
/// everything) or fell through to a non-streaming function (in which case
/// the shell prints the synthetic single-row result via the normal
/// pagination path).
/// </para>
/// </remarks>
internal sealed class TerminalStreamingSink : IModelStreamingSink
{
    private readonly TextWriter _writer;
    private bool _midLine;

    /// <summary>
    /// Number of chunks delivered to <see cref="OnChunkAsync"/> across all
    /// dispatches observed by this sink. Zero when the EXEC target was a
    /// non-streaming function (e.g. <c>EXEC upper('hi')</c>); the shell
    /// uses this to decide whether to print a fallback row summary.
    /// </summary>
    public int ChunksReceived { get; private set; }

    public TerminalStreamingSink()
        : this(Console.Out)
    {
    }

    /// <summary>
    /// Test-friendly constructor that lets a fixture redirect output to a
    /// <see cref="StringWriter"/>. Production use goes through the
    /// parameterless constructor and writes to <see cref="Console.Out"/>.
    /// </summary>
    internal TerminalStreamingSink(TextWriter writer)
    {
        _writer = writer;
    }

    /// <inheritdoc />
    public ValueTask OnChunkAsync(string modelName, ValueRef chunk)
    {
        ChunksReceived++;

        if (chunk.IsNull)
        {
            return ValueTask.CompletedTask;
        }

        if (chunk.Kind == DataKind.String)
        {
            string text = chunk.AsString();
            if (text.Length == 0) return ValueTask.CompletedTask;

            _writer.Write(text);
            _midLine = !text.EndsWith('\n');
        }
        else
        {
            // Fallback rendering for non-string streaming output. No model
            // currently uses this path; treat as a debug aid rather than a
            // production format.
            _writer.Write($"<{chunk.Kind}>");
            _midLine = true;
        }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask OnCompletedAsync(string modelName)
    {
        if (_midLine)
        {
            _writer.WriteLine();
            _midLine = false;
        }

        // Footer is dim grey so it's clearly not part of the model output.
        // Use AnsiConsole here (no untrusted markup to escape) so the colour
        // honours the terminal's theme.
        AnsiConsole.MarkupLine($"[grey](streamed from models.{Markup.Escape(modelName)})[/]");
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask OnFailedAsync(string modelName, Exception exception)
    {
        if (_midLine)
        {
            _writer.WriteLine();
            _midLine = false;
        }
        // The exception itself propagates and the shell's error handler
        // renders it; no need to print here. Just terminate the partial
        // line so the error message starts on a clean row.
        return ValueTask.CompletedTask;
    }
}
