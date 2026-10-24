using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Amazon.S3.Model;

namespace CloudStore.Core;

public sealed record UploadOptions(long MultipartThreshold, long PartSize, int Concurrency, bool Overwrite, bool SkipExisting);

public sealed record UploadResult(string Key, bool Skipped);

/// <summary>
/// 大文件上传：小文件走单次 PUT，大文件走 S3 分片上传（并发、进度、可取消、失败自动中止）。
/// </summary>
public static class MultipartUploader
{
    private const int MaxParts = 10_000;
    private const int MaxAttempts = 3;

    public static async Task<UploadResult> UploadAsync(
        StorageService service,
        string localPath,
        string key,
        Action<long>? progress,
        CancellationToken ct,
        UploadOptions options,
        string? contentType = null)
    {
        var file = new FileInfo(localPath);

        if (!file.Exists)
        {
            throw new FileNotFoundException("本地文件不存在：" + localPath, localPath);
        }

        long size = file.Length;
        contentType ??= Humanize.GuessMime(key);

        // ---- 同名对象处理 ----
        long? remoteSize = await TryGetRemoteSizeAsync(service, key, ct).ConfigureAwait(false);

        if (remoteSize.HasValue)
        {
            if (options.SkipExisting)
            {
                progress?.Invoke(size);
                return new UploadResult(key, true);
            }

            if (!options.Overwrite)
            {
                key = await NextAvailableKeyAsync(service, key, ct).ConfigureAwait(false);
            }
        }

        if (size <= options.MultipartThreshold)
        {
            await PutSingleAsync(service, localPath, key, contentType, size, progress, ct).ConfigureAwait(false);
            return new UploadResult(key, false);
        }

        await PutMultipartAsync(service, localPath, key, contentType, size, progress, ct, options).ConfigureAwait(false);
        return new UploadResult(key, false);
    }

    private static async Task PutSingleAsync(
        StorageService service,
        string localPath,
        string key,
        string contentType,
        long size,
        Action<long>? progress,
        CancellationToken ct)
    {
        await using var fs = new FileStream(
            localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var stream = new ProgressStream(fs, bytes => progress?.Invoke(bytes));

        var request = new PutObjectRequest
        {
            BucketName = service.Bucket,
            // R2 不实现流式 SigV4，逐个请求关掉载荷签名（客户端配置里没有这个开关）。
            DisablePayloadSigning = true,
            Key = key,
            InputStream = stream,
            ContentType = contentType,
            AutoCloseStream = false,
        };

        await service.S3.PutObjectAsync(request, ct).ConfigureAwait(false);
        await stream.DisposeAsync().ConfigureAwait(false);
    }

    private static async Task PutMultipartAsync(
        StorageService service,
        string localPath,
        string key,
        string contentType,
        long size,
        Action<long>? progress,
        CancellationToken ct,
        UploadOptions options)
    {
        long partSize = Math.Max(options.PartSize, 8L * 1024 * 1024);
        int partCount = (int)Math.Ceiling(size / (double)partSize);

        if (partCount > MaxParts)
        {
            long minPartSize = (size + MaxParts - 1) / MaxParts;
            const long align = 8L * 1024 * 1024;
            partSize = Math.Max(partSize, ((minPartSize + align - 1) / align) * align);
            partCount = (int)Math.Ceiling(size / (double)partSize);
        }

        var initiate = await service.S3.InitiateMultipartUploadAsync(
            new InitiateMultipartUploadRequest
            {
                BucketName = service.Bucket,
                Key = key,
                ContentType = contentType,
            }, ct).ConfigureAwait(false);

        string uploadId = initiate.UploadId;

        try
        {
            var parts = new PartETag[partCount];

            await Parallel.ForEachAsync(
                Enumerable.Range(1, partCount),
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = Math.Clamp(options.Concurrency, 1, 16),
                    CancellationToken = ct,
                },
                async (partNumber, token) =>
                {
                    long offset = (partNumber - 1) * partSize;
                    int expected = (int)Math.Min(partSize, size - offset);

                    byte[] buffer = ArrayPool<byte>.Shared.Rent(expected);
                    try
                    {
                        int filled = 0;
                        await using (var fs = new FileStream(
                            localPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20,
                            FileOptions.Asynchronous | FileOptions.SequentialScan))
                        {
                            fs.Seek(offset, SeekOrigin.Begin);

                            while (filled < expected)
                            {
                                int read = await fs
                                    .ReadAsync(buffer.AsMemory(filled, expected - filled), token)
                                    .ConfigureAwait(false);

                                if (read <= 0)
                                {
                                    throw new EndOfStreamException(
                                        $"读取本地文件时提前结束，缺少 {expected - filled} 字节（第 {partNumber} 片）。");
                                }

                                filled += read;
                            }
                        }

                        for (int attempt = 1; ; attempt++)
                        {
                            try
                            {
                                var response = await service.S3.UploadPartAsync(
                                    new UploadPartRequest
                                    {
                                        BucketName = service.Bucket,
                                        Key = key,
                                        UploadId = uploadId,
                                        PartNumber = partNumber,
                                        InputStream = new MemoryStream(buffer, 0, filled, false),
                                        PartSize = filled,
                                        DisablePayloadSigning = true,
                                    }, token).ConfigureAwait(false);

                                parts[partNumber - 1] = new PartETag
                                {
                                    PartNumber = partNumber,
                                    ETag = response.ETag,
                                };

                                break;
                            }
                            catch (Exception ex) when (attempt < MaxAttempts && !ErrorInfo.IsCanceled(ex))
                            {
                                await Task.Delay(TimeSpan.FromSeconds(attempt), token).ConfigureAwait(false);
                            }
                        }

                        progress?.Invoke(filled);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }
                }).ConfigureAwait(false);

            await service.S3.CompleteMultipartUploadAsync(
                new CompleteMultipartUploadRequest
                {
                    BucketName = service.Bucket,
                    Key = key,
                    UploadId = uploadId,
                    PartETags = parts.ToList(),
                }, ct).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await service.S3.AbortMultipartUploadAsync(
                    new AbortMultipartUploadRequest
                    {
                        BucketName = service.Bucket,
                        Key = key,
                        UploadId = uploadId,
                    }, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // 中止失败不影响主流程，遗留的分片会由 R2 生命周期策略清理
            }

            throw;
        }
    }

    private static async Task<long?> TryGetRemoteSizeAsync(StorageService service, string key, CancellationToken ct)
    {
        try
        {
            var meta = await service.HeadAsync(key, ct).ConfigureAwait(false);
            return meta.ContentLength;
        }
        catch (Exception ex) when (ErrorInfo.IsNotFound(ex))
        {
            return null;
        }
    }

    private static async Task<string> NextAvailableKeyAsync(StorageService service, string key, CancellationToken ct)
    {
        string directory = string.Empty;
        string stem = key;
        string extension = string.Empty;

        int slash = key.LastIndexOf('/');
        if (slash >= 0)
        {
            directory = key[..(slash + 1)];
            stem = key[(slash + 1)..];
        }

        int dot = stem.LastIndexOf('.');
        if (dot > 0)
        {
            extension = stem[dot..];
            stem = stem[..dot];
        }

        for (int i = 1; i < 1000; i++)
        {
            string candidate = $"{directory}{stem} ({i}){extension}";
            long? existing = await TryGetRemoteSizeAsync(service, candidate, ct).ConfigureAwait(false);
            if (existing is null)
            {
                return candidate;
            }
        }

        return $"{directory}{stem} ({Guid.NewGuid():N}){extension}";
    }
}
