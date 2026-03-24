using DatumIngest.Parsing;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Tests.Parsing;

/// <summary>
/// Smoke tests that every shipped <c>models/sql/*.sql</c> file parses through
/// <see cref="SqlParser"/>. Catches regressions when grammar changes (e.g. the
/// new <c>CHECK</c> / <c>STEP</c> / <c>UNIT</c> / <c>COMMENT</c> parameter
/// clauses) introduce a syntax error in a canonical model body without anyone
/// noticing until the E2E test fleet runs. Parsing is cheap; the heavy E2E
/// tests skip when the ONNX file isn't downloaded, so without this we could
/// ship a broken catalog and never know.
/// </summary>
public sealed class SqlModelFilesParseTests
{
    private static string ResolveModelsDir()
    {
        // Tests run from .../bin/Debug/net10.0; walk up to repo root, then
        // into models/sql.
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "models", "sql")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "models", "sql");
    }

    public static IEnumerable<object[]> SqlFiles()
    {
        string sqlDir = ResolveModelsDir();
        foreach (string path in Directory.EnumerateFiles(sqlDir, "*.sql"))
        {
            yield return new object[] { Path.GetFileName(path) };
        }
    }

    [Theory]
    [MemberData(nameof(SqlFiles))]
    public void EachShippedModelSql_ParsesCleanly(string fileName)
    {
        string sqlPath = Path.Combine(ResolveModelsDir(), fileName);
        string source = File.ReadAllText(sqlPath);

        // Use ParseBatch so multi-statement bundles (Florence-2 fp16 / Q8
        // ship 4 CREATE MODEL statements per file — one per task variant)
        // parse end-to-end. Single-statement files still pass since the
        // batch parser returns a one-element list.
        IReadOnlyList<Statement> statements = SqlParser.ParseBatch(source);
        Assert.NotEmpty(statements);
        foreach (Statement stmt in statements)
        {
            CreateModelStatement create = Assert.IsType<CreateModelStatement>(stmt);
            Assert.NotEmpty(create.StatementBody);
        }
    }
}
