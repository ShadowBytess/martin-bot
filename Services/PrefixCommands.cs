using Discord;
using Discord.WebSocket;

namespace ShinBreaker.Services;

/// <summary>
/// Text commands: a prefix character followed directly by a command name, e.g. ".ban @user spamming",
/// ",warn 1234567890 reason", ".mute someone 30 cooldown", ".purge 20".
/// A message only counts when the word after the prefix is a known command, so ordinary chat
/// such as "...hello" or ", and then" is ignored. Permissions match the slash versions.
/// </summary>
public sealed class PrefixCommands(
    DiscordSocketClient client,
    ModerationDatabase moderationDatabase,
    ModerationLogger moderationLogger,
    LoggingDatabase loggingDatabase,
    string prefixes)
{
    private static readonly HashSet<string> Known =
        ["test", "ban", "kick", "warn", "mute", "unmute", "unban", "warnings", "modlogs", "purge", "userhistory"];

    public void Attach()
    {
        client.MessageReceived += message =>
        {
            _ = Task.Run(async () =>
            {
                try { await HandleAsync(message); }
                catch (Exception ex) { Console.Error.WriteLine($"Prefix command failed: {ex}"); }
            });
            return Task.CompletedTask;
        };
    }

    private async Task HandleAsync(SocketMessage raw)
    {
        if (raw is not SocketUserMessage { Channel: SocketTextChannel channel } message
            || message.Author.IsBot
            || message.Author is not SocketGuildUser moderator) return;

        var text = message.Content;
        if (text.Length < 2 || !prefixes.Contains(text[0]) || !char.IsLetter(text[1])) return;

        var parts = text[1..].Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
        var name = parts[0].ToLowerInvariant();
        if (!Known.Contains(name)) return;
        var rest = parts.Length > 1 ? parts[1].Trim() : "";
        var guild = channel.Guild;

        async Task<IUserMessage> Reply(string content = "", Embed? embed = null) =>
            await channel.SendMessageAsync(content.Length == 0 ? null : content, embed: embed,
                allowedMentions: AllowedMentions.None,
                messageReference: new MessageReference(message.Id, failIfNotExists: false));

        switch (name)
        {
            case "test":
                await Reply($"Hello {moderator.Username}, the bot works!");
                return;

            case "purge":
                await PurgeAsync(message, channel, moderator, rest, Reply);
                return;

            case "unban":
                await UnbanAsync(guild, moderator, rest, Reply);
                return;

            case "warnings" or "modlogs" or "userhistory":
                await LookupAsync(name, guild, moderator, rest, Reply);
                return;
        }

        await ActionAsync(name, guild, moderator, rest, Reply);
    }

    // ---------------------------------------------------------------- ban / kick / warn / mute / unmute

    private async Task ActionAsync(string name, SocketGuild guild, SocketGuildUser moderator, string rest,
        Func<string, Embed?, Task<IUserMessage>> reply)
    {
        var (permission, label) = name switch
        {
            "ban" => (moderator.GuildPermissions.BanMembers, "Ban Members"),
            "kick" => (moderator.GuildPermissions.KickMembers, "Kick Members"),
            _ => (moderator.GuildPermissions.ModerateMembers, "Moderate Members")
        };
        if (!permission)
        {
            await reply($"You need **{label}** to use this command.", null);
            return;
        }

        var userToken = TakeToken(ref rest);
        if (userToken.Length == 0)
        {
            await reply($"Usage: `{name} <user>{(name == "mute" ? " <minutes>" : "")}{(name == "warn" ? " <reason>" : " [reason]")}`", null);
            return;
        }

        int? durationMinutes = null;
        if (name == "mute")
        {
            if (!int.TryParse(TakeToken(ref rest), out var minutes) || minutes is < 1 or > 40320)
            {
                await reply("Mute duration must be a number of minutes between 1 and 40,320 (28 days).", null);
                return;
            }
            durationMinutes = minutes;
        }

        var reason = rest.Length > 0 ? rest : "No reason provided";
        if (name == "warn" && rest.Length == 0)
        {
            await reply("A warning needs a reason.", null);
            return;
        }

        var resolved = await UserResolver.GetUserAsync(userToken, guild);
        if (resolved is null)
        {
            await reply("I couldn't resolve that user.", null);
            return;
        }
        if (resolved.Id == moderator.Id) { await reply("You can't moderate yourself.", null); return; }
        if (resolved.Id == guild.OwnerId) { await reply("You can't moderate the server owner.", null); return; }

        var target = guild.GetUser(resolved.Id);
        if (target is null)
        {
            await reply("That user isn't currently a member of this server.", null);
            return;
        }
        if (target.Id == guild.CurrentUser.Id || target.Hierarchy >= moderator.Hierarchy || target.Hierarchy >= guild.CurrentUser.Hierarchy)
        {
            await reply("Your role and the bot's role must both be higher than the target's role, and you can't moderate the bot itself.", null);
            return;
        }

        try
        {
            switch (name)
            {
                case "ban": await guild.AddBanAsync(target, 0, reason); break;
                case "kick": await target.KickAsync(reason); break;
                case "mute":
                    await target.SetTimeOutAsync(TimeSpan.FromMinutes(durationMinutes!.Value), new RequestOptions { AuditLogReason = reason });
                    break;
                case "unmute": await target.RemoveTimeOutAsync(new RequestOptions { AuditLogReason = reason }); break;
            }
        }
        catch (Discord.Net.HttpException ex)
        {
            await reply($"Discord refused that action: {ex.Reason ?? ex.Message}", null);
            return;
        }

        var caseNumber = await moderationDatabase.AddCaseAsync(guild.Id, target.Id, moderator.Id, name, reason, durationMinutes);
        await reply($"Done: **{name}** {target.Mention}. Case `#{caseNumber:0000}`.", null);
        await moderationLogger.LogAsync(guild, caseNumber, name, target.Id, target.Username, moderator, reason, durationMinutes);

        if (name == "warn")
        {
            try
            {
                await target.SendMessageAsync(embed: new EmbedBuilder().WithTitle($"Warning in {guild.Name}")
                    .WithDescription($"You have received a warning.\n**Reason:** {reason}\n**Case:** #{caseNumber:0000}")
                    .WithColor(Color.Gold).Build());
            }
            catch (Discord.Net.HttpException) { /* DMs may be disabled; the warning is still recorded. */ }
        }
    }

    // ---------------------------------------------------------------- unban

    private async Task UnbanAsync(SocketGuild guild, SocketGuildUser moderator, string rest,
        Func<string, Embed?, Task<IUserMessage>> reply)
    {
        if (!moderator.GuildPermissions.BanMembers)
        {
            await reply("You need **Ban Members** to unban users.", null);
            return;
        }
        if (!ulong.TryParse(TakeToken(ref rest), out var userId))
        {
            await reply("Usage: `unban <user id> [reason]`", null);
            return;
        }
        var reason = rest.Length > 0 ? rest : "No reason provided";
        try
        {
            await guild.RemoveBanAsync(userId, new RequestOptions { AuditLogReason = reason });
        }
        catch (Discord.Net.HttpException ex)
        {
            await reply($"Discord refused that action: {ex.Reason ?? ex.Message}", null);
            return;
        }
        var caseId = await moderationDatabase.AddCaseAsync(guild.Id, userId, moderator.Id, "unban", reason);
        await reply($"Unbanned user `{userId}`. Case `#{caseId:0000}`.", null);
        await moderationLogger.LogAsync(guild, caseId, "unban", userId, userId.ToString(), moderator, reason);
    }

    // ---------------------------------------------------------------- purge

    private async Task PurgeAsync(SocketUserMessage invoker, SocketTextChannel channel, SocketGuildUser moderator, string rest,
        Func<string, Embed?, Task<IUserMessage>> reply)
    {
        if (!moderator.GuildPermissions.ManageMessages)
        {
            await reply("You need **Manage Messages** to purge messages.", null);
            return;
        }
        if (!int.TryParse(rest.Trim(), out var amount) || amount is < 1 or > 100)
        {
            await reply("Usage: `purge <1-100>`", null);
            return;
        }

        // The messages before the command, then the command message itself.
        var messages = (await channel.GetMessagesAsync(invoker.Id, Direction.Before, amount).FlattenAsync())
            .Where(m => (DateTimeOffset.UtcNow - m.Timestamp).TotalDays < 14)
            .ToArray();
        if (messages.Length > 0) await channel.DeleteMessagesAsync(messages);
        await invoker.DeleteAsync();

        var guild = channel.Guild;
        var caseId = await moderationDatabase.AddCaseAsync(guild.Id, moderator.Id, moderator.Id, "purge",
            $"Deleted {messages.Length} message(s) in #{channel.Name}.");
        await moderationLogger.LogSystemAsync(guild, $"Moderation Case #{caseId:0000} · purge",
            $"**Moderator:** {moderator.Mention}\n**Channel:** {channel.Mention}\n**Messages deleted:** {messages.Length}");

        // The command message is gone, so confirm without replying to it, then tidy up.
        var confirmation = await channel.SendMessageAsync($"Deleted {messages.Length} message(s). Case `#{caseId:0000}`.",
            allowedMentions: AllowedMentions.None);
        await Task.Delay(TimeSpan.FromSeconds(5));
        await confirmation.DeleteAsync();
    }

    // ---------------------------------------------------------------- warnings / modlogs / userhistory

    private async Task LookupAsync(string name, SocketGuild guild, SocketGuildUser moderator, string rest,
        Func<string, Embed?, Task<IUserMessage>> reply)
    {
        if (!moderator.GuildPermissions.ModerateMembers && !moderator.GuildPermissions.ManageGuild)
        {
            await reply("You need **Moderate Members** to use this command.", null);
            return;
        }

        var input = TakeToken(ref rest);
        if (input.Length == 0)
        {
            await reply($"Usage: `{name} <user>`", null);
            return;
        }

        // A member who already left can't be resolved by name, but their raw ID still has records.
        var member = await UserResolver.GetUserAsync(input, guild);
        ulong? userId = member?.Id;
        if (userId is null && ulong.TryParse(input, out var rawId)) userId = rawId;
        if (userId is null)
        {
            await reply("I couldn't resolve that user.", null);
            return;
        }

        if (name == "userhistory")
        {
            var history = await LoggingCommands.BuildHistoryAsync(loggingDatabase, guild.Id, userId.Value);
            if (history is null) await reply($"No username, avatar or nickname changes logged for <@{userId}>.", null);
            else await reply("", history);
            return;
        }

        var cases = await moderationDatabase.GetCasesAsync(guild.Id, userId.Value, 25);
        if (name == "warnings") cases = cases.Where(c => c.Action == "warn").ToArray();
        if (cases.Count == 0)
        {
            await reply($"No {(name == "warnings" ? "warnings" : "moderation cases")} found for <@{userId}>.", null);
            return;
        }
        var description = string.Join("\n", cases.Select(c =>
            $"`#{c.Id:0000}` **{c.Action}** — {c.CreatedAt:yyyy-MM-dd HH:mm} UTC\nReason: {Truncate(c.Reason, 180)} · Moderator: <@{c.ModeratorId}>"));
        await reply("", new EmbedBuilder()
            .WithTitle($"{(name == "warnings" ? "Warnings" : "Moderation history")} for {member?.Username ?? userId.ToString()}")
            .WithDescription(description).WithColor(Color.Blue).Build());
    }

    // ---------------------------------------------------------------- helpers

    // Removes and returns the first whitespace-separated word from `rest`.
    private static string TakeToken(ref string rest)
    {
        rest = rest.TrimStart();
        var end = rest.IndexOfAny([' ', '\t', '\n']);
        string token;
        if (end < 0) { token = rest; rest = ""; }
        else { token = rest[..end]; rest = rest[end..].TrimStart(); }
        return token;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";
}
