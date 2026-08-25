using Discord;
using Discord.Commands;
using Discord.WebSocket;

namespace SysBot.Pokemon.Discord;

public static class DiscordLogUtil
{
    public static string GetChannelLocation(IChannel? channel) => channel switch
    {
        SocketGuildChannel gc => $"Server: {gc.Guild.Name} ({gc.Guild.Id}), Channel: #{gc.Name} ({gc.Id})",
        IGuildChannel igc => $"Server ID: {igc.GuildId}, Channel: #{igc.Name} ({igc.Id})",
        IDMChannel dm => $"Channel: DM ({dm.Id})",
        IChannel c => $"Channel: {c.Id}",
        null => "Channel: Unknown",
    };

    public static string GetChannelLocation(SocketCommandContext context) => GetChannelLocation(context.Channel);
}
