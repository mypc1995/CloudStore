using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace CloudStore.Core;

/// <summary>字节数 / 速度 / 时间 / MIME / 文件名等纯展示层的格式化工具。</summary>
public static class Humanize
{
    private static readonly string[] SizeUnits = { "B", "KB", "MB", "GB", "TB", "PB" };

    public static string Bytes(long value)
    {
        if (value < 0)
        {
            return "-" + Bytes(-value);
        }

        double v = value;
        int unit = 0;
        while (v >= 1024d && unit < SizeUnits.Length - 1)
        {
            v /= 1024d;
            unit++;
        }

        return unit == 0
            ? string.Format("{0} B", value)
            : string.Format("{0:0.##} {1}", v, SizeUnits[unit]);
    }

    public static string Speed(double bytesPerSecond)
    {
        if (double.IsNaN(bytesPerSecond) || bytesPerSecond <= 0)
        {
            return "--";
        }

        return Bytes((long)bytesPerSecond) + "/s";
    }

    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        if (span.TotalSeconds < 1)
        {
            return span.TotalMilliseconds < 1000
                ? string.Format("{0} ms", span.TotalMilliseconds)
                : string.Format("{0:0.#} s", span.TotalSeconds);
        }

        if (span.TotalSeconds < 60)
        {
            return string.Format("{0:0} 秒", span.TotalSeconds);
        }

        if (span.TotalHours < 1)
        {
            return string.Format("{0:0} 分 {1:00} 秒", (int)span.TotalMinutes, span.Seconds);
        }

