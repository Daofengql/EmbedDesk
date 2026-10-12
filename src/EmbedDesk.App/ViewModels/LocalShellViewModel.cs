using System.Runtime.InteropServices;
using System.Text;
using System.IO;
using System.Windows;
using System.Collections;
using Microsoft.Win32.SafeHandles;
using CommunityToolkit.Mvvm.ComponentModel;

namespace EmbedDesk.App.ViewModels;

// ConPTY gives full-screen TUI programs terminal mode, cursor and resize semantics.
public sealed class LocalShellViewModel : ObservableObject, IAsyncDisposable
{
    private readonly object _writeGate = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private CancellationTokenSource? _readCts;
    private FileStream? _input;
    private FileStream? _output;
    private Task? _readTask;
    private IntPtr _pseudoConsole;
    private IntPtr _processHandle;
    private int _shellGeneration;
    private bool _isRunning;
    private string _shellName = "PowerShell";
    private bool _isFullscreen;

    public LocalShellViewModel() => _ = StartAsync();
    public TerminalBufferViewModel Terminal { get; } = new();
    public bool IsRunning { get => _isRunning; private set => SetProperty(ref _isRunning, value); }
    public string ShellName { get => _shellName; private set => SetProperty(ref _shellName, value); }
    public bool IsFullscreen { get => _isFullscreen; set => SetProperty(ref _isFullscreen, value); }

