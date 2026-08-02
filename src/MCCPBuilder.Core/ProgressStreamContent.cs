using System.Net;

namespace MCCPBuilder.Core;

internal sealed class ProgressStreamContent(
    Stream source,
    long length,
    IProgress<PublishProgress>? progress)
    : HttpContent
{
    private const int BufferSize = 1024 * 1024;

    protected override bool TryComputeLength(out long computedLength)
    {
        computedLength = length;
        return true;
    }

    protected override Task SerializeToStreamAsync(
        Stream stream,
        TransportContext? context) =>
        CopyWithProgressAsync(stream, CancellationToken.None);

    protected override Task SerializeToStreamAsync(
        Stream stream,
        TransportContext? context,
        CancellationToken cancellationToken) =>
        CopyWithProgressAsync(stream, cancellationToken);

    private async Task CopyWithProgressAsync(
        Stream destination,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[BufferSize];
        long sent = 0;
        int read;
        progress?.Report(new("Uploading", 0, length));
        while ((read = await source.ReadAsync(
                   buffer,
                   cancellationToken)) != 0)
        {
            await destination.WriteAsync(
                buffer.AsMemory(0, read),
                cancellationToken);
            sent += read;
            progress?.Report(new("Uploading", sent, length));
        }

        progress?.Report(new("Uploaded", sent, length));
    }
}
