using System.Text.Json;
using System.Text.Json.Serialization;
using Aion2DpsMeter.Core.Combat;
using Microsoft.Data.Sqlite;

namespace Aion2DpsMeter.Core.History;

/// <summary>One row of the history list.</summary>
public sealed record EncounterSummary(
    Guid Id,
    DateTimeOffset StartedAt,
    long DurationMs,
    string Name,
    int NpcCode,
    bool IsBoss,
    bool Killed,
    bool IsTrainingDummy,
    string DungeonName,
    long TotalDamage,
    long TotalHeal,
    int PlayerCount,
    string LocalName,
    double LocalDps)
{
    public double DurationSeconds => Math.Max(DurationMs, 1000) / 1000.0;
    public double TotalDps => TotalDamage / DurationSeconds;
}

/// <summary>Per-monster statistics across the history.</summary>
public sealed record MonsterSummary(string Name, int NpcCode, bool IsBoss, int Fights, int Kills, long BestKillMs, double BestLocalDps, DateTimeOffset LastFought);

public sealed class HistoryQuery
{
    public string? NameContains { get; set; }
    public bool BossesOnly { get; set; }
    public bool KillsOnly { get; set; }
    public int Limit { get; set; } = 500;
}

/// <summary>Fight history in a local SQLite database. Each fight keeps a summary row and its full record as JSON.</summary>
public sealed class HistoryRepository
{
    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly string _connectionString;
    private readonly object _writeLock = new();

    public string DatabasePath { get; }

    /// <summary>Most fights kept; the oldest are removed past this.</summary>
    public int MaxFights { get; set; } = 5000;

    public HistoryRepository(string databasePath)
    {
        DatabasePath = databasePath;
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();
        using var c = Open();
        Exec(c, """
            CREATE TABLE IF NOT EXISTS encounters (
                id TEXT PRIMARY KEY,
                started_at INTEGER NOT NULL,
                duration_ms INTEGER NOT NULL,
                name TEXT NOT NULL,
                npc_code INTEGER NOT NULL,
                is_boss INTEGER NOT NULL,
                killed INTEGER NOT NULL,
                is_dummy INTEGER NOT NULL,
                dungeon_name TEXT NOT NULL,
                total_damage INTEGER NOT NULL,
                total_heal INTEGER NOT NULL,
                player_count INTEGER NOT NULL,
                local_name TEXT NOT NULL,
                local_dps REAL NOT NULL,
                data TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_encounters_started ON encounters(started_at DESC);
            CREATE INDEX IF NOT EXISTS ix_encounters_name ON encounters(name);
            """);
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        return c;
    }

