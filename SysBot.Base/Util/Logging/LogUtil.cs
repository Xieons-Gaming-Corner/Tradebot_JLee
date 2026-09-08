using NLog;
using NLog.Config;
using NLog.Targets;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SysBot.Base;

/// <summary>
/// Logic wrapper to handle logging (via NLog).
/// Supports both master log (all bots) and per-bot log files for better organization.
/// </summary>
public static class LogUtil
{
    // Hook in here if you want to forward the message elsewhere.
    // Use AddForwarder / RemoveForwarder instead of editing this list directly.
    public static readonly List<ILogForwarder> Forwarders = [];

    private static readonly object ForwardersLock = new();
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    // Cache of per-bot loggers to avoid recreating them
    private static readonly ConcurrentDictionary<string, Logger> BotLoggers = new();

    // Buffer for early bot logs before trainer identification.
    // Key: Connection IP/USB identifier, Value: List of buffered log entries.
    private static readonly ConcurrentDictionary<string, List<BufferedLogEntry>> LogBuffer = new();

    private static readonly string WorkingDirectory = Path.GetDirectoryName(Environment.ProcessPath)!;

    private record BufferedLogEntry(LogLevel Level, string Message, DateTime Timestamp);

    static LogUtil()
    {
        if (!LogConfig.LoggingEnabled)
            return;

        var config = new LoggingConfiguration();
        Directory.CreateDirectory("logs");

        if (LogConfig.EnableMasterLog)
        {
            var masterLogFile = new FileTarget("masterlog")
            {
                FileName = Path.Combine(WorkingDirectory, "logs", "SysBotLog.txt"),
                ConcurrentWrites = true,
                ArchiveEvery = FileArchivePeriod.Day,
                ArchiveNumbering = ArchiveNumberingMode.Date,
                ArchiveFileName = Path.Combine(WorkingDirectory, "logs", "SysBotLog.{#}.txt"),
                ArchiveDateFormat = "yyyy-MM-dd",
                ArchiveAboveSize = LogConfig.MaxLogFileSize,
                MaxArchiveFiles = LogConfig.MaxArchiveFiles,
                Encoding = Encoding.Unicode,
                WriteBom = true,
            };

            config.AddRule(LogLevel.Debug, LogLevel.Fatal, masterLogFile);
        }

        LogManager.Configuration = config;
    }

    public static DateTime LastLogged { get; private set; } = DateTime.Now;

    /// <summary>
    /// Tracks the last time each trainer-identified bot logged a message.
    /// </summary>
    public static readonly ConcurrentDictionary<string, DateTime> BotLastActivity = new();

    /// <summary>
    /// Maps connection identifiers such as IP/USB names to trainer identifiers.
    /// </summary>
    public static readonly ConcurrentDictionary<string, string> ConnectionToTrainerMap = new();

    /// <summary>
    /// Safely adds a log forwarder, such as a Discord logging channel.
    /// </summary>
    public static void AddForwarder(ILogForwarder forwarder)
    {
        lock (ForwardersLock)
        {
            if (!Forwarders.Contains(forwarder))
                Forwarders.Add(forwarder);
        }
    }

    /// <summary>
    /// Safely removes a log forwarder.
    /// </summary>
    public static bool RemoveForwarder(ILogForwarder forwarder)
    {
        lock (ForwardersLock)
            return Forwarders.Remove(forwarder);
    }

    /// <summary>
    /// Safely removes a collection of log forwarders.
    /// </summary>
    public static void RemoveForwarders(IEnumerable<ILogForwarder> forwarders)
    {
        var toRemove = forwarders.ToHashSet();

        lock (ForwardersLock)
            Forwarders.RemoveAll(toRemove.Contains);
    }

    /// <summary>
    /// Returns a fixed copy of active log forwarders.
    /// This prevents collection-modified errors when channels are added or removed while logging.
    /// </summary>
    public static ILogForwarder[] GetForwarderSnapshot()
    {
        lock (ForwardersLock)
            return Forwarders.ToArray();
    }

