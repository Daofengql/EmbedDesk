namespace EmbedDesk.App;

/// <summary>Stores STCP shared keys in the current user's Windows Credential Manager.</summary>
internal static class StcpCredentialStore
{
    public static void Save(string target, string secret) =>
        WindowsCredentialStore.Save(target, secret, string.Empty, "EmbedDesk STCP shared key");

    public static string? Read(string target) => WindowsCredentialStore.Read(target)?.Secret;

    public static void Delete(string target) => WindowsCredentialStore.Delete(target);
}
