using System;
using System.Drawing;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using CloudStore.Core;
using CloudStore.UI;

namespace CloudStore.UIProbe;

/// <summary>
/// 界面自检：复现「用户在设置窗口下拉里换云厂商」这个动作，
/// 检查界面文案、保存出去的 Provider、以及自建存储的新字段是否都跟着变。
/// </summary>
internal static class SelfTest
{
    private static int _failures;
    private static readonly System.Text.StringBuilder _report = new();

    public static int Run()
    {
        _failures = 0;
        _report.Clear();

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        ApplicationConfiguration.Initialize();

        using var form = new SettingsForm(new StorageSettings
        {
            Provider = StorageProvider.CloudflareR2,
            AccountId = "0123456789abcdef0123456789abcdef",
            AccessKeyId = "AKIAEXAMPLE",
            BucketName = "my-bucket",
        });

        form.Show();
        Application.DoEvents();

        var provider = GetControl<Control>(form, "_provider");
        var addressing = GetControl<Control>(form, "_addressing");
        var console = GetControl<Control>(form, "_btnConsole");
        var publicDomainRow = GetControl<Control>(form, "_publicDomainRow");

        Check("初始厂商为 Cloudflare R2", "CloudflareR2", ReadProvider(provider));
        Check("R2 显示公开域名", "可见", publicDomainRow.Visible ? "可见" : "隐藏");
        Check("R2 隐藏寻址方式", "隐藏", IsVisible(form, "_addressingRow") ? "可见" : "隐藏");
        Check("R2 控制台按钮", "打开 Cloudflare 控制台", console.Text);

        // 这就是用户在界面上点下拉菜单选「阿里云 OSS」时走的同一条路径。
        Invoke(provider, "SelectIndex", 1);
        Application.DoEvents();

        Check("下拉切换后厂商", "AliyunOss", ReadProvider(provider));
        Check("切换后控制台按钮", "打开阿里云 RAM 控制台", console.Text);
        Check("阿里云隐藏公开域名", "隐藏", publicDomainRow.Visible ? "可见" : "隐藏");
        Check("隐藏账户 ID 行", "隐藏", IsVisible(form, "_accountIdRow") ? "可见" : "隐藏");
        Check("显示地域行", "可见", IsVisible(form, "_regionRow") ? "可见" : "隐藏");
        Check("地域默认值", "cn-hangzhou", ReadText(form, "_region"));

        var collected = Invoke<StorageSettings>(form, "Collect");
        Check("保存出去的 Provider", "AliyunOss", collected.Provider.ToString());
        Check("保存出去的地域", "cn-hangzhou", collected.Region);
        Check("寻址方式（阿里云固定虚拟主机式）", "False", collected.UsesPathStyle.ToString());

        // 切到自建存储
        Invoke(provider, "SelectIndex", 3);
        Application.DoEvents();

        Check("自建存储厂商", "SelfHosted", ReadProvider(provider));
        Check("自建存储显示寻址方式", "可见", IsVisible(form, "_addressingRow") ? "可见" : "隐藏");
        Check("自建存储隐藏公开域名", "隐藏", publicDomainRow.Visible ? "可见" : "隐藏");
        Check("自建默认寻址（路径式）", "0", ReadSelected(addressing));
        Check("自建控制台按钮", "自建存储文档", console.Text);

        var selfHosted = Invoke<StorageSettings>(form, "Collect");
        Check("自建保存的寻址方式", "PathStyle", selfHosted.Addressing.ToString());

        // 自建存储缺 Endpoint 必须被拦下
        selfHosted.AccessKeyId = "minioadmin";
        selfHosted.SecretAccessKey = "minioadmin";
        selfHosted.BucketName = "backup";
        Check("自建缺 Endpoint 时报错", "请填写 Endpoint，例如 https://minio.example.com", selfHosted.Validate() ?? "(无)");

        selfHosted.CustomEndpoint = "https://minio.example.com";
        Check("自建填了 Endpoint 后通过", "(无)", selfHosted.Validate() ?? "(无)");
        Check("自建端点解析", "https://minio.example.com", selfHosted.ResolveEndpoint());
        Check("自建公开链接（路径式）",
            "https://minio.example.com/backup/a%20b.txt",
            StorageProviders.BuildPublicUrl(StorageProvider.SelfHosted, selfHosted, "a b.txt"));

        selfHosted.Addressing = S3Addressing.VirtualHosted;
        Check("自建公开链接（虚拟主机式）",
            "https://backup.minio.example.com/a%20b.txt",
            StorageProviders.BuildPublicUrl(StorageProvider.SelfHosted, selfHosted, "a b.txt"));

        form.Close();

        // 布局体检：每种厂商下都不能有内容被父容器裁掉。
        // 之前「公开域名」那行就是这么被切掉一截边框的——FlowLayoutPanel
        // 装不下时既不报错也不出滚动条，只能靠断言盯住。
        foreach (StorageProvider each in StorageProviders.All)
        {
            CheckLayout(each);
        }

        string summary = _failures == 0 ? "自检全部通过。" : $"自检有 {_failures} 项未通过。";
        _report.AppendLine();
        _report.AppendLine(summary);

        // 探针是 WinExe，Console.WriteLine 会丢，所以落盘再看。
        string reportPath = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "r2m-uiprobe-selftest.txt");
        System.IO.File.WriteAllText(reportPath, _report.ToString(), Encoding.UTF8);

