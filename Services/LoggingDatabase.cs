using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ShinBreaker.Services;

/// <summary>
/// Messages and Users are group defaults; Edits/Deletes fall back to Messages, Usernames/Avatars fall back to Users,
/// and everything finally falls back to the /modconfig moderation log channel.
/// </summary>
public enum LogKind { Messages, Users, Edits, Deletes, Usernames, Avatars }

public sealed record StoredMessage(
    ulong MessageId,
    ulong GuildId,
    ulong ChannelId,
    ulong AuthorId,
    string AuthorName,
    string Content,
    string Attachments,
    DateTimeOffset CreatedAt);

/// <summary>What we last saw for a member in a guild. Used to detect username / avatar / nickname changes.</summary>
public sealed record MemberSnapshot(
    string Username,
    string? GlobalName,
    string? Nickname,
    string? AvatarId,
    string? GuildAvatarId);

public sealed record UserChange(
    long Id,
    ulong GuildId,
    ulong UserId,
    string Kind,
    string? OldValue,
    string? NewValue,
    DateTimeOffset ChangedAt);

/// <summary>Message cache, member snapshots, change history and log-channel settings. Shares the Martin.db file.</summary>
public sealed class LoggingDatabase
{
    private readonly string _connectionString;

    public LoggingDatabase(string path)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = fullPath }.ToString();
    }

    private SqliteConnection CreateConnection() => new(_connectionString);

    private static object OrNull(string? value) => value is null ? DBNull.Value : value;

    public async Task InitializeAsync()
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS LoggedMessages (
                MessageId TEXT PRIMARY KEY,
                GuildId TEXT NOT NULL,
                ChannelId TEXT NOT NULL,
                AuthorId TEXT NOT NULL,
                AuthorName TEXT NOT NULL,
                Content TEXT NOT NULL,
                Attachments TEXT NOT NULL,
                CreatedAt TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_LoggedMessages_CreatedAt ON LoggedMessages (CreatedAt);
            CREATE TABLE IF NOT EXISTS MemberSnapshots (
                GuildId TEXT NOT NULL,
                UserId TEXT NOT NULL,
                Username TEXT NOT NULL,
                GlobalName TEXT NULL,
                Nickname TEXT NULL,
                AvatarId TEXT NULL,
                GuildAvatarId TEXT NULL,
                PRIMARY KEY (GuildId, UserId)
            );
            CREATE TABLE IF NOT EXISTS UserChanges (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                GuildId TEXT NOT NULL,
                UserId TEXT NOT NULL,
                Kind TEXT NOT NULL,
                OldValue TEXT NULL,
                NewValue TEXT NULL,
                ChangedAt TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_UserChanges_GuildUser ON UserChanges (GuildId, UserId, Id DESC);
            CREATE TABLE IF NOT EXISTS LogSettings (
                GuildId TEXT PRIMARY KEY,
                MessageLogChannelId TEXT NULL,
                UserLogChannelId TEXT NULL
            );
            """;
        await command.ExecuteNonQueryAsync();

        // Columns added after the first release; older databases get them here.
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var info = connection.CreateCommand();
        info.CommandText = "PRAGMA table_info(LogSettings);";
        await using (var reader = await info.ExecuteReaderAsync())
            while (await reader.ReadAsync()) existing.Add(reader.GetString(1));
        foreach (var kind in new[] { LogKind.Edits, LogKind.Deletes, LogKind.Usernames, LogKind.Avatars })
        {
            if (existing.Contains(Column(kind))) continue;
            var alter = connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE LogSettings ADD COLUMN {Column(kind)} TEXT NULL;";
            await alter.ExecuteNonQueryAsync();
        }
    }

    // ---------------------------------------------------------------- messages

    public async Task UpsertMessageAsync(StoredMessage message)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO LoggedMessages (MessageId, GuildId, ChannelId, AuthorId, AuthorName, Content, Attachments, CreatedAt)
            VALUES ($id, $guild, $channel, $author, $name, $content, $attachments, $created)
            ON CONFLICT(MessageId) DO UPDATE SET Content = excluded.Content, Attachments = excluded.Attachments;
            """;
        command.Parameters.AddWithValue("$id", message.MessageId.ToString());
        command.Parameters.AddWithValue("$guild", message.GuildId.ToString());
        command.Parameters.AddWithValue("$channel", message.ChannelId.ToString());
        command.Parameters.AddWithValue("$author", message.AuthorId.ToString());
        command.Parameters.AddWithValue("$name", message.AuthorName);
        command.Parameters.AddWithValue("$content", message.Content);
        command.Parameters.AddWithValue("$attachments", message.Attachments);
        command.Parameters.AddWithValue("$created", message.CreatedAt.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    public async Task<StoredMessage?> GetMessageAsync(ulong messageId)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        return await ReadMessageAsync(connection, messageId);
    }

    public async Task<IReadOnlyList<StoredMessage>> GetMessagesAsync(IEnumerable<ulong> messageIds)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var result = new List<StoredMessage>();
        foreach (var id in messageIds)
            if (await ReadMessageAsync(connection, id) is { } message) result.Add(message);
        return result.OrderBy(m => m.MessageId).ToList();
    }

    private static async Task<StoredMessage?> ReadMessageAsync(SqliteConnection connection, ulong messageId)
    {
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT MessageId, GuildId, ChannelId, AuthorId, AuthorName, Content, Attachments, CreatedAt
            FROM LoggedMessages WHERE MessageId = $id;
            """;
        command.Parameters.AddWithValue("$id", messageId.ToString());
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return new StoredMessage(
            ulong.Parse(reader.GetString(0)), ulong.Parse(reader.GetString(1)), ulong.Parse(reader.GetString(2)),
            ulong.Parse(reader.GetString(3)), reader.GetString(4), reader.GetString(5), reader.GetString(6),
            DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture));
    }

    public async Task DeleteMessagesAsync(IEnumerable<ulong> messageIds)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction();
        foreach (var id in messageIds)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM LoggedMessages WHERE MessageId = $id;";
            command.Parameters.AddWithValue("$id", id.ToString());
            await command.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    /// <summary>Removes cached messages older than the retention window. Returns how many rows were removed.</summary>
    public async Task<int> PurgeOldMessagesAsync(TimeSpan retention)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM LoggedMessages WHERE CreatedAt < $cutoff;";
        command.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.Subtract(retention).ToString("O"));
        return await command.ExecuteNonQueryAsync();
    }

    // ---------------------------------------------------------------- member snapshots & history

    public async Task<MemberSnapshot?> GetSnapshotAsync(ulong guildId, ulong userId)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Username, GlobalName, Nickname, AvatarId, GuildAvatarId
            FROM MemberSnapshots WHERE GuildId = $guild AND UserId = $user;
            """;
        command.Parameters.AddWithValue("$guild", guildId.ToString());
        command.Parameters.AddWithValue("$user", userId.ToString());
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return new MemberSnapshot(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4));
    }

    public async Task SaveSnapshotAsync(ulong guildId, ulong userId, MemberSnapshot snapshot)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO MemberSnapshots (GuildId, UserId, Username, GlobalName, Nickname, AvatarId, GuildAvatarId)
            VALUES ($guild, $user, $username, $global, $nick, $avatar, $guildAvatar)
            ON CONFLICT(GuildId, UserId) DO UPDATE SET
                Username = excluded.Username, GlobalName = excluded.GlobalName, Nickname = excluded.Nickname,
                AvatarId = excluded.AvatarId, GuildAvatarId = excluded.GuildAvatarId;
            """;
        command.Parameters.AddWithValue("$guild", guildId.ToString());
        command.Parameters.AddWithValue("$user", userId.ToString());
        command.Parameters.AddWithValue("$username", snapshot.Username);
        command.Parameters.AddWithValue("$global", OrNull(snapshot.GlobalName));
        command.Parameters.AddWithValue("$nick", OrNull(snapshot.Nickname));
        command.Parameters.AddWithValue("$avatar", OrNull(snapshot.AvatarId));
        command.Parameters.AddWithValue("$guildAvatar", OrNull(snapshot.GuildAvatarId));
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Records members we have never seen before, without overwriting existing snapshots and without logging.</summary>
    public async Task SeedSnapshotsAsync(ulong guildId, IEnumerable<(ulong UserId, MemberSnapshot Snapshot)> members)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction();
        foreach (var (userId, snapshot) in members)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO MemberSnapshots (GuildId, UserId, Username, GlobalName, Nickname, AvatarId, GuildAvatarId)
                VALUES ($guild, $user, $username, $global, $nick, $avatar, $guildAvatar);
                """;
            command.Parameters.AddWithValue("$guild", guildId.ToString());
            command.Parameters.AddWithValue("$user", userId.ToString());
            command.Parameters.AddWithValue("$username", snapshot.Username);
            command.Parameters.AddWithValue("$global", OrNull(snapshot.GlobalName));
            command.Parameters.AddWithValue("$nick", OrNull(snapshot.Nickname));
            command.Parameters.AddWithValue("$avatar", OrNull(snapshot.AvatarId));
            command.Parameters.AddWithValue("$guildAvatar", OrNull(snapshot.GuildAvatarId));
            await command.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    public async Task AddUserChangesAsync(ulong guildId, ulong userId, IEnumerable<(string Kind, string? Old, string? New)> changes)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction();
        foreach (var (kind, oldValue, newValue) in changes)
        {
            var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO UserChanges (GuildId, UserId, Kind, OldValue, NewValue, ChangedAt)
                VALUES ($guild, $user, $kind, $old, $new, $changed);
                """;
            command.Parameters.AddWithValue("$guild", guildId.ToString());
            command.Parameters.AddWithValue("$user", userId.ToString());
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$old", OrNull(oldValue));
            command.Parameters.AddWithValue("$new", OrNull(newValue));
            command.Parameters.AddWithValue("$changed", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync();
        }
        await transaction.CommitAsync();
    }

    public async Task<IReadOnlyList<UserChange>> GetUserChangesAsync(ulong guildId, ulong userId, int limit = 15)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, GuildId, UserId, Kind, OldValue, NewValue, ChangedAt
            FROM UserChanges WHERE GuildId = $guild AND UserId = $user
            ORDER BY Id DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$guild", guildId.ToString());
        command.Parameters.AddWithValue("$user", userId.ToString());
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 25));
        var changes = new List<UserChange>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            changes.Add(new UserChange(
                reader.GetInt64(0), ulong.Parse(reader.GetString(1)), ulong.Parse(reader.GetString(2)),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture)));
        }
        return changes;
    }

    // ---------------------------------------------------------------- log channels

    // The column name comes from a fixed switch, never from user input.
    private static string Column(LogKind kind) => kind switch
    {
        LogKind.Messages => "MessageLogChannelId",
        LogKind.Users => "UserLogChannelId",
        LogKind.Edits => "EditLogChannelId",
        LogKind.Deletes => "DeleteLogChannelId",
        LogKind.Usernames => "UsernameLogChannelId",
        LogKind.Avatars => "AvatarLogChannelId",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public async Task<ulong?> GetLogChannelAsync(ulong guildId, LogKind kind)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = $"SELECT {Column(kind)} FROM LogSettings WHERE GuildId = $guild;";
        command.Parameters.AddWithValue("$guild", guildId.ToString());
        var value = await command.ExecuteScalarAsync();
        return value is string text && ulong.TryParse(text, out var id) ? id : null;
    }

    /// <summary>Every explicitly configured channel for a guild (kinds with no channel set are absent).</summary>
    public async Task<IReadOnlyDictionary<LogKind, ulong>> GetLogChannelsAsync(ulong guildId)
    {
        var result = new Dictionary<LogKind, ulong>();
        foreach (var kind in Enum.GetValues<LogKind>())
            if (await GetLogChannelAsync(guildId, kind) is { } id) result[kind] = id;
        return result;
    }

    public async Task SetLogChannelAsync(ulong guildId, LogKind kind, ulong? channelId)
    {
        var column = Column(kind);
        await using var connection = CreateConnection();
        await connection.OpenAsync();
        var command = connection.CreateCommand();
        command.CommandText = $"""
            INSERT INTO LogSettings (GuildId, {column}) VALUES ($guild, $channel)
            ON CONFLICT(GuildId) DO UPDATE SET {column} = excluded.{column};
            """;
        command.Parameters.AddWithValue("$guild", guildId.ToString());
        command.Parameters.AddWithValue("$channel", OrNull(channelId?.ToString()));
        await command.ExecuteNonQueryAsync();
    }
}
