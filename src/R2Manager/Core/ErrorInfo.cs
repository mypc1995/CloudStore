using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Amazon.Runtime;
using Amazon.S3;

namespace CloudStore.Core;

/// <summary>统一的异常解释、日志与弹窗入口。</summary>
public static class ErrorInfo
{
    private static readonly object LogLock = new();

    public static string LogDirectory
    {
        get
        {
            // 跟配置放一起：程序目录（安装路径）优先，不可用时 AppPaths 会退回用户目录。
            string dir = Path.Combine(AppPaths.DataDirectory, "logs");

            try
            {
                Directory.CreateDirectory(dir);
                return dir;
            }
            catch (Exception)
            {
                // 数据目录不可写（受限环境、漫游配置异常、磁盘满）时退回临时目录。
                // 这里绝不能抛异常：日志写不出去不应该让整个程序崩掉，
                // 更不能因为「写日志失败」而变成用户看不到任何提示的静默退出。
                string fallback = Path.Combine(Path.GetTempPath(), "R2Manager", "logs");
                try
                {
                    Directory.CreateDirectory(fallback);
                }
                catch (Exception)
                {
                    // 连临时目录都建不出来，那就只能放弃写日志。
                }

                return fallback;
            }
        }
    }

    public static string LogPath => Path.Combine(LogDirectory,
        $"r2manager-{DateTime.Now:yyyyMMdd}.log");

    public static bool IsCanceled(Exception? ex)
    {
        while (ex is not null)
        {
            if (ex is OperationCanceledException or TaskCanceledException)
            {
                return true;
            }

            ex = ex.InnerException;
        }

        return false;
    }

