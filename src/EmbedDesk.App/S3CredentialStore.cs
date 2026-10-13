namespace EmbedDesk.App;

internal sealed record StoredS3Credential(string AccessKeyId, string SecretAccessKey);

/// <summary>Stores an S3 access key and secret in the current Windows user's Credential Manager.</summary>
internal static class S3CredentialStore
{
    public static void Save(string target, string accessKeyId, string secretAccessKey)
    {
        if (string.IsNullOrWhiteSpace(accessKeyId) || string.IsNullOrEmpty(secretAccessKey))
            throw new ArgumentException("请输入 S3 Access Key ID 和 Secret Access Key。");
        WindowsCredentialStore.Save(target, secretAccessKey, accessKeyId, "EmbedDesk S3 connection credentials");
    }

    public static StoredS3Credential? Read(string target)
    {
        var stored = WindowsCredentialStore.Read(target);
        return stored is null ? null : new StoredS3Credential(stored.UserName, stored.Secret);
    }

    public static void Delete(string target)
    {
        WindowsCredentialStore.Delete(target);
    }
}
