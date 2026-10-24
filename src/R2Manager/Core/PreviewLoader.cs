using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CloudStore.Core;

public enum PreviewKind
{
    Image,
    Text,
    Binary,
}

public sealed class PreviewContent
{
    public PreviewKind Kind { get; init; }

    public Image? Image { get; init; }

    public string? Text { get; init; }

    public string? Message { get; init; }

    public string? HexDump { get; init; }

    public string? ContentType { get; init; }

    public string? EncodingName { get; init; }

    public long Length { get; init; }

    public DateTimeOffset? LastModified { get; init; }

    public string? ETag { get; init; }

    public string? StorageClass { get; init; }

    public bool Truncated { get; init; }

    public Dictionary<string, string> Metadata { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>“查看”功能：按内容类型拉取并解析图片 / 文本 / 二进制片段。</summary>
public static class PreviewLoader
{
    public const long MaxImageBytes = 64L * 1024 * 1024;
    public const long MaxTextBytes = 4L * 1024 * 1024;
    private const int HexBytes = 256;

    public static async Task<PreviewContent> LoadAsync(StorageService service, string key, CancellationToken ct)
    {
        var meta = await service.HeadAsync(key, ct).ConfigureAwait(false);

        long length = meta.ContentLength;
        string contentType = string.IsNullOrWhiteSpace(meta.Headers.ContentType)
            ? Humanize.GuessMime(key)
            : meta.Headers.ContentType;

        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string metadataKey in meta.Metadata.Keys)
        {
            metadata[metadataKey] = meta.Metadata[metadataKey];
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (meta.Headers is not null)
        {
            if (!string.IsNullOrWhiteSpace(meta.Headers.ContentType))
            {
                headers["Content-Type"] = meta.Headers.ContentType;
            }

            if (!string.IsNullOrWhiteSpace(meta.Headers.ContentEncoding))
            {
                headers["Content-Encoding"] = meta.Headers.ContentEncoding;
            }

            if (!string.IsNullOrWhiteSpace(meta.Headers.CacheControl))
            {
                headers["Cache-Control"] = meta.Headers.CacheControl;
            }

            if (!string.IsNullOrWhiteSpace(meta.Headers.ContentDisposition))
            {
                headers["Content-Disposition"] = meta.Headers.ContentDisposition;
            }

            headers["Last-Modified"] = Humanize.Date(meta.LastModified);
            headers["ETag"] = meta.ETag ?? "-";
        }

        PreviewContent content;
        bool wantImage = Humanize.IsImageMime(contentType) && length <= MaxImageBytes;
        bool wantText = Humanize.IsTextualMime(contentType) && length <= MaxTextBytes;

        if (wantImage)
        {
            content = await LoadImageAsync(service, key, meta, contentType, metadata, headers, ct).ConfigureAwait(false);
        }
        else if (wantText)
        {
            content = await LoadTextAsync(service, key, meta, contentType, metadata, headers, ct).ConfigureAwait(false);
        }
        else
        {
            content = new PreviewContent
            {
                Kind = PreviewKind.Binary,
                ContentType = contentType,
                Message = DescribeUnsupported(contentType, length),
                HexDump = length > 0 && length <= MaxTextBytes
                    ? await LoadHexDumpAsync(service, key, ct).ConfigureAwait(false)
                    : null,
                Metadata = metadata,
                Headers = headers,
            };
        }

        return new PreviewContent
        {
            Kind = content.Kind,
            Image = content.Image,
            Text = content.Text,
            Message = content.Message,
            HexDump = content.HexDump,
            ContentType = contentType,
            EncodingName = content.EncodingName,
            Length = length,
            LastModified = meta.LastModified,
            ETag = meta.ETag,
            StorageClass = meta.StorageClass,
            Truncated = content.Truncated,
            Metadata = metadata,
            Headers = headers,
        };
    }

    private static string DescribeUnsupported(string contentType, long length)
    {
        if (length > MaxTextBytes && Humanize.IsTextualMime(contentType))
        {
            return $"文件较大（{Humanize.Bytes(length)}），仅显示前 {Humanize.Bytes(HexBytes)} 字节。完整内容请下载后查看。";
        }

        if (Humanize.IsImageMime(contentType) && length > MaxImageBytes)
        {
            return $"图片超过 {Humanize.Bytes(MaxImageBytes)} 的预览上限（{Humanize.Bytes(length)}），请下载后查看。";
        }

        if (contentType.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase))
        {
            return "SVG 属于矢量图，预览时可能被裁剪，建议下载后用浏览器打开。";
        }

        return $"暂不支持预览该类型（{contentType}，{Humanize.Bytes(length)}）。可以下载后用系统默认程序打开。";
    }

    private static async Task<PreviewContent> LoadImageAsync(
        StorageService service,
        string key,
        Amazon.S3.Model.GetObjectMetadataResponse meta,
        string contentType,
        Dictionary<string, string> metadata,
        Dictionary<string, string> headers,
        CancellationToken ct)
    {
        byte[] bytes;
        using (ObjectStream remote = await service.OpenAsync(key, null, null, ct).ConfigureAwait(false))
        using (var buffer = new MemoryStream())
        {
            await remote.Body.CopyToAsync(buffer, 128 * 1024, ct).ConfigureAwait(false);
            bytes = buffer.ToArray();
        }

        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            using Image decoded = Image.FromStream(ms);

            var copy = new Bitmap(decoded.Width, decoded.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics graphics = Graphics.FromImage(copy))
            {
                graphics.Clear(Color.Transparent);
                graphics.DrawImage(decoded, 0, 0, decoded.Width, decoded.Height);
            }

            return new PreviewContent
            {
                Kind = PreviewKind.Image,
                Image = copy,
                Message = $"{decoded.Width} × {decoded.Height} 像素",
                Metadata = metadata,
                Headers = headers,
            };
        }
        catch (Exception ex)
        {
            return new PreviewContent
            {
                Kind = PreviewKind.Binary,
                Message = "无法解码该图片：" + ex.Message,
                Metadata = metadata,
                Headers = headers,
            };
        }
    }

