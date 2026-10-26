using System.Text;
using Discord;
using Discord.WebSocket;

namespace ShinBreaker.Services;

/// <summary>Builds CDN links from stored avatar hashes so old avatars can still be shown in logs and history.</summary>
public static class AvatarUrls
{
    private static string Extension(string hash) => hash.StartsWith("a_", StringComparison.Ordinal) ? "gif" : "png";

    public static string? User(ulong userId, string? hash) =>
        hash is null ? null : $"https://cdn.discordapp.com/avatars/{userId}/{hash}.{Extension(hash)}?size=256";

    public static string? Guild(ulong guildId, ulong userId, string? hash) =>
        hash is null ? null : $"https://cdn.discordapp.com/guilds/{guildId}/users/{userId}/avatars/{hash}.{Extension(hash)}?size=256";
}

/// <summary>
/// Message logging (edits, deletes, bulk deletes) and member change logging
/// (username, display name, nickname, global avatar, server avatar).
/// </summary>
public sealed class ActivityLogger(DiscordSocketClient client, ModerationDatabase moderationDatabase, LoggingDatabase database)
{
    private static readonly TimeSpan PurgeInterval = TimeSpan.FromHours(1);

    private static readonly Color DeleteColor = Color.Red;
    private static readonly Color EditColor = Color.Gold;
    private static readonly Color ChangeColor = Color.Blue;

    public void Attach()
    {
        client.MessageReceived += OnMessageReceived;
        client.MessageUpdated += (before, after, _) => Fire(() => HandleMessageUpdatedAsync(before, after));
        client.MessageDeleted += (message, channel) => Fire(() => HandleMessageDeletedAsync(message, channel));
        client.MessagesBulkDeleted += (messages, channel) => Fire(() => HandleBulkDeletedAsync(messages, channel));
        client.GuildMemberUpdated += (_, after) => Fire(() => HandleMemberUpdatedAsync(after));
        client.UserJoined += member => Fire(() => SeedAsync(member));
        client.GuildMembersDownloaded += guild => Fire(() => SeedGuildAsync(guild));
    }

    /// <summary>Deletes cached messages older than <paramref name="retention"/> once an hour. Run it once, un-awaited.</summary>
    public async Task RunRetentionAsync(TimeSpan retention, CancellationToken cancellationToken = default)
    {
        using var timer = new PeriodicTimer(PurgeInterval);
        do
        {
            try
            {
                var removed = await database.PurgeOldMessagesAsync(retention);
                if (removed > 0) Console.WriteLine($"Message log cache: removed {removed} message(s) older than {retention.TotalDays:0.#} day(s).");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Message log cache purge failed: {ex}");
            }
        } while (await timer.WaitForNextTickAsync(cancellationToken));
    }

    // Gateway handlers must return quickly or Discord.Net warns that the gateway is blocked,
    // so anything that talks to Discord runs in the background. Failures are logged, never thrown.
    private static Task Fire(Func<Task> work)
    {
        _ = Task.Run(async () =>
        {
            try { await work(); }
            catch (Exception ex) { Console.Error.WriteLine($"Activity logging failed: {ex}"); }
        });
        return Task.CompletedTask;
    }

    // ================================================================ messages

    private async Task OnMessageReceived(SocketMessage message)
    {
        if (message is not SocketUserMessage { Channel: SocketGuildChannel channel } userMessage || userMessage.Author.IsBot) return;
        try
        {
            // Awaited (a single fast DB write) so an edit or delete can't race ahead of the insert.
            await database.UpsertMessageAsync(ToStored(userMessage, channel));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not cache message {message.Id}: {ex}");
        }
    }

    private static StoredMessage ToStored(SocketUserMessage message, SocketGuildChannel channel) => new(
        message.Id, channel.Guild.Id, channel.Id, message.Author.Id, message.Author.Username,
        message.Content, string.Join("\n", message.Attachments.Select(a => a.Url)), message.Timestamp);

    private async Task HandleMessageUpdatedAsync(Cacheable<IMessage, ulong> before, SocketMessage after)
    {
        if (after is not SocketUserMessage { Channel: SocketGuildChannel channel } message || message.Author.IsBot) return;

        var stored = await database.GetMessageAsync(message.Id);
        var oldContent = stored?.Content ?? (before.HasValue ? before.Value.Content : null);

        await database.UpsertMessageAsync(ToStored(message, channel));

        // Embeds loading, pins and similar updates fire this event with the content unchanged.
        if (oldContent is not null && oldContent == message.Content) return;

        var target = await GetLogChannelAsync(channel.Guild, LogKind.Edits);
        if (target is null) return;

        var embed = new EmbedBuilder()
            .WithTitle("Message edited")
            .WithDescription($"[Jump to message]({message.GetJumpUrl()})")
            .WithColor(EditColor)
            .WithAuthor(message.Author.Username, message.Author.GetAvatarUrl() ?? message.Author.GetDefaultAvatarUrl())
            .AddField("Author", $"{message.Author.Mention}\n`{message.Author.Id}`", true)
            .AddField("Channel", $"<#{channel.Id}>", true)
            .AddField("Before", oldContent is null
                ? "*Original content unavailable (sent before logging started or already expired).*"
                : Show(oldContent))
            .AddField("After", Show(message.Content))
            .WithTimestamp(DateTimeOffset.UtcNow)
            .WithFooter($"Message ID: {message.Id}");
        await target.SendMessageAsync(embed: embed.Build());
    }

