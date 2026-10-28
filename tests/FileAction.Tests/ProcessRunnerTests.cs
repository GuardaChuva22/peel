using System.Diagnostics;
using FileAction.Core;

namespace FileAction.Tests;

public class ProcessRunnerTests
{
    private static (string Exe, string[] Args) SleepCommand() => OperatingSystem.IsWindows()
        ? (Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe", ["/d", "/c", "echo diagnostic-from-child 1>&2 & ping -n 31 127.0.0.1 >nul"])
        : ("/bin/sh", ["-c", "echo diagnostic-from-child >&2; sleep 30"]);

    [Fact]
    public async Task Timeout_kills_a_long_running_tree_and_keeps_stderr()
    {
        var command = SleepCommand();
        var stopwatch = Stopwatch.StartNew();
        var error = await Assert.ThrowsAsync<ConversionException>(() =>
            ProcessRunner.RunAsync(command.Exe, command.Args, CancellationToken.None, timeout: TimeSpan.FromSeconds(1)));
        Assert.Contains("timed out", error.Message);
        Assert.Contains("diagnostic-from-child", error.Message);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), "Timeout must return promptly.");
    }

    [Fact]
    public async Task Caller_cancellation_is_not_reported_as_a_timeout()
    {
        var command = SleepCommand();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var stopwatch = Stopwatch.StartNew();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ProcessRunner.RunAsync(command.Exe, command.Args, cancel.Token, timeout: TimeSpan.FromSeconds(20)));
        Assert.Equal(cancel.Token, error.CancellationToken);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), "Cancellation must return promptly.");
    }

    [Fact]
    public async Task Already_cancelled_request_does_not_start_a_process()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ProcessRunner.RunAsync("this-executable-does-not-exist", [], cancel.Token));
    }

    [Fact]
    public async Task Cancellation_terminates_descendant_processes()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var temp = OutputPaths.TempDirectory();
        var marker = Path.Combine(temp, "child.pid");
        using var cancel = new CancellationTokenSource();
        Process? descendant = null;
        try
        {
            var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            var script = "$p = Start-Process -FilePath ping.exe -ArgumentList '-n','91','127.0.0.1' -WindowStyle Hidden -PassThru; "
                + "[IO.File]::WriteAllText('" + marker.Replace("'", "''") + "', [string]$p.Id); Start-Sleep -Seconds 90";
            var running = ProcessRunner.RunAsync(powershell, ["-NoProfile", "-NonInteractive", "-Command", script], cancel.Token,
                timeout: TimeSpan.FromSeconds(75));
            // PowerShell can take many seconds to start on a busy CI runner running tests in parallel.
            var ready = Stopwatch.StartNew();
            while (!File.Exists(marker) && ready.Elapsed < TimeSpan.FromSeconds(45) && !running.IsCompleted)
                await Task.Delay(50);
            Assert.True(File.Exists(marker), "The child process must start before cancellation is tested.");
            descendant = Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(marker)));
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
            Assert.True(descendant.WaitForExit(3000), "Cancellation must terminate the spawned process as well as its launcher.");
        }
        finally
        {
            cancel.Cancel();
            if (descendant is not null)
            {
                try { if (!descendant.HasExited) descendant.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                descendant.Dispose();
            }
            OutputPaths.TryDelete(temp);
        }
    }

    [Fact]
    public async Task Successful_process_drains_both_streams()
    {
        var stderr = new List<string>();
        var exe = OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe" : "/bin/sh";
        string[] args = OperatingSystem.IsWindows()
            ? ["/d", "/c", "echo output-complete & echo error-complete 1>&2"]
            : ["-c", "echo output-complete; echo error-complete >&2"];
        var output = await ProcessRunner.RunAsync(exe, args, CancellationToken.None, stderr.Add, timeout: TimeSpan.FromSeconds(5));
        Assert.Contains("output-complete", output);
        Assert.Contains(stderr, line => line.Contains("error-complete"));
    }
}

public class OutputDirectoryProductionTests
{
    [Fact]
    public async Task Directory_output_is_unique_and_published_only_when_complete()
    {
        var root = OutputPaths.TempDirectory();
        try
        {
            var existing = Path.Combine(root, "clips");
            Directory.CreateDirectory(existing);
            await File.WriteAllTextAsync(Path.Combine(existing, "original.txt"), "keep");
            var output = await OutputPaths.ProduceDirectoryAsync(root, "clips", async temporary =>
            {
                Assert.False(Directory.Exists(Path.Combine(root, "clips (2)")));
                await File.WriteAllTextAsync(Path.Combine(temporary, "complete.txt"), "complete");
            });
            Assert.Equal(Path.Combine(root, "clips (2)"), output);
            Assert.Equal("complete", await File.ReadAllTextAsync(Path.Combine(output, "complete.txt")));
            Assert.Equal("keep", await File.ReadAllTextAsync(Path.Combine(existing, "original.txt")));
            Assert.Empty(Directory.GetDirectories(root, ".*.part"));
        }
        finally { OutputPaths.TryDelete(root); }
    }

    [Fact]
    public async Task Failed_directory_production_removes_all_partial_files()
    {
        var root = OutputPaths.TempDirectory();
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => OutputPaths.ProduceDirectoryAsync(root, "clips", async temporary =>
            {
                await File.WriteAllTextAsync(Path.Combine(temporary, "unfinished.mp4"), "partial");
                throw new InvalidOperationException("fixture failure");
            }));
            Assert.Empty(Directory.GetFileSystemEntries(root));
        }
        finally { OutputPaths.TryDelete(root); }
    }
}