        return _failures;
    }

    /// <summary>
    /// 打开设置窗口，逐个厂商检查左右两栏有没有内容超出可视高度。
    /// 超出即失败——那就是「显示不完全」。
    /// </summary>
    private static void CheckLayout(StorageProvider provider)
    {
        using var form = new SettingsForm(new StorageSettings
        {
            Provider = provider,
            AccountId = "0123456789abcdef0123456789abcdef",
            AccessKeyId = "AKIAEXAMPLE",
            BucketName = "my-bucket",
            CustomEndpoint = provider == StorageProvider.SelfHosted ? "https://minio.example.com" : string.Empty,
        });

        form.Show();
        Application.DoEvents();

        int overflow = 0;
        int worst = 0;

        foreach (Control column in new[] { GetControl<Control>(form, "_leftColumn"), GetControl<Control>(form, "_rightColumn") })
        {
            foreach (Control child in column.Controls)
            {
                if (!child.Visible)
                {
                    continue;
                }

                int over = child.Bottom - column.ClientSize.Height;

                if (over > 0)
                {
                    overflow++;
                    worst = Math.Max(worst, over);
                }
            }
        }

        Check($"{provider} 两栏内容无裁切", "0", overflow.ToString());
        Check($"{provider} 裁切余量", ">=1px", worst <= 0 ? ">=1px" : $"少了 {worst}px");
        Check($"{provider} 窗口高度", "540", form.ClientSize.Height.ToString());

        form.Close();
        Application.DoEvents();
    }

    private static void Check(string name, string expected, string actual)
    {
        bool ok = string.Equals(expected, actual, StringComparison.Ordinal);
        _report.AppendLine($"  {(ok ? "✓" : "✗")} {name}：期望「{expected}」实际「{actual}」");

        if (!ok)
        {
            _failures++;
        }
    }

    private static string ReadProvider(Control combo)
    {
        int index = (int)combo.GetType()
            .GetProperty("SelectedIndex", BindingFlags.Public | BindingFlags.Instance)!
            .GetValue(combo)!;

        if (index < 0 || index >= StorageProviders.All.Length)
        {
            index = 0;
        }

        return StorageProviders.All[index].ToString();
    }

    private static string ReadSelected(Control combo) =>
        (combo.GetType().GetProperty("SelectedIndex", BindingFlags.Public | BindingFlags.Instance)!
            .GetValue(combo)?.ToString() ?? "(null)");

    private static T GetControl<T>(Form form, string field) where T : Control
    {
        var value = form.GetType()
            .GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(form);

        return (T)value!;
    }

    private static bool IsVisible(Form form, string field) => GetControl<Control>(form, field).Visible;

    private static string ReadText(Form form, string field)
    {
        object control = form.GetType()
            .GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(form)!;

        return control.GetType().GetProperty("Text")!.GetValue(control)?.ToString() ?? "";
    }

    private static void Invoke(object target, string method, params object[] args)
    {
        target.GetType()
            .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(target, args);
    }

    private static T Invoke<T>(object target, string method)
    {
        return (T)target.GetType()
            .GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(target, null)!;
    }
}