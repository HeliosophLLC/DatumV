using System.Globalization;
using DatumQuery.Model;

namespace DatumQuery.Benchmarks;

/// <summary>
/// Generates synthetic datasets of configurable size for benchmarking.
/// </summary>
public static class SyntheticDataGenerator
{
    /// <summary>
    /// Generates a CSV file with the specified number of rows.
    /// Columns: id (int), name (string), value (float), category (string), score (float).
    /// </summary>
    public static string GenerateCsv(string directory, int rowCount)
    {
        string filePath = Path.Combine(directory, $"synthetic_{rowCount}.csv");
        using StreamWriter writer = new(filePath);
        writer.WriteLine("id,name,value,category,score");

        Random random = new(42);
        string[] categories = ["alpha", "beta", "gamma", "delta", "epsilon"];

        for (int i = 0; i < rowCount; i++)
        {
            string name = $"item_{i:D6}";
            float value = (float)(random.NextDouble() * 1000.0);
            string category = categories[random.Next(categories.Length)];
            float score = (float)(random.NextDouble() * 100.0);
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{i},{name},{value:F4},{category},{score:F4}"));
        }

        return filePath;
    }

    /// <summary>
    /// Generates a JSON file with the specified number of rows as a root array.
    /// </summary>
    public static string GenerateJson(string directory, int rowCount)
    {
        string filePath = Path.Combine(directory, $"synthetic_{rowCount}.json");
        using StreamWriter writer = new(filePath);
        writer.Write('[');

        Random random = new(42);
        string[] categories = ["alpha", "beta", "gamma", "delta", "epsilon"];

        for (int i = 0; i < rowCount; i++)
        {
            if (i > 0)
            {
                writer.Write(',');
            }

            string name = $"item_{i:D6}";
            float value = (float)(random.NextDouble() * 1000.0);
            string category = categories[random.Next(categories.Length)];
            float score = (float)(random.NextDouble() * 100.0);
            writer.Write(string.Create(CultureInfo.InvariantCulture,
                $"{{\"id\":{i},\"name\":\"{name}\",\"value\":{value:F4},\"category\":\"{category}\",\"score\":{score:F4}}}"));
        }

        writer.Write(']');
        return filePath;
    }

    /// <summary>
    /// Generates in-memory rows with mixed column types for execution benchmarks.
    /// </summary>
    public static Row[] GenerateRows(int rowCount)
    {
        Random random = new(42);
        string[] categories = ["alpha", "beta", "gamma", "delta", "epsilon"];
        Row[] rows = new Row[rowCount];

        string[] columnNames = ["id", "name", "value", "category", "score"];

        for (int i = 0; i < rowCount; i++)
        {
            DataValue[] values =
            [
                DataValue.FromScalar(i),
                DataValue.FromString($"item_{i:D6}"),
                DataValue.FromScalar((float)(random.NextDouble() * 1000.0)),
                DataValue.FromString(categories[random.Next(categories.Length)]),
                DataValue.FromScalar((float)(random.NextDouble() * 100.0))
            ];
            rows[i] = new Row(columnNames, values);
        }

        return rows;
    }

    /// <summary>
    /// Generates in-memory rows for join benchmarks — a secondary table.
    /// </summary>
    public static Row[] GenerateLookupRows(int rowCount)
    {
        Random random = new(42);
        Row[] rows = new Row[rowCount];

        string[] columnNames = ["lookup_id", "description", "weight"];

        for (int i = 0; i < rowCount; i++)
        {
            DataValue[] values =
            [
                DataValue.FromScalar(i),
                DataValue.FromString($"desc_{i:D6}"),
                DataValue.FromScalar((float)(random.NextDouble() * 50.0))
            ];
            rows[i] = new Row(columnNames, values);
        }

        return rows;
    }
}