    public async Task StartAsync()
    {
        await _lifecycleGate.WaitAsync();
        var inputRead = IntPtr.Zero; var inputWrite = IntPtr.Zero;
        var outputRead = IntPtr.Zero; var outputWrite = IntPtr.Zero; var attributeList = IntPtr.Zero;
        try
        {
            if (IsRunning) return;
            DisposeCore();
            var generation = Interlocked.Increment(ref _shellGeneration);
            var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            var command = File.Exists(powershell) ? powershell : Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            ShellName = command.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase) ? "PowerShell" : "命令提示符";
            var attributes = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), bInheritHandle = true };
            if (!CreatePipe(out inputRead, out inputWrite, ref attributes, 0) || !CreatePipe(out outputRead, out outputWrite, ref attributes, 0)) ThrowWin32("CreatePipe");
            SetHandleInformation(inputWrite, HANDLE_FLAG_INHERIT, 0); SetHandleInformation(outputRead, HANDLE_FLAG_INHERIT, 0);
            var result = CreatePseudoConsole(new COORD(120, 40), inputRead, outputWrite, 0, out _pseudoConsole);
            if (result != 0) Marshal.ThrowExceptionForHR(result);
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref result);
            attributeList = Marshal.AllocHGlobal(result);
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref result)) ThrowWin32("InitializeProcThreadAttributeList");
            var pseudoConsole = _pseudoConsole;
            if (!UpdateProcThreadAttribute(attributeList, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, pseudoConsole, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero)) ThrowWin32("UpdateProcThreadAttribute");
            var startup = new STARTUPINFOEX { StartupInfo = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFOEX>() }, lpAttributeList = attributeList };
            var commandLine = new StringBuilder($"\"{command}\" {(ShellName == "PowerShell" ? "-NoLogo -NoProfile" : "/Q")}");
            const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
            const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
            var environment = CreateTerminalEnvironment();
            try
            {
                if (!CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                    EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT, environment,
                    Environment.CurrentDirectory, ref startup, out var processInfo)) ThrowWin32("CreateProcess");
                _processHandle = processInfo.hProcess; CloseHandle(processInfo.hThread);
            }
            finally { Marshal.FreeHGlobal(environment); }
            // ConPTY's anonymous pipe handles are synchronous handles. Passing them to an
            // async FileStream makes .NET reject the handle before the shell can start.
            _input = new FileStream(new SafeFileHandle(inputWrite, true), FileAccess.Write, 4096, isAsync: false);
            _output = new FileStream(new SafeFileHandle(outputRead, true), FileAccess.Read, 16384, isAsync: false);
            inputWrite = IntPtr.Zero; outputRead = IntPtr.Zero; _readCts = new CancellationTokenSource(); IsRunning = true;
            _readTask = Task.Run(() => ReadOutputLoop(_output, _readCts.Token, generation));
        }
        catch (Exception exception)
        {
            // The local shell is an optional workspace. Keep the main window usable if
            // ConPTY is unavailable and expose the failure in the terminal buffer.
            IsRunning = false;
            Terminal.Add(DateTimeOffset.Now, true, Encoding.UTF8.GetBytes($"本地 Shell 启动失败：{exception.Message}\r\n"));
            DisposeCore();
        }
        finally
        {
            if (attributeList != IntPtr.Zero) { DeleteProcThreadAttributeList(attributeList); Marshal.FreeHGlobal(attributeList); }
            if (inputRead != IntPtr.Zero) CloseHandle(inputRead);
            if (outputWrite != IntPtr.Zero) CloseHandle(outputWrite);
            if (inputWrite != IntPtr.Zero) CloseHandle(inputWrite);
            if (outputRead != IntPtr.Zero) CloseHandle(outputRead);
            _lifecycleGate.Release();
        }
    }

    public async Task SendAsync(string text)
    {
        if (!IsRunning) await StartAsync();
        if (string.IsNullOrEmpty(text)) return;
        await _lifecycleGate.WaitAsync();
        try
        {
            if (!IsRunning || _input is null) return;
            var bytes = Encoding.UTF8.GetBytes(text);
            lock (_writeGate)
            {
                _input.Write(bytes, 0, bytes.Length);
                _input.Flush();
            }
        }
        finally { _lifecycleGate.Release(); }
    }

    public async Task ResizeAsync(int columns, int rows)
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            if (_pseudoConsole != IntPtr.Zero)
                ResizePseudoConsole(_pseudoConsole, new COORD((short)Math.Clamp(columns, 20, short.MaxValue), (short)Math.Clamp(rows, 5, short.MaxValue)));
        }
        finally { _lifecycleGate.Release(); }
    }

    private void ReadOutputLoop(FileStream output, CancellationToken cancellationToken, int generation)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var count = output.Read(buffer, 0, buffer.Length); if (count <= 0) break;
                var copy = buffer[..count].ToArray(); RunOnUi(() => Terminal.Add(DateTimeOffset.Now, true, copy));
            }
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
        catch (IOException) { }
        finally { RunOnUi(() => { if (generation == Volatile.Read(ref _shellGeneration)) IsRunning = false; }); }
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycleGate.WaitAsync();
        try { DisposeCore(); }
        finally { _lifecycleGate.Release(); }
    }

    private void DisposeCore()
    {
        Interlocked.Increment(ref _shellGeneration);
        _readCts?.Cancel();
        try { if (_pseudoConsole != IntPtr.Zero) ClosePseudoConsole(_pseudoConsole); } catch { }
        _pseudoConsole = IntPtr.Zero; try { if (_processHandle != IntPtr.Zero) { TerminateProcess(_processHandle, 0); CloseHandle(_processHandle); } } catch { }
        _processHandle = IntPtr.Zero;
        if (_input is not null) _input.Dispose();
        if (_output is not null) _output.Dispose();
        _input = null; _output = null;
        _readCts?.Dispose(); _readCts = null;
        _readTask = null;
        RunOnUi(() => IsRunning = false);
    }

    private static void ThrowWin32(string operation) => throw new InvalidOperationException($"{operation} 失败: {Marshal.GetLastWin32Error()}");

    private static IntPtr CreateTerminalEnvironment()
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            values[entry.Key.ToString()!] = entry.Value?.ToString() ?? string.Empty;

        // ConPTY does not infer a Unix terminal type. Without TERM, full-screen
        // programs such as codex and opencode downgrade themselves to dumb mode.
        values["TERM"] = "xterm-256color";
        values["COLORTERM"] = "truecolor";
        values["TERM_PROGRAM"] = "EmbedDesk";
        // The desktop app is commonly launched from a Codex process, whose
        // environment contains NO_COLOR=1. Claude Code honors that variable
        // even inside a real ConPTY and consequently renders a monochrome UI.
        // Remove the inherited opt-out and explicitly advertise color support
        // for programs whose stdout TTY detection does not recognize ConPTY.
        values.Remove("NO_COLOR");
        values["FORCE_COLOR"] = "1";
        var block = string.Join('\0', values.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => $"{pair.Key}={pair.Value}")) + "\0\0";
        return Marshal.StringToHGlobalUni(block);
    }

    private static void RunOnUi(Action action) { var dispatcher = Application.Current?.Dispatcher; if (dispatcher is null || dispatcher.CheckAccess()) action(); else dispatcher.BeginInvoke(action); }
    private const uint HANDLE_FLAG_INHERIT = 1; private const nuint PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
    [StructLayout(LayoutKind.Sequential)] private readonly struct COORD(short x, short y) { public readonly short X = x; public readonly short Y = y; }
    [StructLayout(LayoutKind.Sequential)] private struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle; }
    [StructLayout(LayoutKind.Sequential)] private struct PROCESS_INFORMATION { public IntPtr hProcess; public IntPtr hThread; public int dwProcessId; public int dwThreadId; }
    [StructLayout(LayoutKind.Sequential)] private struct STARTUPINFO { public int cb; public string? lpReserved; public string? lpDesktop; public string? lpTitle; public int dwX; public int dwY; public int dwXSize; public int dwYSize; public int dwXCountChars; public int dwYCountChars; public int dwFillAttribute; public int dwFlags; public short wShowWindow; public short cbReserved2; public IntPtr lpReserved2; public IntPtr hStdInput; public IntPtr hStdOutput; public IntPtr hStdError; }
    [StructLayout(LayoutKind.Sequential)] private struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out IntPtr hReadPipe, out IntPtr hWritePipe, ref SECURITY_ATTRIBUTES pipeAttributes, int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetHandleInformation(IntPtr hObject, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern int CreatePseudoConsole(COORD size, IntPtr hInput, IntPtr hOutput, uint flags, out IntPtr hPC);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);
    [DllImport("kernel32.dll")] private static extern void ClosePseudoConsole(IntPtr hPC);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr attributeList, int attributeCount, int flags, ref int size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr attributeList, uint flags, nuint attribute, IntPtr value, IntPtr size, IntPtr previousValue, IntPtr returnSize);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool CreateProcess(string? applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes, bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory, ref STARTUPINFOEX startupInfo, out PROCESS_INFORMATION processInformation);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
}
