using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using EmbeddedDebugger.App.Controls;
using EmbeddedDebugger.App.ViewModels;
using EmbeddedDebugger.Core.Models;
using EmbeddedDebugger.Infrastructure.Remote;
using Microsoft.Web.WebView2.Wpf;

namespace EmbeddedDebugger.App.SmokeTests;

// Run with loopback_ssh.py to exercise actual SSH.NET, terminal replay and awk.
internal static class SshIntegrationChecks
{
    // Protocol-only check for isolated publish builds; independent of desktop input focus.
    public static async Task RunProtocolAsync(int port)
    {
        await using var session = new RemoteDebugService().CreateSession(new RemoteConnectionProfile
        {
            Host = "127.0.0.1", Port = port, Username = "regression", Password = "loopback-only", AcceptUnknownHostKey = true
        });
        var output = new StringBuilder();
        var gate = new object();
        EmbeddedDebugger.Core.Models.RemotePerformanceSnapshot? latest = null;
        session.TextReceived += (_, text) => { lock (gate) output.Append(text); };
        session.PerformanceChanged += (_, snapshot) => { lock (gate) latest = snapshot; };
        await session.ResizeAsync(91, 23);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        await session.ConnectAsync();
        Console.WriteLine($"METRIC isolated SSH connect (includes intentional fixture delays): {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:0.0} ms");
        await WaitAsync(() => { lock (gate) return Task.FromResult(output.ToString().Contains("MOTD-END")); });
        string received; lock (gate) received = output.ToString();
        Require(session.State == RemoteSessionState.Connected && received.Contains("PTY=91x23") && received.Contains("拆包中文"), "Published SSH authenticates and preserves PTY size and split UTF-8");
        Require(Enumerable.Range(0, 600).All(i => received.Split($"MOTD-{i:000}:").Length == 2), "Published SSH receives all 600 MOTD lines exactly once");
        await WaitAsync(() => { lock (gate) return Task.FromResult(latest?.CpuPercent is not null); });
        lock (gate) Require(Math.Abs(latest!.CpuPercent!.Value - 40) < .01 && latest.MemoryPercent == 60, "Published SSH collects real remote command output");
        await session.ResizeAsync(83, 19);
        await session.SendAsync("__tty_size__PACKED\r");
        await WaitAsync(() => { lock (gate) return Task.FromResult(output.ToString().Contains("TTY_SIZE_PACKED=83,19")); });
        Require(true, "Published SSH sends terminal input and resizes the remote TTY");
    }

