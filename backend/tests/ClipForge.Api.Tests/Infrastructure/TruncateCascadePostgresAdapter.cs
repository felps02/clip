using System.Data.Common;
using Respawn;
using Respawn.Graph;

namespace ClipForge.Api.Tests.Infrastructure;

/// <summary>
/// Respawn's PostgreSQL adapter, except for the delete command: one <c>TRUNCATE … CASCADE</c> of every table. The stock
/// adapter wraps tables that form a foreign-key cycle (clips.current_render_id ↔ clip_renders.clip_id) in
/// <c>ALTER TABLE … DISABLE TRIGGER ALL</c>, which needs a superuser for the system RI triggers; truncating all the
/// tables in a single statement needs no trigger juggling and works for the database owner.
/// </summary>
internal sealed class TruncateCascadePostgresAdapter : IDbAdapter
{
    private static readonly IDbAdapter Inner = DbAdapter.Postgres;

    public string BuildTableCommandText(RespawnerOptions options) => Inner.BuildTableCommandText(options);

    public string BuildTemporalTableCommandText(RespawnerOptions options) => Inner.BuildTemporalTableCommandText(options);

    public string BuildRelationshipCommandText(RespawnerOptions options) => Inner.BuildRelationshipCommandText(options);

    public string BuildDeleteCommandText(GraphBuilder builder, RespawnerOptions options)
    {
        var tables = builder.ToDelete.Select(table => table.GetFullName('"')).ToList();
        return tables.Count == 0 ? string.Empty : $"TRUNCATE TABLE {string.Join(", ", tables)} CASCADE;";
    }

    public string BuildReseedSql(IEnumerable<Table> tablesToDelete) => Inner.BuildReseedSql(tablesToDelete);

    public string BuildTurnOffSystemVersioningCommandText(IEnumerable<TemporalTable> tablesToTurnOffSystemVersioning)
        => Inner.BuildTurnOffSystemVersioningCommandText(tablesToTurnOffSystemVersioning);

    public string BuildTurnOnSystemVersioningCommandText(IEnumerable<TemporalTable> tablesToTurnOnSystemVersioning)
        => Inner.BuildTurnOnSystemVersioningCommandText(tablesToTurnOnSystemVersioning);

    public Task<bool> CheckSupportsTemporalTables(DbConnection connection) => Inner.CheckSupportsTemporalTables(connection);

    public bool RequiresStatementsToBeExecutedIndividually() => Inner.RequiresStatementsToBeExecutedIndividually();
}
