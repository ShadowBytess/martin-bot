using Discord;
using Discord.WebSocket;

namespace ShinBreaker.Services;

public static class ModerationCommands
{
    public static ApplicationCommandProperties[] BuildAll()
    {
        var commands = new List<ApplicationCommandProperties>
        {
            new SlashCommandBuilder().WithName("test").WithDescription("Check whether the bot is responding").Build(),
            new SlashCommandBuilder().WithName("say").WithDescription("Make the bot say something")
                .WithDefaultMemberPermissions(GuildPermission.ManageMessages)
                .AddOption("message", ApplicationCommandOptionType.String, "What to say", isRequired: true).Build(),
            new SlashCommandBuilder().WithName("ban").WithDescription("Ban a member")
                .WithDefaultMemberPermissions(GuildPermission.BanMembers)
                .AddOption("user", ApplicationCommandOptionType.User, "Member to ban", isRequired: true)
                .AddOption("reason", ApplicationCommandOptionType.String, "Reason for the ban", isRequired: false).Build(),
            new SlashCommandBuilder().WithName("kick").WithDescription("Kick a member")
                .WithDefaultMemberPermissions(GuildPermission.KickMembers)
                .AddOption("user", ApplicationCommandOptionType.User, "Member to kick", isRequired: true)
                .AddOption("reason", ApplicationCommandOptionType.String, "Reason for the kick", isRequired: false).Build(),
            new SlashCommandBuilder().WithName("warn").WithDescription("Issue a warning to a member")
                .WithDefaultMemberPermissions(GuildPermission.ModerateMembers)
                .AddOption("user", ApplicationCommandOptionType.User, "Member to warn", isRequired: true)
                .AddOption("reason", ApplicationCommandOptionType.String, "Reason for the warning", isRequired: true).Build(),
            new SlashCommandBuilder().WithName("mute").WithDescription("Mute a member")
                .WithDefaultMemberPermissions(GuildPermission.ModerateMembers)
                .AddOption("user", ApplicationCommandOptionType.User, "Member to mute", isRequired: true)
                .AddOption("minutes", ApplicationCommandOptionType.Integer, "Mute duration in minutes (1-40320)", isRequired: true)
                .AddOption("reason", ApplicationCommandOptionType.String, "Reason for the mute", isRequired: false).Build(),
            new SlashCommandBuilder().WithName("unmute").WithDescription("Unmute a member")
                .WithDefaultMemberPermissions(GuildPermission.ModerateMembers)
                .AddOption("user", ApplicationCommandOptionType.User, "Member to unmute", isRequired: true)
                .AddOption("reason", ApplicationCommandOptionType.String, "Reason", isRequired: false).Build(),
            new SlashCommandBuilder().WithName("unban").WithDescription("Unban a user by their Discord ID")
                .WithDefaultMemberPermissions(GuildPermission.BanMembers)
                .AddOption("user_id", ApplicationCommandOptionType.String, "Discord user ID", isRequired: true)
                .AddOption("reason", ApplicationCommandOptionType.String, "Reason for the unban", isRequired: false).Build(),
            new SlashCommandBuilder().WithName("warnings").WithDescription("View a member's warning history")
                .WithDefaultMemberPermissions(GuildPermission.ModerateMembers)
                .AddOption("user", ApplicationCommandOptionType.User, "Member to look up", isRequired: true).Build(),
            new SlashCommandBuilder().WithName("modlogs").WithDescription("View a member's recent moderation cases")
                .WithDefaultMemberPermissions(GuildPermission.ModerateMembers)
                .AddOption("user", ApplicationCommandOptionType.User, "Member to look up", isRequired: true).Build(),
            new SlashCommandBuilder().WithName("modconfig").WithDescription("Configure moderation logging")
                .WithDefaultMemberPermissions(GuildPermission.ManageGuild)
                .AddOption("log_channel", ApplicationCommandOptionType.Channel, "Channel for moderation logs", isRequired: true).Build(),
            new SlashCommandBuilder().WithName("purge").WithDescription("Delete up to 100 recent messages")
                .WithDefaultMemberPermissions(GuildPermission.ManageMessages)
                .AddOption("amount", ApplicationCommandOptionType.Integer, "Number of messages to delete (1-100)", isRequired: true).Build()
        };
        return commands.ToArray();
    }

