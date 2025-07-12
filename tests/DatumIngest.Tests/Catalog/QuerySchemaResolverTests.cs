using DatumIngest.Catalog;
using DatumIngest.Catalog.Providers;
using DatumIngest.Functions;
using DatumIngest.Model;
using DatumIngest.Parsing.Ast;

namespace DatumIngest.Tests.Catalog;

/// <summary>
/// Tests for <see cref="QuerySchemaResolver"/> covering FROM, JOIN,
/// subquery, and function source resolution.
/// </summary>
public sealed class QuerySchemaResolverTests
{
    private static readonly FunctionRegistry DefaultFunctions = FunctionRegistry.CreateDefault();

    private static string FixturePath(string fileName)
    {
        return Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);
    }

    private static TableCatalog CreateCatalog()
    {
        TableCatalog catalog = new();
        catalog.RegisterProvider("csv", () => new CsvTableProvider());

        catalog.Register(new TableDescriptor(
            "csv", "people", FixturePath("simple.csv"),
            new Dictionary<string, string>()));

        catalog.Register(new TableDescriptor(
            "csv", "scores", FixturePath("simple.csv"),
            new Dictionary<string, string>()));

        return catalog;
    }

    // ───────────────────── Single table FROM ─────────────────────

    [Fact]
    public async Task Resolve_SingleTable_ReturnsTableSchema()
    {
        TableCatalog catalog = CreateCatalog();
        QuerySchemaResolver resolver = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("people")));

        ResolvedQuerySchema schema = await resolver.ResolveAsync(
            statement, CancellationToken.None);

        Assert.Equal(3, schema.Columns.Count);
        Assert.Equal("name", schema.Columns[0].ColumnName);
        Assert.Equal("age", schema.Columns[1].ColumnName);
        Assert.Equal("score", schema.Columns[2].ColumnName);
        Assert.All(schema.Columns, column =>
            Assert.Equal("people", column.SourceTableOrAlias));
    }

    // ───────────────────── Table with alias ─────────────────────

    [Fact]
    public async Task Resolve_TableWithAlias_UsesAliasAsSource()
    {
        TableCatalog catalog = CreateCatalog();
        QuerySchemaResolver resolver = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("people", "p")));

        ResolvedQuerySchema schema = await resolver.ResolveAsync(
            statement, CancellationToken.None);

        Assert.Equal(3, schema.Columns.Count);
        Assert.All(schema.Columns, column =>
            Assert.Equal("p", column.SourceTableOrAlias));

        // Qualified lookup should work.
        Assert.NotNull(schema.FindColumn("p.name"));
        Assert.Equal(DataKind.String, schema.FindColumn("p.name")!.Kind);
    }

    // ───────────────────── JOIN ─────────────────────

    [Fact]
    public async Task Resolve_InnerJoin_MergesSchemas()
    {
        TableCatalog catalog = CreateCatalog();
        QuerySchemaResolver resolver = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("people", "p")),
            Joins:
            [
                new JoinClause(
                    JoinType.Inner,
                    new TableReference("scores", "s"),
                    new BinaryExpression(
                        new ColumnReference("p", "name"),
                        BinaryOperator.Equal,
                        new ColumnReference("s", "name")))
            ]);

        ResolvedQuerySchema schema = await resolver.ResolveAsync(
            statement, CancellationToken.None);

        // Both tables have 3 columns each.
        Assert.Equal(6, schema.Columns.Count);

        // Columns from both sources should be present.
        IReadOnlyList<ResolvedColumn> peopleColumns = schema.FindColumns("p");
        IReadOnlyList<ResolvedColumn> scoreColumns = schema.FindColumns("s");
        Assert.Equal(3, peopleColumns.Count);
        Assert.Equal(3, scoreColumns.Count);
    }

    [Fact]
    public async Task Resolve_LeftJoin_MarksJoinedColumnsNullable()
    {
        TableCatalog catalog = CreateCatalog();
        QuerySchemaResolver resolver = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("people", "p")),
            Joins:
            [
                new JoinClause(
                    JoinType.Left,
                    new TableReference("scores", "s"),
                    new BinaryExpression(
                        new ColumnReference("p", "name"),
                        BinaryOperator.Equal,
                        new ColumnReference("s", "name")))
            ]);

        ResolvedQuerySchema schema = await resolver.ResolveAsync(
            statement, CancellationToken.None);

        IReadOnlyList<ResolvedColumn> joinedColumns = schema.FindColumns("s");
        Assert.All(joinedColumns, column => Assert.True(column.Nullable));
    }

    // ───────────────────── Subquery ─────────────────────

    [Fact]
    public async Task Resolve_Subquery_SelectAll_PassesThroughColumns()
    {
        TableCatalog catalog = CreateCatalog();
        QuerySchemaResolver resolver = new(catalog, DefaultFunctions);

        SelectStatement innerQuery = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("people")));

        SelectStatement outerQuery = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new SubquerySource(innerQuery, "sub")));

        ResolvedQuerySchema schema = await resolver.ResolveAsync(
            outerQuery, CancellationToken.None);

        Assert.Equal(3, schema.Columns.Count);
        Assert.All(schema.Columns, column =>
            Assert.Equal("sub", column.SourceTableOrAlias));
    }

    [Fact]
    public async Task Resolve_Subquery_NamedColumns_InfersTypes()
    {
        TableCatalog catalog = CreateCatalog();
        QuerySchemaResolver resolver = new(catalog, DefaultFunctions);

        SelectStatement innerQuery = new(
            Columns:
            [
                new SelectColumn(new ColumnReference("name"), "person_name"),
                new SelectColumn(new ColumnReference("score"), "total")
            ],
            From: new FromClause(new TableReference("people")));

        SelectStatement outerQuery = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new SubquerySource(innerQuery, "sub")));

        ResolvedQuerySchema schema = await resolver.ResolveAsync(
            outerQuery, CancellationToken.None);

        Assert.Equal(2, schema.Columns.Count);
        Assert.Equal("person_name", schema.Columns[0].ColumnName);
        Assert.Equal(DataKind.String, schema.Columns[0].Kind);
        Assert.Equal("total", schema.Columns[1].ColumnName);
        Assert.Equal(DataKind.Scalar, schema.Columns[1].Kind);
    }

    // ───────────────────── Function source ─────────────────────

    [Fact]
    public async Task Resolve_RangeFunction_ReturnsValueColumn()
    {
        TableCatalog catalog = CreateCatalog();
        QuerySchemaResolver resolver = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(
                new FunctionSource("range", [
                    new LiteralExpression(0),
                    new LiteralExpression(10)
                ], "r")));

        ResolvedQuerySchema schema = await resolver.ResolveAsync(
            statement, CancellationToken.None);

        Assert.Single(schema.Columns);
        Assert.Equal("Value", schema.Columns[0].ColumnName);
        Assert.Equal(DataKind.Scalar, schema.Columns[0].Kind);
        Assert.Equal("r", schema.Columns[0].SourceTableOrAlias);
    }

    [Fact]
    public async Task Resolve_UnnestFunction_ReturnsValueColumn()
    {
        TableCatalog catalog = CreateCatalog();
        QuerySchemaResolver resolver = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(
                new FunctionSource("unnest", [
                    new ColumnReference("embedding")
                ], "u")));

        ResolvedQuerySchema schema = await resolver.ResolveAsync(
            statement, CancellationToken.None);

        Assert.Single(schema.Columns);
        Assert.Equal("value", schema.Columns[0].ColumnName);
        Assert.Equal("u", schema.Columns[0].SourceTableOrAlias);
    }

    // ───────────────────── Unknown table ─────────────────────

    [Fact]
    public async Task Resolve_UnknownTable_ThrowsKeyNotFoundException()
    {
        TableCatalog catalog = CreateCatalog();
        QuerySchemaResolver resolver = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("nonexistent")));

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => resolver.ResolveAsync(statement, CancellationToken.None));
    }

    // ───────────────────── FindColumn lookups ─────────────────────

    [Fact]
    public async Task FindColumn_Unqualified_ReturnsFirstMatch()
    {
        TableCatalog catalog = CreateCatalog();
        QuerySchemaResolver resolver = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("people")));

        ResolvedQuerySchema schema = await resolver.ResolveAsync(
            statement, CancellationToken.None);

        ResolvedColumn? column = schema.FindColumn("age");
        Assert.NotNull(column);
        Assert.Equal(DataKind.Scalar, column.Kind);
    }

    [Fact]
    public async Task FindColumn_CaseInsensitive_ReturnsMatch()
    {
        TableCatalog catalog = CreateCatalog();
        QuerySchemaResolver resolver = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("people")));

        ResolvedQuerySchema schema = await resolver.ResolveAsync(
            statement, CancellationToken.None);

        Assert.NotNull(schema.FindColumn("NAME"));
        Assert.NotNull(schema.FindColumn("Age"));
    }

    // ───────────────────── TableNames ─────────────────────

    [Fact]
    public async Task TableNames_ReturnsDistinctSources()
    {
        TableCatalog catalog = CreateCatalog();
        QuerySchemaResolver resolver = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("people", "p")),
            Joins:
            [
                new JoinClause(
                    JoinType.Inner,
                    new TableReference("scores", "s"),
                    new BinaryExpression(
                        new ColumnReference("p", "name"),
                        BinaryOperator.Equal,
                        new ColumnReference("s", "name")))
            ]);

        ResolvedQuerySchema schema = await resolver.ResolveAsync(
            statement, CancellationToken.None);

        List<string> tableNames = schema.TableNames.ToList();
        Assert.Equal(2, tableNames.Count);
        Assert.Contains("p", tableNames);
        Assert.Contains("s", tableNames);
    }

    // ───────────────────── Cross join with function source ─────────────────────

    [Fact]
    public async Task Resolve_CrossJoinWithRange_MergesBothSchemas()
    {
        TableCatalog catalog = CreateCatalog();
        QuerySchemaResolver resolver = new(catalog, DefaultFunctions);

        SelectStatement statement = new(
            Columns: [new SelectAllColumns()],
            From: new FromClause(new TableReference("people", "t")),
            Joins:
            [
                new JoinClause(
                    JoinType.Cross,
                    new FunctionSource("range", [
                        new LiteralExpression(0),
                        new LiteralExpression(360)
                    ], "r"),
                    OnCondition: null)
            ]);

        ResolvedQuerySchema schema = await resolver.ResolveAsync(
            statement, CancellationToken.None);

        // 3 from people + 1 from range.
        Assert.Equal(4, schema.Columns.Count);
        Assert.NotNull(schema.FindColumn("r.Value"));
        Assert.NotNull(schema.FindColumn("t.name"));
    }
}
