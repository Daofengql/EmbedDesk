using System.IO;

namespace EmbedDesk.App.Controls;

// WebView2's virtual host needs physical files. Keep the distributable self-contained
// and materialize its embedded terminal assets in a cache unique to this build.
internal static class TerminalAssets
{
    private static readonly Lazy<string> CacheDirectory = new(Extract);
    public static string DirectoryPath => CacheDirectory.Value;

    private static string Extract()
    {
        var assembly = typeof(TerminalAssets).Assembly;
        var directory = Path.Combine(AppDataPaths.RootDirectory, "TerminalAssets", assembly.ManifestModule.ModuleVersionId.ToString("N"));
        Directory.CreateDirectory(directory);
        const string prefix = "TerminalAssets/";
        foreach (var name in assembly.GetManifestResourceNames().Where(name => name.StartsWith(prefix, StringComparison.Ordinal)))
        {
            var path = Path.GetFullPath(Path.Combine(directory, name[prefix.Length..]));
            if (!path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Invalid terminal asset path.");
            if (File.Exists(path)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var source = assembly.GetManifestResourceStream(name)!)
                using (var target = File.Create(temporary)) source.CopyTo(target);
                try { File.Move(temporary, path); }
                catch (IOException) when (File.Exists(path)) { /* Another instance finished the same asset. */ }
            }
            finally { File.Delete(temporary); }
        }
        if (!File.Exists(Path.Combine(directory, "index.html")))
            throw new FileNotFoundException("Embedded terminal assets are missing.");
        return directory;
    }
}
