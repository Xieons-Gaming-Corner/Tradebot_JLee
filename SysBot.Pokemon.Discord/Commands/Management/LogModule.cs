using Discord;
using Discord.Commands;
using Discord.WebSocket;
using SysBot.Base;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SysBot.Pokemon.Discord;

public class LogModule : ModuleBase<SocketCommandContext>
{
    private static readonly Dictionary<ulong, ChannelLogger> Channels = [];
    private static readonly object ChannelLock = new();

    public static void RestoreLogging(DiscordSocketClient discord, DiscordSettings settings)
    {
        foreach (var ch in settings.LoggingChannels)
        {
            if (discord.GetChannel(ch.ID) is ISocketMessageChannel channel)
                AddLogChannel(channel, ch.ID);
        }

        LogUtil.LogInfo("Discord", "Added logging to Discord channel(s) on Bot startup.");
    }

    [Command("logHere")]
    [Summary("Makes the bot log to the channel.")]
    [RequireSudo]
    public async Task AddLogAsync()
    {
        var channel = Context.Channel;
        var channelId = channel.Id;

        lock (ChannelLock)
        {
            if (Channels.ContainsKey(channelId))
            {
                channel = null!;
            }
            else
            {
                AddLogChannel(channel, channelId);
            }
        }

        if (channel is null)
        {
            await ReplyAsync("Already logging here.").ConfigureAwait(false);
            return;
        }

        SysCordSettings.Settings.LoggingChannels.AddIfNew([GetReference(Context.Channel)]);

        await ReplyAsync("Added logging output to this channel!")
            .ConfigureAwait(false);
    }

    [Command("logClearAll")]
    [Summary("Clears all the logging settings.")]
    [RequireSudo]
    public async Task ClearLogsAllAsync()
    {
        ChannelLogger[] loggers;

        lock (ChannelLock)
        {
            loggers = Channels.Values.ToArray();
            Channels.Clear();
        }

        foreach (var logger in loggers)
        {
            LogUtil.Forwarders.Remove(logger);

            await ReplyAsync(
                $"Logging cleared from {logger.ChannelName} ({logger.ChannelID})!"
            ).ConfigureAwait(false);
        }

        SysCordSettings.Settings.LoggingChannels.Clear();

        await ReplyAsync("Logging cleared from all channels!")
            .ConfigureAwait(false);
    }

    [Command("logClear")]
    [Summary("Clears the logging settings in that specific channel.")]
    [RequireSudo]
    public async Task ClearLogsAsync()
    {
        var channelId = Context.Channel.Id;
        ChannelLogger? logger;

        lock (ChannelLock)
        {
            if (!Channels.TryGetValue(channelId, out logger))
            {
                logger = null;
            }
            else
            {
                Channels.Remove(channelId);
            }
        }

        if (logger is null)
        {
            await ReplyAsync("Not echoing in this channel.")
                .ConfigureAwait(false);
            return;
        }

        LogUtil.Forwarders.Remove(logger);

        SysCordSettings.Settings.LoggingChannels
            .RemoveAll(entry => entry.ID == channelId);

        await ReplyAsync(
            $"Logging cleared from channel: {Context.Channel.Name}"
        ).ConfigureAwait(false);
    }

    [Command("logInfo")]
    [Summary("Dumps the logging settings.")]
    [RequireSudo]
    public async Task DumpLogInfoAsync()
    {
        KeyValuePair<ulong, ChannelLogger>[] channels;

        lock (ChannelLock)
            channels = Channels.ToArray();

        if (channels.Length == 0)
        {
            await ReplyAsync("No Discord logging channels are configured.")
                .ConfigureAwait(false);
            return;
        }

        foreach (var channel in channels)
        {
            await ReplyAsync($"{channel.Key} - {channel.Value}")
                .ConfigureAwait(false);
        }
    }

    private static void AddLogChannel(ISocketMessageChannel channel, ulong channelId)
    {
        var logger = new ChannelLogger(channelId, channel);

        LogUtil.Forwarders.Add(logger);
        Channels.Add(channelId, logger);
    }

    private RemoteControlAccess GetReference(IChannel channel) => new()
    {
        ID = channel.Id,
        Name = channel.Name,
        Comment = $"Added by {Context.User.Username} on {DateTime.Now:yyyy.MM.dd-HH:mm:ss}",
    };
}
