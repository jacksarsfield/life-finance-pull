using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Finance.Pull;

/// <summary>Monzo client credentials + tokens, encrypted with Windows DPAPI (current user).
/// Lives outside the Life folder and outside git.</summary>
public sealed class MonzoSecrets
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public DateTimeOffset AccessTokenExpiresAt { get; set; }
}

public static class TokenStore
{
    private static readonly string Path_ = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LifeFinance", "monzo.bin");

    public static MonzoSecrets? Load()
    {
        if (!File.Exists(Path_)) return null;
        var plain = ProtectedData.Unprotect(File.ReadAllBytes(Path_), null, DataProtectionScope.CurrentUser);
        return JsonSerializer.Deserialize<MonzoSecrets>(plain);
    }

    public static void Save(MonzoSecrets s)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path_)!);
        var plain = JsonSerializer.SerializeToUtf8Bytes(s);
        File.WriteAllBytes(Path_, ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser));
    }
}
