using System.Text;

namespace Heliosoph.DatumV.Functions.TableValued;

/// <summary>
/// Splits a single CSV line into fields, honouring RFC 4180 quoting:
/// <c>"..."</c>-wrapped fields have their surrounding quotes stripped and
/// embedded <c>""</c> escapes are collapsed to a single <c>"</c>. Shared by
/// <see cref="OpenCsvFunction"/> and <see cref="ReadCsvFunction"/> so both
/// the file-path and bytes variants produce identical field arrays.
/// </summary>
/// <remarks>
/// Both callers materialise the line as a <see cref="string"/> and emit
/// fields into an <c>Array&lt;String&gt;</c> column, so per-field
/// <see cref="string"/> allocation here matches the existing memory shape —
/// no zero-alloc span machinery needed.
/// <see cref="Serialization.Csv.CsvDeserializer"/> keeps a separate,
/// span-based implementation for the typed-ingest hot path.
///
/// <para>
/// Embedded newlines inside quoted fields are not supported: the callers
/// split the payload on bare <c>\n</c> before calling this method, so a
/// quoted field containing a literal newline arrives already broken into
/// parts. Recipes that need multi-line CSV cells should reach for the
/// file-path ingest path (which goes through <c>CsvDeserializer</c>'s
/// <c>LineReader</c>, the one place in the codebase that handles
/// inside-quote line breaks).
/// </para>
/// </remarks>
internal static class CsvLineSplitter
{
    public static string[] Split(string line, char delimiter)
    {
        // Fast path: no quote characters anywhere → plain delimiter split.
        // Picks up the bulk of real-world manifests, where quoting is the
        // exception rather than the rule.
        if (line.IndexOf('"') < 0)
        {
            return line.Split(delimiter);
        }

        List<string> fields = [];
        StringBuilder field = new(line.Length);
        bool inQuotes = false;

        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];

            if (inQuotes)
            {
                if (c != '"')
                {
                    field.Append(c);
                    continue;
                }

                // A quote inside a quoted field: either an escape (`""` → `"`)
                // or the closing quote.
                if (i + 1 < line.Length && line[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                    continue;
                }

                inQuotes = false;
                continue;
            }

            if (c == '"')
            {
                inQuotes = true;
                continue;
            }

            if (c == delimiter)
            {
                fields.Add(field.ToString());
                field.Clear();
                continue;
            }

            field.Append(c);
        }

        fields.Add(field.ToString());
        return fields.ToArray();
    }
}