    public static async Task RunAsync(int port)
    {
        await using var vm = new RemoteWorkspaceViewModel(new RemoteDebugService(), new RemoteConnectionProfile
        {
            Name = "SSH 回归测试", Host = "127.0.0.1", Port = port, Username = "regression",
            Password = "loopback-only", AcceptUnknownHostKey = true
        });
        var control = new RemoteWorkspace { DataContext = vm };
        var window = new Window { Title = "SSH integration", Content = control, Width = 1120, Height = 780 };
        window.Show();
        try
        {
            var terminal = (ShellTerminal)control.FindName("TerminalControl");
            var browser = (WebView2)terminal.FindName("Browser");
            await WaitAsync(async () => browser.CoreWebView2 is not null && await browser.ExecuteScriptAsync("typeof terminal !== 'undefined' && terminal.rows > 0") == "true");
            await Task.Delay(200);
            var size = await browser.ExecuteScriptAsync("({columns:terminal.cols,rows:terminal.rows})");
            using var dimensions = JsonDocument.Parse(size);
            var columns = dimensions.RootElement.GetProperty("columns").GetInt32();
            var rows = dimensions.RootElement.GetProperty("rows").GetInt32();
            var connect = vm.ConnectCommand.ExecuteAsync(null);
            await WaitAsync(() => Task.FromResult(vm.ConnectionLogText.Contains("SSH_MSG_KEXINIT")));
            Require(!vm.IsWorkspaceReady && !browser.IsVisible, "Real handshake is shown before the native terminal becomes visible");
            await FileWorkspaceChecks.CaptureAsync(window, "embedded-debugger-ssh-handshake.png");
            await WaitAsync(() => Task.FromResult(vm.IsWorkspaceReady && Output(vm).Contains("MOTD-END")));
            Require(!connect.IsCompleted, "Full MOTD arrives while optional SFTP setup is still pending");
            await connect;
            Require(vm.IsConnected && !vm.SupportsDirectoryManagement, "A rejected SFTP subsystem leaves the shell connected");
            var expected = "AUTH-BANNER\r\n" + string.Concat(Enumerable.Range(0, 600).Select(i => $"MOTD-{i:000}: welcome 欢迎\r\n")) + $"PTY={columns}x{rows}\r\n拆包中文\r\nMOTD-END\r\nuser@test:~$ ";
            await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "embedded-debugger-ssh-output.txt"), Output(vm));
            await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "embedded-debugger-ssh-expected.txt"), expected);
            await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "embedded-debugger-ssh-log.txt"), vm.ConnectionLogText);
            Require(Output(vm) == expected, "Auth banner, all 600 MOTD lines, initial PTY size and split UTF-8 are preserved exactly");
            Require(vm.ConnectionLogText.Contains("SSH_MSG_USERAUTH_SUCCESS") && vm.ConnectionLogText.Contains("SHA256:") && !vm.ConnectionLogText.Contains("loopback-only"), "Connection log includes actual key exchange/authentication and host fingerprint without the password");
            var stamps = vm.ConnectionLogText.Split('\n').Where(line => line.StartsWith('[')).Select(line => line[..14]).ToArray();
            Require(stamps.SequenceEqual(stamps.OrderBy(stamp => stamp, StringComparer.Ordinal)), "Cross-thread connection events appear in chronological order");
            await WaitAsync(() => Task.FromResult(vm.CpuPercent.HasValue));
            Require(Math.Abs(vm.CpuPercent!.Value - 40) < .01 && vm.MemoryPercent == 60 && Math.Abs(vm.LoadPercent - 30) < .01,
                "Actual awk output yields 40% CPU, 60% memory and 30% normalized load");
            Require(vm.ReceiveBytes == 1300 && vm.TransmitBytes == 900 && vm.Uptime == "1天 01:01:01", "Network totals exclude loopback and uptime is parsed without uptime -p");
            Require(Math.Abs(((ProgressBar)control.FindName("CpuProgress")).Value - 40) < .01, "Performance values reach the visible progress bar");
            await Task.Delay(150);
            var screen = JsonSerializer.Deserialize<string>(await browser.ExecuteScriptAsync("Array.from({length:terminal.buffer.active.length},(_,i)=>terminal.buffer.active.getLine(i).translateToString(true)).join('\\n')"))!;
            Require(Enumerable.Range(0, 600).All(i => screen.Split($"MOTD-{i:000}:").Length == 2) && screen.Contains("拆包中文") && screen.Contains("MOTD-END"), "Every MOTD line is present exactly once in rendered xterm scrollback");
            await FileWorkspaceChecks.CaptureAsync(window, "embedded-debugger-ssh-performance.png");
            await WaitAsync(() => Task.FromResult(vm.PerformanceSource.StartsWith("读取失败：")));
            Require(vm.CpuPercent is null && vm.MemoryPercent is null, "Failed polling clears stale values and shows the server error");
            await WaitAsync(() => Task.FromResult(vm.CpuPercent.HasValue));
            Require(vm.IsConnected, "Polling recovers after an error without reconnecting the shell");
            var query = 0;
            foreach (var key in new byte[] { 0xBD, 0xBD, 0xBB, 0x30 })
            {
                await TerminalRegressionChecks.NativeZoomAsync(window, browser, key);
                var marker = $"TTY_SIZE_{++query}=";
                await vm.SendTerminalInputAsync($"__tty_size__{query}\r");
                var pattern = Regex.Escape(marker) + @"(\d+),(\d+)";
                await WaitAsync(() => Task.FromResult(Regex.IsMatch(Output(vm), pattern)));
                var match = Regex.Match(Output(vm), pattern);
                using var grid = JsonDocument.Parse(await browser.ExecuteScriptAsync("({cols:terminal.cols,rows:terminal.rows})"));
                Require(browser.ZoomFactor == 1 && int.Parse(match.Groups[1].Value) == grid.RootElement.GetProperty("cols").GetInt32()
                    && int.Parse(match.Groups[2].Value) == grid.RootElement.GetProperty("rows").GetInt32(),
                    $"SSH server PTY matches xterm after zoom key {key:X2}: {match.Value}");
            }
            await File.WriteAllTextAsync(Path.Combine(Path.GetTempPath(), "embedded-debugger-ssh-log.txt"), vm.ConnectionLogText);
        }
        finally { window.Close(); }
    }

    private static string Output(RemoteWorkspaceViewModel vm) => Encoding.UTF8.GetString(vm.Terminal.Entries.Where(entry => entry.IsReceived).SelectMany(entry => entry.Data).ToArray());
    private static async Task WaitAsync(Func<Task<bool>> ready)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!await ready()) { if (DateTime.UtcNow > deadline) throw new TimeoutException("SSH integration condition timed out"); await Task.Delay(30); }
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }
}
