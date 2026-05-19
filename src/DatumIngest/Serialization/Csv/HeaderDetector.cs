using System.Globalization;
using Heliosoph.DatumV.Model;

namespace Heliosoph.DatumV.Serialization.Csv;

/// <summary>
/// Result of header detection: column names, inferred types, and whether a header row exists.
/// </summary>
internal readonly record struct HeaderDetectionResult(
    bool HasHeader,
    string[] ColumnNames,
    DataKind[] ColumnKinds);

/// <summary>
/// Detects whether a CSV file has a header row and infers column types from a sample.
/// Operates on a seekable <see cref="Stream"/> and resets position after detection.
/// </summary>
internal static class HeaderDetector
{
    /// <summary>
    /// Reads the first row + sample rows, detects header, infers types.
    /// Resets stream position to 0 after detection.
    /// </summary>
    public static HeaderDetectionResult Detect(Stream stream, char delimiter, bool? headerOverride)
    {
        using StreamReader reader = new(stream, leaveOpen: true);

        string? firstLine = reader.ReadLine();
        if (firstLine is null)
        {
            stream.Seek(0, SeekOrigin.Begin);
            return new(false, [], []);
        }

        string[] firstRowFields = CsvParser.ParseCsvLine(firstLine, delimiter);

        List<string[]> sampleRows = new(100);
        for (int i = 0; i < 100; i++)
        {
            string? line = reader.ReadLine();
            if (line is null) break;
            sampleRows.Add(CsvParser.ParseCsvLine(line, delimiter));
        }

        bool hasHeader = headerOverride ?? DetectHeader(firstRowFields, sampleRows);

        string[] headers;
        List<string[]> dataRows;

        if (hasHeader)
        {
            headers = firstRowFields;
            dataRows = sampleRows;

            int usableColumns = headers.Length;
            while (usableColumns > 0 && headers[usableColumns - 1].Trim().Length == 0)
                usableColumns--;

            if (usableColumns < headers.Length)
                headers = headers[..usableColumns];
        }
        else
        {
            headers = new string[firstRowFields.Length];
            for (int i = 0; i < firstRowFields.Length; i++)
                headers[i] = $"col_{i}";

            dataRows = [firstRowFields, .. sampleRows];
        }

        // Trim header names.
        for (int i = 0; i < headers.Length; i++)
            headers[i] = headers[i].Trim();

        DataKind[] kinds = InferTypes(headers.Length, dataRows);

        stream.Seek(0, SeekOrigin.Begin);
        return new(hasHeader, headers, kinds);
    }

    private static DataKind[] InferTypes(int columnCount, List<string[]> dataRows)
    {
        DataKind[] kinds = new DataKind[columnCount];
        bool[] hasData = new bool[columnCount];
        bool[] numericFailed = new bool[columnCount];
        bool[] integerFailed = new bool[columnCount];
        bool[] hasFractionalValues = new bool[columnCount];
        long[] integerMinimum = new long[columnCount];
        long[] integerMaximum = new long[columnCount];
        Array.Fill(integerMinimum, long.MaxValue);
        Array.Fill(integerMaximum, long.MinValue);

        // Pass 1: Numeric detection.
        foreach (string[] fields in dataRows)
        {
            for (int col = 0; col < Math.Min(fields.Length, columnCount); col++)
            {
                string field = fields[col].Trim();
                if (field.Length == 0 || numericFailed[col]) continue;

                hasData[col] = true;

                if (long.TryParse(field, NumberStyles.Integer, CultureInfo.InvariantCulture, out long longValue))
                {
                    if (longValue < integerMinimum[col]) integerMinimum[col] = longValue;
                    if (longValue > integerMaximum[col]) integerMaximum[col] = longValue;
                    continue;
                }

                if (double.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out double doubleValue))
                {
                    integerFailed[col] = true;
                    if (doubleValue != Math.Floor(doubleValue) || double.IsInfinity(doubleValue))
                        hasFractionalValues[col] = true;
                    continue;
                }

                numericFailed[col] = true;
            }
        }

