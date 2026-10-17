using Discord;
using Discord.WebSocket;

using Microsoft.Data.Sqlite;

namespace ShinBreaker.Services;

public static class ReactionHandler
{

    public static async Task HandleReactionAdded(
        Cacheable<IUserMessage, ulong> cachedMessage,
        Cacheable<IMessageChannel, ulong> cachedChannel,
        SocketReaction reaction)
    {
        if (reaction.User.IsSpecified && reaction.User.Value.IsBot) return;

        var channel = await cachedChannel.GetOrDownloadAsync();
        var message = await cachedMessage.GetOrDownloadAsync();

        if ( true /* Case check here */ ) {
            /* Role operation here */
        }
    }

    public static async Task HandleReactionRemoved(
        Cacheable<IUserMessage, ulong> cachedMessage,
        Cacheable<IMessageChannel, ulong> cachedChannel,
        SocketReaction reaction)
    {
        if ( true /* Case check here */ ) {
                /* Role operation here */
        }
    }
}