using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using CloudStore.Core;
using CloudStore.UI;

namespace CloudStore.UIProbe;

/// <summary>
/// UI 探针入口。用法：
///   uiprobe settings [cloudflare|oss|cos|selfhost]   打开设置窗口（可指定预选的云厂商）
///   uiprobe about                                    打开关于窗口
///   uiprobe selftest                                 界面自检（下拉切换、字段显隐、取值）
///   uiprobe diag [输出路径]                          生成诊断信息并写文件
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0].ToLowerInvariant() : "settings";
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.SetDefaultFont(new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point));
        ApplicationConfiguration.Initialize();

        if (mode == "about")
        {
            Application.Run(new AboutForm(SampleSettings()));
            return 0;
        }

        if (mode == "selftest")
        {
            return SelfTest.Run();
        }

        if (mode == "layout")
        {
            return LayoutProbe.Run();
        }

        if (mode == "diag")
        {
            // 把诊断信息写到文件，便于检查内容而不依赖剪贴板 / 弹窗。
            string text = Diagnostics.CollectAsync(SampleSettings()).GetAwaiter().GetResult();
            string path = args.Length > 1 ? args[1] : "diagnostics.txt";
            System.IO.File.WriteAllText(path, text, Encoding.UTF8);
            Console.WriteLine(text);
            return 0;
        }

        StorageSettings settings = SampleSettings();

        if (args.Length > 1)
        {
            settings.Provider = args[1].ToLowerInvariant() switch
            {
                "oss" => StorageProvider.AliyunOss,
                "cos" => StorageProvider.TencentCos,
                "selfhost" => StorageProvider.SelfHosted,
                _ => StorageProvider.CloudflareR2,
            };

            if (settings.Provider == StorageProvider.SelfHosted)
            {
                settings.AccountId = string.Empty;
                settings.CustomEndpoint = "https://minio.example.com";
                settings.BucketName = "backup";
                settings.AccessKeyId = "minioadmin";
            }
        }

        Application.Run(new SettingsForm(settings));
        return 0;
    }

    private static StorageSettings SampleSettings()
    {
        // 一律使用**中性示例值**：探针与截图都不允许出现真实账号。
        return new StorageSettings
        {
            AccountId = "0123456789abcdef0123456789abcdef",
            AccessKeyId = "AKIAEXAMPLE",
            BucketName = "my-bucket",
            Region = "auto",
        };
    }
}