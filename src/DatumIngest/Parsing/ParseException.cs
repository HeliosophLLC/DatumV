using Superpower.Model;

namespace DatumIngest.Parsing;

/// <summary>
/// Thrown when the SQL parser encounters input it cannot interpret.
/// </summary>
public sealed class ParseException : Exception
{
    /// <summary>
    /// Creates a parse exception with a message and the position of the error.
    /// </summary>
    public ParseException(string message, Position position)
        : base(message)
    {
        ErrorPosition = position;
    }

    /// <summary>
    /// The position in the input where the error was detected.
    /// </summary>
    public Position ErrorPosition { get; }
}