    public static async Task HandleAsync(SocketSlashCommand command, ModerationDatabase database, ModerationLogger logger)
    {
        if (command.GuildId is null || (command.Channel as SocketGuildChannel)?.Guild is not SocketGuild guild || command.User is not SocketGuildUser moderator)
        {
            await command.RespondAsync("Moderation commands can only be used inside a server.", ephemeral: true);
            return;
        }

        // Discord requires a reply within 3 seconds. Acknowledge right away, then send the real
        // answer with FollowupAsync once the database / API calls are done.
        await command.DeferAsync(ephemeral: true);

        var name = command.Data.Name;
        if (name == "modconfig")
        {
            if (!moderator.GuildPermissions.ManageGuild)
            {
                await command.FollowupAsync("You need **Manage Server** to configure moderation logs.", ephemeral: true);
                return;
            }
            var channel = command.Data.Options.First().Value as IChannel;
            if (channel is not SocketGuildChannel guildChannel || guildChannel.Guild.Id != guild.Id || guildChannel is not SocketTextChannel)
            {
                await command.FollowupAsync("Choose a text channel in this server.", ephemeral: true);
                return;
            }
            await database.SetLogChannelAsync(guild.Id, guildChannel.Id);
            await command.FollowupAsync($"Moderation logs will be sent to <#{guildChannel.Id}>.", ephemeral: true);
            await logger.LogSystemAsync(guild, "Moderation logging configured", $"Configured by {moderator.Mention}. Log channel: <#{guildChannel.Id}>.");
            return;
        }

        if (name == "purge")
        {
            if (!moderator.GuildPermissions.ManageMessages)
            {
                await command.FollowupAsync("You need **Manage Messages** to purge messages.", ephemeral: true);
                return;
            }
            if (command.Channel is not SocketTextChannel textChannel)
            {
                await command.FollowupAsync("This command only works in a server text channel.", ephemeral: true);
                return;
            }
            var amount = Convert.ToInt32(command.Data.Options.First().Value);
            if (amount is < 1 or > 100)
            {
                await command.FollowupAsync("Amount must be between 1 and 100.", ephemeral: true);
                return;
            }
            var messages = (await textChannel.GetMessagesAsync(amount).FlattenAsync())
                .Where(m => (DateTimeOffset.UtcNow - m.Timestamp).TotalDays < 14 && m.Id != command.Id)
                .ToArray();
            if (messages.Length > 0) await textChannel.DeleteMessagesAsync(messages);
            var caseId = await database.AddCaseAsync(guild.Id, moderator.Id, moderator.Id, "purge",
                $"Deleted {messages.Length} message(s) in #{textChannel.Name}.");
            await command.FollowupAsync($"Deleted {messages.Length} message(s). Case `#{caseId:0000}`.", ephemeral: true);
            await logger.LogSystemAsync(guild, $"Moderation Case #{caseId:0000} · purge",
                $"**Moderator:** {moderator.Mention}\n**Channel:** {textChannel.Mention}\n**Messages deleted:** {messages.Length}");
            return;
        }

        var userOption = command.Data.Options.FirstOrDefault(o => o.Name == "user")?.Value as SocketUser;
        var reason = command.Data.Options.FirstOrDefault(o => o.Name == "reason")?.Value as string ?? "No reason provided";

        if (name == "unban")
        {
            if (!moderator.GuildPermissions.BanMembers)
            {
                await command.FollowupAsync("You need **Ban Members** to unban users.", ephemeral: true);
                return;
            }
            var rawId = (string)command.Data.Options.First(o => o.Name == "user_id").Value;
            if (!ulong.TryParse(rawId, out var userId))
            {
                await command.FollowupAsync("That doesn't look like a valid Discord user ID.", ephemeral: true);
                return;
            }
            await guild.RemoveBanAsync(userId, new RequestOptions { AuditLogReason = reason });
            var caseId = await database.AddCaseAsync(guild.Id, userId, moderator.Id, "unban", reason);
            await command.FollowupAsync($"Unbanned user `{userId}`. Case `#{caseId:0000}`.", ephemeral: true);
            await logger.LogAsync(guild, caseId, "unban", userId, userId.ToString(), moderator, reason);
            return;
        }

        if (name is "warnings" or "modlogs")
        {
            if (!moderator.GuildPermissions.ModerateMembers && !moderator.GuildPermissions.ManageGuild)
            {
                await command.FollowupAsync("You need **Moderate Members** to view moderation records.", ephemeral: true);
                return;
            }
            if (userOption is null)
            {
                await command.FollowupAsync("I couldn't resolve that user.", ephemeral: true);
                return;
            }
            var cases = await database.GetCasesAsync(guild.Id, userOption.Id, 25);
            if (name == "warnings") cases = cases.Where(c => c.Action == "warn").ToArray();
            if (cases.Count == 0)
            {
                await command.FollowupAsync($"No {(name == "warnings" ? "warnings" : "moderation cases")} found for {userOption.Mention}.", ephemeral: true);
                return;
            }
            var description = string.Join("\n", cases.Select(c => $"`#{c.Id:0000}` **{c.Action}** — {c.CreatedAt:yyyy-MM-dd HH:mm} UTC\nReason: {Truncate(c.Reason, 180)} · Moderator: <@{c.ModeratorId}>"));
            var embed = new EmbedBuilder().WithTitle($"{(name == "warnings" ? "Warnings" : "Moderation history")} for {userOption.Username}")
                .WithDescription(description).WithColor(Color.Blue).Build();
            await command.FollowupAsync(embed: embed, ephemeral: true);
            return;
        }

        if (userOption is null)
        {
            await command.FollowupAsync("I couldn't resolve that user.", ephemeral: true);
            return;
        }
        if (userOption.Id == moderator.Id)
        {
            await command.FollowupAsync("You can't moderate yourself.", ephemeral: true);
            return;
        }
        if (userOption.Id == guild.OwnerId)
        {
            await command.FollowupAsync("You can't moderate the server owner.", ephemeral: true);
            return;
        }

        SocketGuildUser? targetMember = userOption as SocketGuildUser ?? guild.GetUser(userOption.Id);
        if (targetMember is null)
        {
            await command.FollowupAsync("That user isn't currently a member of this server.", ephemeral: true);
            return;
        }
        if (targetMember.Id == guild.CurrentUser.Id || targetMember.Hierarchy >= moderator.Hierarchy || targetMember.Hierarchy >= guild.CurrentUser.Hierarchy)
        {
            await command.FollowupAsync("Your role and the bot's role must both be higher than the target's role, and you can't moderate the bot itself.", ephemeral: true);
            return;
        }

        int? durationMinutes = null;
        if (name == "ban")
        {
            if (!moderator.GuildPermissions.BanMembers) { await DenyAsync(command, "Ban Members"); return; }
            await guild.AddBanAsync(targetMember, 0, reason);
        }
        else if (name == "kick")
        {
            if (!moderator.GuildPermissions.KickMembers) { await DenyAsync(command, "Kick Members"); return; }
            await targetMember.KickAsync(reason);
        }
        else if (name == "warn")
        {
            if (!moderator.GuildPermissions.ModerateMembers) { await DenyAsync(command, "Moderate Members"); return; }
        }
        else if (name == "mute")
        {
            if (!moderator.GuildPermissions.ModerateMembers) { await DenyAsync(command, "Moderate Members"); return; }
            durationMinutes = Convert.ToInt32(command.Data.Options.First(o => o.Name == "minutes").Value);
            if (durationMinutes is < 1 or > 40320)
            {
                await command.FollowupAsync("Mute duration must be between 1 minute and 28 days (40,320 minutes).", ephemeral: true);
                return;
            }
            await targetMember.SetTimeOutAsync(TimeSpan.FromMinutes(durationMinutes.Value), new RequestOptions { AuditLogReason = reason });
        }
        else if (name == "unmute")
        {
            if (!moderator.GuildPermissions.ModerateMembers) { await DenyAsync(command, "Moderate Members"); return; }
            await targetMember.RemoveTimeOutAsync(new RequestOptions { AuditLogReason = reason });
        }
        else
        {
            await command.FollowupAsync("Unknown moderation command.", ephemeral: true);
            return;
        }

        var caseAction = name;
        var caseNumber = await database.AddCaseAsync(guild.Id, targetMember.Id, moderator.Id, caseAction, reason, durationMinutes);
        await command.FollowupAsync($"Done: **{caseAction}** {targetMember.Mention}. Case `#{caseNumber:0000}`.", ephemeral: true);
        await logger.LogAsync(guild, caseNumber, caseAction, targetMember.Id, targetMember.Username, moderator, reason, durationMinutes);

        if (name == "warn")
        {
            try
            {
                await targetMember.SendMessageAsync(embed: new EmbedBuilder().WithTitle($"Warning in {guild.Name}")
                    .WithDescription($"You have received a warning.\n**Reason:** {reason}\n**Case:** #{caseNumber:0000}")
                    .WithColor(Color.Gold).Build());
            }
            catch (Discord.Net.HttpException) { /* DMs may be disabled; warning remains recorded. */ }
        }
    }

    private static Task DenyAsync(SocketSlashCommand command, string permission) =>
        command.FollowupAsync($"You need **{permission}** to use this command.", ephemeral: true);

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";

}