using System.Text;
using Heliosoph.DatumV.Execution;
using Heliosoph.DatumV.Manifest;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Functions.Scalar.Strings;

/// <summary>
/// Shared implementation for <see cref="LpadFunction"/> and
/// <see cref="RpadFunction"/>. Pads or truncates the first argument to the
/// target length using either a single space or the supplied fill-string.
/// </summary>
internal static class PadStrings
{
    public static string Pad(ReadOnlySpan<ValueRef> args, bool leftPad, string functionName)
    {
        string value = args[0].AsString();
        if (!args[1].TryToInt32(out int length))
        {
            throw new FunctionArgumentException(functionName, $"argument 'length' of kind {args[1].Kind} is out of range for Int32.");
        }

        string fill = args.Length >= 3 ? args[2].AsString() : " ";

        if (length <= 0)
        {
            return "";
        }
        if (value.Length >= length)
        {
            return value[..length];
        }
        if (fill.Length == 0)
        {
            // PG: empty fill cannot extend. Truncation-only path already handled
            // above; this branch is reached when value is shorter than length —
            // return value as-is, no padding possible.
            return value;
        }

        int needed = length - value.Length;
        StringBuilder sb = new(length);
        if (leftPad)
        {
            AppendFill(sb, fill, needed);
            sb.Append(value);
        }
        else
        {
            sb.Append(value);
            AppendFill(sb, fill, needed);
        }
        return sb.ToString();
    }

    private static void AppendFill(StringBuilder sb, string fill, int needed)
    {
        int whole = needed / fill.Length;
        int remainder = needed % fill.Length;
        for (int i = 0; i < whole; i++)
        {
            sb.Append(fill);
        }
        if (remainder > 0)
        {
            sb.Append(fill, 0, remainder);
        }
    }
}
