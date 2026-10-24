using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace CloudStore.Core;

public sealed record ListObjectsPage(
    IReadOnlyList<S3Object> Objects,
    IReadOnlyList<string> Prefixes,
    bool IsTruncated,
    string? NextContinuationToken);

public sealed record DeleteSummary(int Requested, int Deleted, IReadOnlyList<string> Errors);

public sealed class ObjectStream : IDisposable
{
    public ObjectStream(GetObjectResponse response)
    {
        Response = response;
        Body = response.ResponseStream;
    }

    public GetObjectResponse Response { get; }

    public Stream Body { get; }

    public long Length => Response.ContentLength;

    public void Dispose()
    {
        Body?.Dispose();
        Response?.Dispose();
    }
}

/// <summary>
/// 对象存储的 S3 兼容端点封装（列举 / 元信息 / 下载 / 删除 / 连通性测试）。
/// 三家云共用这一份实现，差异全部收敛在 <see cref="StorageProviders"/> 与下面的配置里。
/// </summary>
public sealed class StorageService : IDisposable
{
    private readonly AmazonS3Client _client;
    private bool _disposed;

    private StorageService(AmazonS3Client client, StorageSettings settings)
    {
        _client = client;
        Bucket = settings.BucketName.Trim();
        Endpoint = settings.ResolveEndpoint();
        Settings = settings;
    }

    public StorageSettings Settings { get; }

    public AmazonS3Client S3 => _client;

    public string Bucket { get; }

    public string Endpoint { get; }

    public StorageProvider Provider => Settings.Provider;

    public static StorageService Create(StorageSettings settings, TimeSpan? timeout = null)
    {
        string? error = settings.Validate();
        if (error is not null)
        {
            throw new InvalidOperationException(error);
        }

        string endpoint = settings.ResolveEndpoint();

        var config = new AmazonS3Config
        {
            ServiceURL = endpoint,
            AuthenticationRegion = settings.NormalizedRegion,
            UseHttp = endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase),

            // R2 用路径式寻址；阿里云 OSS 与腾讯云 COS 都要虚拟主机式，
            // 腾讯云 2024-01-01 之后新建的桶更是彻底不支持路径式。
            ForcePathStyle = StorageProviders.ForcePathStyle(settings.Provider, settings),

            MaxErrorRetry = 5,
            Timeout = timeout ?? TimeSpan.FromMinutes(30),

            // R2 / 阿里云 OSS 都不实现 aws-chunked 流式签名
            // （STREAMING-AWS4-HMAC-SHA256-PAYLOAD[-TRAILER]），
            // SDK 默认会在 PUT/UploadPart 上启用它，导致所有上传直接被拒。
            // 改成「仅在服务端明确要求时才计算」，与各家官方示例一致。
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        };