        for (int col = 0; col < columnCount; col++)
        {
            if (!hasData[col] || numericFailed[col])
                kinds[col] = DataKind.String;
            else if (!integerFailed[col])
            {
                if (integerMinimum[col] >= 0 && integerMaximum[col] <= 1)
                    kinds[col] = DataKind.Boolean;
                else
                    kinds[col] = integerMinimum[col] >= int.MinValue && integerMaximum[col] <= int.MaxValue
                        ? DataKind.Int32 : DataKind.Int64;
            }
            else if (!hasFractionalValues[col])
                kinds[col] = DataKind.Int64;
            else
                kinds[col] = DataKind.Float64;
        }

        // Pass 2: ISO 8601 dates in String columns.
        for (int col = 0; col < columnCount; col++)
        {
            if (kinds[col] != DataKind.String || !hasData[col]) continue;

            bool allDates = true;
            bool anyHasTime = false;

            foreach (string[] fields in dataRows)
            {
                if (col >= fields.Length) continue;
                string field = fields[col].Trim();
                if (field.Length == 0) continue;

                if (!DateTimeOffset.TryParse(field, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out DateTimeOffset parsed))
                {
                    allDates = false;
                    break;
                }
                if (parsed.TimeOfDay != TimeSpan.Zero) anyHasTime = true;
            }

            if (allDates) kinds[col] = anyHasTime ? DataKind.TimestampTz : DataKind.Date;
        }

        // Pass 3: UUIDs in String columns.
        for (int col = 0; col < columnCount; col++)
        {
            if (kinds[col] != DataKind.String || !hasData[col]) continue;

            bool allUuids = true;
            foreach (string[] fields in dataRows)
            {
                if (col >= fields.Length) continue;
                string field = fields[col].Trim();
                if (field.Length == 0) continue;
                if (!Guid.TryParseExact(field, "D", out _)) { allUuids = false; break; }
            }

            if (allUuids) kinds[col] = DataKind.Uuid;
        }

        // Pass 4: Boolean text in String columns.
        for (int col = 0; col < columnCount; col++)
        {
            if (kinds[col] != DataKind.String || !hasData[col]) continue;

            bool allBoolean = true;
            foreach (string[] fields in dataRows)
            {
                if (col >= fields.Length) continue;
                string field = fields[col].Trim();
                if (field.Length == 0) continue;
                if (!field.Equals("true", StringComparison.OrdinalIgnoreCase) &&
                    !field.Equals("false", StringComparison.OrdinalIgnoreCase))
                {
                    allBoolean = false;
                    break;
                }
            }

            if (allBoolean) kinds[col] = DataKind.Boolean;
        }

        return kinds;
    }

    private static bool DetectHeader(string[] firstRowFields, List<string[]> dataRows)
    {
        if (dataRows.Count == 0) return true;

        int columnCount = firstRowFields.Length;

        // Numeric mismatch heuristic.
        for (int col = 0; col < columnCount; col++)
        {
            string firstValue = firstRowFields[col].Trim();
            bool firstIsNumeric = firstValue.Length > 0 &&
                double.TryParse(firstValue, NumberStyles.Float, CultureInfo.InvariantCulture, out _);

            int numericCount = 0, nonEmptyCount = 0;
            foreach (string[] row in dataRows)
            {
                if (col >= row.Length) continue;
                string field = row[col].Trim();
                if (field.Length == 0) continue;
                nonEmptyCount++;
                if (double.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    numericCount++;
            }

            bool columnIsNumeric = nonEmptyCount > 0 && (double)numericCount / nonEmptyCount > 0.5;
            if (columnIsNumeric && !firstIsNumeric) return true;
        }

        // Value disjointness heuristic.
        if (dataRows.Count >= 2)
        {
            bool allDisjoint = true;
            int nonEmptyFirstRowValues = 0;

            for (int col = 0; col < columnCount; col++)
            {
                string firstValue = firstRowFields[col].Trim();
                if (firstValue.Length == 0) continue;
                nonEmptyFirstRowValues++;

                foreach (string[] row in dataRows)
                {
                    if (col >= row.Length) continue;
                    if (row[col].Trim().Equals(firstValue, StringComparison.OrdinalIgnoreCase))
                    {
                        allDisjoint = false;
                        break;
                    }
                }

                if (!allDisjoint) break;
            }

            if (allDisjoint && nonEmptyFirstRowValues > 0) return true;
        }

        return false;
    }
}
