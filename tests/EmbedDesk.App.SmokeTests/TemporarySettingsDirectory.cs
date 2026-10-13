using System;
using System.IO;

namespace EmbedDesk.App.SmokeTests;

internal sealed class TemporarySettingsDirectory : IDisposable
{
    public TemporarySettingsDirectory()
    {
        DirectoryPath = Path.Combine(Path.GetTempPath(), "EmbedDesk-Smoke-Settings", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
    }

    public string DirectoryPath { get; }

    public void Dispose()
    {
        try { if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
