using System;
using System.Linq;
using System.Text.Json.Serialization;

namespace CloudStore.Core;

/// <summary>支持的云厂商。</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StorageProvider
{
    /// <summary>Cloudflare R2。</summary>
    CloudflareR2 = 0,

    /// <summary>阿里云 OSS。</summary>
    AliyunOss = 1,

    /// <summary>腾讯云 COS。</summary>
    TencentCos = 2,

    /// <summary>自建 / 私有 S3 兼容存储（MinIO、Ceph RGW、SeaweedFS 等）。</summary>
    SelfHosted = 3,
}

/// <summary>自建 S3 兼容存储的寻址方式。公有云是固定的，只有自建需要用户选。</summary>
public enum S3Addressing
{
    /// <summary>https://minio.example.com/bucket/key（MinIO 默认）</summary>
    PathStyle = 0,

    /// <summary>https://bucket.minio.example.com/key</summary>
    VirtualHosted = 1,
}

/// <summary>
/// 各家云在「接入方式」上的差异全集中在这张表里。
/// <para>
/// 三家都提供 S3 兼容接口，所以传输、分片、预览这些逻辑完全共用，
/// 真正需要按厂商区分的只有五件事：
/// 端点怎么拼、用 path 还是 virtual-hosted 寻址、签名用哪个地域、
/// 凭据字段叫什么、桶名和公开链接各有什么规矩。
/// </para>
/// </summary>
public static class StorageProviders
{
    public static readonly StorageProvider[] All =
    {
        StorageProvider.CloudflareR2,
        StorageProvider.AliyunOss,
        StorageProvider.TencentCos,
        StorageProvider.SelfHosted,
    };

    public static string DisplayName(StorageProvider provider) => provider switch
    {
        StorageProvider.AliyunOss => "阿里云 OSS",
        StorageProvider.TencentCos => "腾讯云 COS",
        StorageProvider.SelfHosted => "自建 / 私有 S3 兼容存储",
        _ => "Cloudflare R2",
    };

    /// <summary>只有 R2 需要 Account ID 来推导端点。</summary>
    public static bool RequiresAccountId(StorageProvider provider) =>
        provider == StorageProvider.CloudflareR2;

    /// <summary>
    /// 只有公有云的 R2 有「公开开发域名」这种内置概念，
    /// 阿里云 / 腾讯云 / 自建都不需要，界面上按厂商隐藏。
    /// </summary>
    public static bool ShowsPublicDomain(StorageProvider provider) =>
        provider == StorageProvider.CloudflareR2;

    /// <summary>自建存储要手填完整端点，公有云则按地域自动推导。</summary>
    public static bool RequiresCustomEndpoint(StorageProvider provider) =>
        provider == StorageProvider.SelfHosted;

    /// <summary>自建存储的寻址方式不固定，必须让用户选。</summary>
    public static bool ShowsAddressingMode(StorageProvider provider) =>
        provider == StorageProvider.SelfHosted;

    public static string AccountIdLabel(StorageProvider provider) =>
        provider == StorageProvider.CloudflareR2 ? "Cloudflare 账户 ID（Account ID）" : "账户 ID";

    public static string AccessKeyLabel(StorageProvider provider) => provider switch
    {
        StorageProvider.AliyunOss => "AccessKey ID",
        StorageProvider.TencentCos => "SecretId",
        StorageProvider.SelfHosted => "Access Key",
        _ => "Access Key ID",
    };

    public static string SecretKeyLabel(StorageProvider provider) => provider switch
    {
        StorageProvider.AliyunOss => "AccessKey Secret",
        StorageProvider.TencentCos => "SecretKey",
        StorageProvider.SelfHosted => "Secret Key",
        _ => "Secret Access Key",
    };

    public static string RegionLabel(StorageProvider provider) => provider switch
    {
        StorageProvider.SelfHosted => "地域（Region，可留空）",
        _ => "地域（Region）",
    };

    /// <summary>输入框下方的示例提示。</summary>
    public static string RegionHint(StorageProvider provider) => provider switch
    {
        StorageProvider.AliyunOss => "例如 cn-hangzhou、cn-shanghai、oss-cn-hangzhou",
        StorageProvider.TencentCos => "例如 ap-guangzhou、ap-shanghai、cos.ap-guangzhou.myqcloud.com",
        StorageProvider.SelfHosted => "多数自建存储不校验，留空即可",
        _ => "R2 固定为 auto，无需修改",
    };

    /// <summary>该厂商的默认地域。</summary>
    public static string DefaultRegion(StorageProvider provider) => provider switch
    {
        StorageProvider.AliyunOss => "cn-hangzhou",
        StorageProvider.TencentCos => "ap-guangzhou",
        StorageProvider.SelfHosted => "us-east-1",
        _ => "auto",
    };

