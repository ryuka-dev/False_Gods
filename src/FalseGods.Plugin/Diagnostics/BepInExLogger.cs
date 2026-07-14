using BepInEx.Logging;
using ILogger = FalseGods.RuntimeContracts.Diagnostics.ILogger;

namespace FalseGods.Plugin.Diagnostics
{
    /// <summary>
    /// The one concrete <see cref="ILogger"/> implementation, adapting the project-owned diagnostics seam onto
    /// BepInEx's <see cref="ManualLogSource"/>.
    /// </summary>
    /// <remarks>
    /// It lives in the Composition Root because that is the only module that both owns the BepInEx dependency and
    /// wires the inner/presentation code — everything else receives an <see cref="ILogger"/> by injection and never
    /// sees BepInEx. Keeping the single BepInEx logging touch-point here is what lets the boss domain, presentation,
    /// and adapters stay free of any concrete logger (global engineering rules §7).
    /// </remarks>
    internal sealed class BepInExLogger : ILogger
    {
        private readonly ManualLogSource _log;

        public BepInExLogger(ManualLogSource log) => _log = log;

        public void Log(string message) => _log.LogInfo(message);

        public void LogWarning(string message) => _log.LogWarning(message);
    }
}
