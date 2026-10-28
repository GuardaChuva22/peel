using System.Diagnostics;
using System.Text;
using static FileAction.Core.Loc;

namespace FileAction.Core;

public static class ProcessRunner
{
    private const int MaxCapturedOutputCharacters = 1024 * 1024;
    private static readonly object ProcessGate = new();
    private static readonly HashSet<Process> ActiveProcesses = [];
    private static bool _stopping;

    /// <summary>Final app shutdown: synchronously kills active trees and prevents any further launches.</summary>
    public static void StopAll()
    {
        Process[] processes;
        lock (ProcessGate)
        {
            _stopping = true;
            processes = ActiveProcesses.ToArray();
            foreach (var process in processes)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                catch (System.Runtime.InteropServices.COMException) { }
            }
        }
        var reaping = Stopwatch.StartNew();
        foreach (var process in processes)
        {
            var remaining = TimeSpan.FromSeconds(5) - reaping.Elapsed;
            if (remaining <= TimeSpan.Zero)
                break;
            try { process.WaitForExit((int)remaining.TotalMilliseconds); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            catch (System.Runtime.InteropServices.COMException) { }
        }
    }

    private sealed class RunningProcessLease : IDisposable
    {
        private readonly Process _process;
        public RunningProcessLease(Process process)
        {
            _process = process;
            lock (ProcessGate)
            {
                if (_stopping)
                    throw new OperationCanceledException("The application is shutting down.");
                process.Start();
                ActiveProcesses.Add(process);
            }
        }
        public void Dispose()
        {
            lock (ProcessGate)
                ActiveProcesses.Remove(_process);
        }
    }
    /// <summary>
    /// Runs a program without a window, streaming stderr lines to <paramref name="onStdErr"/>.
    /// Kills the process tree on cancellation and throws <see cref="ConversionException"/> on a non-zero exit.
    /// </summary>
    public static async Task<string> RunAsync(
        string exe,
        IEnumerable<string> args,
        CancellationToken ct,
        Action<string>? onStdErr = null,
        string? workingDirectory = null,
        IDictionary<string, string>? environment = null,
        TimeSpan? timeout = null)
    {
        ct.ThrowIfCancellationRequested();
        var limit = timeout ?? TimeSpan.FromMinutes(30);
        if (limit <= TimeSpan.Zero && limit != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        using var deadline = new CancellationTokenSource(limit);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory ?? "",
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        if (environment is not null)
        {
            foreach (var (k, v) in environment)
                psi.Environment[k] = v;
        }

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderrTail = new Queue<string>();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            lock (stdout)
            {
                stdout.AppendLine(e.Data.Length > MaxCapturedOutputCharacters ? e.Data[^MaxCapturedOutputCharacters..] : e.Data);
                if (stdout.Length > MaxCapturedOutputCharacters)
                    stdout.Remove(0, stdout.Length - MaxCapturedOutputCharacters);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            lock (stderrTail)
            {
                stderrTail.Enqueue(e.Data.Length > 8192 ? e.Data[^8192..] : e.Data);
                while (stderrTail.Count > 15)
                    stderrTail.Dequeue();
            }
            onStdErr?.Invoke(e.Data);
        };

        RunningProcessLease lease;
        try
        {
            lease = new RunningProcessLease(process);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new ConversionException(T($"Nelze spustit {Path.GetFileName(exe)}: {ex.Message}", $"Cannot start {Path.GetFileName(exe)}: {ex.Message}"), ex);
        }
        using var running = lease;
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            // Give the OS time to reap the process, but never hang cancellation on a broken child.
            using var reap = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(reap.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            if (ct.IsCancellationRequested)
                throw new OperationCanceledException(ct);
            string tail;
            lock (stderrTail)
                tail = string.Join(Environment.NewLine, stderrTail.Where(l => !string.IsNullOrWhiteSpace(l)).TakeLast(5));
            throw new ConversionException($"{Path.GetFileNameWithoutExtension(exe)} timed out after {limit.TotalMinutes:0.##} minutes."
                + (tail.Length > 0 ? Environment.NewLine + tail : ""));
        }
        // Make sure the async readers drained.
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            string tail;
            lock (stderrTail)
                tail = string.Join(Environment.NewLine, stderrTail.Where(l => !string.IsNullOrWhiteSpace(l)).TakeLast(5));
            throw new ConversionException(
                T($"{Path.GetFileNameWithoutExtension(exe)} skončil s chybou {process.ExitCode}.", $"{Path.GetFileNameWithoutExtension(exe)} failed with exit code {process.ExitCode}.")
                + (tail.Length > 0 ? Environment.NewLine + tail : ""));
        }
        lock (stdout)
            return stdout.ToString();
    }
}