    private static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Save(EncounterRecord r)
    {
        var local = r.Players.FirstOrDefault(p => p.IsLocal);
        lock (_writeLock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                INSERT OR REPLACE INTO encounters
                (id, started_at, duration_ms, name, npc_code, is_boss, killed, is_dummy, dungeon_name,
                 total_damage, total_heal, player_count, local_name, local_dps, data)
                VALUES ($id, $started, $duration, $name, $npc, $boss, $killed, $dummy, $dungeon,
                        $dmg, $heal, $players, $local, $localDps, $data)
                """;
            cmd.Parameters.AddWithValue("$id", r.Id.ToString());
            cmd.Parameters.AddWithValue("$started", r.StartedAt.ToUnixTimeMilliseconds());
            cmd.Parameters.AddWithValue("$duration", r.DurationMs);
            cmd.Parameters.AddWithValue("$name", r.Name);
            cmd.Parameters.AddWithValue("$npc", r.NpcCode);
            cmd.Parameters.AddWithValue("$boss", r.IsBoss ? 1 : 0);
            cmd.Parameters.AddWithValue("$killed", r.Killed ? 1 : 0);
            cmd.Parameters.AddWithValue("$dummy", r.IsTrainingDummy ? 1 : 0);
            cmd.Parameters.AddWithValue("$dungeon", r.DungeonName);
            cmd.Parameters.AddWithValue("$dmg", r.TotalDamage);
            cmd.Parameters.AddWithValue("$heal", r.TotalHeal);
            cmd.Parameters.AddWithValue("$players", r.Players.Count);
            cmd.Parameters.AddWithValue("$local", local?.Name ?? "");
            cmd.Parameters.AddWithValue("$localDps", local?.Dps ?? 0);
            cmd.Parameters.AddWithValue("$data", JsonSerializer.Serialize(r, Json));
            cmd.ExecuteNonQuery();

            using var prune = c.CreateCommand();
            prune.CommandText = "DELETE FROM encounters WHERE id IN (SELECT id FROM encounters ORDER BY started_at DESC LIMIT -1 OFFSET $max)";
            prune.Parameters.AddWithValue("$max", MaxFights);
            prune.ExecuteNonQuery();
        }
    }

    public List<EncounterSummary> List(HistoryQuery q)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        var where = new List<string>();
        if (!string.IsNullOrWhiteSpace(q.NameContains))
        {
            where.Add("name LIKE $name ESCAPE '\\'");
            cmd.Parameters.AddWithValue("$name", "%" + EscapeLike(q.NameContains.Trim()) + "%");
        }
        if (q.BossesOnly)
            where.Add("is_boss = 1");
        if (q.KillsOnly)
            where.Add("killed = 1");
        cmd.CommandText = $"""
            SELECT id, started_at, duration_ms, name, npc_code, is_boss, killed, is_dummy, dungeon_name,
                   total_damage, total_heal, player_count, local_name, local_dps
            FROM encounters
            {(where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "")}
            ORDER BY started_at DESC
            LIMIT $limit
            """;
        cmd.Parameters.AddWithValue("$limit", q.Limit);

        var list = new List<EncounterSummary>();
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            list.Add(new EncounterSummary(
                Guid.Parse(rd.GetString(0)),
                DateTimeOffset.FromUnixTimeMilliseconds(rd.GetInt64(1)).ToLocalTime(),
                rd.GetInt64(2),
                rd.GetString(3),
                rd.GetInt32(4),
                rd.GetInt32(5) != 0,
                rd.GetInt32(6) != 0,
                rd.GetInt32(7) != 0,
                rd.GetString(8),
                rd.GetInt64(9),
                rd.GetInt64(10),
                rd.GetInt32(11),
                rd.GetString(12),
                rd.GetDouble(13)));
        }
        return list;
    }

    public List<MonsterSummary> Monsters()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT name, MAX(npc_code), MAX(is_boss), COUNT(*), SUM(killed),
                   MIN(CASE WHEN killed = 1 THEN duration_ms END), MAX(local_dps), MAX(started_at)
            FROM encounters
            GROUP BY name
            ORDER BY MAX(is_boss) DESC, COUNT(*) DESC
            """;
        var list = new List<MonsterSummary>();
        using var rd = cmd.ExecuteReader();
        while (rd.Read())
        {
            list.Add(new MonsterSummary(
                rd.GetString(0),
                rd.GetInt32(1),
                rd.GetInt32(2) != 0,
                rd.GetInt32(3),
                rd.GetInt32(4),
                rd.IsDBNull(5) ? 0 : rd.GetInt64(5),
                rd.GetDouble(6),
                DateTimeOffset.FromUnixTimeMilliseconds(rd.GetInt64(7)).ToLocalTime()));
        }
        return list;
    }

    public EncounterRecord? Get(Guid id)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT data FROM encounters WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        return cmd.ExecuteScalar() is string json ? JsonSerializer.Deserialize<EncounterRecord>(json, Json) : null;
    }

    public void Delete(Guid id)
    {
        lock (_writeLock)
        {
            using var c = Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "DELETE FROM encounters WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", id.ToString());
            cmd.ExecuteNonQuery();
        }
    }

    public static string ToJson(EncounterRecord r) =>
        JsonSerializer.Serialize(r, new JsonSerializerOptions(Json) { WriteIndented = true });

    private static string EscapeLike(string s) => s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