    private static async Task<PreviewContent> LoadTextAsync(
        StorageService service,
        string key,
        Amazon.S3.Model.GetObjectMetadataResponse meta,
        string contentType,
        Dictionary<string, string> metadata,
        Dictionary<string, string> headers,
        CancellationToken ct)
    {
        byte[] bytes;
        long read = 0;

        using (ObjectStream remote = await service.OpenAsync(key, 0, meta.ContentLength - 1, ct).ConfigureAwait(false))
        using (var buffer = new MemoryStream())
        {
            byte[] chunk = new byte[128 * 1024];
            int n;
            while ((n = await remote.Body.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, n);
                read += n;
                if (buffer.Length >= MaxTextBytes)
                {
                    break;
                }
            }

            bytes = buffer.ToArray();
        }

        if (LooksBinary(bytes))
        {
            return new PreviewContent
            {
                Kind = PreviewKind.Binary,
                Message = "内容不是文本，可能是压缩包或其他二进制格式。",
                HexDump = BuildHexDump(bytes),
                Metadata = metadata,
                Headers = headers,
            };
        }

        (string text, string encoding) = Decode(bytes);

        int cut = Array.IndexOf(bytes, (byte)'\n', bytes.Length - 1);
        bool truncated = meta.ContentLength > bytes.Length;
        if (truncated && cut > 0 && cut + 1 < bytes.Length)
        {
            text = text[..(cut + 1)] + Environment.NewLine + "...（内容过长，已截断）";
        }

        return new PreviewContent
        {
            Kind = PreviewKind.Text,
            Text = text,
            EncodingName = encoding,
            Truncated = truncated,
            Metadata = metadata,
            Headers = headers,
        };
    }

    private static async Task<string?> LoadHexDumpAsync(StorageService service, string key, CancellationToken ct)
    {
        try
        {
            using ObjectStream remote = await service.OpenAsync(key, 0, HexBytes - 1, ct).ConfigureAwait(false);
            byte[] bytes = new byte[HexBytes];
            int total = 0;
            int n;
            while (total < HexBytes && (n = await remote.Body.ReadAsync(bytes.AsMemory(total), ct).ConfigureAwait(false)) > 0)
            {
                total += n;
            }

            return BuildHexDump(bytes.AsSpan(0, total).ToArray());
        }
        catch
        {
            return null;
        }
    }

    private static bool LooksBinary(byte[] data)
    {
        int limit = Math.Min(data.Length, 8192);
        for (int i = 0; i < limit; i++)
        {
            if (data[i] == 0)
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildHexDump(byte[] data)
    {
        var sb = new StringBuilder();
        for (int offset = 0; offset < data.Length; offset += 16)
        {
            sb.Append(offset.ToString("x8")).Append("  ");

            for (int i = 0; i < 16; i++)
            {
                sb.Append(offset + i < data.Length ? data[offset + i].ToString("x2") : "  ").Append(' ');
                if (i == 7)
                {
                    sb.Append(' ');
                }
            }

            sb.Append(' ');

            for (int i = 0; i < 16 && offset + i < data.Length; i++)
            {
                byte b = data[offset + i];
                sb.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static (string Text, string Encoding) Decode(byte[] data)
    {
        try
        {
            return (new UTF8Encoding(false, true).GetString(data), "UTF-8");
        }
        catch (DecoderFallbackException)
        {
            // 继续尝试其他编码
        }

        foreach (string name in new[] { "GB18030", "GBK", "Big5", "Shift-JIS", "Windows-1252" })
        {
            try
            {
                Encoding encoding = Encoding.GetEncoding(name);
                string text = encoding.GetString(data);

                if (!LooksBinary(Encoding.UTF8.GetBytes(text)))
                {
                    return (text, name);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or DecoderFallbackException)
            {
                // 该编码不可用，继续
            }
        }

        return (Encoding.Latin1.GetString(data), "ISO-8859-1");
    }
}