    /// <summary>
    /// Gets or creates a per-bot logger for the specified bot identity.
    /// </summary>
    private static Logger GetOrCreateBotLogger(string identity)
    {
        if (!LogConfig.EnablePerBotLogging || !LogConfig.LoggingEnabled)
            return Logger;

        return BotLoggers.GetOrAdd(identity, botName =>
        {
            var safeBotName = SanitizeBotName(botName);
            var botLogDir = Path.Combine(WorkingDirectory, "logs", safeBotName);
            Directory.CreateDirectory(botLogDir);

            var loggerName = $"BotLogger_{safeBotName}";
            var botLogger = LogManager.GetLogger(loggerName);
            var config = LogManager.Configuration ?? new LoggingConfiguration();

            var fileName = LogConfig.IncludeTimestampInFilename
                ? $"SysBotLog_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt"
                : "SysBotLog.txt";

            var botLogTarget = new FileTarget($"botlog_{safeBotName}")
            {
                FileName = Path.Combine(botLogDir, fileName),
                ConcurrentWrites = true,
                ArchiveEvery = FileArchivePeriod.Day,
                ArchiveNumbering = ArchiveNumberingMode.Date,
                ArchiveFileName = Path.Combine(botLogDir, "SysBotLog.{#}.txt"),
                ArchiveDateFormat = "yyyy-MM-dd",
                ArchiveAboveSize = LogConfig.MaxLogFileSize,
                MaxArchiveFiles = LogConfig.MaxArchiveFiles,
                Encoding = Encoding.Unicode,
                WriteBom = true,
                Layout = "${longdate}|${level:uppercase=true}|${logger}|${message}${onexception:inner=${newline}${exception:format=tostring}}"
            };

            config.AddTarget(botLogTarget);
            config.AddRule(LogLevel.Debug, LogLevel.Fatal, botLogTarget, loggerName);

            LogManager.Configuration = config;

            return botLogger;
        });
    }

    /// <summary>
    /// Sanitizes a bot name for use in folder/file paths.
    /// </summary>
    private static string SanitizeBotName(string botName)
    {
        if (string.IsNullOrWhiteSpace(botName))
            return "UnknownBot";

        if (LogConfig.ConsolidateSystemLogs)
        {
            foreach (var systemIdentity in LogConfig.SystemIdentities)
            {
                if (botName.Equals(systemIdentity, StringComparison.OrdinalIgnoreCase) ||
                    botName.StartsWith(systemIdentity + " ", StringComparison.OrdinalIgnoreCase) ||
                    botName.StartsWith(systemIdentity + ":", StringComparison.OrdinalIgnoreCase))
                {
                    return "System";
                }
            }
        }

        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = string.Join("_", botName.Split(invalid, StringSplitOptions.RemoveEmptyEntries));
        sanitized = sanitized.Trim('_', ' ');

        return string.IsNullOrWhiteSpace(sanitized) ? "UnknownBot" : sanitized;
    }

    /// <summary>
    /// Checks whether an identity uses the trainer format Name-XXXXXX.
    /// </summary>
    private static bool IsTrainerIdentifier(string identity)
    {
        return identity.Contains('-') &&
               System.Text.RegularExpressions.Regex.IsMatch(identity, @"-\d{6}$");
    }

