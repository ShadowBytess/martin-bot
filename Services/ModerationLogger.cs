using Discord;
using Discord.WebSocket;

namespace ShinBreaker.Services;

public sealed class ModerationLogger(ModerationDatabase database)
{
    public async Task LogAsync(SocketGuild guild, long caseId, string action, ulong targetId,
        string targetName, SocketUser moderator, string reason, int? durationMinutes = null)
    {
        var channelId = await database.GetLogChannelAsync(guild.Id);
        if (channelId is null) return;
        if (guild.GetTextChannel(channelId.Value) is not { } channel) return;

        var embed = new EmbedBuilder()
            .WithTitle($"Moderation Case #{caseId:0000} · {action}")
            .WithColor(action switch
            {
                "ban" => Color.DarkRed,
                "kick" => Color.Orange,
                "warn" => Color.Gold,
                "timeout" => Color.Orange,
                "untimeout" or "unban" => Color.Green,
                _ => Color.Blue
            })
            .AddField("User", $"<@{targetId}>\n`{targetName}` (`{targetId}`)", true)
            .AddField("Moderator", $"{moderator.Mention}\n`{moderator.Id}`", true)
            .AddField("Reason", string.IsNullOrWhiteSpace(reason) ? "No reason provided" : reason)
            .WithTimestamp(DateTimeOffset.UtcNow)
            .WithFooter($"Guild: {guild.Name} · {guild.Id}");
        if (durationMinutes is not null)
            embed.AddField("Duration", $"{durationMinutes} minute(s)", true);
        await channel.SendMessageAsync(embed: embed.Build());
    }

    public async Task LogSystemAsync(SocketGuild guild, string title, string description)
    {
        var channelId = await database.GetLogChannelAsync(guild.Id);
        if (channelId is null || guild.GetTextChannel(channelId.Value) is not { } channel) return;
        await channel.SendMessageAsync(embed: new EmbedBuilder()
            .WithTitle(title).WithDescription(description).WithColor(Color.Blue)
            .WithTimestamp(DateTimeOffset.UtcNow).Build());
    }
}
