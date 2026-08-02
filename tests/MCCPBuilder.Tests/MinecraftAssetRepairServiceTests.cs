using System.Net;
using System.Security.Cryptography;
using System.Text;
using MCCPBuilder.Core;

namespace MCCPBuilder.Tests;

public sealed class MinecraftAssetRepairServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "MCCPBuilderAssetRepairTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void DefaultHttpHandler_DisablesProxy()
    {
        using var handler = MinecraftAssetRepairService.CreateHttpHandler();

        Assert.False(handler.UseProxy);
    }

    [Fact]
    public async Task EnsureSelectedLanguage_DownloadsMissingIndexedObject()
    {
        var content = Encoding.UTF8.GetBytes("{\"menu.singleplayer\":\"单人游戏\"}");
        var hash = Convert.ToHexString(SHA1.HashData(content)).ToLowerInvariant();
        var (minecraft, game) = CreateLayout("zh_cn", hash, content.Length);
        var requests = 0;
        var handler = new DelegateHandler(request =>
        {
            Interlocked.Increment(ref requests);
            Assert.Equal(
                $"https://resources.download.minecraft.net/{hash[..2]}/{hash}",
                request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content)
            });
        });

        var result = await new MinecraftAssetRepairService(
                new HttpClient(handler))
            .EnsureSelectedLanguageAsync(minecraft, game, "5");

        Assert.True(result.Downloaded);
        Assert.Equal("zh_cn", result.LanguageCode);
        Assert.Equal("5", result.AssetIndexId);
        Assert.Equal(1, requests);
        Assert.Equal(content, await File.ReadAllBytesAsync(result.ObjectPath));
    }

    [Fact]
    public async Task EnsureSelectedLanguage_ValidObjectDoesNotUseNetwork()
    {
        var content = Encoding.UTF8.GetBytes("{\"menu.singleplayer\":\"单人游戏\"}");
        var hash = Convert.ToHexString(SHA1.HashData(content)).ToLowerInvariant();
        var (minecraft, game) = CreateLayout("zh_cn", hash, content.Length);
        var objectPath = Path.Combine(
            minecraft,
            "assets",
            "objects",
            hash[..2],
            hash);
        Directory.CreateDirectory(Path.GetDirectoryName(objectPath)!);
        await File.WriteAllBytesAsync(objectPath, content);
        var handler = new DelegateHandler(_ =>
            throw new InvalidOperationException("不应访问网络。"));

        var result = await new MinecraftAssetRepairService(
                new HttpClient(handler))
            .EnsureSelectedLanguageAsync(minecraft, game, "5");

        Assert.False(result.Downloaded);
    }

    [Fact]
    public async Task EnsureSelectedLanguage_ReplacesCorruptObjectAfterHashCheck()
    {
        var content = Encoding.UTF8.GetBytes("{\"menu.singleplayer\":\"单人游戏\"}");
        var hash = Convert.ToHexString(SHA1.HashData(content)).ToLowerInvariant();
        var (minecraft, game) = CreateLayout("zh_cn", hash, content.Length);
        var objectPath = Path.Combine(
            minecraft,
            "assets",
            "objects",
            hash[..2],
            hash);
        Directory.CreateDirectory(Path.GetDirectoryName(objectPath)!);
        await File.WriteAllBytesAsync(
            objectPath,
            Enumerable.Repeat((byte)'x', content.Length).ToArray());
        var handler = new DelegateHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content)
            }));

        var result = await new MinecraftAssetRepairService(
                new HttpClient(handler))
            .EnsureSelectedLanguageAsync(minecraft, game, "5");

        Assert.True(result.Downloaded);
        Assert.Equal(content, await File.ReadAllBytesAsync(objectPath));
    }

    [Fact]
    public async Task EnsureSelectedLanguage_RejectsInvalidIndexHashBeforeNetwork()
    {
        var (minecraft, game) = CreateLayout("zh_cn", "../escape", 10);
        var handler = new DelegateHandler(_ =>
            throw new InvalidOperationException("不应访问网络。"));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new MinecraftAssetRepairService(new HttpClient(handler))
                .EnsureSelectedLanguageAsync(minecraft, game, "5"));
    }

    [Fact]
    public async Task EnsureSelectedLanguage_EnglishDoesNotRequireAssetIndex()
    {
        var minecraft = Path.Combine(_root, ".minecraft");
        var game = Path.Combine(minecraft, "versions", "test");
        Directory.CreateDirectory(game);
        await File.WriteAllTextAsync(
            Path.Combine(game, "options.txt"),
            "lang:en_us\n");

        var result = await new MinecraftAssetRepairService(
                new HttpClient(new DelegateHandler(_ =>
                    throw new InvalidOperationException("不应访问网络。"))))
            .EnsureSelectedLanguageAsync(minecraft, game, "missing");

        Assert.False(result.LanguageConfigured);
        Assert.False(result.Downloaded);
    }

    private (string Minecraft, string Game) CreateLayout(
        string language,
        string hash,
        int size)
    {
        var minecraft = Path.Combine(_root, ".minecraft");
        var game = Path.Combine(minecraft, "versions", "test");
        var indexes = Path.Combine(minecraft, "assets", "indexes");
        Directory.CreateDirectory(game);
        Directory.CreateDirectory(indexes);
        File.WriteAllText(
            Path.Combine(game, "options.txt"),
            $"lang:{language}\n");
        File.WriteAllText(
            Path.Combine(indexes, "5.json"),
            $$"""
            {
              "objects": {
                "minecraft/lang/{{language}}.json": {
                  "hash": "{{hash}}",
                  "size": {{size}}
                }
              }
            }
            """);
        return (minecraft, game);
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
}
