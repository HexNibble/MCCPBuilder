using System.Net.NetworkInformation;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace MCCPBuilder.Core;

public sealed record SavedLoginRecord(
    int Version,
    string ProviderKey,
    string Username,
    string Uuid,
    string AccessToken,
    string ClientId,
    string UserType,
    string Xuid,
    DateTimeOffset SavedAtUtc,
    string Password = "");

public sealed class SecureLoginStore
{
    private const string CurrentProductDirectoryName = "MCCPBuilder";
    private const string LegacyProductDirectoryName = "MCCBuilder";
    private const string CurrentKeyPurpose =
        "MCCPBuilder.Launcher.SavedLogin.v2|";
    private const string LegacyKeyPurpose =
        "MCCBuilder.Launcher.SavedLogin.v2|";
    private const string CurrentAssociatedDataPrefix =
        "MCCPBuilder|AES-256-GCM|";
    private const string LegacyAssociatedDataPrefix =
        "MCCBuilder|AES-256-GCM|";
    private const int CurrentFormatVersion = 2;
    private const int KeySize = 32;
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KdfIterations = 210_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _applicationIdentity;
    private readonly string? _legacyFilePath;

    public SecureLoginStore(string applicationDirectory, string? storageRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectory);

        _applicationIdentity = NormalizeApplicationIdentity(applicationDirectory);
        var identityHash = CreateApplicationIdentityHash(applicationDirectory);
        var localApplicationData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        var root = string.IsNullOrWhiteSpace(storageRoot)
            ? Path.Combine(
                localApplicationData,
                CurrentProductDirectoryName,
                "SavedLogins")
            : Path.GetFullPath(storageRoot);

