using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Amazon.Runtime;
using CloudStore.Core;

namespace CloudStore.S3Probe;

/// <summary>
/// 拦截 AWS SDK 发出的请求，打印原始形状。
/// 不发真实网络请求：所有请求都被本地处理器接住并返回一段假的 S3 XML。
/// </summary>
internal static class Program
{
    private static async Task<int> Main()
    {
        var cases = new[]
        {
            new Case("Cloudflare R2", StorageProvider.CloudflareR2, "my-bucket", "auto", "0123456789abcdef0123456789abcdef"),
            new Case("阿里云 OSS", StorageProvider.AliyunOss, "my-bucket", "cn-hangzhou", ""),
            new Case("腾讯云 COS", StorageProvider.TencentCos, "mybucket-1250000000", "ap-guangzhou", ""),
            new Case("自建存储（路径式）", StorageProvider.SelfHosted, "backup", "us-east-1", "", S3Addressing.PathStyle),
            new Case("自建存储（虚拟主机式）", StorageProvider.SelfHosted, "backup", "us-east-1", "", S3Addressing.VirtualHosted),
        };

        int failures = 0;

        foreach (Case c in cases)
        {
            string endpoint = StorageProviders.ResolveEndpoint(
                c.Provider,
                new StorageSettings { Provider = c.Provider, Region = c.Region, AccountId = c.AccountId });

            Console.WriteLine("════════════════════════════════════════════════════");
            Console.WriteLine($"■ {c.Name}");
            Console.WriteLine($"  地域        : {c.Region}");
            Console.WriteLine($"  桶名        : {c.Bucket}");
            Console.WriteLine($"  推导 Endpoint: {endpoint}");
            Console.WriteLine($"  寻址方式    : {(StorageProviders.ForcePathStyle(c.Provider, c.Settings) ? "path-style" : "virtual-hosted")}");

            var recorder = new Recorder();
            var settings = new StorageSettings
            {
                Provider = c.Provider,
                AccountId = c.AccountId,
                AccessKeyId = "AKIDEXAMPLE",
                SecretAccessKey = "SecretExample",
                BucketName = c.Bucket,
                Region = c.Region,
                Addressing = c.Addressing,
                CustomEndpoint = c.Settings.CustomEndpoint,

                // 其余厂商刻意不用自定义 endpoint：指向 127.0.0.1 时 SDK 会因为「主机是 IP」
                // 自动退回 path-style，那样就测不出 virtual-hosted 了。
                // 请求根本不会发出去——HttpClientFactory 把它拦在本地。
            };

            try
            {
                await RunAsync(settings, recorder).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ✗ 请求失败：{ex.GetType().Name} {ex.Message}");
                failures++;
                continue;
            }

            if (recorder.Requests.Count == 0)
            {
                Console.WriteLine("  ✗ 没有捕获到任何请求");
                failures++;
                continue;
            }

            foreach (string request in recorder.Requests)
            {
                Console.WriteLine();
                Console.WriteLine("  ── 原始请求 ──");
                foreach (string line in request.Split('\n'))
                {
                    Console.WriteLine("  " + line.TrimEnd('\r'));
                }
            }

            failures += Check(c, recorder);
        }

        Console.WriteLine("════════════════════════════════════════════════════");
        Console.WriteLine(failures == 0 ? "全部检查通过。" : $"有 {failures} 项检查未通过。");
        return failures == 0 ? 0 : 1;
    }

