namespace DatumIngest.Serialization;

/// <summary>
/// Thrown when a format deserializer encounters malformed or unexpected data.
/// Carries the line (row) and optional column position where the error occurred.
/// </summary>
public class DeserializationException : Exception
{
    /// <summary>1-based line number where the error occurred.</summary>
    public int Line { get; }

    /// <summary>0-based column (character) position within the line, or -1 if unknown.</summary>
    public int Column { get; }

    /// <summary>Creates an exception with a message, line number, and optional column.</summary>
    public DeserializationException(string message, int line, int column = -1)
        : base(message)
    {
        Line = line;
        Column = column;
    }

    /// <summary>Creates an exception with a message, line, column, and inner exception.</summary>
    public DeserializationException(string message, int line, int column, Exception innerException)
        : base(message, innerException)
    {
        Line = line;
        Column = column;
    }

    /// <summary>Creates an exception with a message, line number, and inner exception.</summary>
    public DeserializationException(string message, int line, Exception innerException)
        : base(message, innerException)
    {
        Line = line;
        Column = -1;
    }
}
