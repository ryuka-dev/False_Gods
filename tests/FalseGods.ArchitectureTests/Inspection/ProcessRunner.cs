using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace FalseGods.ArchitectureTests.Inspection;

/// <summary>The outcome of a child process. <see cref="ExitCode"/> is null only when the process never exited.</summary>
public sealed record ProcessResult(int? ExitCode, string StandardOutput, string StandardError, bool TimedOut)
{
    public bool Succeeded => !TimedOut && ExitCode == 0;
}

/// <summary>
/// Runs a child process, reading both output streams concurrently and enforcing a real timeout.
///
/// The obvious implementation deadlocks:
///
///     var stdout = process.StandardOutput.ReadToEnd();   // blocks until the child closes stdout
///     var stderr = process.StandardError.ReadToEnd();    // never reached
///     process.WaitForExit(timeout);                      // never reached
///
/// A child that fills its stderr pipe buffer (a few KB) blocks writing to it, so it never finishes writing
/// stdout, so the first ReadToEnd never returns. The timeout on the third line is unreachable, and the
/// "timeout" is decoration. MSBuild on a failing project writes plenty to both streams.
///
/// So: start both reads first, await them and the exit concurrently, and cancel on a real deadline. On
/// timeout, kill the whole process tree — killing only the direct child leaves grandchildren holding the
/// pipe handles open, and the reads still never complete — then collect whatever output was produced.
/// </summary>
public static class ProcessRunner
{
    /// <summary>How long to wait for the pipes to drain after the tree has been killed.</summary>
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(5);

    public static ProcessResult Run(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start '{fileName}'.");

        // Start draining both pipes before waiting on anything. Neither read is cancelled: each completes
        // when its pipe closes, which is guaranteed once the process tree is gone.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        var timedOut = false;

        using (var cts = new CancellationTokenSource(timeout))
        {
            try
            {
                process.WaitForExitAsync(cts.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                timedOut = true;
                KillTree(process);
            }
        }

        // Bounded: a grandchild that survived the kill and still holds a pipe handle must not hang the checks.
        Task.WhenAll(stdoutTask, stderrTask).Wait(DrainGrace);

        if (!timedOut)
        {
            // Ensures the exit code and the redirected streams are fully settled.
            process.WaitForExit();
        }

        return new ProcessResult(
            ExitCode: process.HasExited ? process.ExitCode : null,
            StandardOutput: ResultOrEmpty(stdoutTask),
            StandardError: ResultOrEmpty(stderrTask),
            TimedOut: timedOut);
    }

    private static void KillTree(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Exited between the timeout firing and the kill. Nothing to do.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Access denied on a process that is already terminating.
        }

        try
        {
            process.WaitForExit((int)DrainGrace.TotalMilliseconds);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static string ResultOrEmpty(Task<string> readTask) =>
        readTask.IsCompletedSuccessfully ? readTask.Result : string.Empty;
}
