using Discord;
using Discord.WebSocket;

using Microsoft.Data.Sqlite;

namespace ShinBreaker.Services;

public class RoleHandler
{

    private async Task HandleReactionAddedAsync(
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

    private async Task HandleReactionRemovedAsync(
        Cacheable<IUserMessage, ulong> cachedMessage,
        Cacheable<IMessageChannel, ulong> cachedChannel,
        SocketReaction reaction)
    {
        if ( true /* Case check here */ ) {
                /* Role operation here */
        }
    }
}