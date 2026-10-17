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
    GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.GuildMessageReactions,
    LogLevel = LogSeverity.Info
});
var logger = new ModerationLogger(database);

client.Log += message =>
{
    Console.WriteLine(message.ToString());
    return Task.CompletedTask;
};

client.Ready += async () =>
{
    var commands = ModerationCommands.BuildAll();
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
await Task.Delay(Timeout.Infinite);