    /// <summary>
    /// 密钥本身有问题（填错、过期、被删），与「令牌有效但没有某个操作的权限」是两回事，
    /// 给用户的整改动作完全不同，不能混为一谈。
    /// </summary>
    public static bool IsBadCredentials(Exception? ex)
    {
        while (ex is not null)
        {
            if (ex is AmazonServiceException ase)
            {
                string code = ase.ErrorCode ?? string.Empty;
                if (code.Contains("SignatureDoesNotMatch", StringComparison.OrdinalIgnoreCase) ||
                    code.Contains("InvalidAccessKeyId", StringComparison.OrdinalIgnoreCase) ||
                    code.Contains("ExpiredToken", StringComparison.OrdinalIgnoreCase) ||
                    code.Contains("TokenRefreshRequired", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            ex = ex.InnerException;
        }

        return false;
    }

    public static bool IsAuthFailure(Exception? ex)
    {
        while (ex is not null)
        {
            if (ex is AmazonServiceException ase)
            {
                string code = ase.ErrorCode ?? string.Empty;
                if (code.Contains("SignatureDoesNotMatch", StringComparison.OrdinalIgnoreCase) ||
                    code.Contains("InvalidAccessKeyId", StringComparison.OrdinalIgnoreCase) ||
                    code.Contains("AccessDenied", StringComparison.OrdinalIgnoreCase) ||
                    ase.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    return true;
                }
            }

            ex = ex.InnerException;
        }

        return false;
    }

    public static bool IsNotFound(Exception? ex)
    {
        while (ex is not null)
        {
            if (ex is AmazonS3Exception s3 && s3.StatusCode == HttpStatusCode.NotFound)
            {
                return true;
            }

            if (ex is AmazonS3Exception s3b)
            {
                string code = s3b.ErrorCode ?? string.Empty;
                if (code.Contains("NoSuchKey", StringComparison.OrdinalIgnoreCase) ||
                    code.Contains("NoSuchBucket", StringComparison.OrdinalIgnoreCase) ||
                    code.Contains("NotFound", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            ex = ex.InnerException;
        }

        return false;
    }

    /// <summary>把异常翻译成面向用户的中文说明。</summary>
    public static string Describe(Exception? ex)
    {
        if (ex is null)
        {
            return "未知错误。";
        }

        try
        {
            return DescribeCore(ex);
        }
        catch (Exception)
        {
            // 解释异常本身不该再抛异常，否则报错路径会变成静默退出。
            return $"{ex.GetType().Name}：{ex.Message}";
        }
    }

    private static string DescribeCore(Exception ex)
    {
        if (IsCanceled(ex))
        {
            return "操作已取消。";
        }

        var builder = new StringBuilder();

        switch (ex)
        {
            case AmazonS3Exception s3:
                builder.Append(DescribeS3(s3));
                break;
            case AmazonServiceException ase:
                builder.Append($"R2 返回错误：{ase.ErrorCode} {ase.StatusCode} {ase.Message}");
                break;
            case HttpRequestException http:
                builder.Append("网络请求失败：").Append(http.Message);
                break;
            case SocketException sock:
                builder.Append("无法连接服务器：").Append(sock.Message);
                break;
            case IOException io:
                builder.Append("本地文件读写失败：").Append(io.Message);
                break;
            case UnauthorizedAccessException ua:
                builder.Append("没有权限访问该路径：").Append(ua.Message);
                break;
            case NotSupportedException ns:
                builder.Append("不支持的操作：").Append(ns.Message);
                break;
            case TimeoutException te:
                builder.Append("请求超时：").Append(te.Message);
                break;
            default:
                builder.Append(ex.GetBaseException().Message);
                break;
        }

        return builder.ToString();
    }

    private static string DescribeS3(AmazonS3Exception s3)
    {
        string code = s3.ErrorCode ?? "Unknown";
        string hint = code switch
        {
            "NoSuchBucket" => "存储桶不存在，请检查桶名与 Account ID。",
            "NoSuchKey" => "对象不存在，可能已被删除。",
            "SignatureDoesNotMatch" => "签名不匹配：Secret Access Key 与 Access Key ID 不属于同一对。",
            "InvalidAccessKeyId" => "Access Key ID 无效。",
            "InvalidBucketName" => "桶名不合法。",
            "AccessDenied" => "权限不足：该 Token 没有该桶的读写权限。",
            "EntityTooLarge" => "对象超过单次上传限制，已自动改用分片上传仍失败。",
            "InvalidPart" or "InvalidPartOrder" or "EntityTooSmall" => "分片数据不完整，请重试。",
            "SlowDown" => "请求过于频繁，SDK 已自动重试，仍失败请降低并发数。",
            _ => "请检查凭据、桶名、网络与 R2 用量。",
        };

        return $"R2 返回错误：{code}（HTTP {(int)s3.StatusCode}）\r\n{hint}\r\n{s3.Message}";
    }

    public static void Report(string title, Exception ex, bool writeLog = true)
    {
        string message = Describe(ex);
        string logPath = string.Empty;

        if (writeLog)
        {
            try
            {
                logPath = AppendLog(title, ex);
            }
            catch (Exception)
            {
                // 写日志失败不阻断弹窗
            }
        }

        try
        {
            string body = string.IsNullOrEmpty(logPath)
                ? message
                : message + "\r\n\r\n日志位置：" + logPath;

            MessageBox.Show(body, "云存储管家 - " + title,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception)
        {
            // 连弹窗都失败（例如会话已被注销），那就真的无处可提示了，不能再抛。
        }
    }

    public static string AppendLog(string title, Exception ex)
    {
        string path;

        try
        {
            // LogPath 会去创建目录，放在 try 里面：万一建不出来，
            // 也不应该把异常抛给调用方（那会造成「报错时程序反而静默退出」）。
            path = LogPath;
        }
        catch (Exception)
        {
            path = Path.Combine(Path.GetTempPath(), "R2Manager-logs-unavailable.txt");
        }

        try
        {
            lock (LogLock)
            {
                File.AppendAllText(path,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {title}{Environment.NewLine}" +
                    $"{ex}{Environment.NewLine}{new string('-', 60)}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // 日志写入失败不应影响主流程
        }

        return path;
    }

    public static string WriteCrashLog(Exception ex) => AppendLog("未处理异常", ex);

    public static async Task RunGuardedAsync(string title, Func<Task> action, CancellationToken ct = default)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (IsCanceled(ex))
        {
            // 用户取消，静默
        }
        catch (Exception ex)
        {
            Report(title, ex);
        }
    }
}
