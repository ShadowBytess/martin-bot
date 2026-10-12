using Microsoft.Data.Sqlite;

namespace ShinBreaker.Services;

public sealed record ModerationCase(
    long Id,
    ulong GuildId,
    ulong TargetId,
    ulong ModeratorId,
    string Action,
    string Reason,
    DateTimeOffset CreatedAt,
    int? DurationMinutes);

public sealed class ModerationDatabase
{
    private readonly string _connectionString;

    public ModerationDatabase(string path)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = fullPath }.ToString();
    }

    private SqliteConnection CreateConnection() => new(_connectionString);

    public async Task InitializeAsync()
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS ModerationCases (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                GuildId TEXT NOT NULL,
                TargetId TEXT NOT NULL,
                ModeratorId TEXT NOT NULL,
                Action TEXT NOT NULL,
                Reason TEXT NOT NULL,
                CreatedAt TEXT NOT NULL,
                DurationMinutes INTEGER NULL
            );
            CREATE INDEX IF NOT EXISTS IX_ModerationCases_GuildTarget
                ON ModerationCases (GuildId, TargetId, Id DESC);
            CREATE TABLE IF NOT EXISTS GuildSettings (
                GuildId TEXT PRIMARY KEY,
                LogChannelId TEXT NULL
            );
            """;
        await command.ExecuteNonQueryAsync();
    }

    public async Task<long> AddCaseAsync(ulong guildId, ulong targetId, ulong moderatorId,
        string action, string reason, int? durationMinutes = null)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ModerationCases (GuildId, TargetId, ModeratorId, Action, Reason, CreatedAt, DurationMinutes)
            VALUES ($guild, $target, $moderator, $action, $reason, $created, $duration);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$guild", guildId.ToString());
        command.Parameters.AddWithValue("$target", targetId.ToString());
        command.Parameters.AddWithValue("$moderator", moderatorId.ToString());
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$reason", reason);
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$duration", (object?)durationMinutes ?? DBNull.Value);
        return (long)(await command.ExecuteScalarAsync() ?? 0L);
    }

    public async Task<IReadOnlyList<ModerationCase>> GetCasesAsync(ulong guildId, ulong targetId, int limit = 10)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, GuildId, TargetId, ModeratorId, Action, Reason, CreatedAt, DurationMinutes
            FROM ModerationCases WHERE GuildId = $guild AND TargetId = $target
            ORDER BY Id DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$guild", guildId.ToString());
        command.Parameters.AddWithValue("$target", targetId.ToString());
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 25));
        var cases = new List<ModerationCase>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            cases.Add(new ModerationCase(
                reader.GetInt64(0), ulong.Parse(reader.GetString(1)), ulong.Parse(reader.GetString(2)),
                ulong.Parse(reader.GetString(3)), reader.GetString(4), reader.GetString(5),
                DateTimeOffset.Parse(reader.GetString(6)), reader.IsDBNull(7) ? null : reader.GetInt32(7)));
        }
        return cases;
    }

    public async Task<ulong?> GetLogChannelAsync(ulong guildId)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "SELECT LogChannelId FROM GuildSettings WHERE GuildId = $guild;";
        command.Parameters.AddWithValue("$guild", guildId.ToString());
        var value = await command.ExecuteScalarAsync();
        return value is string text && ulong.TryParse(text, out var id) ? id : null;
    }

    public async Task SetLogChannelAsync(ulong guildId, ulong? channelId)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO GuildSettings (GuildId, LogChannelId) VALUES ($guild, $channel)
            ON CONFLICT(GuildId) DO UPDATE SET LogChannelId = excluded.LogChannelId;
            """;
        command.Parameters.AddWithValue("$guild", guildId.ToString());
        command.Parameters.AddWithValue("$channel", channelId?.ToString() is { } id ? id : DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }
}
