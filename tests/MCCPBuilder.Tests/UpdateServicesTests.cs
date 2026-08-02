using System.IO.Compression;
using System.Formats.Tar;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MCCPBuilder.Core;

namespace MCCPBuilder.Tests;

public sealed class UpdateServicesTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "MCCPBuilderUpdateTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Publisher_DefaultHttpHandler_DisablesProxy()
    {
        using var handler = UpdatePublisherService.CreateHttpHandler();

        Assert.False(handler.UseProxy);
    }

    [Fact]
    public void ClientUpdater_DefaultHttpHandler_DisablesProxyAndAllows200Connections()
    {
        using var handler = ClientUpdateService.CreateHttpHandler();

        Assert.False(handler.UseProxy);
        Assert.Equal(
            ClientUpdateService.MaxDownloadConcurrency,
            handler.MaxConnectionsPerServer);
    }

    [Fact]
    public void NewProject_DoesNotPrefillUpdateServerOrProductId()
    {
        var project = new MCCPBuilder.Models.ProjectConfig();

        Assert.Empty(project.Update.ServerBaseUrl);
        Assert.Empty(project.Update.ProductId);
        Assert.True(project.Update.RequireSuccessfulCheck);
        Assert.False(project.Update.ShowServerNotice);
        Assert.Empty(project.Update.ServerNoticeTitle);
        Assert.Empty(project.Update.ServerNoticeMessage);
        Assert.False(project.Update.BlockGameLaunch);
        Assert.False(project.Installation.RunLauncherAsAdministrator);
    }

    [Fact]
    public async Task BootstrapConfig_PreservesAdministratorLaunchPolicy()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "update.json");
        await File.WriteAllTextAsync(
            path,
            """
            {
              "schemaVersion": 1,
              "serverBaseUrl": "https://updates.example/",
              "productId": "test-client",
              "launcherVersion": "1.2.3",
              "requireSuccessfulCheck": true,
              "requireAdministrator": true
            }
            """);

        var bootstrap = ClientUpdateService.LoadBootstrap(path);

        Assert.True(bootstrap.RequireAdministrator);
        Assert.Equal(
            ClientUpdateService.MaxDownloadConcurrency,
            bootstrap.DownloadConcurrency);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public async Task ClientUpdater_RejectsInvalidConcurrencyBeforeNetworkRequest(
        int downloadConcurrency)
    {
        var requests = 0;
        var handler = new DelegateHandler(_ =>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK));
        });
        var updater = new ClientUpdateService(new HttpClient(handler));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            updater.CheckAndApplyAsync(
                _root,
                new()
                {
                    ServerBaseUrl = "https://updates.example/",
                    ProductId = "test-client",
                    LauncherVersion = "1.0.0",
                    DownloadConcurrency = downloadConcurrency
                }));

        Assert.Equal(0, Volatile.Read(ref requests));
    }

    [Fact]
    public async Task ReleaseBundle_ContainsManifestAndExcludesBootstrapFiles()
    {
        var payload = Path.Combine(_root, "payload");
        Directory.CreateDirectory(Path.Combine(payload, ".minecraft", "配置"));
        Directory.CreateDirectory(Path.Combine(payload, ".minecraft", "config"));
        Directory.CreateDirectory(Path.Combine(payload, "LauncherConfig"));
        await File.WriteAllTextAsync(
            Path.Combine(payload, ".minecraft", "配置", "中文.txt"),
            "payload");
        await File.WriteAllTextAsync(
            Path.Combine(payload, ".minecraft", "config", "user.toml"),
            "user setting");
        await File.WriteAllTextAsync(
            Path.Combine(payload, "Launcher.exe"),
            "launcher");
        await File.WriteAllTextAsync(
            Path.Combine(payload, "LauncherConfig", "update.json"),
            "bootstrap");
        await File.WriteAllTextAsync(
            Path.Combine(payload, "LauncherConfig", "launcher.json"),
            "runtime");

        var archivePath = Path.Combine(_root, "release.zip");
        var result = await new ReleaseBundleService().CreateAsync(
            payload,
            archivePath,
            "test-client",
            "1.2.3");

        Assert.Equal("test-client", result.Manifest.ProductId);
        Assert.Equal(3, result.Manifest.Files.Count);
        Assert.True(result.Manifest.Files.Single(file =>
            file.Path == ".minecraft/config/user.toml").PreserveExisting);
        Assert.DoesNotContain(
            result.Manifest.Files,
            file => file.Path.Equals(
                "Launcher.exe",
                StringComparison.OrdinalIgnoreCase));
        Assert.Matches("^[A-F0-9]{64}$", result.Sha256);
        using var archive = ZipFile.OpenRead(archivePath);
        Assert.NotNull(archive.GetEntry("manifest.json"));
        Assert.NotNull(archive.GetEntry(
            "payload/.minecraft/配置/中文.txt"));
        Assert.NotNull(archive.GetEntry(
            "payload/LauncherConfig/launcher.json"));
        Assert.Null(archive.GetEntry("payload/Launcher.exe"));
        Assert.Null(archive.GetEntry(
            "payload/LauncherConfig/update.json"));
    }

    [Fact]
    public async Task Publisher_SignsArchiveUsingSelectedKeyFile()
    {
        Directory.CreateDirectory(_root);
        var archive = Path.Combine(_root, "release.zip");
        var bytes = Encoding.UTF8.GetBytes("release");
        await File.WriteAllBytesAsync(archive, bytes);
        var key = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var keyPath = Path.Combine(_root, "publisher.key");
        await File.WriteAllTextAsync(
            keyPath,
            Convert.ToBase64String(key));
        var handler = new DelegateHandler(async request =>
        {
            var uploaded = await request.Content!.ReadAsByteArrayAsync();
            Assert.Equal(bytes, uploaded);
            var timestamp = request.Headers.GetValues(
                "X-MCCP-Timestamp").Single();
            var nonce = request.Headers.GetValues("X-MCCP-Nonce").Single();
            var contentHash = request.Headers.GetValues(
                "X-MCCP-Content-SHA256").Single();
            var signature = request.Headers.GetValues(
                "X-MCCP-Signature").Single();
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(bytes)),
                contentHash);
            var expected = Convert.ToHexString(HMACSHA256.HashData(
                key,
                Encoding.ASCII.GetBytes(
                    $"{timestamp}\n{nonce}\n{contentHash}")));
            Assert.Equal(expected, signature);
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("{\"published\":true}")
            };
        });
        var reportedProgress = new List<PublishProgress>();

        var response = await new UpdatePublisherService(
                new HttpClient(handler))
            .PublishAsync(
                new Uri("https://updates.example/"),
                archive,
                keyPath,
                progress: new InlineProgress<PublishProgress>(
                    reportedProgress.Add));

        Assert.Contains("\"published\":true", response);
        Assert.Contains(
            reportedProgress,
            item => item.Stage == "Hashing");
        Assert.Contains(
            reportedProgress,
            item => item.Stage == "Uploaded" &&
                    item.ProcessedBytes == bytes.Length &&
                    item.TotalBytes == bytes.Length);
    }

    [Fact]
    public async Task Publisher_ReportsDetailedChineseNetworkFailure()
    {
        Directory.CreateDirectory(_root);
        var archive = Path.Combine(_root, "release.zip");
        await File.WriteAllTextAsync(archive, "release");
        var keyPath = Path.Combine(_root, "publisher.key");
        await File.WriteAllTextAsync(
            keyPath,
            Convert.ToBase64String(new byte[32]));
        var handler = new DelegateHandler(_ =>
            throw new HttpRequestException(
                "Error while copying content to a stream.",
                new IOException("connection reset by peer")));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => new UpdatePublisherService(
                    new HttpClient(handler))
                .PublishAsync(
                    new Uri("https://updates.example/"),
                    archive,
                    keyPath));

        Assert.Contains("MC 更新包上传", exception.Message);
        Assert.Contains("connection reset by peer", exception.Message);
        Assert.Contains("本机代理", exception.Message);
    }

    [Fact]
    public async Task Publisher_HealthCheckFailsBeforeUploadWithNetworkDetail()
    {
        var handler = new DelegateHandler(_ =>
            throw new HttpRequestException(
                "The SSL connection could not be established.",
                new IOException("connection reset")));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => new UpdatePublisherService(
                    new HttpClient(handler))
                .CheckServerHealthAsync(
                    new Uri("https://updates.example/")));

        Assert.Contains("上传尚未开始", exception.Message);
        Assert.Contains("connection reset", exception.Message);
        Assert.Contains("DNS", exception.Message);
    }

    [Fact]
    public async Task Publisher_ReadsPublishedVersionsAndPlansUploadsIndependently()
    {
        var handler = new DelegateHandler(request =>
        {
            Assert.Equal(
                HttpMethod.Get,
                request.Method);
            Assert.Equal(
                "/v1/products/test-client/manifest",
                request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "productId": "test-client",
                      "version": "2.3.2",
                      "launcher": {
                        "version": "0.1.2",
                        "size": 123,
                        "sha256": "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
                      }
                    }
                    """,
                    Encoding.UTF8,
                    "application/json")
            });
        });
        var publisher = new UpdatePublisherService(new HttpClient(handler));

        var published = await publisher.GetPublishedVersionsAsync(
            new Uri("https://updates.example/"),
            "test-client");
        var onlyLauncherChanged = UpdatePublisherService.CreatePublishPlan(
            "2.3.2",
            "0.1.3",
            published);
        var onlyClientChanged = UpdatePublisherService.CreatePublishPlan(
            "2.3.3",
            "0.1.2",
            published);
        var neitherChanged = UpdatePublisherService.CreatePublishPlan(
            "2.3.2",
            "0.1.2",
            published);

        Assert.Equal("2.3.2", published.ClientVersion);
        Assert.Equal("0.1.2", published.LauncherVersion);
        Assert.False(onlyLauncherChanged.PublishClient);
        Assert.True(onlyLauncherChanged.PublishLauncher);
        Assert.True(onlyClientChanged.PublishClient);
        Assert.False(onlyClientChanged.PublishLauncher);
        Assert.False(neitherChanged.HasChanges);
    }

    [Fact]
    public async Task Publisher_TreatsMissingServerReleaseAsBothUnpublished()
    {
        var handler = new DelegateHandler(_ =>
            Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.NotFound)));
        var publisher = new UpdatePublisherService(new HttpClient(handler));

        var published = await publisher.GetPublishedVersionsAsync(
            new Uri("https://updates.example/"),
            "test-client");
        var plan = UpdatePublisherService.CreatePublishPlan(
            "1.0.0",
            "1.0.0",
            published);

        Assert.Empty(published.ClientVersion);
        Assert.Empty(published.LauncherVersion);
        Assert.True(plan.PublishClient);
        Assert.True(plan.PublishLauncher);
    }

    [Fact]
    public async Task Publisher_TreatsExplicitUnpublished503AsEmptyProduct()
    {
        var handler = new DelegateHandler(_ =>
            Task.FromResult(
                new HttpResponseMessage(
                    HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent(
                        """{"error":"no release has been published"}""",
                        Encoding.UTF8,
                        "application/json")
                }));
        var publisher = new UpdatePublisherService(
            new HttpClient(handler));

        var published = await publisher.GetPublishedVersionsAsync(
            new Uri("https://updates.example/"),
            "test-client-test");

        Assert.Empty(published.ClientVersion);
        Assert.Empty(published.LauncherVersion);
    }

    [Fact]
    public async Task Publisher_DoesNotHideUnrelated503Failure()
    {
        var handler = new DelegateHandler(_ =>
            Task.FromResult(
                new HttpResponseMessage(
                    HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent(
                        """{"error":"maintenance"}""",
                        Encoding.UTF8,
                        "application/json")
                }));
        var publisher = new UpdatePublisherService(
            new HttpClient(handler));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            () => publisher.GetPublishedVersionsAsync(
                new Uri("https://updates.example/"),
                "test-client-test"));

        Assert.Contains("HTTP 503", exception.Message);
        Assert.Contains("maintenance", exception.Message);
    }

    [Fact]
    public async Task Publisher_UploadsSignedClientLaunchPolicy()
    {
        Directory.CreateDirectory(_root);
        var key = Enumerable.Range(0, 32)
            .Select(value => (byte)value)
            .ToArray();
        var keyPath = Path.Combine(_root, "publisher.key");
        await File.WriteAllTextAsync(
            keyPath,
            Convert.ToBase64String(key));
        var handler = new DelegateHandler(async request =>
        {
            Assert.Equal(
                "/v1/products/test-client/policy",
                request.RequestUri!.AbsolutePath);
            var body = await request.Content!.ReadAsByteArrayAsync();
            using var document = JsonDocument.Parse(body);
            Assert.True(
                document.RootElement.GetProperty("showMessage").GetBoolean());
            Assert.True(
                document.RootElement.GetProperty("blockLaunch").GetBoolean());
            Assert.Equal(
                "维护",
                document.RootElement.GetProperty("title").GetString());
            var contentHash = request.Headers.GetValues(
                "X-MCCP-Content-SHA256").Single();
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(body)),
                contentHash);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"updated\":true}")
            };
        });

        var response = await new UpdatePublisherService(
                new HttpClient(handler))
            .PublishPolicyAsync(
                new Uri("https://updates.example/"),
                "test-client",
                keyPath,
                new()
                {
                    ShowMessage = true,
                    Title = " 维护 ",
                    Message = "暂时停止启动",
                    BlockLaunch = true
                });

        Assert.Contains("\"updated\":true", response);
    }

    [Fact]
    public async Task Publisher_RejectsBlockingPolicyWithoutMessage()
    {
        Directory.CreateDirectory(_root);
        var keyPath = Path.Combine(_root, "publisher.key");
        await File.WriteAllTextAsync(
            keyPath,
            Convert.ToBase64String(new byte[32]));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new UpdatePublisherService(
                    new HttpClient(new DelegateHandler(_ =>
                        throw new InvalidOperationException(
                            "HTTP must not be called"))))
                .PublishPolicyAsync(
                    new Uri("https://updates.example/"),
                    "test-client",
                    keyPath,
                    new() { BlockLaunch = true }));
    }

    [Fact]
    public async Task Publisher_UploadsSignedLauncherInstallerAndVersion()
    {
        Directory.CreateDirectory(_root);
        var installer = Path.Combine(_root, "LauncherSetup.exe");
        var bytes = Encoding.UTF8.GetBytes("MZ-test-launcher-installer");
        await File.WriteAllBytesAsync(installer, bytes);
        var key = Enumerable.Range(0, 32)
            .Select(value => (byte)(255 - value))
            .ToArray();
        var keyPath = Path.Combine(_root, "publisher.key");
        await File.WriteAllTextAsync(
            keyPath,
            Convert.ToBase64String(key));
        var handler = new DelegateHandler(async request =>
        {
            Assert.Equal(
                "/v1/products/test-client/launcher",
                request.RequestUri!.AbsolutePath);
            Assert.Equal(
                "2.1.0",
                request.Headers.GetValues(
                    "X-MCCP-Launcher-Version").Single());
            var uploaded = await request.Content!.ReadAsByteArrayAsync();
            Assert.Equal(bytes, uploaded);
            var contentHash = request.Headers.GetValues(
                "X-MCCP-Content-SHA256").Single();
            var timestamp = request.Headers.GetValues(
                "X-MCCP-Timestamp").Single();
            var nonce = request.Headers.GetValues(
                "X-MCCP-Nonce").Single();
            var expectedSignature = Convert.ToHexString(
                HMACSHA256.HashData(
                    key,
                    Encoding.ASCII.GetBytes(
                        $"{timestamp}\n{nonce}\n{contentHash}")));
            Assert.Equal(
                Convert.ToHexString(SHA256.HashData(bytes)),
                contentHash);
            Assert.Equal(
                expectedSignature,
                request.Headers.GetValues(
                    "X-MCCP-Signature").Single());
            return new HttpResponseMessage(HttpStatusCode.Created)
            {
                Content = new StringContent("{\"published\":true}")
            };
        });

        var response = await new UpdatePublisherService(
                new HttpClient(handler))
            .PublishLauncherAsync(
                new Uri("https://updates.example/"),
                "test-client",
                installer,
                "2.1.0",
                keyPath);

        Assert.Contains("\"published\":true", response);
    }

    [Fact]
    public async Task ClientUpdater_DownloadsVerifiesAndThenRecognizesCurrentRelease()
    {
        var application = Path.Combine(_root, "application");
        Directory.CreateDirectory(application);
        var fileBytes = Encoding.UTF8.GetBytes("新的中文配置");
        var manifest = new UpdateManifest
        {
            ProductId = "test-client",
            ReleaseId = "1.0.0-test",
            Version = "1.0.0",
            PublishedAt = DateTimeOffset.UtcNow,
            Policy = new()
            {
                ShowMessage = true,
                Title = "服务器公告",
                Message = "维护中",
                BlockLaunch = true
            },
            Launcher = new()
            {
                Version = "1.0.0",
                Size = 123,
                Sha256 = new string('A', 64)
            },
            Files =
            [
                new()
                {
                    Path = "LauncherConfig/launcher.json",
                    Size = fileBytes.Length,
                    Sha256 = Convert.ToHexString(
                        SHA256.HashData(fileBytes))
                }
            ]
        };
        var fileRequests = 0;
        var handler = new DelegateHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(
                    "/manifest",
                    StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(
                            manifest,
                            new JsonSerializerOptions
                            {
                                PropertyNamingPolicy =
                                    JsonNamingPolicy.CamelCase
                            }),
                        Encoding.UTF8,
                        "application/json")
                });
            }

            fileRequests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(fileBytes)
            });
        });
        var updater = new ClientUpdateService(new HttpClient(handler));
        var bootstrap = new UpdateBootstrapConfig
        {
            ServerBaseUrl = "https://updates.example/",
            ProductId = "test-client",
            LauncherVersion = "1.0.0",
            RequireSuccessfulCheck = true
        };

        var first = await updater.CheckAndApplyAsync(
            application,
            bootstrap);
        var second = await updater.CheckAndApplyAsync(
            application,
            bootstrap);

        Assert.True(first.Updated);
        Assert.False(second.Updated);
        Assert.True(first.Policy.ShowMessage);
        Assert.True(first.Policy.BlockLaunch);
        Assert.Equal("维护中", first.Policy.Message);
        Assert.True(second.Policy.BlockLaunch);
        Assert.Equal(1, fileRequests);
        Assert.Equal(
            fileBytes,
            await File.ReadAllBytesAsync(Path.Combine(
                application,
                "LauncherConfig",
                "launcher.json")));
    }

    [Fact]
    public async Task ClientUpdater_SameVersionDoesNotScanOrRepairLocalFiles()
    {
        var application = Path.Combine(_root, "version-only-application");
        Directory.CreateDirectory(application);
        var fileBytes = Encoding.UTF8.GetBytes("managed payload");
        var manifest = new UpdateManifest
        {
            ProductId = "test-client",
            ReleaseId = "release-one",
            Version = "1.0.0",
            PublishedAt = DateTimeOffset.UtcNow,
            Launcher = new()
            {
                Version = "1.0.0",
                Size = 1,
                Sha256 = new string('A', 64)
            },
            Files =
            [
                new()
                {
                    Path = ".minecraft/mods/managed.jar",
                    Size = fileBytes.Length,
                    Sha256 = Convert.ToHexString(
                        SHA256.HashData(fileBytes))
                }
            ]
        };
        var fileRequests = 0;
        var handler = new DelegateHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(
                    "/manifest",
                    StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse(manifest));
            }

            Interlocked.Increment(ref fileRequests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(fileBytes)
            });
        });
        var updater = new ClientUpdateService(new HttpClient(handler));
        var bootstrap = new UpdateBootstrapConfig
        {
            ServerBaseUrl = "https://updates.example/",
            ProductId = "test-client",
            LauncherVersion = "1.0.0"
        };

        var first = await updater.CheckAndApplyAsync(
            application,
            bootstrap);
        var installed = Path.Combine(
            application,
            ".minecraft",
            "mods",
            "managed.jar");
        File.Delete(installed);
        manifest.ReleaseId = "release-two-with-the-same-version";
        var second = await updater.CheckAndApplyAsync(
            application,
            bootstrap);

        Assert.True(first.Updated);
        Assert.False(second.Updated);
        Assert.Equal(1, Volatile.Read(ref fileRequests));
        Assert.False(File.Exists(installed));
    }

    [Fact]
    public async Task ClientUpdater_NewVersionUsesManifestMetadataWithoutReadingUnchangedFile()
    {
        var application = Path.Combine(_root, "metadata-diff-application");
        Directory.CreateDirectory(application);
        var fileBytes = Encoding.UTF8.GetBytes("unchanged payload");
        var entry = new UpdateManifestEntry
        {
            Path = ".minecraft/mods/unchanged.jar",
            Size = fileBytes.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(fileBytes))
        };
        var manifest = new UpdateManifest
        {
            ProductId = "test-client",
            ReleaseId = "release-one",
            Version = "1.0.0",
            PublishedAt = DateTimeOffset.UtcNow,
            Launcher = new()
            {
                Version = "1.0.0",
                Size = 1,
                Sha256 = new string('A', 64)
            },
            Files = [entry]
        };
        var fileRequests = 0;
        var handler = new DelegateHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(
                    "/manifest",
                    StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse(manifest));
            }

            Interlocked.Increment(ref fileRequests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(fileBytes)
            });
        });
        var updater = new ClientUpdateService(new HttpClient(handler));
        var bootstrap = new UpdateBootstrapConfig
        {
            ServerBaseUrl = "https://updates.example/",
            ProductId = "test-client",
            LauncherVersion = "1.0.0"
        };

        await updater.CheckAndApplyAsync(application, bootstrap);
        var installed = Path.Combine(
            application,
            ".minecraft",
            "mods",
            "unchanged.jar");
        await using var lockStream = new FileStream(
            installed,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        manifest.ReleaseId = "release-two";
        manifest.Version = "2.0.0";
        var result = await updater.CheckAndApplyAsync(
            application,
            bootstrap);

        Assert.True(result.Updated);
        Assert.Equal(0, result.DownloadedFiles);
        Assert.Equal(0, result.DownloadedBytes);
        Assert.Equal(1, Volatile.Read(ref fileRequests));
    }

    [Fact]
    public async Task ClientUpdater_PausesBeforeFileRequestAndReportsChunkProgress()
    {
        var application = Path.Combine(_root, "paused-application");
        Directory.CreateDirectory(application);
        var fileBytes = RandomNumberGenerator.GetBytes(700 * 1024);
        var manifest = new UpdateManifest
        {
            ProductId = "test-client",
            ReleaseId = "paused-release",
            Version = "1.0.0",
            PublishedAt = DateTimeOffset.UtcNow,
            Launcher = new()
            {
                Version = "1.0.0",
                Size = 1,
                Sha256 = new string('A', 64)
            },
            Files =
            [
                new()
                {
                    Path = "files/large.bin",
                    Size = fileBytes.Length,
                    Sha256 = Convert.ToHexString(
                        SHA256.HashData(fileBytes))
                }
            ]
        };
        var manifestRequested = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var fileRequests = 0;
        var handler = new DelegateHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(
                    "/manifest",
                    StringComparison.Ordinal))
            {
                manifestRequested.TrySetResult();
                return Task.FromResult(JsonResponse(manifest));
            }

            Interlocked.Increment(ref fileRequests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(fileBytes)
            });
        });
        var reports = new List<UpdateProgress>();
        var pauseController = new DownloadPauseController();
        Assert.True(pauseController.Pause());
        var updateTask = new ClientUpdateService(new HttpClient(handler))
            .CheckAndApplyAsync(
                application,
                new()
                {
                    ServerBaseUrl = "https://updates.example/",
                    ProductId = "test-client",
                    LauncherVersion = "1.0.0"
                },
                new InlineProgress<UpdateProgress>(reports.Add),
                pauseController: pauseController);

        await manifestRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(100);
        Assert.Equal(0, Volatile.Read(ref fileRequests));
        Assert.True(pauseController.Resume());
        var result = await updateTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(result.Updated);
        Assert.Equal(1, Volatile.Read(ref fileRequests));
        Assert.Contains(
            reports,
            value => value.Stage == "Downloading" &&
                     value.CompletedBytes > 0 &&
                     value.CompletedBytes < value.TotalBytes);
        Assert.Equal(
            fileBytes.Length,
            reports
                .Where(value => value.Stage == "Downloading")
                .Max(value => value.CompletedBytes));
    }

    [Fact]
    public async Task ClientUpdater_CompletesAtManifestSizeWithoutWaitingForResponseEof()
    {
        var application = Path.Combine(
            _root,
            "non-terminating-response-application");
        Directory.CreateDirectory(application);
        var fileBytes = RandomNumberGenerator.GetBytes(512 * 1024);
        var manifest = new UpdateManifest
        {
            ProductId = "test-client",
            ReleaseId = "non-terminating-response-release",
            Version = "1.0.0",
            PublishedAt = DateTimeOffset.UtcNow,
            Launcher = new()
            {
                Version = "1.0.0",
                Size = 1,
                Sha256 = new string('A', 64)
            },
            Files =
            [
                new()
                {
                    Path = "files/payload.bin",
                    Size = fileBytes.Length,
                    Sha256 = Convert.ToHexString(
                        SHA256.HashData(fileBytes))
                }
            ]
        };
        var stream = new NonTerminatingReadStream(fileBytes);
        var handler = new DelegateHandler(request =>
            Task.FromResult(
                request.RequestUri!.AbsolutePath.EndsWith(
                    "/manifest",
                    StringComparison.Ordinal)
                    ? JsonResponse(manifest)
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StreamContent(stream)
                    }));
        var reports = new List<UpdateProgress>();

        var result = await new ClientUpdateService(new HttpClient(handler))
            .CheckAndApplyAsync(
                application,
                new()
                {
                    ServerBaseUrl = "https://updates.example/",
                    ProductId = "test-client",
                    LauncherVersion = "1.0.0"
                },
                new InlineProgress<UpdateProgress>(reports.Add))
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.Updated);
        Assert.False(stream.ReadAfterPayloadStarted.Task.IsCompleted);
        Assert.Equal(
            fileBytes,
            await File.ReadAllBytesAsync(Path.Combine(
                application,
                "files",
                "payload.bin")));
        var applyingIndex = reports.FindIndex(
            value => value.Stage == "Applying");
        var cleaningIndex = reports.FindIndex(
            value => value.Stage == "Cleaning");
        var completeIndex = reports.FindIndex(
            value => value.Stage == "Complete");
        Assert.True(applyingIndex >= 0);
        Assert.True(cleaningIndex > applyingIndex);
        Assert.True(completeIndex > cleaningIndex);
    }

    [Fact]
    public async Task ClientUpdater_RetriesFileWhenResponseStopsSendingData()
    {
        var application = Path.Combine(
            _root,
            "stalled-response-application");
        Directory.CreateDirectory(application);
        var fileBytes = RandomNumberGenerator.GetBytes(525095);
        var manifest = new UpdateManifest
        {
            ProductId = "test-client",
            ReleaseId = "stalled-response-release",
            Version = "1.0.0",
            PublishedAt = DateTimeOffset.UtcNow,
            Launcher = new()
            {
                Version = "1.0.0",
                Size = 1,
                Sha256 = new string('A', 64)
            },
            Files =
            [
                new()
                {
                    Path = "assets/objects/stalled-file",
                    Size = fileBytes.Length,
                    Sha256 = Convert.ToHexString(
                        SHA256.HashData(fileBytes))
                }
            ]
        };
        var fileRequests = 0;
        var handler = new DelegateHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(
                    "/manifest",
                    StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse(manifest));
            }

            var requestNumber = Interlocked.Increment(ref fileRequests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = requestNumber == 1
                    ? new StreamContent(new StalledReadStream())
                    : new ByteArrayContent(fileBytes)
            });
        });
        var reports = new List<UpdateProgress>();
        var updater = new ClientUpdateService(
            new HttpClient(handler),
            downloadInactivityTimeout: TimeSpan.FromMilliseconds(50),
            downloadAttempts: 3);

        var result = await updater.CheckAndApplyAsync(
                application,
                new()
                {
                    ServerBaseUrl = "https://updates.example/",
                    ProductId = "test-client",
                    LauncherVersion = "1.0.0"
                },
                new InlineProgress<UpdateProgress>(reports.Add))
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(result.Updated);
        Assert.Equal(2, Volatile.Read(ref fileRequests));
        Assert.Contains(
            reports,
            value => value.Message.Contains(
                "第 2 / 3 次尝试",
                StringComparison.Ordinal));
        Assert.Equal(
            fileBytes,
            await File.ReadAllBytesAsync(Path.Combine(
                application,
                "assets",
                "objects",
                "stalled-file")));
        Assert.Equal(
            fileBytes.Length,
            reports
                .Where(value => value.Stage == "Downloading")
                .Max(value => value.CompletedBytes));
    }

    [Fact]
    public async Task ClientUpdater_MidReadPauseStopsBeforeWritingNextChunk()
    {
        var application = Path.Combine(_root, "mid-read-pause-application");
        Directory.CreateDirectory(application);
        var fileBytes = RandomNumberGenerator.GetBytes(512 * 1024);
        var manifest = new UpdateManifest
        {
            ProductId = "test-client",
            ReleaseId = "mid-read-pause-release",
            Version = "1.0.0",
            PublishedAt = DateTimeOffset.UtcNow,
            Launcher = new()
            {
                Version = "1.0.0",
                Size = 1,
                Sha256 = new string('A', 64)
            },
            Files =
            [
                new()
                {
                    Path = "files/payload.bin",
                    Size = fileBytes.Length,
                    Sha256 = Convert.ToHexString(
                        SHA256.HashData(fileBytes))
                }
            ]
        };
        var stream = new GatedReadStream(fileBytes);
        var handler = new DelegateHandler(request =>
            Task.FromResult(
                request.RequestUri!.AbsolutePath.EndsWith(
                    "/manifest",
                    StringComparison.Ordinal)
                    ? JsonResponse(manifest)
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StreamContent(stream)
                    }));
        var reports = new List<UpdateProgress>();
        var pauseController = new DownloadPauseController();
        var updateTask = new ClientUpdateService(new HttpClient(handler))
            .CheckAndApplyAsync(
                application,
                new()
                {
                    ServerBaseUrl = "https://updates.example/",
                    ProductId = "test-client",
                    LauncherVersion = "1.0.0"
                },
                new InlineProgress<UpdateProgress>(reports.Add),
                pauseController: pauseController);

        await stream.FirstReadStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5));
        Assert.True(pauseController.Pause());
        stream.ReleaseFirstRead();
        await Task.Delay(100);

        var stagingFile = Directory
            .EnumerateFiles(
                Path.Combine(application, ".mccp-update"),
                "payload.bin",
                SearchOption.AllDirectories)
            .Single();
        Assert.Equal(0, new FileInfo(stagingFile).Length);
        Assert.DoesNotContain(
            reports,
            value => value.Stage == "Downloading" &&
                     value.CompletedBytes > 0);

        Assert.True(pauseController.Resume());
        var result = await updateTask.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(result.Updated);
        Assert.Equal(
            fileBytes,
            await File.ReadAllBytesAsync(Path.Combine(
                application,
                "files",
                "payload.bin")));
    }

    [Fact]
    public async Task ClientUpdater_CancellationWhilePausedCleansStaging()
    {
        var application = Path.Combine(_root, "paused-cancel-application");
        Directory.CreateDirectory(application);
        var fileBytes = Encoding.UTF8.GetBytes("payload");
        var manifest = new UpdateManifest
        {
            ProductId = "test-client",
            ReleaseId = "paused-cancel-release",
            Version = "1.0.0",
            PublishedAt = DateTimeOffset.UtcNow,
            Launcher = new()
            {
                Version = "1.0.0",
                Size = 1,
                Sha256 = new string('A', 64)
            },
            Files =
            [
                new()
                {
                    Path = "files/payload.bin",
                    Size = fileBytes.Length,
                    Sha256 = Convert.ToHexString(
                        SHA256.HashData(fileBytes))
                }
            ]
        };
        var reachedDownload = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new DelegateHandler(request =>
            Task.FromResult(
                request.RequestUri!.AbsolutePath.EndsWith(
                    "/manifest",
                    StringComparison.Ordinal)
                    ? JsonResponse(manifest)
                    : new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(fileBytes)
                    }));
        var pauseController = new DownloadPauseController();
        pauseController.Pause();
        using var cancellation = new CancellationTokenSource();
        var updateTask = new ClientUpdateService(new HttpClient(handler))
            .CheckAndApplyAsync(
                application,
                new()
                {
                    ServerBaseUrl = "https://updates.example/",
                    ProductId = "test-client",
                    LauncherVersion = "1.0.0"
                },
                new InlineProgress<UpdateProgress>(value =>
                {
                    if (value.Stage == "Downloading")
                    {
                        reachedDownload.TrySetResult();
                    }
                }),
                cancellation.Token,
                pauseController);

        await reachedDownload.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => updateTask);

        var metadataRoot = Path.Combine(application, ".mccp-update");
        Assert.Empty(
            Directory.Exists(metadataRoot)
                ? Directory.EnumerateDirectories(
                    metadataRoot,
                    "staging-*",
                    SearchOption.TopDirectoryOnly)
                : []);
    }

    [Fact]
    public async Task ClientUpdater_DownloadsFilesInParallelWithinConfiguredLimit()
    {
        var application = Path.Combine(_root, "parallel-application");
        Directory.CreateDirectory(application);
        var fileBytes = Encoding.UTF8.GetBytes("parallel payload");
        var files = Enumerable.Range(0, 24)
            .Select(index => new UpdateManifestEntry
            {
                Path = $"files/file-{index:D2}.bin",
                Size = fileBytes.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(fileBytes))
            })
            .ToList();
        var manifest = new UpdateManifest
        {
            ProductId = "test-client",
            ReleaseId = "1.0.0-parallel",
            Version = "1.0.0",
            PublishedAt = DateTimeOffset.UtcNow,
            Launcher = new()
            {
                Version = "1.0.0",
                Size = 1,
                Sha256 = new string('A', 64)
            },
            Files = files
        };
        var activeRequests = 0;
        var maximumRequests = 0;
        var atLeastTwoRequests = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new DelegateHandler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(
                    "/manifest",
                    StringComparison.Ordinal))
            {
                return JsonResponse(manifest);
            }

            var active = Interlocked.Increment(ref activeRequests);
            var observedMaximum = Volatile.Read(ref maximumRequests);
            while (active > observedMaximum)
            {
                var previous = Interlocked.CompareExchange(
                    ref maximumRequests,
                    active,
                    observedMaximum);
                if (previous == observedMaximum)
                {
                    break;
                }

                observedMaximum = previous;
            }

            if (active >= 2)
            {
                atLeastTwoRequests.TrySetResult();
            }

            try
            {
                await atLeastTwoRequests.Task.WaitAsync(
                    TimeSpan.FromSeconds(5));
                await Task.Delay(20);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(fileBytes)
                };
            }
            finally
            {
                Interlocked.Decrement(ref activeRequests);
            }
        });
        var updater = new ClientUpdateService(new HttpClient(handler));

        var result = await updater.CheckAndApplyAsync(
            application,
            new()
            {
                ServerBaseUrl = "https://updates.example/",
                ProductId = "test-client",
                LauncherVersion = "1.0.0",
                DownloadConcurrency = 8
            });

        Assert.True(result.Updated);
        Assert.Equal(files.Count, result.DownloadedFiles);
        Assert.Equal(files.Count * fileBytes.Length, result.DownloadedBytes);
        Assert.InRange(Volatile.Read(ref maximumRequests), 2, 8);
        foreach (var file in files)
        {
            Assert.Equal(
                fileBytes,
                await File.ReadAllBytesAsync(Path.Combine(
                    application,
                    file.Path.Replace('/', Path.DirectorySeparatorChar))));
        }
    }

    [Fact]
    public async Task ClientUpdater_IgnoresBundleAndDownloadsIndividualFiles()
    {
        var application = Path.Combine(
            _root,
            "segmented-bundle-application");
        Directory.CreateDirectory(application);
        var smallBytes = Encoding.UTF8.GetBytes(
            "first file is extracted before later segments finish");
        var fileBytes = RandomNumberGenerator.GetBytes(
            256 * 1024);
        const string smallRelativePath =
            ".minecraft/config/first.toml";
        const string relativePath =
            ".minecraft/versions/test/large-client.bin";
        var bundleBytes = RandomNumberGenerator.GetBytes(1024);
        var manifest = new UpdateManifest
        {
            ProductId = "test-client",
            ReleaseId = "streaming-release",
            Version = "1.0.0",
            PublishedAt = DateTimeOffset.UtcNow,
            Launcher = new()
            {
                Version = "1.0.0",
                Size = 1,
                Sha256 = new string('A', 64)
            },
            Bundle = new()
            {
                Format = "tar.gz",
                Size = bundleBytes.Length,
                Sha256 = Convert.ToHexString(
                    SHA256.HashData(bundleBytes))
            },
            Files =
            [
                new()
                {
                    Path = smallRelativePath,
                    Size = smallBytes.Length,
                    Sha256 = Convert.ToHexString(
                        SHA256.HashData(smallBytes))
                },
                new()
                {
                    Path = relativePath,
                    Size = fileBytes.Length,
                    Sha256 = Convert.ToHexString(
                        SHA256.HashData(fileBytes))
                }
            ]
        };
        var rangeRequests = 0;
        var fileRequests = 0;
        var activeRanges = 0;
        var maximumRanges = 0;
        var reports = new List<UpdateProgress>();
        var firstFileExtracted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new DelegateHandler(async request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(
                    "/manifest",
                    StringComparison.Ordinal))
            {
                return JsonResponse(manifest);
            }

            if (request.RequestUri.AbsolutePath.Contains(
                    "/v1/files/",
                    StringComparison.Ordinal))
            {
                Interlocked.Increment(ref fileRequests);
                var responseBytes = request.RequestUri.AbsolutePath.EndsWith(
                    "/first.toml",
                    StringComparison.Ordinal)
                    ? smallBytes
                    : fileBytes;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(responseBytes)
                };
            }

            Assert.EndsWith(
                "/bundle.tar.gz",
                request.RequestUri.AbsolutePath,
                StringComparison.Ordinal);
            var requestedRange = Assert.Single(
                request.Headers.Range!.Ranges);
            var start = requestedRange.From!.Value;
            var end = requestedRange.To!.Value;
            Interlocked.Increment(ref rangeRequests);
            var active = Interlocked.Increment(ref activeRanges);
            var observed = Volatile.Read(ref maximumRanges);
            while (active > observed)
            {
                var previous = Interlocked.CompareExchange(
                    ref maximumRanges,
                    active,
                    observed);
                if (previous == observed)
                {
                    break;
                }

                observed = previous;
            }

            try
            {
                if (start == 0)
                {
                    await Task.Delay(30);
                }
                else
                {
                    await firstFileExtracted.Task.WaitAsync(
                        TimeSpan.FromSeconds(5));
                }

                var length = checked((int)(end - start + 1));
                var content = new ByteArrayContent(
                    bundleBytes,
                    checked((int)start),
                    length);
                content.Headers.ContentRange = new(
                    start,
                    end,
                    bundleBytes.Length);
                return new HttpResponseMessage(
                    HttpStatusCode.PartialContent)
                {
                    Content = content
                };
            }
            finally
            {
                Interlocked.Decrement(ref activeRanges);
            }
        });

        var result = await new ClientUpdateService(
                new HttpClient(handler))
            .CheckAndApplyAsync(
                application,
                new()
                {
                    ServerBaseUrl = "https://updates.example/",
                    ProductId = "test-client",
                    LauncherVersion = "1.0.0",
                    DownloadConcurrency = 4
                },
                new InlineProgress<UpdateProgress>(value =>
                {
                    reports.Add(value);
                    if (value.Message.Equals(
                            $"解压完成：{smallRelativePath}",
                            StringComparison.Ordinal))
                    {
                        firstFileExtracted.TrySetResult();
                    }
                }));

        Assert.True(result.Updated);
        Assert.Equal(2, Volatile.Read(ref fileRequests));
        Assert.Equal(0, Volatile.Read(ref rangeRequests));
        Assert.Equal(0, Volatile.Read(ref maximumRanges));
        Assert.Equal(
            smallBytes.Length + fileBytes.Length,
            result.DownloadedBytes);
        Assert.False(firstFileExtracted.Task.IsCompleted);
        Assert.Contains(
            reports,
            value => value.Message.Contains(
                "下载完成",
                StringComparison.Ordinal));
        Assert.Equal(
            smallBytes,
            await File.ReadAllBytesAsync(Path.Combine(
                application,
                smallRelativePath.Replace(
                    '/',
                    Path.DirectorySeparatorChar))));
        Assert.Equal(
            fileBytes,
            await File.ReadAllBytesAsync(Path.Combine(
                application,
                relativePath.Replace(
                    '/',
                    Path.DirectorySeparatorChar))));
    }

    [Fact]
    public async Task ClientUpdater_BadIndividualFileDoesNotCommitFiles()
    {
        var application = Path.Combine(
            _root,
            "bad-individual-file-application");
        Directory.CreateDirectory(application);
        var fileBytes = Encoding.UTF8.GetBytes(
            "expected file content");
        const string relativePath = "files/payload.bin";
        var invalidFileBytes = Encoding.UTF8.GetBytes(
            "invalid response body with a different length");
        var manifest = new UpdateManifest
        {
            ProductId = "test-client",
            ReleaseId = "bad-individual-file",
            Version = "1.0.0",
            Launcher = new()
            {
                Version = "1.0.0",
                Size = 1,
                Sha256 = new string('A', 64)
            },
            Files =
            [
                new()
                {
                    Path = relativePath,
                    Size = fileBytes.Length,
                    Sha256 = Convert.ToHexString(
                        SHA256.HashData(fileBytes))
                }
            ]
        };
        var handler = new DelegateHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(
                    "/manifest",
                    StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse(manifest));
            }

            return Task.FromResult(new HttpResponseMessage(
                HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(invalidFileBytes)
            });
        });

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => new ClientUpdateService(new HttpClient(handler))
                .CheckAndApplyAsync(
                    application,
                    new()
                    {
                        ServerBaseUrl = "https://updates.example/",
                        ProductId = "test-client",
                        LauncherVersion = "1.0.0"
                    }));

        Assert.Contains("大小不匹配", exception.Message);
        Assert.False(File.Exists(Path.Combine(
            application,
            "files",
            "payload.bin")));
        Assert.False(File.Exists(Path.Combine(
            application,
            ".mccp-update",
            "state.json")));
        Assert.Empty(
            Directory.Exists(Path.Combine(
                application,
                ".mccp-update"))
                ? Directory.EnumerateDirectories(
                    Path.Combine(application, ".mccp-update"),
                    "staging-*",
                    SearchOption.TopDirectoryOnly)
                : []);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientUpdater_ParallelFailureDoesNotCommitOrLeaveStaging(
        bool failWithHttpStatus)
    {
        var application = Path.Combine(
            _root,
            failWithHttpStatus
                ? "parallel-http-failure"
                : "parallel-hash-failure");
        Directory.CreateDirectory(Path.Combine(application, "files"));
        var expectedByPath = new Dictionary<string, byte[]>(
            StringComparer.OrdinalIgnoreCase);
        var originalByPath = new Dictionary<string, byte[]>(
            StringComparer.OrdinalIgnoreCase);
        var manifest = new UpdateManifest
        {
            ProductId = "test-client",
            ReleaseId = "1.0.0-failure",
            Version = "1.0.0",
            PublishedAt = DateTimeOffset.UtcNow,
            Launcher = new()
            {
                Version = "1.0.0",
                Size = 1,
                Sha256 = new string('A', 64)
            }
        };
        for (var index = 0; index < 8; index++)
        {
            var relative = $"files/file-{index}.txt";
            var expected = Encoding.UTF8.GetBytes($"new-{index}");
            var original = Encoding.UTF8.GetBytes($"old-{index}");
            expectedByPath.Add(relative, expected);
            originalByPath.Add(relative, original);
            manifest.Files.Add(new()
            {
                Path = relative,
                Size = expected.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(expected))
            });
            await File.WriteAllBytesAsync(
                Path.Combine(
                    application,
                    relative.Replace('/', Path.DirectorySeparatorChar)),
                original);
        }

        var handler = new DelegateHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(
                    "/manifest",
                    StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse(manifest));
            }

            var relative = string.Join(
                "/",
                request.RequestUri.Segments
                    .TakeLast(2)
                    .Select(segment => Uri.UnescapeDataString(
                        segment.Trim('/'))));
            if (relative.Equals(
                    "files/file-3.txt",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (failWithHttpStatus)
                {
                    return Task.FromResult(
                        new HttpResponseMessage(
                            HttpStatusCode.ServiceUnavailable));
                }

                return Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent(
                            Encoding.UTF8.GetBytes("bad-3"))
                    });
            }

            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(expectedByPath[relative])
                });
        });
        var updater = new ClientUpdateService(new HttpClient(handler));

        var exception = await Record.ExceptionAsync(() =>
            updater.CheckAndApplyAsync(
                application,
                new()
                {
                    ServerBaseUrl = "https://updates.example/",
                    ProductId = "test-client",
                    LauncherVersion = "1.0.0",
                    DownloadConcurrency = 8
                }));

        Assert.NotNull(exception);
        foreach (var pair in originalByPath)
        {
            Assert.Equal(
                pair.Value,
                await File.ReadAllBytesAsync(Path.Combine(
                    application,
                    pair.Key.Replace(
                        '/',
                        Path.DirectorySeparatorChar))));
        }

        var metadataRoot = Path.Combine(application, ".mccp-update");
        Assert.False(File.Exists(Path.Combine(metadataRoot, "state.json")));
        Assert.Empty(
            Directory.Exists(metadataRoot)
                ? Directory.EnumerateDirectories(
                    metadataRoot,
                    "staging-*",
                    SearchOption.TopDirectoryOnly)
                : []);
    }

    [Fact]
    public async Task ClientUpdater_DownloadsNewLauncherBeforeMinecraftFiles()
    {
        var application = Path.Combine(_root, "application");
        var downloadRoot = Path.Combine(_root, "launcher-downloads");
        Directory.CreateDirectory(application);
        var launcherBytes =
            Encoding.UTF8.GetBytes("MZ-new-launcher-installer");
        var minecraftBytes = Encoding.UTF8.GetBytes("new minecraft");
        var manifest = new UpdateManifest
        {
            ProductId = "test-client",
            ReleaseId = "2.0.0-test",
            Version = "2.0.0",
            PublishedAt = DateTimeOffset.UtcNow,
            Launcher = new()
            {
                Version = "2.0.0",
                Size = launcherBytes.Length,
                Sha256 = Convert.ToHexString(
                    SHA256.HashData(launcherBytes))
            },
            Files =
            [
                new()
                {
                    Path = ".minecraft/versions/test/test.jar",
                    Size = minecraftBytes.Length,
                    Sha256 = Convert.ToHexString(
                        SHA256.HashData(minecraftBytes))
                }
            ]
        };
        var minecraftDownloadRequests = 0;
        var handler = new DelegateHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(
                    "/manifest",
                    StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse(manifest));
            }

            if (request.RequestUri.AbsolutePath.EndsWith(
                    "/setup.exe",
                    StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(
                    HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(launcherBytes)
                });
            }

            minecraftDownloadRequests++;
            return Task.FromResult(new HttpResponseMessage(
                HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(minecraftBytes)
            });
        });
        var updater = new ClientUpdateService(
            new HttpClient(handler),
            downloadRoot);

        var result = await updater.CheckAndApplyAsync(
            application,
            new()
            {
                ServerBaseUrl = "https://updates.example/",
                ProductId = "test-client",
                LauncherVersion = "1.0.0"
            });

        Assert.NotNull(result.LauncherUpdate);
        Assert.Equal("2.0.0", result.LauncherUpdate.Version);
        Assert.Equal(0, minecraftDownloadRequests);
        Assert.Equal(
            launcherBytes,
            await File.ReadAllBytesAsync(
                result.LauncherInstallerPath));
        Assert.False(File.Exists(Path.Combine(
            application,
            ".minecraft",
            "versions",
            "test",
            "test.jar")));
    }

    [Fact]
    public async Task ClientUpdater_PreservesExistingUserDataDuringMcUpdate()
    {
        var application = Path.Combine(_root, "application");
        var configPath = Path.Combine(
            application,
            ".minecraft",
            "config",
            "user.toml");
        var savePath = Path.Combine(
            application,
            ".minecraft",
            "saves",
            "世界",
            "level.dat");
        var modPath = Path.Combine(
            application,
            ".minecraft",
            "mods",
            "core.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(savePath)!);
        Directory.CreateDirectory(Path.GetDirectoryName(modPath)!);
        await File.WriteAllTextAsync(configPath, "用户自己的配置");
        await File.WriteAllTextAsync(savePath, "用户存档");
        await File.WriteAllTextAsync(modPath, "old mod");
        var newConfig = Encoding.UTF8.GetBytes("server config");
        var newMod = Encoding.UTF8.GetBytes("new mod");
        var manifest = new UpdateManifest
        {
            ProductId = "test-client",
            ReleaseId = "2.2.0-test",
            Version = "2.2.0",
            PublishedAt = DateTimeOffset.UtcNow,
            Launcher = new()
            {
                Version = "1.0.0",
                Size = 1,
                Sha256 = new string('B', 64)
            },
            Files =
            [
                new()
                {
                    Path = ".minecraft/config/user.toml",
                    Size = newConfig.Length,
                    Sha256 = Convert.ToHexString(
                        SHA256.HashData(newConfig)),
                    PreserveExisting = true
                },
                new()
                {
                    Path = ".minecraft/mods/core.jar",
                    Size = newMod.Length,
                    Sha256 = Convert.ToHexString(
                        SHA256.HashData(newMod))
                }
            ]
        };
        var configDownloads = 0;
        var modDownloads = 0;
        var handler = new DelegateHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(
                    "/manifest",
                    StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse(manifest));
            }

            if (request.RequestUri.AbsolutePath.EndsWith(
                    "user.toml",
                    StringComparison.Ordinal))
            {
                configDownloads++;
                return Task.FromResult(new HttpResponseMessage(
                    HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(newConfig)
                });
            }

            modDownloads++;
            return Task.FromResult(new HttpResponseMessage(
                HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(newMod)
            });
        });
        var updater = new ClientUpdateService(new HttpClient(handler));
        var bootstrap = new UpdateBootstrapConfig
        {
            ServerBaseUrl = "https://updates.example/",
            ProductId = "test-client",
            LauncherVersion = "1.0.0"
        };

        var first = await updater.CheckAndApplyAsync(
            application,
            bootstrap);
        var second = await updater.CheckAndApplyAsync(
            application,
            bootstrap);

        Assert.True(first.Updated);
        Assert.False(second.Updated);
        Assert.Equal(0, configDownloads);
        Assert.Equal(1, modDownloads);
        Assert.Equal(
            "用户自己的配置",
            await File.ReadAllTextAsync(configPath));
        Assert.Equal(
            "用户存档",
            await File.ReadAllTextAsync(savePath));
        Assert.Equal(
            "new mod",
            await File.ReadAllTextAsync(modPath));
    }

    [Fact]
    public async Task ClientUpdater_DoesNotDeleteProtectedFileRemovedFromRelease()
    {
        var application = Path.Combine(_root, "application");
        Directory.CreateDirectory(application);
        var configBytes = Encoding.UTF8.GetBytes("default config");
        var firstMod = Encoding.UTF8.GetBytes("mod v1");
        var secondMod = Encoding.UTF8.GetBytes("mod v2");
        static UpdateManifestEntry Entry(
            string path,
            byte[] bytes,
            bool preserve = false) =>
            new()
            {
                Path = path,
                Size = bytes.Length,
                Sha256 = Convert.ToHexString(
                    SHA256.HashData(bytes)),
                PreserveExisting = preserve
            };
        var launcher = new LauncherPackageInfo
        {
            Version = "1.0.0",
            Size = 1,
            Sha256 = new string('C', 64)
        };
        var firstManifest = new UpdateManifest
        {
            ProductId = "test-client",
            ReleaseId = "1.0.0-test",
            Version = "1.0.0",
            PublishedAt = DateTimeOffset.UtcNow,
            Launcher = launcher,
            Files =
            [
                Entry(
                    ".minecraft/config/user.toml",
                    configBytes,
                    preserve: true),
                Entry(".minecraft/mods/core.jar", firstMod)
            ]
        };
        var secondManifest = new UpdateManifest
        {
            ProductId = "test-client",
            ReleaseId = "2.0.0-test",
            Version = "2.0.0",
            PublishedAt = DateTimeOffset.UtcNow,
            Launcher = launcher,
            Files =
            [
                Entry(".minecraft/mods/core.jar", secondMod)
            ]
        };
        var currentManifest = firstManifest;
        var handler = new DelegateHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith(
                    "/manifest",
                    StringComparison.Ordinal))
            {
                return Task.FromResult(
                    JsonResponse(currentManifest));
            }

            var bytes = request.RequestUri.AbsolutePath.EndsWith(
                "user.toml",
                StringComparison.Ordinal)
                ? configBytes
                : currentManifest.ReleaseId.StartsWith(
                    "1.",
                    StringComparison.Ordinal)
                    ? firstMod
                    : secondMod;
            return Task.FromResult(new HttpResponseMessage(
                HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            });
        });
        var updater = new ClientUpdateService(new HttpClient(handler));
        var bootstrap = new UpdateBootstrapConfig
        {
            ServerBaseUrl = "https://updates.example/",
            ProductId = "test-client",
            LauncherVersion = "1.0.0"
        };

        await updater.CheckAndApplyAsync(application, bootstrap);
        var configPath = Path.Combine(
            application,
            ".minecraft",
            "config",
            "user.toml");
        await File.WriteAllTextAsync(configPath, "用户修改");
        currentManifest = secondManifest;
        await updater.CheckAndApplyAsync(application, bootstrap);

        Assert.True(File.Exists(configPath));
        Assert.Equal(
            "用户修改",
            await File.ReadAllTextAsync(configPath));
        Assert.Equal(
            "mod v2",
            await File.ReadAllTextAsync(Path.Combine(
                application,
                ".minecraft",
                "mods",
                "core.jar")));
    }

    [Fact]
    public async Task ClientUpdater_BlocksWhenManifestCheckFails()
    {
        Directory.CreateDirectory(_root);
        var handler = new DelegateHandler(_ => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("offline")
            }));
        var updater = new ClientUpdateService(new HttpClient(handler));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            updater.CheckAndApplyAsync(
                _root,
                new()
                {
                    ServerBaseUrl = "https://updates.example/",
                    ProductId = "test-client",
                    RequireSuccessfulCheck = true
                }));
    }

    [Fact]
    public async Task ClientUpdater_RejectsInvalidServerClientVersion()
    {
        Directory.CreateDirectory(_root);
        var bytes = Encoding.UTF8.GetBytes("payload");
        var manifest = new UpdateManifest
        {
            ProductId = "test-client",
            ReleaseId = "invalid-version-release",
            Version = "1.0.0 ",
            Launcher = new()
            {
                Version = "1.0.0",
                Size = 1,
                Sha256 = new string('A', 64)
            },
            Files =
            [
                new()
                {
                    Path = "payload.bin",
                    Size = bytes.Length,
                    Sha256 = Convert.ToHexString(SHA256.HashData(bytes))
                }
            ]
        };
        var handler = new DelegateHandler(_ =>
            Task.FromResult(JsonResponse(manifest)));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new ClientUpdateService(new HttpClient(handler))
                .CheckAndApplyAsync(
                    _root,
                    new()
                    {
                        ServerBaseUrl = "https://updates.example/",
                        ProductId = "test-client",
                        LauncherVersion = "1.0.0"
                    }));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("folder\\escape.txt")]
    [InlineData("folder//file.txt")]
    public void ReleasePathValidation_RejectsUnsafePaths(string path)
    {
        Assert.Throws<InvalidDataException>(
            () => ReleaseBundleService.EnsureSafeRelativePath(path));
    }

    [Fact]
    public void Publisher_RejectsNonHttpsServer()
    {
        Assert.Throws<InvalidDataException>(() =>
            UpdatePublisherService.ValidateServerUri(
                new Uri("http://updates.example/")));
    }

    [Fact]
    public void Publisher_AcceptsConsistentBuiltLauncherVersion()
    {
        var files = CreateBuiltLauncherVersionFiles(
            "0.1.2",
            "0.1.2");

        UpdatePublisherService.ValidateBuiltLauncherPackage(
            files.Installer,
            files.Bootstrap,
            files.Script,
            "0.1.2");
    }

    [Fact]
    public void Publisher_RejectsStaleBootstrapBeforeUpload()
    {
        var files = CreateBuiltLauncherVersionFiles(
            "0.1.1",
            "0.1.2");

        var exception = Assert.Throws<InvalidDataException>(() =>
            UpdatePublisherService.ValidateBuiltLauncherPackage(
                files.Installer,
                files.Bootstrap,
                files.Script,
                "0.1.2"));

        Assert.Contains("当前填写 0.1.2", exception.Message);
        Assert.Contains("update.json 内为 0.1.1", exception.Message);
        Assert.Contains("禁止上传旧安装包", exception.Message);
    }

    [Fact]
    public void Publisher_RejectsStaleInstallerScriptBeforeUpload()
    {
        var files = CreateBuiltLauncherVersionFiles(
            "0.1.2",
            "0.1.1");

        var exception = Assert.Throws<InvalidDataException>(() =>
            UpdatePublisherService.ValidateBuiltLauncherPackage(
                files.Installer,
                files.Bootstrap,
                files.Script,
                "0.1.2"));

        Assert.Contains("setup.iss 内为 0.1.1", exception.Message);
        Assert.Contains("重新点击“开始打包”", exception.Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, true);
        }
    }

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> callback)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => callback(request);
    }

    private sealed class InlineProgress<T>(Action<T> report)
        : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class GatedReadStream(byte[] bytes) : Stream
    {
        private readonly TaskCompletionSource _releaseFirstRead = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _position;
        private bool _firstRead = true;

        public TaskCompletionSource FirstReadStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public void ReleaseFirstRead() =>
            _releaseFirstRead.TrySetResult();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_firstRead)
            {
                _firstRead = false;
                FirstReadStarted.TrySetResult();
                await _releaseFirstRead.Task.WaitAsync(cancellationToken);
            }

            if (_position >= bytes.Length)
            {
                return 0;
            }

            var count = Math.Min(
                buffer.Length,
                bytes.Length - _position);
            bytes.AsMemory(_position, count).CopyTo(buffer);
            _position += count;
            return count;
        }

        public override void Flush()
        {
        }

        public override int Read(
            byte[] buffer,
            int offset,
            int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(
            byte[] buffer,
            int offset,
            int count) =>
            throw new NotSupportedException();
    }

    private sealed class NonTerminatingReadStream(byte[] bytes) : Stream
    {
        private int _position;

        public TaskCompletionSource ReadAfterPayloadStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_position < bytes.Length)
            {
                var count = Math.Min(
                    buffer.Length,
                    bytes.Length - _position);
                bytes.AsMemory(_position, count).CopyTo(buffer);
                _position += count;
                return count;
            }

            ReadAfterPayloadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override void Flush()
        {
        }

        public override int Read(
            byte[] buffer,
            int offset,
            int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(
            byte[] buffer,
            int offset,
            int count) =>
            throw new NotSupportedException();
    }

    private sealed class StalledReadStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;

        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override void Flush()
        {
        }

        public override int Read(
            byte[] buffer,
            int offset,
            int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(
            byte[] buffer,
            int offset,
            int count) =>
            throw new NotSupportedException();
    }

    private static byte[] CreateTarGzipBundle(
        IReadOnlyDictionary<string, byte[]> files)
    {
        using var output = new MemoryStream();
        using (var compressed = new GZipStream(
                   output,
                   CompressionLevel.Fastest,
                   leaveOpen: true))
        using (var archive = new TarWriter(
                   compressed,
                   TarEntryFormat.Pax,
                   leaveOpen: true))
        {
            foreach (var file in files)
            {
                using var content = new MemoryStream(
                    file.Value,
                    writable: false);
                var entry = new PaxTarEntry(
                    TarEntryType.RegularFile,
                    "payload/" + file.Key)
                {
                    DataStream = content,
                    ModificationTime = DateTimeOffset.UnixEpoch
                };
                archive.WriteEntry(entry);
            }
        }

        return output.ToArray();
    }

    private static HttpResponseMessage JsonResponse(object value) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(
                    value,
                    new JsonSerializerOptions
                    {
                        PropertyNamingPolicy =
                            JsonNamingPolicy.CamelCase
                    }),
                Encoding.UTF8,
                "application/json")
        };

    private (string Installer, string Bootstrap, string Script)
        CreateBuiltLauncherVersionFiles(
            string bootstrapVersion,
            string scriptVersion)
    {
        var directory = Path.Combine(
            _root,
            "built-launcher-" + Guid.NewGuid().ToString("N"));
        var bootstrapDirectory = Path.Combine(
            directory,
            "BootstrapPayload",
            "LauncherConfig");
        var installerSourceDirectory = Path.Combine(
            directory,
            "InstallerSource");
        Directory.CreateDirectory(bootstrapDirectory);
        Directory.CreateDirectory(installerSourceDirectory);
        var installer = Path.Combine(directory, "setup.exe");
        var bootstrap = Path.Combine(bootstrapDirectory, "update.json");
        var script = Path.Combine(installerSourceDirectory, "setup.iss");
        File.WriteAllBytes(installer, [0x4d, 0x5a]);
        File.WriteAllText(
            bootstrap,
            JsonSerializer.Serialize(
                new UpdateBootstrapConfig
                {
                    LauncherVersion = bootstrapVersion
                }));
        File.WriteAllText(
            script,
            $"[Setup]{Environment.NewLine}AppVersion={scriptVersion}" +
            Environment.NewLine);
        return (installer, bootstrap, script);
    }
}
