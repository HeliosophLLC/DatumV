using System.Reflection;
using System.Text.RegularExpressions;
using Heliosoph.DatumV.Catalog;
using Heliosoph.DatumV.Data;
using Heliosoph.DatumV.Model;
using Microsoft.Extensions.Logging;

namespace Heliosoph.DatumV.Web.Migrations;

// Discovers embedded `NNN_*.sql` migrations under Migrations/, applies the
// ones above the catalog's current version (read from __schema_migrations),
// and records each. Runs on startup via CatalogInitializationService when
// WebHostOptions.ManageLocalCatalog is true.
//
// Design notes:
//  - The migration .sql files are pure DDL/DML; they do *not* INSERT into
//    __schema_migrations themselves. The runner extracts the version from
//    the filename and records the row programmatically. Keeps a single
//    source of truth for the version (the filename) and avoids "I bumped
//    the file but forgot the INSERT" bugs.
//  - Version 1 is special: its migration creates the tracking table itself,
//    so the recording INSERT must run *after* the migration body. Subsequent
//    versions get the same treatment for consistency.
//  - We use SqlParser.ParseBatchWithText to split the script into
//    statements rather than a naive `;`-split. The parser handles quoted
//    strings, comments, and any future statement-spanning constructs.
internal sealed class MigrationRunner
{
    private static readonly Regex MigrationFileName =
        new(@"^_?(?<version>\d+)_(?<name>[^.]+)$", RegexOptions.Compiled);

    private readonly TableCatalog _catalog;
    private readonly ILogger<MigrationRunner> _logger;

    public MigrationRunner(TableCatalog catalog, ILogger<MigrationRunner> logger)
    {
        _catalog = catalog;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        int currentVersion = await GetCurrentVersionAsync(ct).ConfigureAwait(false);
        List<Migration> migrations = DiscoverMigrations();

        int applied = 0;
        foreach (Migration migration in migrations)
        {
            if (migration.Version <= currentVersion) continue;

            _logger.LogInformation("Applying migration {Version:000} {Name}", migration.Version, migration.Name);
            await ApplyAsync(migration.Sql, ct).ConfigureAwait(false);
            await RecordAsync(migration.Version, migration.Name, ct).ConfigureAwait(false);
            applied++;
        }

        if (applied == 0)
        {
            _logger.LogInformation("Catalog at version {Version}; no migrations to apply.", currentVersion);
        }
        else
        {
            _logger.LogInformation("Applied {Count} migration(s).", applied);
        }
    }

    private async Task ApplyAsync(string sql, CancellationToken ct)
    {
        // PrepareAsync auto-detects single vs multi-statement; the
        // command's ExecuteNonQueryAsync drains every result set in
        // source order so a CREATE; INSERT; SELECT migration script runs
        // end-to-end with one call.
        using InProcessDatumDbConnection conn = new(_catalog);
        using InProcessDatumDbCommand cmd = conn.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task<int> GetCurrentVersionAsync(CancellationToken ct)
    {
        // Treat the absence of the tracking table as version 0. This is the
        // exact case on first launch: we're about to create the table as
        // part of migration 001.
        if (!_catalog.TryGetTable("__schema_migrations", out _))
        {
            return 0;
        }

        using InProcessDatumDbConnection conn = new(_catalog);
        using InProcessDatumDbCommand cmd = conn.CreateCommand(
            "SELECT max(version) FROM __schema_migrations");
        DataValue? scalar = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return scalar is { IsNull: false } cell ? cell.AsInt32() : 0;
    }

    private async Task RecordAsync(int version, string name, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Name comes from a controlled filename — SQL injection isn't a
        // real risk here. Doubling single quotes is the minimum defensive
        // hygiene; if migrations ever take user-supplied names we'd switch
        // to parameter binding.
        string escapedName = name.Replace("'", "''");
        string sql = $"INSERT INTO __schema_migrations (version, name) VALUES ({version}, '{escapedName}')";

        using InProcessDatumDbConnection conn = new(_catalog);
        using InProcessDatumDbCommand cmd = conn.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static List<Migration> DiscoverMigrations()
    {
        Assembly assembly = typeof(MigrationRunner).Assembly;
        const string prefix = ".Migrations.";
        const string suffix = ".sql";

        List<Migration> migrations = new();
        foreach (string resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;

            int prefixIdx = resourceName.IndexOf(prefix, StringComparison.Ordinal);
            if (prefixIdx < 0) continue;

            int baseStart = prefixIdx + prefix.Length;
            int baseLength = resourceName.Length - baseStart - suffix.Length;
            string baseName = resourceName.Substring(baseStart, baseLength);

            Match match = MigrationFileName.Match(baseName);
            if (!match.Success) continue;

            int version = int.Parse(match.Groups["version"].Value);
            string name = match.Groups["name"].Value;

            using Stream stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException(
                    $"Manifest resource '{resourceName}' enumerated but stream was null.");
            using StreamReader reader = new(stream);
            string sql = reader.ReadToEnd();

            migrations.Add(new Migration(version, name, sql));
        }

        migrations.Sort((a, b) => a.Version.CompareTo(b.Version));
        return migrations;
    }

    private readonly record struct Migration(int Version, string Name, string Sql);
}
