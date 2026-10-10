using System.Diagnostics;
using EmbeddedDebugger.Core.Abstractions;

namespace EmbeddedDebugger.Infrastructure.Serial;

public sealed class ExternalVirtualSerialMapper : IVirtualSerialMapper
{
    private readonly string _setupExecutable;

    public ExternalVirtualSerialMapper(string setupExecutable = "")
    {
        _setupExecutable = setupExecutable;
    }

    public async Task<IReadOnlyList<string>> CreatePairAsync(string displayName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_setupExecutable))
        {
            return Array.Empty<string>();
        }

        var psi = new ProcessStartInfo
        {
            FileName = _setupExecutable,
            Arguments = $"install {displayName}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Unable to start virtual serial mapper.");
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync(cancellationToken);
            throw new InvalidOperationException($"Virtual serial mapper failed: {error}");
        }

        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        return output.Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public async Task RemovePairAsync(IReadOnlyList<string> portNames, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_setupExecutable) || portNames.Count == 0) return;
        foreach (var portName in portNames)
        {
            var psi = new ProcessStartInfo
            {
                FileName = _setupExecutable,
                Arguments = $"remove {portName}",
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Unable to start virtual serial mapper.");
            await process.WaitForExitAsync(cancellationToken);
        }
    }
}