    /// <summary>填写地域时的示例值（用于占位提示）。</summary>
    public static string RegionPlaceholder(StorageProvider provider) => provider switch
    {
        StorageProvider.AliyunOss => "cn-hangzhou",
        StorageProvider.TencentCos => "ap-guangzhou",
        _ => "auto",
    };

    /// <summary>
    /// 归一化地域写法。用户可能填 cn-hangzhou，也可能直接粘控制台上的
    /// oss-cn-hangzhou 甚至完整域名，这里统一还原成地域 ID。
    /// </summary>
    public static string NormalizeRegion(StorageProvider provider, string? region)
    {
        string raw = (region ?? string.Empty).Trim();

        if (provider == StorageProvider.CloudflareR2)
        {
            return "auto";
        }

        // "auto" 只有 R2 才认；阿里云 / 腾讯云必须给真实地域。
        if (raw.Length == 0 || raw.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return DefaultRegion(provider);
        }

        return provider == StorageProvider.AliyunOss ? ExtractOssRegion(raw) : ExtractCosRegion(raw);
    }

    private static string ExtractOssRegion(string raw)
    {
        string s = raw.ToLowerInvariant();

        int scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            s = s[(scheme + 3)..];
        }

        int slash = s.IndexOf('/');
        if (slash >= 0)
        {
            s = s[..slash];
        }

        // s3.oss-cn-hangzhou.aliyuncs.com → oss-cn-hangzhou.aliyuncs.com
        int dot = s.IndexOf('.');
        if (dot >= 0)
        {
            s = s[(dot + 1)..];
        }

        int dot2 = s.IndexOf('.');
        if (dot2 >= 0)
        {
            s = s[..dot2];
        }

        if (s.StartsWith("oss-", StringComparison.Ordinal))
        {
            s = s[4..];
        }