    private async Task HandleMessageDeletedAsync(Cacheable<IMessage, ulong> message, Cacheable<IMessageChannel, ulong> channelRef)
    {
        if (client.GetChannel(channelRef.Id) is not SocketGuildChannel channel) return;

        var stored = await database.GetMessageAsync(message.Id);
        if (stored is not null) await database.DeleteMessagesAsync([message.Id]);

        // Fall back to the gateway cache. If we know nothing about the message, stay quiet:
        // it was probably sent by a bot (which we don't store) or before logging started.
        string content, attachments, authorName;
        ulong authorId;
        DateTimeOffset sent;
        if (stored is not null)
        {
            (content, attachments, authorName, authorId, sent) =
                (stored.Content, stored.Attachments, stored.AuthorName, stored.AuthorId, stored.CreatedAt);
        }
        else if (message.HasValue && message.Value is IUserMessage { Author.IsBot: false } cached)
        {
            (content, attachments, authorName, authorId, sent) =
                (cached.Content, string.Join("\n", cached.Attachments.Select(a => a.Url)), cached.Author.Username,
                 cached.Author.Id, cached.Timestamp);
        }
        else return;

        var target = await GetLogChannelAsync(channel.Guild, LogKind.Deletes);
        if (target is null) return;

        var embed = new EmbedBuilder()
            .WithTitle("Message deleted")
            .WithColor(DeleteColor)
            .AddField("Author", $"<@{authorId}>\n`{Format.Sanitize(authorName)}` (`{authorId}`)", true)
            .AddField("Channel", $"<#{channel.Id}>", true)
            .AddField("Sent", $"<t:{sent.ToUnixTimeSeconds()}:R>", true)
            .AddField("Content", Show(content))
            .WithTimestamp(DateTimeOffset.UtcNow)
            .WithFooter($"Message ID: {message.Id}");
        if (attachments.Length > 0) embed.AddField("Attachments", Truncate(attachments, 1024));
        await target.SendMessageAsync(embed: embed.Build());
    }

