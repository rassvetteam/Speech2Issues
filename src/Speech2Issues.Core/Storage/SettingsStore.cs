using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Speech2Issues.Core.Configuration;

namespace Speech2Issues.Core.Storage;

public sealed class SettingsStore(AppPaths paths)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Speech2Issues/v1/local-secrets");

    public async Task<AppSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        paths.EnsureCreated();
        if (!File.Exists(paths.SettingsFile))
        {
            return new AppSettings();
        }

        await using var stream = File.OpenRead(paths.SettingsFile);
        return await JsonSerializer.DeserializeAsync<AppSettings>(stream, JsonOptions, cancellationToken) ?? new AppSettings();
    }

    public Task SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default) =>
        AtomicFile.WriteJsonAsync(paths.SettingsFile, settings, JsonOptions, cancellationToken);

    public async Task<AppSecrets> LoadSecretsAsync(CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Speech2Issues secrets require Windows DPAPI.");
        }
        paths.EnsureCreated();
        if (!File.Exists(paths.SecretsFile))
        {
            return new AppSecrets();
        }

        var protectedBytes = await File.ReadAllBytesAsync(paths.SecretsFile, cancellationToken);
        var plainBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
        return JsonSerializer.Deserialize<AppSecrets>(plainBytes, JsonOptions) ?? new AppSecrets();
    }

    public async Task SaveSecretsAsync(AppSecrets secrets, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Speech2Issues secrets require Windows DPAPI.");
        }
        paths.EnsureCreated();
        var plainBytes = JsonSerializer.SerializeToUtf8Bytes(secrets, JsonOptions);
        var protectedBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
        await AtomicFile.WriteBytesAsync(paths.SecretsFile, protectedBytes, cancellationToken);
    }
}