        var client = new AmazonS3Client(settings.AccessKeyId.Trim(), settings.SecretAccessKey, config);
        return new StorageService(client, settings);
    }

    public async Task<IReadOnlyList<string>> ListBucketNamesAsync(CancellationToken ct)
    {
        ListBucketsResponse response = await _client.ListBucketsAsync(ct).ConfigureAwait(false);
        return response.Buckets
            .Select(b => b.BucketName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>用一次最小范围的列举来验证凭据与桶是否可用。</summary>
    public async Task TestAsync(CancellationToken ct)
    {
        var request = new ListObjectsV2Request { BucketName = Bucket, MaxKeys = 1 };
        await _client.ListObjectsV2Async(request, ct).ConfigureAwait(false);
    }

    public async Task<ListObjectsPage> ListAsync(
        string prefix,
        string? continuationToken,
        int maxKeys,
        CancellationToken ct,
        bool useDelimiter = true)
    {
        var request = new ListObjectsV2Request
        {
            BucketName = Bucket,
            Prefix = prefix,
            MaxKeys = maxKeys,
        };

        if (useDelimiter)
        {
            request.Delimiter = "/";
        }

        if (!string.IsNullOrEmpty(continuationToken))
        {
            request.ContinuationToken = continuationToken;
        }

        ListObjectsV2Response response = await _client.ListObjectsV2Async(request, ct).ConfigureAwait(false);

        var objects = response.S3Objects is null
            ? new List<S3Object>()
            : response.S3Objects.Where(o => o is not null).ToList();

        var prefixes = response.CommonPrefixes is null
            ? new List<string>()
            : response.CommonPrefixes
                .Where(p => !string.IsNullOrEmpty(p))
                .ToList();

        bool truncated = response.IsTruncated;
        string? next = truncated ? response.NextContinuationToken : null;

        return new ListObjectsPage(objects, prefixes, truncated, next);
    }

    public async Task<GetObjectMetadataResponse> HeadAsync(string key, CancellationToken ct)
    {
        var request = new GetObjectMetadataRequest { BucketName = Bucket, Key = key };
        return await _client.GetObjectMetadataAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>获取对象内容，可选字节范围（offset..endInclusive，均含）。</summary>
    public async Task<ObjectStream> OpenAsync(
        string key,
        long? offset = null,
        long? endInclusive = null,
        CancellationToken ct = default)
    {
        var request = new GetObjectRequest { BucketName = Bucket, Key = key };

        if (offset.HasValue || endInclusive.HasValue)
        {
            long start = offset ?? 0;
            long end = endInclusive ?? long.MaxValue - 1;
            request.ByteRange = new ByteRange(start, end);
        }

        GetObjectResponse response = await _client.GetObjectAsync(request, ct).ConfigureAwait(false);
        return new ObjectStream(response);
    }

    public async Task<DeleteSummary> DeleteAsync(
        IReadOnlyList<string> keys,
        IProgress<int>? progress,
        CancellationToken ct)
    {
        if (keys.Count == 0)
        {
            return new DeleteSummary(0, 0, Array.Empty<string>());
        }

        int deleted = 0;
        var errors = new List<string>();

        foreach (var batch in keys.Chunk(1000).ToList())
        {
            ct.ThrowIfCancellationRequested();

            var request = new DeleteObjectsRequest { BucketName = Bucket, Quiet = true };
            foreach (string key in batch)
            {
                request.Objects.Add(new KeyVersion { Key = key });
            }

            try
            {
                DeleteObjectsResponse response = await _client.DeleteObjectsAsync(request, ct).ConfigureAwait(false);
                deleted += batch.Length;

                if (response.DeleteErrors is not null)
                {
                    foreach (var error in response.DeleteErrors)
                    {
                        errors.Add($"{error.Key}：{error.Message}");
                    }
                }

                progress?.Report(deleted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 单批失败时逐个重试，尽量删掉可删的部分
                foreach (string key in batch)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        await _client.DeleteObjectAsync(
                            new DeleteObjectRequest { BucketName = Bucket, Key = key }, ct).ConfigureAwait(false);
                        deleted++;
                    }
                    catch (Exception single)
                    {
                        errors.Add($"{key}：{ErrorInfo.Describe(single)}");
                    }
                }

                progress?.Report(deleted);
            }
        }

        return new DeleteSummary(keys.Count, deleted, errors);
    }

    /// <summary>服务端同桶复制（用于“改名 / 移动”）。失败时返回 false，调用方可退回客户端拷贝。</summary>
    public async Task<bool> TryServerSideCopyAsync(string sourceKey, string destKey, CancellationToken ct)
    {
        try
        {
            await _client.CopyObjectAsync(
                new CopyObjectRequest
                {
                    SourceBucket = Bucket,
                    SourceKey = sourceKey,
                    DestinationBucket = Bucket,
                    DestinationKey = destKey,
                }, ct).ConfigureAwait(false);

            return true;
        }
        catch (AmazonS3Exception)
        {
            return false;
        }
    }

    public string BuildPublicUrl(string key) =>
        StorageProviders.BuildPublicUrl(Settings.Provider, Settings, key);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _client.Dispose();
    }
}
