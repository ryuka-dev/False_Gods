using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Logging;

namespace FalseGods.Probe
{
    /// <summary>
    /// Collects probe output, echoes it to the BepInEx log, and writes one file per run.
    ///
    /// The file matters more than the log: these values get transcribed into
    /// CollisionAndNavigationProposal.md §4.4 and RiskList R1/R3/R5, and a log that has scrolled past is
    /// not evidence.
    /// </summary>
    internal sealed class ProbeReport
    {
        private readonly ManualLogSource _log;
        private readonly StringBuilder _text = new StringBuilder();

        public ProbeReport(ManualLogSource log) => _log = log;

        public void Line(string text = "")
        {
            _text.AppendLine(text);
            _log.LogInfo(text);
        }

        public void Section(string title)
        {
            Line();
            Line("── " + title + " " + new string('─', Math.Max(0, 70 - title.Length)));
        }

        public void Value(string name, object value) => Line($"  {name,-34} = {Describe(value)}");

        /// <summary>
        /// Records a failure without letting the probe die. A probe that throws teaches nothing; a probe
        /// that reports "this member does not exist" teaches exactly what we came to find out.
        /// </summary>
        public void Failure(string what, Exception exception)
        {
            Line($"  !! {what}: {exception.GetType().Name}: {exception.Message}");
        }

        public void Try(string what, Action action)
        {
            try { action(); }
            catch (Exception exception) { Failure(what, exception); }
        }

        private static string Describe(object value) => value switch
        {
            null => "<null>",
            bool b => b ? "true" : "false",
            float f => f.ToString("0.####"),
            IEnumerable<string> strings => string.Join(", ", strings),
            _ => value.ToString(),
        };

        public string WriteToDisk()
        {
            var directory = Path.Combine(Paths.BepInExRootPath, "FalseGods.Probe");
            Directory.CreateDirectory(directory);

            var path = Path.Combine(directory, $"probe-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(path, _text.ToString());

            _log.LogMessage($"Probe report written to {path}");
            return path;
        }
    }
}
