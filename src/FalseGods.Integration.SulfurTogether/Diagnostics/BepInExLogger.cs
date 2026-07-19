using System;
using BepInEx.Logging;
using ILogger = FalseGods.RuntimeContracts.Diagnostics.ILogger;

namespace FalseGods.Integration.SulfurTogether.Diagnostics
{
    /// <summary>The adapter's <see cref="ILogger"/> over its own BepInEx log source.</summary>
    internal sealed class BepInExLogger : ILogger
    {
        private readonly ManualLogSource _log;

        public BepInExLogger(ManualLogSource log) => _log = log ?? throw new ArgumentNullException(nameof(log));

        public void Log(string message) => _log.LogMessage(message);

        public void LogWarning(string message) => _log.LogWarning(message);
    }
}
