using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;

namespace MCCPBuilder.Core;

internal sealed class StreamingBundleDownloadService(HttpClient httpClient)
{
    internal const int SegmentSize = 8 * 1024 * 1024;

    public async Task DownloadAndExtractAsync(
        Uri bundleUri,
        StreamingBundleInfo bundle,
        string stagingDirectory,
        IReadOnlyList<UpdateManifestEntry> manifestFiles,
        IReadOnlyCollection<UpdateManifestEntry> changedFiles,
        int maxConcurrency,
        DownloadPauseController? pauseController,
        Action<int, string> bytesDownloaded,
        Action<string> extractionProgress,
        Action<string> extractionCompleted,
        CancellationToken cancellationToken)
    {
        var stagingRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(stagingDirectory));
        var segmentRoot = Path.Combine(
            Path.GetDirectoryName(stagingRoot)!,
            $"segments-{Guid.NewGuid():N}");
        Directory.CreateDirectory(segmentRoot);

        var segmentCountLong =
            (bundle.Size + SegmentSize - 1) / SegmentSize;
        if (segmentCountLong is <= 0 or > int.MaxValue)
        {
            throw new InvalidDataException("流式更新包分段数量无效。");
        }

        var segmentCount = checked((int)segmentCountLong);
        var ready = Enumerable.Range(0, segmentCount)
            .Select(_ => new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        using var bufferedSegments = new SemaphoreSlim(
            Math.Min(maxConcurrency, segmentCount));
        using var linkedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        var token = linkedCancellation.Token;
        var downloadTask = DownloadSegmentsAsync(
            bundleUri,
            bundle.Size,
            segmentRoot,
            ready,
            bufferedSegments,
            maxConcurrency,
            pauseController,
            bytesDownloaded,
            linkedCancellation,
            token);
        var extractTask = ExtractAsync(
            ready,
            bufferedSegments,
            stagingRoot,
            manifestFiles,
            changedFiles,
            bundle,
            pauseController,
            extractionProgress,
            extractionCompleted,
            token);

        try
        {
            await Task.WhenAll(downloadTask, extractTask);
        }
        catch
        {
            await linkedCancellation.CancelAsync();
            try
            {
                await Task.WhenAll(downloadTask, extractTask);
            }
            catch
            {
                // The original download or extraction failure is selected
                // below after both sides of the pipeline have stopped.
            }

            var downloadFailure =
                downloadTask.Exception?.GetBaseException();
            if (downloadFailure is not null &&
                downloadFailure is not OperationCanceledException)
            {
                ExceptionDispatchInfo.Capture(downloadFailure).Throw();
            }

            var extractionFailure =
                extractTask.Exception?.GetBaseException();
            if (extractionFailure is not null &&
                extractionFailure is not OperationCanceledException)
            {
                ExceptionDispatchInfo.Capture(extractionFailure).Throw();
            }

            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        finally
        {
            DeleteDirectoryBestEffort(segmentRoot);
        }
    }

    private async Task DownloadSegmentsAsync(
        Uri uri,
        long totalSize,
        string segmentRoot,
        IReadOnlyList<TaskCompletionSource<string>> ready,
        SemaphoreSlim bufferedSegments,
        int maxConcurrency,
        DownloadPauseController? pauseController,
        Action<int, string> bytesDownloaded,
        CancellationTokenSource linkedCancellation,
        CancellationToken cancellationToken)
    {
        try
        {
            await Parallel.ForEachAsync(
                Enumerable.Range(0, ready.Count),
                new ParallelOptions
                {
                    CancellationToken = cancellationToken,
                    MaxDegreeOfParallelism = Math.Min(
                        maxConcurrency,
                        ready.Count)
                },
                async (index, token) =>
                {
                    await bufferedSegments.WaitAsync(token);
                    var slotOwnedByReader = false;
                    var finalPath = Path.Combine(
                        segmentRoot,
                        $"{index:D8}.part");
                    var temporaryPath =
                        finalPath + $".{Guid.NewGuid():N}.tmp";
                    try
                    {
                        var start = (long)index * SegmentSize;
                        var end = Math.Min(
                            totalSize - 1,
                            start + SegmentSize - 1);
                        await DownloadSegmentAsync(
                            uri,
                            start,
                            end,
                            temporaryPath,
                            pauseController,
                            count => bytesDownloaded(
                                count,
                                $"正在分段下载并解压更新包 " +
                                $"({index + 1}/{ready.Count})…"),
                            token);
                        File.Move(temporaryPath, finalPath);
                        slotOwnedByReader = ready[index].TrySetResult(
                            finalPath);
                        if (!slotOwnedByReader)
                        {
                            throw new InvalidOperationException(
                                "流式下载分段状态重复完成。");
                        }
                    }
                    finally
                    {
                        if (File.Exists(temporaryPath))
                        {
                            File.Delete(temporaryPath);
                        }

                        if (!slotOwnedByReader)
                        {
                            bufferedSegments.Release();
                        }
                    }
                });
        }
        catch (Exception exception)
        {
            await linkedCancellation.CancelAsync();
            foreach (var completion in ready)
            {
                completion.TrySetException(exception);
            }

            throw;
        }
    }

    private async Task DownloadSegmentAsync(
        Uri uri,
        long start,
        long end,
        string destination,
        DownloadPauseController? pauseController,
        Action<int> bytesDownloaded,
        CancellationToken cancellationToken)
    {
        if (pauseController is not null)
        {
            await pauseController.WaitWhilePausedAsync(cancellationToken);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Range = new RangeHeaderValue(start, end);
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            throw new InvalidDataException(
                $"更新服务器不支持分段下载（HTTP " +
                $"{(int)response.StatusCode}）。");
        }

        var expectedLength = end - start + 1;
        var range = response.Content.Headers.ContentRange;
        if (range?.From != start ||
            range.To != end ||
            range.Length is not long completeLength ||
            completeLength < end + 1 ||
            (response.Content.Headers.ContentLength is long contentLength &&
             contentLength != expectedLength))
        {
            throw new InvalidDataException(
                $"服务器返回了无效的下载分段：{start}-{end}。");
        }

        await using var source =
            await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            256 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[256 * 1024];
        long received = 0;
        while (true)
        {
            if (pauseController is not null)
            {
                await pauseController.WaitWhilePausedAsync(
                    cancellationToken);
            }

            var read = await source.ReadAsync(
                buffer,
                cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (pauseController is not null)
            {
                await pauseController.WaitWhilePausedAsync(
                    cancellationToken);
            }

            await output.WriteAsync(
                buffer.AsMemory(0, read),
                cancellationToken);
            received += read;
            bytesDownloaded(read);
            if (received > expectedLength)
            {
                throw new InvalidDataException(
                    "服务器返回的下载分段超过预期大小。");
            }
        }

        await output.FlushAsync(cancellationToken);
        if (received != expectedLength)
        {
            throw new InvalidDataException(
                "服务器返回的下载分段长度不足。");
        }
    }

    private static async Task ExtractAsync(
        IReadOnlyList<TaskCompletionSource<string>> ready,
        SemaphoreSlim bufferedSegments,
        string stagingRoot,
        IReadOnlyList<UpdateManifestEntry> manifestFiles,
        IReadOnlyCollection<UpdateManifestEntry> changedFiles,
        StreamingBundleInfo bundle,
        DownloadPauseController? pauseController,
        Action<string> extractionProgress,
        Action<string> extractionCompleted,
        CancellationToken cancellationToken)
    {
        var expected = manifestFiles.ToDictionary(
            file => file.Path,
            StringComparer.OrdinalIgnoreCase);
        var changed = changedFiles.ToDictionary(
            file => file.Path,
            StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var extracted = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        await using var segmented = new OrderedSegmentStream(
            ready,
            bufferedSegments,
            cancellationToken);
        await using var hashing = new HashingReadStream(segmented);
        using (var compressed = new GZipStream(
                   hashing,
                   CompressionMode.Decompress,
                   leaveOpen: true))
        using (var archive = new TarReader(
                   compressed,
                   leaveOpen: true))
        {
            TarEntry? entry;
            while ((entry = await archive.GetNextEntryAsync(
                       copyData: false,
                       cancellationToken)) is not null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.EntryType is not (
                        TarEntryType.RegularFile or
                        TarEntryType.V7RegularFile))
                {
                    throw new InvalidDataException(
                        $"流式更新包包含不支持的条目：{entry.Name}");
                }

                const string prefix = "payload/";
                if (!entry.Name.StartsWith(
                        prefix,
                        StringComparison.Ordinal) ||
                    entry.Name.Length == prefix.Length)
                {
                    throw new InvalidDataException(
                        $"流式更新包包含无效路径：{entry.Name}");
                }

                var relative = entry.Name[prefix.Length..];
                ReleaseBundleService.EnsureSafeRelativePath(relative);
                if (!expected.TryGetValue(relative, out var expectedFile) ||
                    !seen.Add(relative) ||
                    entry.Length != expectedFile.Size)
                {
                    throw new InvalidDataException(
                        $"流式更新包文件与清单不一致：{relative}");
                }

                var content = entry.DataStream ?? Stream.Null;
                if (!changed.ContainsKey(relative))
                {
                    await CopyEntryAsync(
                        content,
                        Stream.Null,
                        pauseController,
                        null,
                        cancellationToken);
                    continue;
                }

                extractionProgress($"正在解压：{relative}");
                var destination = ResolveInside(
                    stagingRoot,
                    relative);
                EnsureNoReparsePoints(stagingRoot, destination);
                Directory.CreateDirectory(
                    Path.GetDirectoryName(destination)!);
                await using var output = new FileStream(
                    destination,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    256 * 1024,
                    FileOptions.Asynchronous |
                    FileOptions.SequentialScan);
                using var hasher = IncrementalHash.CreateHash(
                    HashAlgorithmName.SHA256);
                var length = await CopyEntryAsync(
                    content,
                    output,
                    pauseController,
                    hasher,
                    cancellationToken);
                await output.FlushAsync(cancellationToken);
                var hash = Convert.ToHexString(
                    hasher.GetHashAndReset());
                if (length != expectedFile.Size ||
                    !hash.Equals(
                        expectedFile.Sha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"解压文件完整性校验失败：{relative}");
                }

                extracted.Add(relative);
                extractionCompleted(relative);
            }
        }

        await hashing.DrainToEndAsync(cancellationToken);
        if (seen.Count != expected.Count ||
            extracted.Count != changed.Count ||
            changed.Keys.Any(path => !extracted.Contains(path)))
        {
            throw new InvalidDataException(
                "流式更新包缺少清单中的文件。");
        }

        var bundleHash = hashing.GetHash();
        if (!bundleHash.Equals(
                bundle.Sha256,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "流式更新包 SHA-256 校验失败。");
        }
    }

    private static async Task<long> CopyEntryAsync(
        Stream source,
        Stream destination,
        DownloadPauseController? pauseController,
        IncrementalHash? hasher,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[256 * 1024];
        long total = 0;
        while (true)
        {
            if (pauseController is not null)
            {
                await pauseController.WaitWhilePausedAsync(
                    cancellationToken);
            }

            var read = await source.ReadAsync(
                buffer,
                cancellationToken);
            if (read == 0)
            {
                return total;
            }

            if (pauseController is not null)
            {
                await pauseController.WaitWhilePausedAsync(
                    cancellationToken);
            }

            hasher?.AppendData(buffer, 0, read);
            await destination.WriteAsync(
                buffer.AsMemory(0, read),
                cancellationToken);
            total += read;
        }
    }

    private static string ResolveInside(
        string root,
        string relativePath)
    {
        ReleaseBundleService.EnsureSafeRelativePath(
            relativePath.Replace('\\', '/'));
        var normalizedRoot =
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var path = Path.GetFullPath(Path.Combine(
            normalizedRoot,
            relativePath.Replace('/', '\\')));
        if (!path.StartsWith(
                normalizedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"更新路径越过暂存目录：{relativePath}");
        }

        return path;
    }

    private static void EnsureNoReparsePoints(
        string root,
        string path)
    {
        var rootInfo = new DirectoryInfo(root);
        if ((rootInfo.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                "更新暂存根目录不能是重解析点。");
        }

        var current = new DirectoryInfo(
            Path.GetDirectoryName(path)!);
        while (current.FullName.StartsWith(
                   rootInfo.FullName,
                   StringComparison.OrdinalIgnoreCase))
        {
            if (current.Exists &&
                (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException(
                    $"更新暂存路径包含重解析点：{current.FullName}");
            }

            if (current.FullName.Equals(
                    rootInfo.FullName,
                    StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = current.Parent
                ?? throw new InvalidDataException(
                    "无法验证更新暂存路径。");
        }
    }

    private static void DeleteDirectoryBestEffort(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, true);
            }
        }
        catch
        {
            // A failed or cancelled segmented transfer is never committed.
        }
    }

    private sealed class OrderedSegmentStream(
        IReadOnlyList<TaskCompletionSource<string>> ready,
        SemaphoreSlim bufferedSegments,
        CancellationToken pipelineCancellation) : Stream
    {
        private int _index;
        private FileStream? _current;
        private string? _currentPath;
        private bool _disposed;

        public override bool CanRead => !_disposed;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length =>
            throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            using var linked =
                CancellationTokenSource.CreateLinkedTokenSource(
                    pipelineCancellation,
                    cancellationToken);
            while (_index < ready.Count)
            {
                if (_current is null)
                {
                    _currentPath = await ready[_index].Task.WaitAsync(
                        linked.Token);
                    _current = new FileStream(
                        _currentPath,
                        FileMode.Open,
                        FileAccess.Read,
                        FileShare.Read,
                        256 * 1024,
                        FileOptions.Asynchronous |
                        FileOptions.SequentialScan);
                }

                var read = await _current.ReadAsync(
                    buffer,
                    linked.Token);
                if (read != 0)
                {
                    return read;
                }

                ConsumeCurrent();
            }

            return 0;
        }

        private void ConsumeCurrent()
        {
            _current?.Dispose();
            _current = null;
            if (!string.IsNullOrWhiteSpace(_currentPath))
            {
                try
                {
                    File.Delete(_currentPath);
                }
                catch
                {
                    // The enclosing transfer directory is cleaned later.
                }
            }

            _currentPath = null;
            _index++;
            bufferedSegments.Release();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                if (_current is not null)
                {
                    ConsumeCurrent();
                }
            }

            base.Dispose(disposing);
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

    private sealed class HashingReadStream(Stream inner) : Stream
    {
        private readonly IncrementalHash _hasher =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private bool _hashFinalized;
        private string? _hash;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length =>
            throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public async Task DrainToEndAsync(
            CancellationToken cancellationToken)
        {
            var buffer = new byte[256 * 1024];
            while (await ReadAsync(
                       buffer,
                       cancellationToken) != 0)
            {
            }
        }

        public string GetHash()
        {
            if (!_hashFinalized)
            {
                _hash = Convert.ToHexString(
                    _hasher.GetHashAndReset());
                _hashFinalized = true;
            }

            return _hash!;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(
                buffer,
                cancellationToken);
            if (read > 0)
            {
                if (_hashFinalized)
                {
                    throw new InvalidOperationException(
                        "流式更新包哈希已完成。");
                }

                _hasher.AppendData(buffer.Span[..read]);
            }

            return read;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hasher.Dispose();
            }

            base.Dispose(disposing);
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
}