    /// <summary>
    /// Checks whether the identity belongs to a global/system service.
    /// </summary>
    private static bool IsGlobalIdentity(string identity)
    {
        return LogConfig.SystemIdentities.Any(prefix =>
            identity.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
            identity.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Flushes buffered logs from an early identifier, such as an IP or USB name,
    /// to the final trainer-specific logging folder.
    /// </summary>
    public static void FlushBufferedLogs(string earlyIdentifier, string trainerIdentifier)
    {
        ConnectionToTrainerMap[earlyIdentifier] = trainerIdentifier;

        if (!LogBuffer.TryRemove(earlyIdentifier, out var bufferedLogs))
            return;

        var botLogger = GetOrCreateBotLogger(trainerIdentifier);

        lock (bufferedLogs)
        {
            foreach (var entry in bufferedLogs)
                botLogger.Log(entry.Level, entry.Message);
        }
    }

    public static void LogError(string message, string identity)
    {
        if (IsTrainerIdentifier(identity))
            BotLastActivity[identity] = DateTime.Now;

        if (LogConfig.EnableMasterLog)
            Logger.Log(LogLevel.Error, $"{identity} {message}");

        if (LogConfig.EnablePerBotLogging && !IsGlobalIdentity(identity))
        {
            if (IsTrainerIdentifier(identity))
            {
                var botLogger = GetOrCreateBotLogger(identity);
                botLogger.Log(LogLevel.Error, message);
            }
            else
            {
                var bufferedLogs = LogBuffer.GetOrAdd(identity, _ => []);

                lock (bufferedLogs)
                    bufferedLogs.Add(new BufferedLogEntry(LogLevel.Error, message, DateTime.Now));
            }
        }

        ForwardLog(message, identity);
    }

    public static void LogInfo(string message, string identity)
    {
        if (IsTrainerIdentifier(identity))
            BotLastActivity[identity] = DateTime.Now;

        if (LogConfig.EnableMasterLog)
            Logger.Log(LogLevel.Info, $"{identity} {message}");

        if (LogConfig.EnablePerBotLogging && !IsGlobalIdentity(identity))
        {
            if (IsTrainerIdentifier(identity))
            {
                var botLogger = GetOrCreateBotLogger(identity);
                botLogger.Log(LogLevel.Info, message);
            }
            else
            {
                var bufferedLogs = LogBuffer.GetOrAdd(identity, _ => []);

                lock (bufferedLogs)
                    bufferedLogs.Add(new BufferedLogEntry(LogLevel.Info, message, DateTime.Now));
            }
        }

        ForwardLog(message, identity);
    }

    public static void LogSuspicious(string message, string identity)
    {
        if (LogConfig.EnableMasterLog)
            Logger.Log(LogLevel.Warn, $"[SECURITY] {identity} {message}");

        if (LogConfig.EnablePerBotLogging)
        {
            var botLogger = GetOrCreateBotLogger(identity);
            botLogger.Log(LogLevel.Warn, $"[SECURITY] {message}");
        }

        ForwardLog($"[SECURITY] {message}", identity);
    }

    public static void LogSafe(Exception exception, string identity)
    {
        if (LogConfig.EnableMasterLog)
        {
            Logger.Log(LogLevel.Error, $"Exception from {identity}:");
            Logger.Log(LogLevel.Error, exception);
        }

        if (LogConfig.EnablePerBotLogging)
        {
            var botLogger = GetOrCreateBotLogger(identity);
            botLogger.Log(LogLevel.Error, "Exception occurred:");
            botLogger.Log(LogLevel.Error, exception);
        }

        var error = exception.InnerException;

        while (error is not null)
        {
            if (LogConfig.EnableMasterLog)
                Logger.Log(LogLevel.Error, error);

            if (LogConfig.EnablePerBotLogging)
            {
                var botLogger = GetOrCreateBotLogger(identity);
                botLogger.Log(LogLevel.Error, error);
            }

            error = error.InnerException;
        }
    }

    public static void LogText(string message)
    {
        Logger.Log(LogLevel.Info, message);
    }

    /// <summary>
    /// Clears the cached logger for a disconnected bot.
    /// </summary>
    public static void ClearBotLogger(string identity)
    {
        BotLoggers.TryRemove(identity, out _);
    }

    /// <summary>
    /// Gets the expected log-file path for a bot identity.
    /// </summary>
    public static string GetBotLogPath(string identity)
    {
        var safeBotName = SanitizeBotName(identity);
        var botLogDir = Path.Combine(WorkingDirectory, "logs", safeBotName);

        var fileName = LogConfig.IncludeTimestampInFilename
            ? $"SysBotLog_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.txt"
            : "SysBotLog.txt";

        return Path.Combine(botLogDir, fileName);
    }

    /// <summary>
    /// Legacy log forwarding method retained for callers inside SysBot.Base.
    /// </summary>
    private static void Log(string message, string identity)
    {
        ForwardLog(message, identity);
    }

    /// <summary>
    /// Sends the message to a stable snapshot of active forwarding targets.
    /// </summary>
    private static void ForwardLog(string message, string identity)
    {
        var forwarders = GetForwarderSnapshot();

        foreach (var forwarder in forwarders)
        {
            try
            {
                forwarder.Forward(message, identity);
            }
            catch (Exception ex)
            {
                Logger.Log(
                    LogLevel.Error,
                    $"Failed to forward log from {identity} - {message}"
                );

                Logger.Log(LogLevel.Error, ex);
            }
        }

        LastLogged = DateTime.Now;
    }
}
