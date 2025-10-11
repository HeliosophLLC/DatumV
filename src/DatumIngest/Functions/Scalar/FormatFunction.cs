using System.Buffers;
using System.Text;
using DatumIngest.Model;

namespace DatumIngest.Functions.Scalar;

/// <summary>
/// Formats arguments according to a format string, similar to PostgreSQL's format() / C sprintf.
/// <c>format(formatstr, arg1, arg2, ...)</c>
/// Supports <c>%s</c> (string), <c>%I</c> (SQL identifier), <c>%L</c> (SQL literal),
/// and positional <c>%n$s</c> forms. <c>%%</c> produces a literal percent sign.
/// </summary>
public sealed class FormatFunction : IScalarFunction
{
    /// <inheritdoc />
    public string Name => "format";

    /// <inheritdoc />
    public DataKind ValidateArguments(ReadOnlySpan<DataKind> argumentKinds)
    {
        if (argumentKinds.Length < 1)
        {
            throw new ArgumentException("format() requires at least 1 argument (format string).");
        }

        if (argumentKinds[0] != DataKind.String)
        {
            throw new ArgumentException(
                $"format() first argument must be String, got {argumentKinds[0]}.");
        }

        return DataKind.String;
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments)
    {
        if (arguments[0].IsNull)
        {
            return DataValue.Null(DataKind.String);
        }

        string fmt = arguments[0].AsString();
        StringBuilder sb = new();
        int autoIndex = 1; // 1-based index for sequential args

        for (int i = 0; i < fmt.Length; i++)
        {
            if (fmt[i] != '%')
            {
                sb.Append(fmt[i]);
                continue;
            }

            i++;
            if (i >= fmt.Length) break;

            // %% → literal %
            if (fmt[i] == '%')
            {
                sb.Append('%');
                continue;
            }

            // Check for positional: %n$s
            int argIndex = -1;
            int numStart = i;
            while (i < fmt.Length && char.IsDigit(fmt[i])) i++;

            if (i < fmt.Length && fmt[i] == '$' && i > numStart)
            {
                argIndex = int.Parse(fmt.AsSpan(numStart, i - numStart));
                i++; // skip '$'
            }
            else
            {
                i = numStart; // reset, not positional
            }

            if (i >= fmt.Length) break;

            char spec = fmt[i];
            int idx = argIndex > 0 ? argIndex : autoIndex++;

            if (idx >= arguments.Length)
            {
                throw new InvalidOperationException(
                    $"format(): not enough arguments for format specifier at position {i}.");
            }

            DataValue arg = arguments[idx];
            string value = arg.IsNull ? "" : arg.ToDisplayString();

            switch (spec)
            {
                case 's':
                    sb.Append(value);
                    break;
                case 'I':
                    // SQL identifier quoting
                    sb.Append('"').Append(value.Replace("\"", "\"\"")).Append('"');
                    break;
                case 'L':
                    // SQL literal quoting
                    if (arg.IsNull)
                    {
                        sb.Append("NULL");
                    }
                    else
                    {
                        sb.Append('\'').Append(value.Replace("'", "''")).Append('\'');
                    }
                    break;
                default:
                    throw new InvalidOperationException(
                        $"format(): unrecognized format specifier '%{spec}'.");
            }

            if (argIndex > 0)
            {
                // Positional arg doesn't advance autoIndex
            }
        }

        return DataValue.FromString(sb.ToString());
    }

    /// <inheritdoc />
    public DataValue Execute(ReadOnlySpan<DataValue> arguments, IValueStore store)
    {
        if (arguments[0].IsNull)
        {
            return DataValue.Null(DataKind.String);
        }

        string fmt = arguments[0].AsString(store);
        StringBuilder sb = new();
        int autoIndex = 1; // 1-based index for sequential args

        for (int i = 0; i < fmt.Length; i++)
        {
            if (fmt[i] != '%')
            {
                sb.Append(fmt[i]);
                continue;
            }

            i++;
            if (i >= fmt.Length) break;

            // %% → literal %
            if (fmt[i] == '%')
            {
                sb.Append('%');
                continue;
            }

            // Check for positional: %n$s
            int argIndex = -1;
            int numStart = i;
            while (i < fmt.Length && char.IsDigit(fmt[i])) i++;

            if (i < fmt.Length && fmt[i] == '$' && i > numStart)
            {
                argIndex = int.Parse(fmt.AsSpan(numStart, i - numStart));
                i++; // skip '$'
            }
            else
            {
                i = numStart; // reset, not positional
            }

            if (i >= fmt.Length) break;

            char spec = fmt[i];
            int idx = argIndex > 0 ? argIndex : autoIndex++;

            if (idx >= arguments.Length)
            {
                throw new InvalidOperationException(
                    $"format(): not enough arguments for format specifier at position {i}.");
            }

            DataValue arg = arguments[idx];
            string value = arg.IsNull ? "" : arg.ToDisplayString();

            switch (spec)
            {
                case 's':
                    sb.Append(value);
                    break;
                case 'I':
                    // SQL identifier quoting
                    sb.Append('"').Append(value.Replace("\"", "\"\"")).Append('"');
                    break;
                case 'L':
                    // SQL literal quoting
                    if (arg.IsNull)
                    {
                        sb.Append("NULL");
                    }
                    else
                    {
                        sb.Append('\'').Append(value.Replace("'", "''")).Append('\'');
                    }
                    break;
                default:
                    throw new InvalidOperationException(
                        $"format(): unrecognized format specifier '%{spec}'.");
            }

            if (argIndex > 0)
            {
                // Positional arg doesn't advance autoIndex
            }
        }

        char[] resultBuf = ArrayPool<char>.Shared.Rent(sb.Length);
        sb.CopyTo(0, resultBuf.AsSpan(), sb.Length);
        DataValue result = DataValue.FromCharSpan(resultBuf.AsSpan(0, sb.Length), store);
        ArrayPool<char>.Shared.Return(resultBuf);
        return result;
    }
}
