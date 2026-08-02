using System.Text;
using MCCPBuilder.Core;

namespace MCCPBuilder.Tests;

public sealed class SecureLoginStoreTests : IDisposable
{
    private readonly string _temporaryDirectory =
        Path.Combine(Path.GetTempPath(), "MCCPBuilderTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void SaveAndLoad_RoundTripsTokenOnlyWithoutPlaintextCredentials()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var store = new SecureLoginStore(
            @"C:\Program Files\示例客户端",
            _temporaryDirectory);
        var record = CreateRecord();

        store.Save(record);
        var encryptedBytes = File.ReadAllBytes(store.FilePath);
        var loaded = store.Load();

        Assert.NotNull(loaded);
        Assert.Equal(record.ProviderKey, loaded.ProviderKey);
        Assert.Equal(record.Username, loaded.Username);
        Assert.Equal(record.AccessToken, loaded.AccessToken);
        using var envelope = System.Text.Json.JsonDocument.Parse(encryptedBytes);
        Assert.Equal(3, envelope.RootElement.GetProperty("version").GetInt32());
        Assert.True(envelope.RootElement.TryGetProperty("ciphertext", out _));
        Assert.DoesNotContain(
            record.Username,
            Encoding.UTF8.GetString(encryptedBytes),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            record.AccessToken,
            Encoding.UTF8.GetString(encryptedBytes),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "password",
            Encoding.UTF8.GetString(encryptedBytes),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Load_DeletesVersionTwoRecordThatMayContainPassword()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var store = new SecureLoginStore(@"C:\Games\Client", _temporaryDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        File.WriteAllText(
            store.FilePath,
            """
            {
              "version": 2,
              "macAddress": "001122334455",
              "salt": "AAAAAAAAAAAAAAAAAAAAAA==",
              "nonce": "AAAAAAAAAAAAAAAA",
              "tag": "AAAAAAAAAAAAAAAAAAAAAA==",
              "ciphertext": "AA=="
            }
            """);

        Assert.Null(store.Load());
        Assert.False(File.Exists(store.FilePath));
    }

    [Fact]
    public void Delete_RemovesSavedLogin()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var store = new SecureLoginStore(@"C:\Games\Client", _temporaryDirectory);
        store.Save(CreateRecord());

        store.Delete();

        Assert.False(File.Exists(store.FilePath));
        Assert.Null(store.Load());
    }

    [Fact]
    public void Load_ReturnsNullForTamperedCiphertext()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var store = new SecureLoginStore(@"C:\Games\Client", _temporaryDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(store.FilePath)!);
        File.WriteAllBytes(store.FilePath, [1, 2, 3, 4]);

        Assert.Null(store.Load());
    }

    private static SavedLoginRecord CreateRecord() =>
        new(
            1,
            "UnifiedPassport|server-id",
            "测试账号@example.com",
            "0123456789abcdef0123456789abcdef",
            "secret-access-token",
            "client-token",
            "mojang",
            "0",
            DateTimeOffset.UtcNow);

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, true);
        }
    }
}