    private async Task HandleBulkDeletedAsync(IReadOnlyCollection<Cacheable<IMessage, ulong>> messages, Cacheable<IMessageChannel, ulong> channelRef)
    {
        if (client.GetChannel(channelRef.Id) is not SocketGuildChannel channel) return;

        var ids = messages.Select(m => m.Id).ToArray();
        var stored = await database.GetMessagesAsync(ids);
        await database.DeleteMessagesAsync(ids);

        var target = await GetLogChannelAsync(channel.Guild, LogKind.Deletes);
        if (target is null) return;

        var embed = new EmbedBuilder()
            .WithTitle("Bulk message delete")
            .WithColor(DeleteColor)
            .AddField("Channel", $"<#{channel.Id}>", true)
            .AddField("Deleted", ids.Length.ToString(), true)
            .AddField("Recovered", $"{stored.Count} of {ids.Length}", true)
            .WithTimestamp(DateTimeOffset.UtcNow);

        if (stored.Count == 0)
        {
            await target.SendMessageAsync(embed: embed.Build());
            return;
        }

        var transcript = new StringBuilder();
        foreach (var m in stored)
        {
            transcript.Append('[').Append(m.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss")).Append(" UTC] ")
                .Append(m.AuthorName).Append(" (").Append(m.AuthorId).Append("): ")
                .AppendLine(m.Content.Length == 0 ? "(no text)" : m.Content);
            if (m.Attachments.Length > 0)
                foreach (var url in m.Attachments.Split('\n')) transcript.Append("    attachment: ").AppendLine(url);
        }
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(transcript.ToString()));
        await target.SendFileAsync(stream, $"deleted-messages-{channel.Id}.txt", embed: embed.Build());
    }

    // ================================================================ member changes

    private static MemberSnapshot Snapshot(SocketGuildUser member) =>
        new(member.Username, member.GlobalName, member.Nickname, member.AvatarId, member.GuildAvatarId);

    private async Task SeedAsync(SocketGuildUser member)
    {
        if (member.IsBot) return;
        await database.SeedSnapshotsAsync(member.Guild.Id, [(member.Id, Snapshot(member))]);
    }

    private async Task SeedGuildAsync(SocketGuild guild)
    {
        var members = guild.Users.Where(u => !u.IsBot).Select(u => (u.Id, Snapshot(u))).ToList();
        await database.SeedSnapshotsAsync(guild.Id, members);
        Console.WriteLine($"Member log: tracking {members.Count} member(s) in {guild.Name}.");
    }

    private async Task HandleMemberUpdatedAsync(SocketGuildUser member)
    {
        if (member.IsBot) return;

        // Compare against our own stored snapshot rather than the event's "before": global username /
        // avatar changes are shared across every server the user is in, so the cached "before" can
        // already hold the new values.
        var current = Snapshot(member);
        var previous = await database.GetSnapshotAsync(member.Guild.Id, member.Id);
        if (previous is null)
        {
            await database.SaveSnapshotAsync(member.Guild.Id, member.Id, current);
            return;
        }
        if (previous == current) return;

        await database.SaveSnapshotAsync(member.Guild.Id, member.Id, current);

        var changes = new List<(string Kind, string? Old, string? New)>();
        if (previous.Username != current.Username) changes.Add(("username", previous.Username, current.Username));
        if (previous.GlobalName != current.GlobalName) changes.Add(("displayname", previous.GlobalName, current.GlobalName));
        if (previous.Nickname != current.Nickname) changes.Add(("nickname", previous.Nickname, current.Nickname));
        if (previous.AvatarId != current.AvatarId) changes.Add(("avatar", previous.AvatarId, current.AvatarId));
        if (previous.GuildAvatarId != current.GuildAvatarId) changes.Add(("server_avatar", previous.GuildAvatarId, current.GuildAvatarId));
        if (changes.Count == 0) return;

        await database.AddUserChangesAsync(member.Guild.Id, member.Id, changes);

        foreach (var (kind, oldValue, newValue) in changes)
        {
            var target = await GetLogChannelAsync(member.Guild, kind.EndsWith("avatar") ? LogKind.Avatars : LogKind.Usernames);
            if (target is null) continue;

            var embed = new EmbedBuilder()
                .WithColor(ChangeColor)
                .WithAuthor(current.Username, member.GetAvatarUrl() ?? member.GetDefaultAvatarUrl())
                .AddField("User", $"{member.Mention}\n`{member.Id}`")
                .WithTimestamp(DateTimeOffset.UtcNow)
                .WithFooter($"User ID: {member.Id}");

            switch (kind)
            {
                case "avatar":
                case "server_avatar":
                    var isServer = kind == "server_avatar";
                    var oldUrl = isServer ? AvatarUrls.Guild(member.Guild.Id, member.Id, oldValue) : AvatarUrls.User(member.Id, oldValue);
                    var newUrl = isServer ? AvatarUrls.Guild(member.Guild.Id, member.Id, newValue) : AvatarUrls.User(member.Id, newValue);
                    embed.WithTitle(isServer ? "Server avatar changed" : "Avatar changed")
                        .WithDescription($"Old (thumbnail) → new (large image).{(newUrl is null ? "\nThe custom avatar was removed." : "")}");
                    if (oldUrl is not null) embed.WithThumbnailUrl(oldUrl);
                    if (newUrl is not null) embed.WithImageUrl(newUrl);
                    break;
                default:
                    embed.WithTitle(kind switch
                    {
                        "username" => "Username changed",
                        "displayname" => "Display name changed",
                        _ => "Nickname changed"
                    })
                    .AddField("Before", Value(oldValue), true)
                    .AddField("After", Value(newValue), true);
                    break;
            }
            await target.SendMessageAsync(embed: embed.Build());
        }
    }

    // ================================================================ helpers

    // Channel set with /logconfig, falling back to the /modconfig moderation log channel.
    private async Task<SocketTextChannel?> GetLogChannelAsync(SocketGuild guild, LogKind kind)
    {
        var id = await database.GetLogChannelAsync(guild.Id, kind);
        if (id is null && ParentOf(kind) is { } parent) id = await database.GetLogChannelAsync(guild.Id, parent);
        id ??= await moderationDatabase.GetLogChannelAsync(guild.Id);
        return id is null ? null : guild.GetTextChannel(id.Value);
    }

    private static LogKind? ParentOf(LogKind kind) => kind switch
    {
        LogKind.Edits or LogKind.Deletes => LogKind.Messages,
        LogKind.Usernames or LogKind.Avatars => LogKind.Users,
        _ => null
    };

    private static string Value(string? text) => string.IsNullOrEmpty(text) ? "*none*" : Truncate(Format.Sanitize(text), 1024);

    // Message text is shown as typed (markdown renders); an empty message is usually attachments only.
    private static string Show(string text) => text.Length == 0 ? "*no text*" : Truncate(text, 1024);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";
}
