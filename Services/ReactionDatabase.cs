using Microsoft.Data.Sqlite;

namespace ShinBreaker.Services;

public sealed record reactionRole(
    long Id,
    ulong GuildId,
    string[] Roles,
    string[] Emojis;

public sealed class ReactionRoleDatabase
{
    private readonly string _connectionString;

    public ReactionRoleDatabase(string path)
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
            CREATE TABLE IF NOT EXISTS ReactionRoles (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                GuildId TEXT NOT NULL,
                Roles ARRAY NOT NULL,
                Emojis ARRAY NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_ReactionRoles_IdTarget
                ON ReactionRoles (Id);
            CREATE INDEX IF NOT EXISTS IX_ReactionRoles_GuildTarget
                ON ReactionRoles (GuildId);
            """;
        await command.ExecuteNonQueryAsync();
    }
}