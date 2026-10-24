using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CloudStore.Core;

/// <summary>
/// 环境自检信息。
/// <para>
/// 「双击没反应」这类反馈光靠猜没有用——用户描述不清，日志也未必能拿到。
/// 这里把最可能出问题的几项（临时目录能不能写、单文件解压缓存、网络能不能通）
/// 一次性收集出来，用户复制粘贴给作者就能定位。
/// </para>
/// </summary>
public static class Diagnostics
{
    public static async Task<string> CollectAsync(StorageSettings? settings = null)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"【{AppInfo.Name} 诊断信息】");
        sb.AppendLine($"生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        AppendRuntime(sb);
        AppendExecutable(sb);
        AppendTemp(sb);
        AppendSingleFileCache(sb);
        AppendDataDirectory(sb);
        AppendConfiguration(sb, settings);
        await AppendNetworkAsync(sb, settings).ConfigureAwait(false);

        sb.AppendLine();
        sb.AppendLine("—— 若启动时毫无反应，最可能是下面三项之一 ——");
        sb.AppendLine("1) 杀毒软件拦截：把本程序加入白名单后重试；");
        sb.AppendLine("2) 文件不完整：重新下载并核对 SHA256；");
        sb.AppendLine("3) 首次运行需解压运行时到临时目录，窗口出现前会有一段时间没反应，属正常。");

        return sb.ToString();
    }

    private static void AppendRuntime(StringBuilder sb)
    {
        sb.AppendLine("— 运行环境 —");

        sb.AppendLine($"运行时：{RuntimeInformation.FrameworkDescription}");
        sb.AppendLine($"操作系统：{RuntimeInformation.OSDescription}（{RuntimeInformation.OSArchitecture}）");
        sb.AppendLine($"进程位数：{(Environment.Is64BitProcess ? "64 位" : "32 位")}");

        // 是不是「单文件自包含」：
        // 单文件版会把运行时解压到临时目录再运行，BaseDirectory 指向解压目录，
        // 而 exe 本体在别处；普通版两者在同一目录。
        // （不能用 Assembly.Location 判断——单文件下它恒为空，编译期还会报 IL3000。）
        string exeDir = string.Empty;
        try
        {
            string? processPath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(processPath))
            {
                exeDir = Path.GetDirectoryName(processPath) ?? string.Empty;
            }
        }
        catch (Exception)
        {
            // 取不到就算了
        }

        string baseDir = AppContext.BaseDirectory;
        bool singleFile = exeDir.Length > 0 &&
            !string.Equals(exeDir.TrimEnd('\\'), baseDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

        sb.AppendLine($"发布形式：{(singleFile ? "免安装单文件（自带运行时）" : "依赖系统已装 .NET")}");
        sb.AppendLine($"程序目录：{exeDir}");
        sb.AppendLine($"工作目录：{baseDir}");
        sb.AppendLine();
    }

    private static void AppendExecutable(StringBuilder sb)
    {
        sb.AppendLine("— 程序文件 —");

        try
        {
            string exe = Environment.ProcessPath ?? string.Empty;

            if (exe.Length > 0 && File.Exists(exe))
            {
                var info = new FileInfo(exe);
                sb.AppendLine($"文件名：{info.Name}");
                sb.AppendLine($"大小：{info.Length / 1024.0 / 1024.0:F1} MB");
                sb.AppendLine($"修改时间：{info.LastWriteTime:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"SHA256：{ComputeSha256(exe)}");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"读取程序文件失败：{ex.Message}");
        }

        sb.AppendLine();
    }

    private static void AppendTemp(StringBuilder sb)
    {
        sb.AppendLine("— 临时目录（首次启动要往这里解压运行时）—");

        string temp = Path.GetTempPath();
        sb.AppendLine($"路径：{temp}");

        try
        {
            string probe = Path.Combine(temp, "r2m-write-probe.tmp");
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
            sb.AppendLine("可写性：正常");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"可写性：异常（{ex.Message}）——这会直接导致程序启动失败");
        }

        sb.AppendLine();
    }

    private static void AppendSingleFileCache(StringBuilder sb)
    {
        sb.AppendLine("— 单文件解压缓存 —");

        string cache = Path.Combine(Path.GetTempPath(), ".net");

        try
        {
            if (Directory.Exists(cache))
            {
                var files = Directory.EnumerateFiles(cache, "*", SearchOption.AllDirectories).ToList();
                long bytes = files.Sum(f => new FileInfo(f).Length);
                sb.AppendLine($"缓存目录：{cache}");
                sb.AppendLine($"已解压：{files.Count} 个文件，{bytes / 1024.0 / 1024.0:F1} MB");
                sb.AppendLine("（有缓存说明已经启动过一次；没有缓存说明是首次运行，会慢一些）");
            }
            else
            {
                sb.AppendLine("尚无缓存（首次运行，需解压运行时，窗口出现前会等待数秒到数十秒）");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"检查失败：{ex.Message}");
        }

        sb.AppendLine();
    }

    /// <summary>配置与日志落在哪——用户问「我的密钥存在哪」时先看这段。</summary>
    private static void AppendDataDirectory(StringBuilder sb)
    {
        sb.AppendLine("— 配置与日志 —");

        try
        {
            sb.AppendLine($"配置目录：{AppPaths.DataDirectory}");
            sb.AppendLine($"位置来源：{AppPaths.DataDirectoryHint}");
            sb.AppendLine($"配置文件：{Path.Combine(AppPaths.DataDirectory, "settings.json")}");
            sb.AppendLine($"日志目录：{Path.Combine(AppPaths.DataDirectory, "logs")}");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"读取失败：{ex.Message}");
        }

        sb.AppendLine();
    }

    private static void AppendConfiguration(StringBuilder sb, StorageSettings? settings)    {
        sb.AppendLine("— 当前配置（不含任何密钥）—");

        if (settings is null)
        {
            sb.AppendLine("尚未配置");
            sb.AppendLine();
            return;
        }

        sb.AppendLine($"云厂商：{StorageProviders.DisplayName(settings.Provider)}");
        sb.AppendLine($"Endpoint：{settings.ResolveEndpoint()}");
        sb.AppendLine($"地域：{settings.NormalizedRegion}");
        sb.AppendLine($"寻址方式：{(settings.UsesPathStyle ? "路径式（path-style）" : "虚拟主机式（virtual-hosted）")}");
        sb.AppendLine($"桶名：{(string.IsNullOrWhiteSpace(settings.BucketName) ? "未填写" : settings.BucketName)}");
        sb.AppendLine($"已保存密钥：{(settings.RememberSecret ? "是" : "否")}");
        sb.AppendLine();
    }

    private static async Task AppendNetworkAsync(StringBuilder sb, StorageSettings? settings)
    {
        sb.AppendLine("— 网络连通性 —");

        if (settings is null || string.IsNullOrWhiteSpace(settings.EndpointHost))
        {
            sb.AppendLine("尚未配置端点，跳过");
            sb.AppendLine();
            return;
        }

        string host = settings.EndpointHost;

        try
        {
            IPAddress[] addresses = await Dns.GetHostAddressesAsync(host).ConfigureAwait(false);
            sb.AppendLine($"DNS 解析 {host}：{string.Join(", ", addresses.Select(a => a.ToString()))}");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"DNS 解析 {host}：失败（{ex.Message}）");
            sb.AppendLine();
            return;
        }

        int port = settings.ResolveEndpoint().StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? 80 : 443;

        try
        {
            using var client = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            sb.AppendLine($"TCP 连接 {host}:{port}：正常");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"TCP 连接 {host}:{port}：失败（{ex.Message}）");
        }

        sb.AppendLine();
    }

    private static string ComputeSha256(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var sha = System.Security.Cryptography.SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(stream));
        }
        catch (Exception ex)
        {
            return "计算失败：" + ex.Message;
        }
    }
}
