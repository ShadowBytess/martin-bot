using System.Text.RegularExpressions;
using Discord;
using Discord.WebSocket;

namespace ShinBreaker.Services;

/// <summary>
/// C# port of the JavaScript getUser(input, guild) helper.
/// Resolves a member from a mention, a raw ID, a username / nickname / display name, or a Discord API search.
/// </summary>
public static partial class UserResolver
{
    [GeneratedRegex(@"^<@!?(\d+)>$")]
    private static partial Regex MentionPattern();

    public static async Task<IGuildUser?> GetUserAsync(string? input, SocketGuild? guild)
    {
        // checks
        if (guild is null || string.IsNullOrWhiteSpace(input)) return null;
        input = input.Trim();

        // mention
        var mention = MentionPattern().Match(input);
        if (mention.Success) return await FetchByIdAsync(guild, mention.Groups[1].Value);

        // id
        if (input.All(char.IsAsciiDigit)) return await FetchByIdAsync(guild, input);

        // search cache
        var cached = guild.Users.FirstOrDefault(m =>
            Matches(m.Username, input) ||
            Matches(m.Nickname, input) ||
            Matches(m.GlobalName, input));
        if (cached is not null) return cached;

        // discord api
        try
        {
            var found = await ((IGuild)guild).SearchUsersAsync(input, 1);
            return found.FirstOrDefault();
        }
        catch (Discord.Net.HttpException)
        {
            return null;
        }
    }

    private static bool Matches(string? candidate, string input) =>
        candidate is not null && candidate.Equals(input, StringComparison.OrdinalIgnoreCase);

    // Cache first, then the API. A failed lookup is "not found", never an exception.
    private static async Task<IGuildUser?> FetchByIdAsync(SocketGuild guild, string rawId)
    {
        if (!ulong.TryParse(rawId, out var id)) return null;
        if (guild.GetUser(id) is { } cached) return cached;
        try
        {
            return await ((IGuild)guild).GetUserAsync(id);
        }
        catch (Discord.Net.HttpException)
        {
            return null;
        }
    }
}
