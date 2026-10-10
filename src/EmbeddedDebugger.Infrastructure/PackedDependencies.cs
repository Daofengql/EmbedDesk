using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace EmbeddedDebugger.Infrastructure;

internal static class PackedDependencies
{
    private const string ResourceName = "PackedDependencies.BouncyCastle.Cryptography";
    private static readonly Lazy<Assembly> CryptoAssembly = new(LoadCryptoAssembly);

    // This application-owned assembly must install its resolver before SSH/VNC
    // methods are JIT-compiled; an explicit call inside those methods is too late.
#pragma warning disable CA2255
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Initialize()
    {
        // The resource exists only in packed Release builds. Decompress lazily,
        // so serial/local Shell users do not pay the crypto loading cost.
        if (typeof(PackedDependencies).Assembly.GetManifestResourceInfo(ResourceName) is not null)
            AssemblyLoadContext.Default.Resolving += Resolve;
    }

    private static Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
        => name.Name == "BouncyCastle.Cryptography" ? CryptoAssembly.Value : null;

    private static Assembly LoadCryptoAssembly()
    {
        using var resource = typeof(PackedDependencies).Assembly.GetManifestResourceStream(ResourceName)!;
        using var compressed = new DeflateStream(resource, CompressionMode.Decompress);
        using var bytes = new MemoryStream(6 * 1024 * 1024);
        compressed.CopyTo(bytes);
        bytes.Position = 0;
        return AssemblyLoadContext.Default.LoadFromStream(bytes);
    }
}