        return string.Format("{0} 小时 {1:00} 分", (int)span.TotalHours, span.Minutes);
    }

    public static string Eta(long remainingBytes, double bytesPerSecond)
    {
        if (bytesPerSecond <= 1 || remainingBytes <= 0)
        {
            return "--";
        }

        return Duration(TimeSpan.FromSeconds(remainingBytes / bytesPerSecond));
    }

    public static string Date(DateTimeOffset? value)
    {
        return value is null ? "-" : value.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    }

    public static string ETag(string? etag, int maxLength = 12)
    {
        if (string.IsNullOrEmpty(etag))
        {
            return "-";
        }

        string value = etag.Trim('"');
        return value.Length <= maxLength ? value : value[..maxLength];
    }

    private static readonly Dictionary<string, string> MimeByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".txt"] = "text/plain",
        [".log"] = "text/plain",
        [".md"] = "text/markdown",
        [".csv"] = "text/csv",
        [".html"] = "text/html",
        [".htm"] = "text/html",
        [".css"] = "text/css",
        [".js"] = "text/javascript",
        [".mjs"] = "text/javascript",
        [".json"] = "application/json",
        [".xml"] = "application/xml",
        [".yml"] = "application/yaml",
        [".yaml"] = "application/yaml",
        [".ini"] = "text/plain",
        [".conf"] = "text/plain",
        [".toml"] = "text/plain",
        [".sql"] = "application/sql",
        [".cs"] = "text/plain",
        [".ts"] = "text/plain",
        [".py"] = "text/plain",
        [".sh"] = "text/plain",
        [".ps1"] = "text/plain",
        [".bat"] = "text/plain",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".bmp"] = "image/bmp",
        [".webp"] = "image/webp",
        [".ico"] = "image/x-icon",
        [".svg"] = "image/svg+xml",
        [".tif"] = "image/tiff",
        [".tiff"] = "image/tiff",
        [".heic"] = "image/heic",
        [".avif"] = "image/avif",
        [".pdf"] = "application/pdf",
        [".zip"] = "application/zip",
        [".gz"] = "application/gzip",
        [".tgz"] = "application/gzip",
        [".bz2"] = "application/x-bzip2",
        [".xz"] = "application/x-xz",
        [".7z"] = "application/x-7z-compressed",
        [".rar"] = "application/vnd.rar",
        [".tar"] = "application/x-tar",
        [".mp4"] = "video/mp4",
        [".mov"] = "video/quicktime",
        [".mkv"] = "video/x-matroska",
        [".avi"] = "video/x-msvideo",
        [".webm"] = "video/webm",
        [".mp3"] = "audio/mpeg",
        [".wav"] = "audio/wav",
        [".flac"] = "audio/flac",
        [".ogg"] = "audio/ogg",
        [".m4a"] = "audio/mp4",
        [".ttf"] = "font/ttf",
        [".otf"] = "font/otf",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
        [".exe"] = "application/vnd.microsoft.portable-executable",
        [".msi"] = "application/x-msi",
        [".apk"] = "application/vnd.android.package-archive",
        [".iso"] = "application/x-iso9660-image",
        [".bin"] = "application/octet-stream",
        [".wasm"] = "application/wasm",
        [".doc"] = "application/msword",
        [".xls"] = "application/vnd.ms-excel",
        [".ppt"] = "application/vnd.ms-powerpoint",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
    };

    public static string GuessMime(string? fileNameOrKey)
    {
        if (string.IsNullOrWhiteSpace(fileNameOrKey))
        {
            return "application/octet-stream";
        }

        string ext = Path.GetExtension(fileNameOrKey);
        return MimeByExtension.TryGetValue(ext, out string? mime) ? mime : "application/octet-stream";
    }

    public static bool IsTextualMime(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        if (contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (contentType.EndsWith("+json", StringComparison.OrdinalIgnoreCase) ||
            contentType.EndsWith("+xml", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return contentType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
               contentType.Equals("application/xml", StringComparison.OrdinalIgnoreCase) ||
               contentType.Equals("application/javascript", StringComparison.OrdinalIgnoreCase) ||
               contentType.Equals("application/x-yaml", StringComparison.OrdinalIgnoreCase) ||
               contentType.Equals("application/x-sh", StringComparison.OrdinalIgnoreCase) ||
               contentType.Equals("application/sql", StringComparison.OrdinalIgnoreCase) ||
               contentType.Equals("application/x-msdownload", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsImageMime(string? contentType)
    {
        return !string.IsNullOrWhiteSpace(contentType) &&
               contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) &&
               !contentType.Equals("image/svg+xml", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly char[] InvalidChars =
        Path.GetInvalidFileNameChars().Concat(new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }).Distinct().ToArray();

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>把对象 key 转换成安全的本地相对路径（逐段清洗，保留目录结构）。</summary>
    public static string ToSafeRelativePath(string key)
    {
        string normalized = key.Replace('\\', '/').TrimStart('/');
        if (normalized.Length == 0)
        {
            return "_unnamed_";
        }

        var segments = normalized.Split('/', StringSplitOptions.None)
            .Select(SanitizeSegment)
            .ToArray();

        return string.Join(Path.DirectorySeparatorChar.ToString(), segments);
    }

    public static string SanitizeSegment(string segment)
    {
        if (segment.Length == 0)
        {
            return "_empty_";
        }

        var sb = new StringBuilder(segment.Length);
        foreach (char c in segment)
        {
            sb.Append(Array.IndexOf(InvalidChars, c) >= 0 || char.IsControl(c) ? '_' : c);
        }

        string value = sb.ToString().TrimEnd('.', ' ');

        // 防止 key 形如 "a/../b" 越出下载目录
        if (value is "." or "..")
        {
            value = "_" + value.Replace(".", "_");
        }

        if (ReservedNames.Contains(Path.GetFileNameWithoutExtension(value)) && value.Contains('.'))
        {
            value = "_" + value;
        }

        if (value.Length == 0)
        {
            value = "_unnamed_";
        }

        if (value.Length > 120)
        {
            string ext = Path.GetExtension(value);
            if (ext.Length > 12)
            {
                ext = "";
            }

            value = value[..(120 - ext.Length)] + ext;
        }

        return value;
    }

    /// <summary>把 key 的最后一段作为文件名（用于“另存为”默认名）。</summary>
    public static string LeafName(string key)
    {
        string normalized = key.Replace('\\', '/').TrimEnd('/');
        int index = normalized.LastIndexOf('/');
        string leaf = index >= 0 ? normalized[(index + 1)..] : normalized;
        return SanitizeSegment(leaf);
    }

    public static string ParentPrefix(string prefix)
    {
        if (string.IsNullOrEmpty(prefix))
        {
            return string.Empty;
        }

        string normalized = prefix.TrimEnd('/');
        int index = normalized.LastIndexOf('/');
        return index < 0 ? string.Empty : normalized[..(index + 1)];
    }

    /// <summary>图标分类，供 UI 选择 16x16 图标。</summary>
    public static string IconKind(string key, string? contentType = null)
    {
        string mime = contentType ?? GuessMime(key);
        string ext = Path.GetExtension(key).ToLowerInvariant();

        if (mime.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return "image";
        }

        if (mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
        {
            return "video";
        }

        if (mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
        {
            return "audio";
        }

        if (ext is ".zip" or ".gz" or ".tgz" or ".7z" or ".rar" or ".bz2" or ".xz" or ".tar")
        {
            return "archive";
        }

        if (ext is ".cs" or ".ts" or ".js" or ".json" or ".xml" or ".yml" or ".yaml" or ".py" or ".sh"
            or ".ps1" or ".bat" or ".html" or ".css" or ".sql" or ".go" or ".rs" or ".java" or ".c"
            or ".h" or ".cpp" or ".toml" or ".ini")
        {
            return "code";
        }

        if (ext is ".pdf" or ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx" or ".md")
        {
            return "doc";
        }

        if (IsTextualMime(mime) || ext is ".txt" or ".log" or ".csv")
        {
            return "text";
        }

        return "file";
    }
}
