using Discord;
using Discord.WebSocket;

namespace ShinBreaker.Services;

public static class LoggingCommands
{
    public static bool Handles(string commandName) => commandName is "logconfig" or "logchannels" or "userhistory";

    public static ApplicationCommandProperties[] BuildAll() =>
    [
        new SlashCommandBuilder().WithName("logconfig").WithDescription("Choose where message and user-change logs are sent")
            .WithDefaultMemberPermissions(GuildPermission.ManageGuild)
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("type").WithDescription("Which logs to configure")
                .WithType(ApplicationCommandOptionType.String).WithRequired(true)
                .AddChoice("Messages: edits and deletes", "messages")
                .AddChoice("Messages: edits only", "edits")
                .AddChoice("Messages: deletes only", "deletes")
                .AddChoice("Users: all changes", "users")
                .AddChoice("Users: username / display name / nickname", "usernames")
                .AddChoice("Users: avatars (global and server)", "avatars"))
            .AddOption(new SlashCommandOptionBuilder()
                .WithName("channel").WithDescription("Log channel (leave empty to reset to the default)")
                .WithType(ApplicationCommandOptionType.Channel).WithRequired(false)
                .AddChannelType(ChannelType.Text))
            .Build(),
        new SlashCommandBuilder().WithName("logchannels").WithDescription("Show where each kind of log is currently sent")
            .WithDefaultMemberPermissions(GuildPermission.ManageGuild)
            .Build(),
        new SlashCommandBuilder().WithName("userhistory").WithDescription("View a member's logged username, avatar and nickname changes")
            .WithDefaultMemberPermissions(GuildPermission.ModerateMembers)
            .AddOption("user", ApplicationCommandOptionType.String, "Mention, user ID, username or nickname", isRequired: true)
            .Build()
    ];

    public static async Task HandleAsync(SocketSlashCommand command, LoggingDatabase database, ModerationDatabase moderationDatabase)
    {
        if ((command.Channel as SocketGuildChannel)?.Guild is not SocketGuild guild || command.User is not SocketGuildUser moderator)
        {
            await command.RespondAsync("This command can only be used inside a server.", ephemeral: true);
            return;
        }

        await command.DeferAsync(ephemeral: true);

        if (command.Data.Name == "logchannels")
        {
            if (!moderator.GuildPermissions.ManageGuild)
            {
                await command.FollowupAsync("You need **Manage Server** to view log settings.", ephemeral: true);
                return;
            }
            var set = await database.GetLogChannelsAsync(guild.Id);
            var fallback = await moderationDatabase.GetLogChannelAsync(guild.Id);
            string Row(string label, LogKind kind, LogKind? parent)
            {
                if (set.TryGetValue(kind, out var id)) return $"**{label}:** <#{id}>";
                if (parent is { } p && set.TryGetValue(p, out var parentId)) return $"**{label}:** <#{parentId}> *(group default)*";
                return fallback is { } mod ? $"**{label}:** <#{mod}> *(mod log)*" : $"**{label}:** *not logged*";
            }
            var text = string.Join("\n", new[]
            {
                Row("Message edits", LogKind.Edits, LogKind.Messages),
                Row("Message deletes", LogKind.Deletes, LogKind.Messages),
                Row("Username / nickname changes", LogKind.Usernames, LogKind.Users),
                Row("Avatar changes", LogKind.Avatars, LogKind.Users)
            });
            await command.FollowupAsync(embed: new EmbedBuilder().WithTitle("Log channels").WithDescription(text)
                .WithColor(Color.Blue).WithFooter("Change with /logconfig").Build(), ephemeral: true);
            return;
        }

        if (command.Data.Name == "logconfig")
        {
            if (!moderator.GuildPermissions.ManageGuild)
            {
                await command.FollowupAsync("You need **Manage Server** to configure logs.", ephemeral: true);
                return;
            }

            var type = (string)command.Data.Options.First(o => o.Name == "type").Value;
            var (kind, label) = type switch
            {
                "edits" => (LogKind.Edits, "Message edit"),
                "deletes" => (LogKind.Deletes, "Message delete"),
                "users" => (LogKind.Users, "User change"),
                "usernames" => (LogKind.Usernames, "Username change"),
                "avatars" => (LogKind.Avatars, "Avatar change"),
                _ => (LogKind.Messages, "Message")
            };

            SocketTextChannel? target = null;
            if (command.Data.Options.FirstOrDefault(o => o.Name == "channel")?.Value is IChannel option)
            {
                target = option as SocketTextChannel;
                if (target is null || target.Guild.Id != guild.Id)
                {
                    await command.FollowupAsync("Choose a text channel in this server.", ephemeral: true);
                    return;
                }
            }

            await database.SetLogChannelAsync(guild.Id, kind, target?.Id);
            await command.FollowupAsync(target is null
                ? $"{label} logs reset: they will use the group default, or the `/modconfig` log channel if none is set."
                : $"{label} logs will be sent to {target.Mention}.", ephemeral: true);
            return;
        }

        // userhistory
        if (!moderator.GuildPermissions.ModerateMembers && !moderator.GuildPermissions.ManageGuild)
        {
            await command.FollowupAsync("You need **Moderate Members** to view user history.", ephemeral: true);
            return;
        }

        var input = (string)command.Data.Options.First(o => o.Name == "user").Value;
        var member = await UserResolver.GetUserAsync(input, guild);

        // A member who already left can't be resolved by name, but their raw ID still has history.
        ulong? userId = member?.Id;
        if (userId is null && ulong.TryParse(input.Trim(), out var rawId)) userId = rawId;
        if (userId is null)
        {
            await command.FollowupAsync($"I couldn't find a member matching `{Format.Sanitize(input)}`.", ephemeral: true);
            return;
        }

        var history = await BuildHistoryAsync(database, guild.Id, userId.Value);
        if (history is null)
            await command.FollowupAsync($"No username, avatar or nickname changes logged for <@{userId}>.", ephemeral: true);
        else
            await command.FollowupAsync(embed: history, ephemeral: true);
    }

    /// <summary>Latest logged changes for a member, or null when there are none. Shared by /userhistory and the prefix command.</summary>
    internal static async Task<Embed?> BuildHistoryAsync(LoggingDatabase database, ulong guildId, ulong userId)
    {
        var changes = await database.GetUserChangesAsync(guildId, userId, 15);
        if (changes.Count == 0) return null;

        var description = string.Join("\n", changes.Select(c => Describe(c, guildId)));
        if (description.Length > 4000) description = description[..3999] + "…";
        return new EmbedBuilder()
            .WithTitle("Change history")
            .WithDescription($"<@{userId}> · `{userId}`\n\n{description}")
            .WithColor(Color.Blue)
            .WithFooter($"Showing the latest {changes.Count} change(s)")
            .Build();
    }

    private static string Describe(UserChange change, ulong guildId)
    {
        var when = $"<t:{change.ChangedAt.ToUnixTimeSeconds()}:f>";
        string Text(string? value) => string.IsNullOrEmpty(value) ? "*none*" : $"`{value.Replace('`', '\'')}`";
        string Link(string label, string? url) => url is null ? "*none*" : $"[{label}]({url})";

        return change.Kind switch
        {
            "username" => $"{when} · **Username** {Text(change.OldValue)} → {Text(change.NewValue)}",
            "displayname" => $"{when} · **Display name** {Text(change.OldValue)} → {Text(change.NewValue)}",
            "nickname" => $"{when} · **Nickname** {Text(change.OldValue)} → {Text(change.NewValue)}",
            "avatar" => $"{when} · **Avatar** {Link("old", AvatarUrls.User(change.UserId, change.OldValue))} → {Link("new", AvatarUrls.User(change.UserId, change.NewValue))}",
            "server_avatar" => $"{when} · **Server avatar** {Link("old", AvatarUrls.Guild(guildId, change.UserId, change.OldValue))} → {Link("new", AvatarUrls.Guild(guildId, change.UserId, change.NewValue))}",
            _ => $"{when} · {change.Kind}"
        };
    }
}
