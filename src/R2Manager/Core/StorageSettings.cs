using System;
using System.Net;
using System.Text.Json.Serialization;

namespace CloudStore.Core;

/// <summary>连接对象存储所需的全部配置（密钥字段单独加密保存）。</summary>
public sealed class StorageSettings
{
    /// <summary>云厂商。决定端点怎么拼、用哪种寻址方式、凭据字段叫什么。</summary>
    public StorageProvider Provider { get; set; } = StorageProvider.CloudflareR2;

    /// <summary>Cloudflare 账户 ID（R2 端点由它推导；阿里云 / 腾讯云不需要）。</summary>
    public string AccountId { get; set; } = string.Empty;

    public string AccessKeyId { get; set; } = string.Empty;

    [JsonIgnore]
    public string SecretAccessKey { get; set; } = string.Empty;

    /// <summary>DPAPI 加密后的密钥，仅用于本地持久化。</summary>
    public string ProtectedSecret { get; set; } = string.Empty;

    public string BucketName { get; set; } = string.Empty;

    /// <summary>
    /// 地域。R2 固定 auto；阿里云填 cn-hangzhou 这类地域 ID；
    /// 腾讯云填 ap-guangzhou 这类地域 ID。写法不统一也没关系，取用时会归一化。
    /// </summary>
    public string Region { get; set; } = "auto";

    /// <summary>留空则按厂商与地域自动生成。</summary>
    public string CustomEndpoint { get; set; } = string.Empty;

    /// <summary>自建存储的寻址方式（公有云固定，无需设置）。</summary>
    public S3Addressing Addressing { get; set; } = S3Addressing.PathStyle;

    /// <summary>可选：绑在桶上的公开域名，用于“复制链接”。</summary>
    public string PublicDomain { get; set; } = string.Empty;

    public bool RememberSecret { get; set; }

    public bool AutoConnect { get; set; } = true;

    public int MaxConcurrency { get; set; } = 3;

    public int MultipartThresholdMb { get; set; } = 16;

    public int PartSizeMb { get; set; } = 16;

    /// <summary>上传遇到同名对象时覆盖。</summary>
    public bool OverwriteExisting { get; set; }

    /// <summary>上传遇到同名对象时跳过。</summary>
    public bool SkipExisting { get; set; }

    public string LastPrefix { get; set; } = string.Empty;

    public string DownloadDirectory { get; set; } = string.Empty;

    public int WindowWidth { get; set; } = 1280;

    public int WindowHeight { get; set; } = 820;

    public bool WindowMaximized { get; set; }

    public int SplitterDistance { get; set; } = 940;

    public int TransferPanelHeight { get; set; } = 190;

    public bool TransferPanelVisible { get; set; } = true;

    /// <summary>启动时的提示（例如密钥无法解密），不参与序列化。</summary>
    [JsonIgnore]
    public string? Warning { get; set; }

    public const int MinPartSizeMb = 5;
    public const int MaxPartSizeMb = 5120;

    public long MultipartThresholdBytes => Math.Clamp(MultipartThresholdMb, 5, 4096) * 1024L * 1024L;

    public long PartSizeBytes => Math.Clamp(PartSizeMb, MinPartSizeMb, MaxPartSizeMb) * 1024L * 1024L;

    public int Concurrency => Math.Clamp(MaxConcurrency, 1, 16);

    /// <summary>归一化后的地域，取用时一律走这个。</summary>
    public string NormalizedRegion => StorageProviders.NormalizeRegion(Provider, Region);

    public bool HasCredentials =>
        (Provider != StorageProvider.CloudflareR2 || !string.IsNullOrWhiteSpace(AccountId)) &&
        (Provider != StorageProvider.SelfHosted || !string.IsNullOrWhiteSpace(CustomEndpoint)) &&
        !string.IsNullOrWhiteSpace(AccessKeyId) &&
        !string.IsNullOrWhiteSpace(SecretAccessKey);

    public bool IsReady => HasCredentials && !string.IsNullOrWhiteSpace(BucketName);

    /// <summary>寻址方式。自建存储由用户选，公有云是固定的。</summary>
    public bool UsesPathStyle => StorageProviders.ForcePathStyle(Provider, this);

    public string ResolveEndpoint() => StorageProviders.ResolveEndpoint(Provider, this);

    public string EndpointHost
    {
        get
        {
            try
            {
                return new Uri(ResolveEndpoint()).Host;
            }
            catch (UriFormatException)
            {
                return ResolveEndpoint();
            }
        }
    }

    /// <summary>返回第一条校验错误；全部通过时返回 null。</summary>
    public string? Validate()
    {
        if (Provider == StorageProvider.CloudflareR2)
        {
            if (string.IsNullOrWhiteSpace(AccountId))
            {
                return "请填写 Cloudflare 账户 ID（Account ID）。";
            }

            if (!AccountId.Trim().All(c => char.IsAsciiLetterOrDigit(c) || c == '-') || AccountId.Trim().Length < 8)
            {
                return "Account ID 格式不正确，应为 32 位十六进制字符串。";
            }
        }
        else if (StorageProviders.RequiresCustomEndpoint(Provider))
        {
            // 自建存储没有统一的端点规则，必须由用户给出。
            if (string.IsNullOrWhiteSpace(CustomEndpoint))
            {
                return "请填写 Endpoint，例如 https://minio.example.com";
            }

            if (!Uri.TryCreate(CustomEndpoint.Trim(), UriKind.Absolute, out var selfHosted) ||
                (selfHosted.Scheme != Uri.UriSchemeHttp && selfHosted.Scheme != Uri.UriSchemeHttps))
            {
                return "Endpoint 必须是以 http:// 或 https:// 开头的完整地址。";
            }
        }
        else if (string.IsNullOrWhiteSpace(NormalizedRegion))
        {
            return $"请填写{StorageProviders.RegionLabel(Provider)}。";
        }

        if (string.IsNullOrWhiteSpace(AccessKeyId))
        {
            return $"请填写{StorageProviders.AccessKeyLabel(Provider)}。";
        }

        if (string.IsNullOrWhiteSpace(SecretAccessKey))
        {
            return $"请填写{StorageProviders.SecretKeyLabel(Provider)}。";
        }

        string? bucketError = StorageProviders.ValidateBucket(Provider, BucketName);
        if (bucketError is not null)
        {
            return bucketError;
        }

        if (!string.IsNullOrWhiteSpace(CustomEndpoint) && CustomEndpoint.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(CustomEndpoint.Trim(), UriKind.Absolute, out _))
            {
                return "自定义 Endpoint 不是合法的绝对地址。";
            }
        }

        if (!string.IsNullOrWhiteSpace(PublicDomain))
        {
            string domain = PublicDomain.Trim();
            if (!domain.Contains('.', StringComparison.Ordinal))
            {
                return "公开域名看起来不合法，例如 https://cdn.example.com。";
            }
        }

        if (MultipartThresholdMb < 5)
        {
            return "分片阈值不能小于 5 MB。";
        }

        if (PartSizeMb < MinPartSizeMb || PartSizeMb > MaxPartSizeMb)
        {
            return $"分片大小需在 {MinPartSizeMb} ~ {MaxPartSizeMb} MB 之间。";
        }

        if (MaxConcurrency is < 1 or > 16)
        {
            return "并发数需在 1 ~ 16 之间。";
        }

        return null;
    }

    public StorageSettings Clone()
    {
        return (StorageSettings)MemberwiseClone();
    }

    public string DisplayTarget => IsReady
        ? $"{BucketName} @ {EndpointHost}"
        : "未配置";
}
