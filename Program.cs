using Discord;
using Discord.WebSocket;
using Microsoft.Data.Sqlite;
using ShinBreaker.Services;

var token = Environment.GetEnvironmentVariable("Martin_TOKEN");
if (string.IsNullOrWhiteSpace(token))
{
    Console.Error.WriteLine("Missing Martin_TOKEN environment variable. Set it to your Discord bot token before starting Martin.");
    return;
}

var databasePath = Environment.GetEnvironmentVariable("Martin_DB")
    ?? Path.Combine(AppContext.BaseDirectory, "data", "Martin.db");
var database = new ModerationDatabase(databasePath);
await database.InitializeAsync();

var client = new DiscordSocketClient(new DiscordSocketConfig
{
    // GuildMembers and MessageContent are privileged intents: enable both under
    // Bot > Privileged Gateway Intents in the Discord developer portal.
    GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.GuildMessageReactions
        | GatewayIntents.GuildMembers | GatewayIntents.MessageContent,
    AlwaysDownloadUsers = true,
    LogLevel = LogSeverity.Info
});
var logger = new ModerationLogger(database);

var loggingDatabase = new LoggingDatabase(databasePath);
await loggingDatabase.InitializeAsync();
var activityLogger = new ActivityLogger(client, database, loggingDatabase);
activityLogger.Attach();

// Characters that start a text command (".ban @user reason"). Keep it to characters other bots in your
// servers don't use, or both bots will react. Override with Martin_PREFIXES, e.g. ".,;".
var prefixes = Environment.GetEnvironmentVariable("Martin_PREFIXES") is { Length: > 0 } configured ? configured : ".,";
new PrefixCommands(client, database, logger, loggingDatabase, prefixes).Attach();

// How long message text is kept for edit/delete logs.
var retentionDays = int.TryParse(Environment.GetEnvironmentVariable("Martin_MESSAGE_RETENTION_DAYS"), out var days) && days > 0 ? days : 14;

client.Log += message =>
{
    Console.WriteLine(message.ToString());
    return Task.CompletedTask;
};

client.Ready += async () =>
{
    var commands = ModerationCommands.BuildAll().Concat(LoggingCommands.BuildAll()).ToArray();
    await client.BulkOverwriteGlobalApplicationCommandsAsync(commands);
    Console.WriteLine($"Martin is ready. Registered {commands.Length} global slash commands.");
};

client.SlashCommandExecuted += async command =>
{
    try
    {
        if (command.Data.Name == "say")
        {
            if (command.User is not SocketGuildUser sayUser || !sayUser.GuildPermissions.ManageMessages)
            {
                await command.RespondAsync("You need **Manage Messages** to use this command.", ephemeral: true);
                return;
            }
            if (command.User.Id != 1244709270533181516UL && command.User.Id != 1484017244139815122UL)
            {
                await command.RespondAsync("Fuck off.", ephemeral: true);
                return;
            }

            var message = (string)command.Data.Options.First(o => o.Name == "message").Value;
            await command.Channel.SendMessageAsync(message, allowedMentions: AllowedMentions.None);
            await command.RespondAsync("Sent.", ephemeral: true);
            return;
        }

        if (command.Data.Name == "test")
        {
            await command.RespondAsync($"Hello {command.User.Username}, the bot works!", ephemeral: true);
            return;
        }

        if (LoggingCommands.Handles(command.Data.Name))
        {
            await LoggingCommands.HandleAsync(command, loggingDatabase, database);
            return;
        }

        await ModerationCommands.HandleAsync(command, database, logger);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Command /{command.Data.Name} failed: {ex}");
        if (command.HasResponded)
            await command.FollowupAsync("Something went wrong while running that command. Check the bot logs.", ephemeral: true);
        else
            await command.RespondAsync("Something went wrong while running that command. Check the bot logs.", ephemeral: true);
    }
};

client.ReactionAdded += ReactionHandler.HandleReactionAdded;
client.ReactionRemoved += ReactionHandler.HandleReactionRemoved;

await client.LoginAsync(TokenType.Bot, token);
await client.StartAsync();
_ = activityLogger.RunRetentionAsync(TimeSpan.FromDays(retentionDays));
await Task.Delay(Timeout.Infinite);
