using FalseGods.RuntimeContracts.Diagnostics;

namespace FalseGods.Probe.Boss
{
    /// <summary>
    /// Bridges the production <see cref="ILogger"/> seam onto the probe's <see cref="ProbeReport"/>, so that the
    /// diagnostics the real <c>BossPresentation</c> emits (chosen shader, collision layer) still show up in the B0
    /// report when the probe drives the production renderer.
    /// </summary>
    internal sealed class ProbeLogger : ILogger
    {
        private readonly ProbeReport _report;

        public ProbeLogger(ProbeReport report) => _report = report;

        public void Log(string message) => _report.Line($"  {message}");

        public void LogWarning(string message) => _report.Line($"  !! {message}");
    }
}
