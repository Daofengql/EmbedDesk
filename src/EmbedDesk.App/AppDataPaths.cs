using System.IO;

namespace EmbedDesk.App;

internal static class AppDataPaths
{
    private const string LegacyDirectoryName = "EmbeddedDebugger";
    private const string ProductDirectoryName = "EmbedDesk";
    private static readonly string LocalAppDataDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string RootDirectory => Path.Combine(LocalAppDataDirectory, ProductDirectoryName);
    public static string TerminalDirectory => Path.Combine(RootDirectory, "Terminal");

    public static string SettingsDirectory
    {
        get
        {
            var currentDirectory = RootDirectory;
            var legacyDirectory = Path.Combine(LocalAppDataDirectory, LegacyDirectoryName);
            if (!Directory.Exists(legacyDirectory)) return currentDirectory;

            try
            {
                foreach (var fileName in new[] { "remote-profiles.json", "ssh-keys.json" })
                {
                    var legacyFile = Path.Combine(legacyDirectory, fileName);
                    var currentFile = Path.Combine(currentDirectory, fileName);
                    if (!File.Exists(legacyFile) || File.Exists(currentFile)) continue;
                    Directory.CreateDirectory(currentDirectory);
                    File.Copy(legacyFile, currentFile);
                }
                return currentDirectory;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Keep existing profiles and key-path indexes usable if the legacy
                // directory is temporarily locked or cannot be copied.
                return legacyDirectory;
            }
        }
    }
}
