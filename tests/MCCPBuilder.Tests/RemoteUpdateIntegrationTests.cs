using MCCPBuilder.Core;

namespace MCCPBuilder.Tests;

public sealed class RemoteUpdateIntegrationTests
{
    [Fact]
    public async Task ClientReceivesBlockingNoticeAndPolicyCanBeCleared()
    {
        var serverText = Environment.GetEnvironmentVariable(
            "MCCP_TEST_UPDATE_SERVER");
        var keyPath = Environment.GetEnvironmentVariable(
            "MCCP_TEST_PUBLISHER_KEY");
        if (string.IsNullOrWhiteSpace(serverText) ||
            string.IsNullOrWhiteSpace(keyPath))
        {
            return;
        }

        var server = new Uri(serverText);
        var publisher = new UpdatePublisherService();
        var root = Path.Combine(
            Path.GetTempPath(),
            "MCCPRemotePolicyTest",
            Guid.NewGuid().ToString("N"));
        try
        {
            await publisher.PublishPolicyAsync(
                server,
                "mccp-smoke-test",
                keyPath,
                new()
                {
                    ShowMessage = true,
                    Title = "维护公告",
                    Message = "测试期间禁止启动",
                    BlockLaunch = true
                });
            Directory.CreateDirectory(root);
            var result = await new ClientUpdateService().CheckAndApplyAsync(
                root,
                new()
                {
                    ServerBaseUrl = serverText,
                    ProductId = "mccp-smoke-test",
                    RequireSuccessfulCheck = true,
                    RequireLauncherUpdateCheck = false
                });

            Assert.True(result.Policy.ShowMessage);
            Assert.Equal("维护公告", result.Policy.Title);
            Assert.Equal("测试期间禁止启动", result.Policy.Message);
            Assert.True(result.Policy.BlockLaunch);
        }
        finally
        {
            await publisher.PublishPolicyAsync(
                server,
                "mccp-smoke-test",
                keyPath,
                new());
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task PublishRejectsWrongKey_WhenRemoteEnvironmentIsConfigured()
    {
        var serverText = Environment.GetEnvironmentVariable(
            "MCCP_TEST_UPDATE_SERVER");
        if (string.IsNullOrWhiteSpace(serverText))
        {
            return;
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            "MCCPRemoteWrongKeyTest",
            Guid.NewGuid().ToString("N"));
        try
        {
            var payload = Path.Combine(root, "payload");
            Directory.CreateDirectory(payload);
            await File.WriteAllTextAsync(
                Path.Combine(payload, "probe.txt"),
                "must not publish");
            var bundle = await new ReleaseBundleService().CreateAsync(
                payload,
                Path.Combine(root, "release.zip"),
                "mccp-rejected-test",
                "1.0.0");
            var wrongKey = Path.Combine(root, "wrong.key");
            await File.WriteAllTextAsync(
                wrongKey,
                Convert.ToBase64String(
                    System.Security.Cryptography.RandomNumberGenerator
                        .GetBytes(32)));

            var exception = await Assert.ThrowsAsync<HttpRequestException>(
                () => new UpdatePublisherService().PublishAsync(
                    new Uri(serverText),
                    bundle.ArchivePath,
                    wrongKey));
            Assert.Equal(
                System.Net.HttpStatusCode.Unauthorized,
                exception.StatusCode);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }

    [Fact]
    public async Task PublishAndDownload_WhenRemoteEnvironmentIsConfigured()
    {
        var serverText = Environment.GetEnvironmentVariable(
            "MCCP_TEST_UPDATE_SERVER");
        var keyPath = Environment.GetEnvironmentVariable(
            "MCCP_TEST_PUBLISHER_KEY");
        if (string.IsNullOrWhiteSpace(serverText) ||
            string.IsNullOrWhiteSpace(keyPath))
        {
            return;
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            "MCCPRemoteUpdateTest",
            Guid.NewGuid().ToString("N"));
        try
        {
            var payload = Path.Combine(root, "payload");
            Directory.CreateDirectory(
                Path.Combine(payload, "LauncherConfig"));
            var expected = "远程发布验证 " + Guid.NewGuid().ToString("N");
            await File.WriteAllTextAsync(
                Path.Combine(payload, "LauncherConfig", "smoke-test.txt"),
                expected);
            var bundle = await new ReleaseBundleService().CreateAsync(
                payload,
                Path.Combine(root, "release.zip"),
                "mccp-smoke-test",
                "1.0.0");
            var response = await new UpdatePublisherService().PublishAsync(
                new Uri(serverText),
                bundle.ArchivePath,
                keyPath);
            Assert.Contains("\"published\":true", response);

            var installed = Path.Combine(root, "installed");
            Directory.CreateDirectory(installed);
            var result = await new ClientUpdateService().CheckAndApplyAsync(
                installed,
                new()
                {
                    ServerBaseUrl = serverText,
                    ProductId = "mccp-smoke-test",
                    RequireSuccessfulCheck = true,
                    RequireLauncherUpdateCheck = false
                });

            Assert.True(result.Updated);
            Assert.Equal(
                expected,
                await File.ReadAllTextAsync(Path.Combine(
                    installed,
                    "LauncherConfig",
                    "smoke-test.txt")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, true);
            }
        }
    }
}
