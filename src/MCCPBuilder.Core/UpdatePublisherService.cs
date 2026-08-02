using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MCCPBuilder.Core;

public sealed class UpdatePublisherService
{
    private readonly HttpClient _httpClient;

    public UpdatePublisherService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient(
            CreateHttpHandler())
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    internal static SocketsHttpHandler CreateHttpHandler() =>
        new()
        {
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(20),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };

    public async Task<string> PublishAsync(
        Uri serverBaseUri,
        string archivePath,
        string keyFilePath,
        CancellationToken cancellationToken = default,
        IProgress<PublishProgress>? progress = null)
    {
        ValidateServerUri(serverBaseUri);
        var key = ReadKeyFile(keyFilePath);
        var archive = Path.GetFullPath(archivePath);
        if (!File.Exists(archive))
        {
            throw new FileNotFoundException("更新发布包不存在。", archive);
        }

        var contentHash = await ComputeFileHashAsync(
            archive,
            progress,
            cancellationToken);

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var message = Encoding.ASCII.GetBytes(
            $"{timestamp}\n{nonce}\n{contentHash}");
        var signature = Convert.ToHexString(
            HMACSHA256.HashData(key, message));

        await using var upload = new FileStream(
            archive,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var content = new ProgressStreamContent(
            upload,
            upload.Length,
            progress);
        content.Headers.ContentType =
            new MediaTypeHeaderValue("application/zip");
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(serverBaseUri, "v1/publish"))
        {
            Content = content
        };
        request.Headers.Add("X-MCCP-Timestamp", timestamp);
        request.Headers.Add("X-MCCP-Nonce", nonce);
        request.Headers.Add("X-MCCP-Content-SHA256", contentHash);
        request.Headers.Add("X-MCCP-Signature", signature);

        using var response = await SendAsync(
            request,
            serverBaseUri,
            "MC 更新包",
            cancellationToken);
        var responseText =
            await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"服务器拒绝发布（HTTP {(int)response.StatusCode}）：{responseText}",
                null,
                response.StatusCode);
        }

        return responseText;
    }

    public async Task CheckServerHealthAsync(
        Uri serverBaseUri,
        CancellationToken cancellationToken = default)
    {
        ValidateServerUri(serverBaseUri);
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(serverBaseUri, "v1/health"));
        try
        {
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(
                    cancellationToken);
                throw new HttpRequestException(
                    $"更新服务器连接测试失败（HTTP " +
                    $"{(int)response.StatusCode}）：{detail}",
                    null,
                    response.StatusCode);
            }
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException(
                "连接更新服务器超时，上传尚未开始。请检查本机代理、" +
                "网络连接和服务器 HTTPS。");
        }
        catch (HttpRequestException exception)
            when (exception.StatusCode is null)
        {
            throw new HttpRequestException(
                $"无法连接更新服务器 {serverBaseUri.Host}，上传尚未开始：" +
                $"{DescribeException(exception)}。请检查本机代理、" +
                "DNS、HTTPS 证书和防火墙。",
                exception);
        }
    }

    public async Task<PublishedUpdateVersions> GetPublishedVersionsAsync(
        Uri serverBaseUri,
        string productId,
        CancellationToken cancellationToken = default)
    {
        ValidateServerUri(serverBaseUri);
        var normalizedProductId =
            ReleaseBundleService.NormalizeProductId(productId);
        if (normalizedProductId != productId)
        {
            throw new InvalidDataException(
                "产品标识必须使用规范的小写字母、数字、点、下划线或短横线。");
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(
                serverBaseUri,
                $"v1/products/{Uri.EscapeDataString(normalizedProductId)}/manifest"));
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken);
        var responseText =
            await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return new("", "");
        }

        if (IsNoReleasePublishedResponse(
                response.StatusCode,
                responseText))
        {
            return new("", "");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"读取服务器现有版本失败（HTTP " +
                $"{(int)response.StatusCode}）：{responseText}",
                null,
                response.StatusCode);
        }

        var manifest = JsonSerializer.Deserialize<UpdateManifest>(
            responseText,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? throw new InvalidDataException("服务器更新清单为空。");
        if (!string.Equals(
                manifest.ProductId,
                normalizedProductId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("服务器更新清单的产品标识不匹配。");
        }

        return new(
            (manifest.Version ?? "").Trim(),
            (manifest.Launcher?.Version ?? "").Trim());
    }

    internal static bool IsNoReleasePublishedResponse(
        System.Net.HttpStatusCode statusCode,
        string responseText)
    {
        if (statusCode !=
            System.Net.HttpStatusCode.ServiceUnavailable)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(responseText);
            return document.RootElement.ValueKind ==
                       JsonValueKind.Object &&
                   document.RootElement.TryGetProperty(
                       "error",
                       out var error) &&
                   string.Equals(
                       error.GetString()?.Trim(),
                       "no release has been published",
                       StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static UpdatePublishPlan CreatePublishPlan(
        string clientVersion,
        string launcherVersion,
        PublishedUpdateVersions published)
    {
        ArgumentNullException.ThrowIfNull(published);
        var normalizedClientVersion = (clientVersion ?? "").Trim();
        var normalizedLauncherVersion = (launcherVersion ?? "").Trim();
        if (!InputValidator.IsValidVersion(normalizedClientVersion))
        {
            throw new InvalidDataException("客户端版本号格式无效。");
        }

        ValidateLauncherVersion(normalizedLauncherVersion);
        return new(
            !string.Equals(
                normalizedClientVersion,
                published.ClientVersion,
                StringComparison.OrdinalIgnoreCase),
            !string.Equals(
                normalizedLauncherVersion,
                published.LauncherVersion,
                StringComparison.OrdinalIgnoreCase));
    }

    public async Task<string> PublishPolicyAsync(
        Uri serverBaseUri,
        string productId,
        string keyFilePath,
        ClientLaunchPolicy policy,
        CancellationToken cancellationToken = default)
    {
        ValidateServerUri(serverBaseUri);
        ArgumentNullException.ThrowIfNull(policy);
        var normalizedProductId =
            ReleaseBundleService.NormalizeProductId(productId);
        if (normalizedProductId != productId)
        {
            throw new InvalidDataException(
                "产品标识必须使用规范的小写字母、数字、点、下划线或短横线。");
        }

        var normalizedPolicy = new ClientLaunchPolicy
        {
            ShowMessage = policy.ShowMessage,
            Title = (policy.Title ?? "").Trim(),
            Message = (policy.Message ?? "").Trim(),
            BlockLaunch = policy.BlockLaunch
        };
        if (normalizedPolicy.Title.Length > 128 ||
            normalizedPolicy.Message.Length > 4000 ||
            ((normalizedPolicy.ShowMessage || normalizedPolicy.BlockLaunch) &&
             string.IsNullOrWhiteSpace(normalizedPolicy.Message)))
        {
            throw new InvalidDataException(
                "启用公告或禁止启动时必须填写正文；标题最多128字符，正文最多4000字符。");
        }

        var body = JsonSerializer.SerializeToUtf8Bytes(
            normalizedPolicy,
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
        var key = ReadKeyFile(keyFilePath);
        var contentHash = Convert.ToHexString(SHA256.HashData(body));
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var nonce = Convert.ToHexString(
            RandomNumberGenerator.GetBytes(24));
        var message = Encoding.ASCII.GetBytes(
            $"{timestamp}\n{nonce}\n{contentHash}");
        var signature = Convert.ToHexString(
            HMACSHA256.HashData(key, message));

        using var content = new ByteArrayContent(body);
        content.Headers.ContentType =
            new MediaTypeHeaderValue("application/json");
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(
                serverBaseUri,
                $"v1/products/{Uri.EscapeDataString(normalizedProductId)}/policy"))
        {
            Content = content
        };
        request.Headers.Add("X-MCCP-Timestamp", timestamp);
        request.Headers.Add("X-MCCP-Nonce", nonce);
        request.Headers.Add("X-MCCP-Content-SHA256", contentHash);
        request.Headers.Add("X-MCCP-Signature", signature);

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseContentRead,
            cancellationToken);
        var responseText =
            await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"服务器拒绝更新公告策略（HTTP {(int)response.StatusCode}）：{responseText}",
                null,
                response.StatusCode);
        }

        return responseText;
    }

    public async Task<string> PublishLauncherAsync(
        Uri serverBaseUri,
        string productId,
        string installerPath,
        string launcherVersion,
        string keyFilePath,
        CancellationToken cancellationToken = default,
        IProgress<PublishProgress>? progress = null)
    {
        ValidateServerUri(serverBaseUri);
        var normalizedProductId =
            ReleaseBundleService.NormalizeProductId(productId);
        if (normalizedProductId != productId)
        {
            throw new InvalidDataException(
                "产品标识必须使用规范的小写字母、数字、点、下划线或短横线。");
        }

        ValidateLauncherVersion(launcherVersion);
        var installer = Path.GetFullPath(installerPath);
        if (!File.Exists(installer) ||
            !string.Equals(
                Path.GetExtension(installer),
                ".exe",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new FileNotFoundException(
                "启动器安装包不存在或不是 EXE。",
                installer);
        }

        var key = ReadKeyFile(keyFilePath);
        var contentHash = await ComputeFileHashAsync(
            installer,
            progress,
            cancellationToken);

        var timestamp =
            DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var nonce = Convert.ToHexString(
            RandomNumberGenerator.GetBytes(24));
        var message = Encoding.ASCII.GetBytes(
            $"{timestamp}\n{nonce}\n{contentHash}");
        var signature = Convert.ToHexString(
            HMACSHA256.HashData(key, message));

        await using var upload = new FileStream(
            installer,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous |
            FileOptions.SequentialScan);
        using var content = new ProgressStreamContent(
            upload,
            upload.Length,
            progress);
        content.Headers.ContentType =
            new MediaTypeHeaderValue(
                "application/vnd.microsoft.portable-executable");
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(
                serverBaseUri,
                $"v1/products/{Uri.EscapeDataString(normalizedProductId)}/launcher"))
        {
            Content = content
        };
        request.Headers.Add("X-MCCP-Timestamp", timestamp);
        request.Headers.Add("X-MCCP-Nonce", nonce);
        request.Headers.Add(
            "X-MCCP-Content-SHA256",
            contentHash);
        request.Headers.Add("X-MCCP-Signature", signature);
        request.Headers.Add(
            "X-MCCP-Launcher-Version",
            launcherVersion);

        using var response = await SendAsync(
            request,
            serverBaseUri,
            "Launcher 安装包",
            cancellationToken);
        var responseText =
            await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"服务器拒绝发布启动器（HTTP " +
                $"{(int)response.StatusCode}）：{responseText}",
                null,
                response.StatusCode);
        }

        return responseText;
    }

    public static void ValidateBuiltLauncherPackage(
        string installerPath,
        string bootstrapPath,
        string installerScriptPath,
        string expectedVersion)
    {
        var installer = Path.GetFullPath(installerPath);
        if (!File.Exists(installer) ||
            !string.Equals(
                Path.GetExtension(installer),
                ".exe",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new FileNotFoundException(
                "待发布的启动器安装包不存在或不是 EXE，请重新点击“开始打包”。",
                installer);
        }

        var expected = NormalizeLauncherVersion(
            expectedVersion,
            "当前填写的启动器版本");
        var bootstrap = ClientUpdateService.LoadBootstrap(
            Path.GetFullPath(bootstrapPath));
        var embedded = NormalizeLauncherVersion(
            bootstrap.LauncherVersion,
            "待发布安装包内的启动器版本");
        var scriptPath = Path.GetFullPath(installerScriptPath);
        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException(
                "缺少待发布安装包对应的 InstallerSource\\setup.iss，" +
                "无法确认安装版本，请重新点击“开始打包”。",
                scriptPath);
        }

        var appVersionLine = File.ReadLines(scriptPath)
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith(
                "AppVersion=",
                StringComparison.OrdinalIgnoreCase));
        if (appVersionLine is null)
        {
            throw new InvalidDataException(
                "InstallerSource\\setup.iss 中缺少 AppVersion，" +
                "请重新点击“开始打包”。");
        }

        var scriptVersion = NormalizeLauncherVersion(
            appVersionLine["AppVersion=".Length..],
            "安装脚本中的启动器版本");
        if (!string.Equals(expected, embedded, StringComparison.Ordinal) ||
            !string.Equals(expected, scriptVersion, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"启动器版本不一致，禁止上传旧安装包：当前填写 {expected}，" +
                $"update.json 内为 {embedded}，setup.iss 内为 {scriptVersion}。" +
                "请重新点击“开始打包”，完成后再发布更新。");
        }
    }

    public static byte[] ReadKeyFile(string keyFilePath)
    {
        if (string.IsNullOrWhiteSpace(keyFilePath))
        {
            throw new InvalidDataException("请选择服务器发布密钥文件。");
        }

        var encoded = File.ReadAllText(
            Path.GetFullPath(keyFilePath),
            Encoding.ASCII).Trim();
        byte[] key;
        try
        {
            key = Convert.FromBase64String(encoded);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("发布密钥文件不是有效的 Base64 格式。", exception);
        }

        return key.Length == 32
            ? key
            : throw new InvalidDataException(
                "发布密钥文件必须包含 32 字节随机密钥。");
    }

    public static void ValidateServerUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("更新服务器必须使用 HTTPS 地址。");
        }

        if (!uri.AbsolutePath.EndsWith('/'))
        {
            throw new InvalidDataException(
                "更新服务器地址必须以斜杠结尾，例如 https://download.example.com/。");
        }
    }

    private static void ValidateLauncherVersion(string value)
    {
        _ = NormalizeLauncherVersion(value, "启动器版本");
    }

    private static string NormalizeLauncherVersion(
        string value,
        string fieldName)
    {
        var normalized = (value ?? "").Trim();
        if (normalized.Split('.').Length != 3 ||
            !Version.TryParse(normalized, out var version) ||
            version.Major < 0 ||
            version.Minor < 0 ||
            version.Build < 0 ||
            version.Revision >= 0)
        {
            throw new InvalidDataException(
                $"{fieldName}必须使用 x.y.z 格式。");
        }

        return version.ToString(3);
    }

    private static async Task<string> ComputeFileHashAsync(
        string path,
        IProgress<PublishProgress>? progress,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous |
            FileOptions.SequentialScan);
        using var hasher = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        long processed = 0;
        int read;
        progress?.Report(new("Hashing", 0, stream.Length));
        while ((read = await stream.ReadAsync(
                   buffer,
                   cancellationToken)) != 0)
        {
            hasher.AppendData(buffer, 0, read);
            processed += read;
            progress?.Report(new(
                "Hashing",
                processed,
                stream.Length));
        }

        return Convert.ToHexString(hasher.GetHashAndReset());
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        Uri serverBaseUri,
        string contentName,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseContentRead,
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            throw new HttpRequestException(
                $"{contentName}上传超时。请检查更新服务器、HTTPS、" +
                "本机代理和网络连接后重试。");
        }
        catch (Exception exception)
            when (exception is HttpRequestException or IOException)
        {
            throw new HttpRequestException(
                $"{contentName}上传到 {serverBaseUri.Host} 时连接中断：" +
                $"{DescribeException(exception)}。请检查本机代理、" +
                "HTTPS 证书、服务器空间和 Nginx 上传限制。",
                exception);
        }
    }

    private static string DescribeException(Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception;
             current is not null;
             current = current.InnerException)
        {
            if (!string.IsNullOrWhiteSpace(current.Message) &&
                !messages.Contains(
                    current.Message,
                    StringComparer.Ordinal))
            {
                messages.Add(current.Message.Trim());
            }
        }

        return string.Join(" → ", messages);
    }
}
