//using Discord;
//using Discord.WebSocket;
//
//namespace ShinBreaker.Services;
//
//public static class ReactionRoleCommands
//{
//    public static ApplicationCommandProperties[] BuildAll()
//    {
//        var commands = new List<ApplicationCommandProperties>
//        {
//            new SlashCommandBuilder().WithName("create").WithDescription("Create a new reaction role panel")
//                .WithDefaultMemberPermissions(GuildPermission.ManageRoles)
//                .AddOption("user", ApplicationCommandOptionType.User, "Member to unmute", isRequired: true)
//                .AddOption("reason", ApplicationCommandOptionType.String, "Reason", isRequired: false).Build(),
//            new SlashCommandBuilder().WithName("add").WithDescription("Unban a user by their Discord ID")
//                .WithDefaultMemberPermissions(GuildPermission.ManageRoles)
//                .AddOption("user_id", ApplicationCommandOptionType.String, "Discord user ID", isRequired: true)
//                .AddOption("reason", ApplicationCommandOptionType.String, "Reason for the unban", isRequired: false).Build(),
//            new SlashCommandBuilder().WithName("edit").WithDescription("View a member's warning history")
//                .WithDefaultMemberPermissions(GuildPermission.ManageRoles)
//                .AddOption("user", ApplicationCommandOptionType.User, "Member to look up", isRequired: true).Build(),
//            new SlashCommandBuilder().WithName("remove").WithDescription("View a member's recent moderation cases")
//                .WithDefaultMemberPermissions(GuildPermission.ManageRoles)
//                .AddOption("user", ApplicationCommandOptionType.User, "Member to look up", isRequired: true).Build(),
//            new SlashCommandBuilder().WithName("delete").WithDescription("Configure moderation logging")
//                .WithDefaultMemberPermissions(GuildPermission.ManageRoles)
//                .AddOption("log_channel", ApplicationCommandOptionType.Channel, "Channel for moderation logs", isRequired: true).Build(),
//            new SlashCommandBuilder().WithName("list").WithDescription("Delete up to 100 recent messages")
//                .WithDefaultMemberPermissions(GuildPermission.ManageRoles).Build()
//        };
//        return commands.ToArray();
//    }
//
//        await using var connection = CreateConnection();
//            await connection.OpenAsync();
//            var command = connection.CreateCommand();
//            command.CommandText = """
//                INSERT INTO ReactionRoles (GuildId, TargetId, ModeratorId, Action, Reason, CreatedAt, DurationMinutes)
//                VALUES ($guild, $target, $moderator, $action, $reason, $created, $duration);
//                SELECT last_insert_rowid();
//                """;
//            command.Parameters.AddWithValue("$guild", guildId.ToString());
//            command.Parameters.AddWithValue("$target", targetId.ToString());
//            command.Parameters.AddWithValue("$moderator", moderatorId.ToString());
//            command.Parameters.AddWithValue("$action", action);
//            command.Parameters.AddWithValue("$reason", reason);
//            command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
//            command.Parameters.AddWithValue("$duration", (object?)durationMinutes ?? DBNull.Value);
//            return (long)(await command.ExecuteScalarAsync() ?? 0L);
//}