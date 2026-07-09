using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using FalseGods.ArchitectureTests.Inspection;
using Xunit;

namespace FalseGods.ArchitectureTests.SelfTests;

/// <summary>
/// Proves the process runner does not deadlock, honours a real timeout, and kills the whole tree.
///
/// Windows-only, like the rest of this repository (net472, BepInEx, verify.ps1). The child is
/// powershell.exe purely because it is guaranteed present and can be told to misbehave on demand.
/// </summary>
public sealed class ProcessRunnerSelfTests
{
    private static readonly TimeSpan Generous = TimeSpan.FromSeconds(60);

    private static ProcessResult RunPowerShell(string script, TimeSpan timeout) =>
        ProcessRunner.Run(
            "powershell.exe",
            new[] { "-NoProfile", "-NonInteractive", "-Command", script },
            RepoLayout.Root,
            timeout);

    [Fact]
    public void Captures_a_normal_exit()
    {
        var result = RunPowerShell("Write-Output 'hello'; exit 0", Generous);

        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Succeeded);
        Assert.Contains("hello", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void Captures_a_non_zero_exit_and_stderr()
    {
        var result = RunPowerShell("[Console]::Error.WriteLine('boom'); exit 3", Generous);

        Assert.False(result.TimedOut);
        Assert.Equal(3, result.ExitCode);
        Assert.False(result.Succeeded);
        Assert.Contains("boom", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public void Does_not_deadlock_when_both_streams_produce_large_output()
    {
        // The regression this exists for. A pipe buffer is a few KB; ~1 MB on each stream is far past it.
        // Reading stdout to the end before touching stderr would block the child forever, and the timeout
        // that "protects" the call would sit on the line after the blocking read, never reached.
        const int lines = 5_000;
        const int lineLength = 200;

        var script =
            $"$line = 'x' * {lineLength}; " +
            $"1..{lines} | ForEach-Object {{ [Console]::Out.WriteLine($line); [Console]::Error.WriteLine($line) }}; " +
            "exit 0";

        var result = RunPowerShell(script, Generous);

        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.True(result.StandardOutput.Length >= lines * lineLength);
        Assert.True(result.StandardError.Length >= lines * lineLength);
    }

    [Fact]
    public void Times_out_and_kills_the_process_tree()
    {
        // The parent spawns a grandchild and prints its PID. Killing only the direct child would leave the
        // grandchild alive holding the inherited pipe handles, so the reads would never complete.
        var script =
            "$c = Start-Process powershell -PassThru -WindowStyle Hidden " +
            "-ArgumentList '-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 120'; " +
            "[Console]::Out.WriteLine($c.Id); [Console]::Out.Flush(); " +
            "Start-Sleep -Seconds 120";

        var startedAt = Stopwatch.StartNew();
        var result = RunPowerShell(script, TimeSpan.FromSeconds(5));
        startedAt.Stop();

        Assert.True(result.TimedOut);

        // The timeout was real: it did not wait for the 120-second child.
        Assert.True(startedAt.Elapsed < TimeSpan.FromSeconds(45),
            $"the runner took {startedAt.Elapsed.TotalSeconds:0.0}s, so the timeout did not fire.");

        // Output produced before the kill is still recovered.
        var grandchildPid = ParseFirstInt(result.StandardOutput);
        Assert.True(grandchildPid.HasValue,
            $"expected the grandchild PID on stdout, got: '{result.StandardOutput.Trim()}'");

        Assert.True(WaitForExit(grandchildPid!.Value, TimeSpan.FromSeconds(10)),
            $"grandchild process {grandchildPid} survived the timeout; the tree was not killed.");
    }

    private static int? ParseFirstInt(string text)
    {
        var token = text.Split('\n', '\r').FirstOrDefault(t => int.TryParse(t.Trim(), out _));
        return token is null ? null : int.Parse(token.Trim());
    }

    private static bool WaitForExit(int processId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited)
                    return true;
            }
            catch (ArgumentException)
            {
                return true; // No such process: it is gone.
            }

            Thread.Sleep(200);
        }

        return false;
    }
}