    private static async Task RunAsync(StorageSettings settings, Recorder recorder)
    {
        // 复用真实业务路径：构造 StorageService，调用 TestAsync（一次 ListObjectsV2）。
        // 这里改用反射之外的办法——直接把 HttpClientFactory 注入到 SDK 的配置里。
        var config = new Amazon.S3.AmazonS3Config
        {
            ServiceURL = settings.ResolveEndpoint(),
            AuthenticationRegion = settings.NormalizedRegion,
            UseHttp = true,
            ForcePathStyle = StorageProviders.ForcePathStyle(settings.Provider, settings),
            MaxErrorRetry = 0,
            Timeout = TimeSpan.FromSeconds(10),
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            HttpClientFactory = recorder,
        };

        var client = new Amazon.S3.AmazonS3Client(
            settings.AccessKeyId, settings.SecretAccessKey, config);

        // 手动加 DisablePayloadSigning，与业务代码 MultipartUploader 里的做法一致。
        var request = new Amazon.S3.Model.ListObjectsV2Request
        {
            BucketName = settings.BucketName,
            MaxKeys = 1,
        };

        await client.ListObjectsV2Async(request, CancellationToken.None).ConfigureAwait(false);

        // 再来一次上传：确认 PUT 也没落到 aws-chunked 上。
        var put = new Amazon.S3.Model.PutObjectRequest
        {
            BucketName = settings.BucketName,
            Key = "probe.txt",
            InputStream = new System.IO.MemoryStream(Encoding.UTF8.GetBytes("hello")),
            DisablePayloadSigning = true,
        };

        try
        {
            await client.PutObjectAsync(put, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  （PUT 未完成：{ex.GetType().Name}，但请求形状已被记录）");
        }
    }

    private static int Check(Case c, Recorder recorder)
    {
        int failures = 0;
        string request = recorder.Requests[0];

        bool pathStyle = StorageProviders.ForcePathStyle(c.Provider, c.Settings);
        string host = Extract(request, "HOST ");
        string path = Extract(request, "PATH ");
        string auth = Extract(request, "H Authorization=");

        Console.WriteLine();
        Console.WriteLine("  ── 断言 ──");

        // 1) 寻址方式
        if (pathStyle)
        {
            bool ok = path.Contains("/" + c.Bucket + "/") || path.Contains("/" + c.Bucket + "?");
            Console.WriteLine($"  {(ok ? "✓" : "✗")} path-style：路径里应含桶名 /{c.Bucket}/，实际 {path}");
            if (!ok) failures++;
        }
        else
        {
            bool ok = host.StartsWith(c.Bucket + ".", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"  {(ok ? "✓" : "✗")} virtual-hosted：主机名应以「{c.Bucket}.」开头，实际 {host}");
            if (!ok) failures++;

            bool pathOk = !path.Contains("/" + c.Bucket + "/");
            Console.WriteLine($"  {(pathOk ? "✓" : "✗")} virtual-hosted：路径里不应再出现桶名，实际 {path}");
            if (!pathOk) failures++;
        }

        // 2) 签名地域
        string expectedScope = $"/{c.Region}/s3/aws4_request";
        bool regionOk = auth.Contains(expectedScope, StringComparison.Ordinal);
        Console.WriteLine($"  {(regionOk ? "✓" : "✗")} 签名地域应为 {expectedScope}");
        if (!regionOk)
        {
            failures++;
            Console.WriteLine($"      实际 Authorization: {auth}");
        }

        // 3) 不能用 aws-chunked 流式签名
        bool chunked = request.Contains("STREAMING-", StringComparison.OrdinalIgnoreCase) ||
                       request.Contains("aws-chunked", StringComparison.OrdinalIgnoreCase);

        if (chunked)
        {
            Console.WriteLine("  ✗ 请求用了 aws-chunked 流式签名——R2 / OSS 会直接拒绝");
            failures++;
        }
        else
        {
            string sha = Extract(request, "H x-amz-content-sha256=");
            Console.WriteLine($"  ✓ 未使用 aws-chunked 流式签名（x-amz-content-sha256 = {sha}）");
        }

        // 4) 上传请求也不能用 chunked
        foreach (string put in recorder.Requests.Where(r => r.StartsWith("METHOD PUT")))
        {
            bool putChunked = put.Contains("STREAMING-", StringComparison.OrdinalIgnoreCase) ||
                              put.Contains("aws-chunked", StringComparison.OrdinalIgnoreCase);
            Console.WriteLine($"  {(putChunked ? "✗" : "✓")} PUT 请求{(putChunked ? "用了" : "没用")} aws-chunked");
            if (putChunked) failures++;
        }

        return failures;
    }

    private static string Extract(string request, string marker)
    {
        foreach (string line in request.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith(marker, StringComparison.Ordinal))
            {
                return trimmed.Substring(marker.Length).Trim();
            }
        }

        return "(无)";
    }

    private sealed record Case(
        string Name,
        StorageProvider Provider,
        string Bucket,
        string Region,
        string AccountId,
        S3Addressing Addressing = S3Addressing.PathStyle)
    {
        public StorageSettings Settings => new()
        {
            Provider = Provider,
            Region = Region,
            AccountId = AccountId,
            BucketName = Bucket,
            Addressing = Addressing,

            // 自建存储没有统一端点规则，用一个测试域名代替。
            CustomEndpoint = Provider == StorageProvider.SelfHosted
                ? "https://minio.example.com"
                : string.Empty,
        };
    }

    /// <summary>把 SDK 的请求全部拦下来，记下形状后返回一段假响应。</summary>
    private sealed class Recorder : HttpClientFactory
    {
        public List<string> Requests { get; } = new();

        public override HttpClient CreateHttpClient(IClientConfig clientConfig)
        {
            return new HttpClient(new Handler(this)) { Timeout = TimeSpan.FromSeconds(10) };
        }

        private sealed class Handler : HttpMessageHandler
        {
            private readonly Recorder _owner;

            public Handler(Recorder owner) => _owner = owner;

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var sb = new StringBuilder();
                sb.AppendLine($"METHOD {request.Method}");
                sb.AppendLine($"URI {request.RequestUri}");
                sb.AppendLine($"HOST {request.RequestUri!.Host}");
                sb.AppendLine($"PATH {request.RequestUri.AbsolutePath}");
                sb.AppendLine($"QUERY {request.RequestUri.Query}");

                foreach (var header in request.Headers)
                {
                    sb.AppendLine($"H {header.Key}={string.Join(",", header.Value)}");
                }

                if (request.Content is not null)
                {
                    foreach (var header in request.Content.Headers)
                    {
                        sb.AppendLine($"CH {header.Key}={string.Join(",", header.Value)}");
                    }
                }

                _owner.Requests.Add(sb.ToString());

                const string xml =
                    "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                    "<ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\">" +
                    "<Name>bucket</Name><IsTruncated>false</IsTruncated>" +
                    "<KeyCount>0</KeyCount><MaxKeys>1</MaxKeys>" +
                    "</ListBucketResult>";

                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(xml, Encoding.UTF8, "application/xml"),
                };

                return Task.FromResult(response);
            }
        }
    }
}
