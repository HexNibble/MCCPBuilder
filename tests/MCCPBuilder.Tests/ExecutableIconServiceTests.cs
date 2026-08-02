using MCCPBuilder.Core;

namespace MCCPBuilder.Tests;

public sealed class ExecutableIconServiceTests : IDisposable
{
    private readonly string _temporaryDirectory =
        Path.Combine(Path.GetTempPath(), "MCCPBuilderIconTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void ValidateIcon_AcceptsStructurallyValidIco()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var iconPath = Path.Combine(_temporaryDirectory, "中文 图标.ico");
        File.WriteAllBytes(iconPath, CreateOnePixelIcon());

        var error = ExecutableIconService.ValidateIcon(iconPath);

        Assert.Null(error);
    }

    [Fact]
    public void ValidateIcon_RejectsRenamedNonIcoFile()
    {
        Directory.CreateDirectory(_temporaryDirectory);
        var iconPath = Path.Combine(_temporaryDirectory, "invalid.ico");
        File.WriteAllText(iconPath, "not an icon");

        var error = ExecutableIconService.ValidateIcon(iconPath);

        Assert.NotNull(error);
        Assert.Contains("无效", error, StringComparison.Ordinal);
    }

    private static byte[] CreateOnePixelIcon()
    {
        // 1x1、32 位 BGRA 的最小 DIB 图标。
        var image = new byte[]
        {
            40, 0, 0, 0, 1, 0, 0, 0, 2, 0, 0, 0, 1, 0, 32, 0,
            0, 0, 0, 0, 4, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 255, 255,
            0, 0, 0, 0
        };
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write((byte)1);
        writer.Write((byte)1);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write((uint)image.Length);
        writer.Write((uint)22);
        writer.Write(image);
        return stream.ToArray();
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, true);
        }
    }
}
