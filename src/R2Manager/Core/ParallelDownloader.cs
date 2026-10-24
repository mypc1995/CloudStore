using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CloudStore.Core;

public sealed record DownloadOptions(long MultipartThreshold, long PartSize, int Concurrency);

public sealed record DownloadResult(string LocalPath, long Size);

/// <summary>
/// 下载：大文件拆成多个字节范围并发拉取并写入同一个临时文件，完成后再原子替换目标文件。
/// </summary>
public static class ParallelDownloader
{
    private const int MaxAttempts = 3;
    private const long MinChunk = 4L * 1024 * 1024;
    private const long MaxChunk = 64L * 1024 * 1024;

    public static async Task<DownloadResult> DownloadAsync(
        StorageService service,
        string key,
        string localPath,
        Action<long>? progress,
        CancellationToken ct,
        DownloadOptions options,
        long? knownSize = null)
    {
        long size = knownSize ?? await GetRemoteSizeAsync(service, key, ct).ConfigureAwait(false);

        string fullPath = Path.GetFullPath(localPath);
        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = fullPath + ".r2tmp";

        try
        {
            if (size <= options.MultipartThreshold)
            {
                await DownloadSingleAsync(service, key, tempPath, size, progress, ct).ConfigureAwait(false);
            }
            else
            {
                await DownloadChunkedAsync(service, key, tempPath, size, progress, ct, options).ConfigureAwait(false);
            }

            File.Move(tempPath, fullPath, overwrite: true);
            return new DownloadResult(fullPath, size);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static async Task DownloadSingleAsync(
        StorageService service,
        string key,
        string tempPath,
        long size,
        Action<long>? progress,
        CancellationToken ct)
    {
        using ObjectStream remote = await service.OpenAsync(key, null, null, ct).ConfigureAwait(false);

        await using var fs = new FileStream(
            tempPath, FileMode.Create, FileAccess.Write, FileShare.Read, 256 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        await CopyExactAsync(remote.Body, fs, size, progress, ct).ConfigureAwait(false);
        await fs.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task DownloadChunkedAsync(
        StorageService service,
        string key,
        string tempPath,
        long size,
        Action<long>? progress,
        CancellationToken ct,
        DownloadOptions options)
    {
        int concurrency = Math.Clamp(options.Concurrency, 1, 16);
        long chunkSize = Math.Clamp((long)(size / (double)(concurrency * 4)), MinChunk, MaxChunk);
        chunkSize = Math.Max(chunkSize, Math.Max(options.PartSize, MinChunk));

        int chunks = (int)Math.Ceiling(size / (double)chunkSize);

        await using (var fs = new FileStream(
            tempPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, 1 << 20,
            FileOptions.Asynchronous))
        {
            fs.SetLength(size);
        }

        await Parallel.ForEachAsync(
            Enumerable.Range(0, chunks),
            new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = ct },
            async (index, token) =>
            {
                long start = index * chunkSize;
                long end = Math.Min(size - 1, start + chunkSize - 1);
                long length = end - start + 1;

                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        using ObjectStream remote = await service
                            .OpenAsync(key, start, end, token).ConfigureAwait(false);

                        await using var fs = new FileStream(
                            tempPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 1 << 20,
                            FileOptions.Asynchronous);

                        fs.Seek(start, SeekOrigin.Begin);
                        await CopyExactAsync(remote.Body, fs, length, progress, token).ConfigureAwait(false);
                        await fs.FlushAsync(token).ConfigureAwait(false);
                        break;
                    }
                    catch (Exception ex) when (attempt < MaxAttempts && !ErrorInfo.IsCanceled(ex))
                    {
                        await Task.Delay(TimeSpan.FromSeconds(attempt), token).ConfigureAwait(false);
                    }
                }
            }).ConfigureAwait(false);
    }

    private static async Task CopyExactAsync(
        Stream source,
        Stream destination,
        long expectedBytes,
        Action<long>? progress,
        CancellationToken ct)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(256 * 1024);
        try
        {
            long remaining = expectedBytes;

            while (remaining > 0)
            {
                int want = (int)Math.Min(buffer.Length, remaining);
                int read = await source.ReadAsync(buffer.AsMemory(0, want), ct).ConfigureAwait(false);

                if (read <= 0)
                {
                    throw new IOException($"数据提前结束，仍缺少 {remaining} 字节。");
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                remaining -= read;
                progress?.Invoke(read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static async Task<long> GetRemoteSizeAsync(StorageService service, string key, CancellationToken ct)
    {
        var meta = await service.HeadAsync(key, ct).ConfigureAwait(false);
        return meta.ContentLength;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 临时文件清理失败不阻塞流程
        }
    }
}