        FilePath = Path.Combine(root, identityHash[..32] + ".bin");
        if (string.IsNullOrWhiteSpace(storageRoot))
        {
            _legacyFilePath = Path.Combine(
                localApplicationData,
                LegacyProductDirectoryName,
                "SavedLogins",
                identityHash[..32] + ".bin");
        }
    }

    public string FilePath { get; }

    public static string CreateApplicationIdentityHash(string applicationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectory);
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(
                NormalizeApplicationIdentity(applicationDirectory)))).ToLowerInvariant();
    }

    public SavedLoginRecord? Load()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        foreach (var candidate in GetLoadCandidates())
        {
            byte[]? plaintext = null;
            try
            {
                if (!File.Exists(candidate))
                {
                    continue;
                }

                plaintext = Decrypt(File.ReadAllBytes(candidate));
                var record = JsonSerializer.Deserialize<SavedLoginRecord>(
                    plaintext,
                    JsonOptions);
                if (!IsValid(record))
                {
                    continue;
                }

                if (!string.Equals(
                        candidate,
                        FilePath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    TryMigrateLegacyRecord(record!);
                }

                return record;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or
                CryptographicException or JsonException or FormatException or
                InvalidDataException or SecurityException)
            {
                // 继续尝试兼容路径或兼容加密用途。
            }
            finally
            {
                if (plaintext is not null)
                {
                    CryptographicOperations.ZeroMemory(plaintext);
                }
            }
        }

        return null;
    }

    public void Save(SavedLoginRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("保存登录信息仅支持 Windows。");
        }

        var normalized = record with
        {
            Version = CurrentFormatVersion,
            SavedAtUtc = DateTimeOffset.UtcNow
        };
        if (!IsValid(normalized))
        {
            throw new InvalidDataException("登录会话不完整，无法安全保存。");
        }

        var plaintext = JsonSerializer.SerializeToUtf8Bytes(normalized, JsonOptions);
        byte[]? encryptedBytes = null;
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(FilePath)!,
            "." + Path.GetRandomFileName() + ".tmp");
        try
        {
            encryptedBytes = Encrypt(plaintext);
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(encryptedBytes);
                stream.Flush(true);
            }

            File.Move(temporaryPath, FilePath, true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            if (encryptedBytes is not null)
            {
                CryptographicOperations.ZeroMemory(encryptedBytes);
            }

            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
                // 临时密文清理失败不应覆盖原始保存异常。
            }
        }
    }

    public void Delete()
    {
        foreach (var path in GetLoadCandidates())
        {
            try
            {
                File.Delete(path);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    private IEnumerable<string> GetLoadCandidates()
    {
        yield return FilePath;
        if (!string.IsNullOrWhiteSpace(_legacyFilePath) &&
            !string.Equals(
                _legacyFilePath,
                FilePath,
                StringComparison.OrdinalIgnoreCase))
        {
            yield return _legacyFilePath;
        }
    }

    private void TryMigrateLegacyRecord(SavedLoginRecord record)
    {
        try
        {
            Save(record);
        }
        catch
        {
            // 兼容读取成功即可；迁移失败不应阻止用户登录。
        }
    }

    private byte[] Encrypt(byte[] plaintext)
    {
        var macAddress = GetLocalMacAddress();
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var tag = new byte[TagSize];
        var ciphertext = new byte[plaintext.Length];
        var key = DeriveKey(macAddress, salt, legacyPurpose: false);
        var associatedData = CreateAssociatedData(
            macAddress,
            legacyPurpose: false);
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
            var envelope = new EncryptedLoginEnvelope(
                CurrentFormatVersion,
                macAddress,
                Convert.ToBase64String(salt),
                Convert.ToBase64String(nonce),
                Convert.ToBase64String(tag),
                Convert.ToBase64String(ciphertext));
            return JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(associatedData);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(salt);
        }
    }

    private byte[] Decrypt(byte[] encryptedBytes)
    {
        try
        {
            return DecryptCore(encryptedBytes, legacyPurpose: false);
        }
        catch (CryptographicException)
        {
            return DecryptCore(encryptedBytes, legacyPurpose: true);
        }
    }

    private byte[] DecryptCore(
        byte[] encryptedBytes,
        bool legacyPurpose)
    {
        var envelope = JsonSerializer.Deserialize<EncryptedLoginEnvelope>(
                           encryptedBytes,
                           JsonOptions)
                       ?? throw new InvalidDataException("保存的登录信息格式无效。");
        if (envelope.Version != CurrentFormatVersion ||
            string.IsNullOrWhiteSpace(envelope.MacAddress))
        {
            throw new InvalidDataException("保存的登录信息版本不受支持。");
        }

        var salt = Convert.FromBase64String(envelope.Salt);
        var nonce = Convert.FromBase64String(envelope.Nonce);
        var tag = Convert.FromBase64String(envelope.Tag);
        var ciphertext = Convert.FromBase64String(envelope.Ciphertext);
        if (salt.Length != SaltSize || nonce.Length != NonceSize || tag.Length != TagSize)
        {
            throw new InvalidDataException("保存的登录信息加密参数无效。");
        }

        var plaintext = new byte[ciphertext.Length];
        var key = DeriveKey(
            envelope.MacAddress,
            salt,
            legacyPurpose);
        var associatedData = CreateAssociatedData(
            envelope.MacAddress,
            legacyPurpose);
        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
            return plaintext;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(associatedData);
            CryptographicOperations.ZeroMemory(ciphertext);
            CryptographicOperations.ZeroMemory(tag);
            CryptographicOperations.ZeroMemory(nonce);
            CryptographicOperations.ZeroMemory(salt);
        }
    }

    private byte[] DeriveKey(
        string macAddress,
        byte[] salt,
        bool legacyPurpose)
    {
        var machineIdentifier = GetMachineIdentifier();
        var keyMaterial = Encoding.UTF8.GetBytes(
            (legacyPurpose
                ? LegacyKeyPurpose
                : CurrentKeyPurpose) +
            machineIdentifier + "|" +
            macAddress + "|" +
            _applicationIdentity);
        try
        {
            return Rfc2898DeriveBytes.Pbkdf2(
                keyMaterial,
                salt,
                KdfIterations,
                HashAlgorithmName.SHA256,
                KeySize);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(keyMaterial);
        }
    }

    private byte[] CreateAssociatedData(
        string macAddress,
        bool legacyPurpose) =>
        Encoding.UTF8.GetBytes(
            $"{(legacyPurpose ? LegacyAssociatedDataPrefix : CurrentAssociatedDataPrefix)}" +
            $"{CurrentFormatVersion}|{macAddress}|{_applicationIdentity}");

    private static string GetMachineIdentifier()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("读取计算机唯一标识符仅支持 Windows。");
        }

        using var key = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Cryptography",
            writable: false);
        var machineGuid = key?.GetValue("MachineGuid")?.ToString()?.Trim();
        if (string.IsNullOrWhiteSpace(machineGuid))
        {
            throw new CryptographicException("无法读取 Windows 计算机唯一标识符。");
        }

        return machineGuid.ToUpperInvariant();
    }

    private static string GetLocalMacAddress()
    {
        var address = NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter =>
                adapter.NetworkInterfaceType is not NetworkInterfaceType.Loopback and
                    not NetworkInterfaceType.Tunnel)
            .Select(adapter => adapter.GetPhysicalAddress().ToString())
            .Where(value =>
                value.Length >= 12 &&
                value.Any(character => character != '0'))
            .OrderBy(value => value, StringComparer.Ordinal)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(address))
        {
            throw new CryptographicException("无法读取本机网卡 MAC 地址。");
        }

        return address.ToUpperInvariant();
    }

    private static bool IsValid(SavedLoginRecord? record) =>
        record is
        {
            Version: CurrentFormatVersion,
            ProviderKey.Length: > 0,
            Username.Length: > 0,
            Uuid.Length: > 0,
            AccessToken.Length: > 0,
            ClientId.Length: > 0,
            UserType.Length: > 0,
            Xuid.Length: > 0
        };

    private static string NormalizeApplicationIdentity(string applicationDirectory) =>
        Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(applicationDirectory)).ToUpperInvariant();

    private sealed record EncryptedLoginEnvelope(
        int Version,
        string MacAddress,
        string Salt,
        string Nonce,
        string Tag,
        string Ciphertext);
}
