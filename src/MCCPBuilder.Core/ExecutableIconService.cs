namespace MCCPBuilder.Core;

public static class ExecutableIconService
{
    public static string? ValidateIcon(string iconPath)
    {
        if (string.IsNullOrWhiteSpace(iconPath))
        {
            return null;
        }

        if (!File.Exists(iconPath))
        {
            return "图标文件不存在。";
        }

        if (!string.Equals(Path.GetExtension(iconPath), ".ico", StringComparison.OrdinalIgnoreCase))
        {
            return "图标必须是 Windows .ico 文件。";
        }

        try
        {
            _ = ReadIcon(iconPath);
            return null;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return $"ICO 文件无效：{exception.Message}";
        }
    }

    private static IconFile ReadIcon(string iconPath)
    {
        using var stream = new FileStream(iconPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new BinaryReader(stream);
        if (stream.Length < 6 || reader.ReadUInt16() != 0 || reader.ReadUInt16() != 1)
        {
            throw new InvalidDataException("文件头不是 Windows ICO 格式。");
        }

        var count = reader.ReadUInt16();
        if (count is 0 or > 256 || stream.Length < 6L + count * 16L)
        {
            throw new InvalidDataException("图标图片数量无效。");
        }

        var entries = new List<IconDirectoryEntry>(count);
        for (var index = 0; index < count; index++)
        {
            entries.Add(new(
                reader.ReadByte(),
                reader.ReadByte(),
                reader.ReadByte(),
                reader.ReadByte(),
                reader.ReadUInt16(),
                reader.ReadUInt16(),
                reader.ReadUInt32(),
                reader.ReadUInt32()));
        }

        var images = new List<IconImage>(count);
        foreach (var entry in entries)
        {
            var endOffset = (long)entry.ImageOffset + entry.BytesInResource;
            if (entry.BytesInResource == 0 || entry.ImageOffset < 6 + count * 16 || endOffset > stream.Length)
            {
                throw new InvalidDataException("ICO 图片数据范围无效。");
            }

            stream.Position = entry.ImageOffset;
            var data = reader.ReadBytes(checked((int)entry.BytesInResource));
            if (data.Length != entry.BytesInResource)
            {
                throw new InvalidDataException("ICO 图片数据不完整。");
            }

            images.Add(new(
                entry.Width,
                entry.Height,
                entry.ColorCount,
                entry.Reserved,
                entry.Planes,
                entry.BitCount,
                data));
        }

        return new(images);
    }

    private sealed record IconFile(IReadOnlyList<IconImage> Images);

    private sealed record IconImage(
        byte Width,
        byte Height,
        byte ColorCount,
        byte Reserved,
        ushort Planes,
        ushort BitCount,
        byte[] Data);

    private sealed record IconDirectoryEntry(
        byte Width,
        byte Height,
        byte ColorCount,
        byte Reserved,
        ushort Planes,
        ushort BitCount,
        uint BytesInResource,
        uint ImageOffset);
}