        return s.Length == 0 ? DefaultRegion(StorageProvider.AliyunOss) : s;
    }

    private static string ExtractCosRegion(string raw)
    {
        string s = raw.ToLowerInvariant();

        int scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            s = s[(scheme + 3)..];
        }

        int slash = s.IndexOf('/');
        if (slash >= 0)
        {
            s = s[..slash];
        }

        if (s.StartsWith("cos.", StringComparison.Ordinal))
        {
            s = s[4..];
        }

        int dot = s.IndexOf('.');
        if (dot >= 0)
        {
            s = s[..dot];
        }

        return s.Length == 0 ? DefaultRegion(StorageProvider.TencentCos) : s;
    }

    /// <summary>按厂商拼出 S3 端点（不含桶名）。</summary>
    public static string ResolveEndpoint(StorageProvider provider, StorageSettings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.CustomEndpoint) &&
            settings.CustomEndpoint.Contains("://", StringComparison.Ordinal))
        {
            return settings.CustomEndpoint.Trim().TrimEnd('/');
        }

        // 自建存储没有统一的端点规则，必须由用户给出。
        if (provider == StorageProvider.SelfHosted)
        {
            return string.Empty;
        }

        string region = NormalizeRegion(provider, settings.Region);

        return provider switch
        {
            // R2 的端点由账户 ID 推导
            StorageProvider.AliyunOss => $"https://s3.oss-{region}.aliyuncs.com",
            StorageProvider.TencentCos => $"https://cos.{region}.myqcloud.com",
            _ => $"https://{settings.AccountId.Trim()}.r2.cloudflarestorage.com",
        };
    }

    /// <summary>
    /// 寻址方式。
    /// R2 用 path-style（桶名放在路径里）；OSS 与 COS 都要求 virtual-hosted（桶名放主机名），
    /// 腾讯云 2024 年 1 月 1 日后新建的存储桶更是明确不再支持 path-style。
    /// 自建存储两种都常见（MinIO 默认 path-style），由用户自己选。
    /// </summary>
    public static bool ForcePathStyle(StorageProvider provider, StorageSettings? settings = null)
    {
        if (provider == StorageProvider.CloudflareR2)
        {
            return true;
        }

        if (provider == StorageProvider.SelfHosted)
        {
            return (settings?.Addressing ?? S3Addressing.PathStyle) == S3Addressing.PathStyle;
        }

        return false;
    }

    /// <summary>公开访问链接（桶权限允许公开读时有效）。</summary>
    public static string BuildPublicUrl(StorageProvider provider, StorageSettings settings, string key)
    {
        string domain = settings.PublicDomain?.Trim() ?? string.Empty;

        if (domain.Length > 0)
        {
            if (!domain.Contains("://", StringComparison.Ordinal))
            {
                domain = "https://" + domain;
            }

            return $"{domain.TrimEnd('/')}/{EscapeKey(key)}";
        }

        string bucket = settings.BucketName.Trim();
        string region = NormalizeRegion(provider, settings.Region);
        string endpoint = ResolveEndpoint(provider, settings);

        string prefix = provider switch
        {
            // 阿里云的访问域名不带 s3. 前缀
            StorageProvider.AliyunOss => $"https://{bucket}.oss-{region}.aliyuncs.com",
            StorageProvider.TencentCos => $"https://{bucket}.cos.{region}.myqcloud.com",
            StorageProvider.SelfHosted => ForcePathStyle(provider, settings)
                ? $"{endpoint}/{Uri.EscapeDataString(bucket)}"
                : InsertBucketIntoHost(endpoint, bucket),
            _ => $"{endpoint}/{Uri.EscapeDataString(bucket)}",
        };

        return $"{prefix}/{EscapeKey(key)}";
    }

    /// <summary>把桶名插进主机名：https://minio.example.com → https://bucket.minio.example.com</summary>
    private static string InsertBucketIntoHost(string endpoint, string bucket)
    {
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
        {
            var builder = new UriBuilder(uri) { Host = bucket + "." + uri.Host };
            return builder.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        }

        return endpoint;
    }

    private static string EscapeKey(string key) =>
        string.Join('/', key.Split('/').Select(Uri.EscapeDataString));

    /// <summary>桶名校验，返回 null 表示合法。</summary>
    public static string? ValidateBucket(StorageProvider provider, string bucket)
    {
        string name = bucket.Trim();

        if (name.Length == 0)
        {
            return "请填写或选择一个存储桶。";
        }

        if (provider == StorageProvider.AliyunOss)
        {
            if (name.Length is < 3 or > 63)
            {
                return "阿里云 OSS 的桶名长度需在 3 ~ 63 个字符之间。";
            }

            if (name.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')))
            {
                return "阿里云 OSS 的桶名只能包含小写字母、数字和短横线。";
            }

            if (!char.IsAsciiLetterLower(name[0]) && !char.IsAsciiDigit(name[0]))
            {
                return "阿里云 OSS 的桶名必须以小写字母或数字开头。";
            }

            if (!char.IsAsciiLetterLower(name[^1]) && !char.IsAsciiDigit(name[^1]))
            {
                return "阿里云 OSS 的桶名必须以小写字母或数字结尾。";
            }

            return null;
        }

        if (name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_')))
        {
            return "桶名只能包含字母、数字、短横线、下划线和点。";
        }

        return null;
    }

    /// <summary>桶名规则说明，显示在输入框下方。</summary>
    public static string BucketHint(StorageProvider provider) => provider switch
    {
        StorageProvider.AliyunOss => "全局唯一，小写字母 / 数字 / 短横线，3~63 字符",
        StorageProvider.TencentCos => "必须带 APPID 后缀，例如 mybucket-1250000000",
        StorageProvider.SelfHosted => "填自建存储里的桶名，例如 backup",
        _ => "Cloudflare R2 中创建的桶名，全局唯一",
    };

    /// <summary>去哪里拿密钥，写在界面上免得用户到处找。</summary>
    public static string CredentialHint(StorageProvider provider) => provider switch
    {
        StorageProvider.AliyunOss => "RAM 访问控制 → AccessKey 页面创建，需要授予目标桶的读写权限。",
        StorageProvider.TencentCos => "访问管理 → API 密钥管理，填 SecretId / SecretKey；并记下账号 APPID。",
        StorageProvider.SelfHosted => "自建存储的服务端凭据，例如 MinIO 的 mc admin user add 创建的账号。",
        _ => "Cloudflare 控制台 → R2 → Manage R2 API Tokens，Account-level 令牌才能列出全部桶。",
    };

    /// <summary>打开控制台的按钮用得上。</summary>
    public static string ConsoleUrl(StorageProvider provider) => provider switch
    {
        StorageProvider.AliyunOss => "https://ram.console.aliyun.com/manage/ak",
        StorageProvider.TencentCos => "https://console.cloud.tencent.com/cam/capi",
        StorageProvider.SelfHosted => "https://min.io/docs/minio/linux/integrations-and-tools.html",
        _ => "https://dash.cloudflare.com/",
    };

    /// <summary>「打开控制台」按钮上的文字。</summary>
    public static string ConsoleButtonText(StorageProvider provider) => provider switch
    {
        StorageProvider.AliyunOss => "打开阿里云 RAM 控制台",
        StorageProvider.TencentCos => "打开腾讯云密钥页",
        StorageProvider.SelfHosted => "自建存储文档",
        _ => "打开 Cloudflare 控制台",
    };
}
