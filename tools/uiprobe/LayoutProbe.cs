using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;
using System.Windows.Forms;
using CloudStore.Core;
using CloudStore.UI;

namespace CloudStore.UIProbe;

/// <summary>
/// 布局体检：把设置窗口按每种云厂商各开一次，
/// 找出「子控件底部超出父容器可视高度」的地方，也就是被裁掉的行。
/// </summary>
internal static class LayoutProbe
{
    public static int Run()
    {
        var sb = new StringBuilder();

        foreach (StorageProvider provider in StorageProviders.All)
        {
            var settings = new StorageSettings
            {
                Provider = provider,
                AccountId = "0123456789abcdef0123456789abcdef",
                AccessKeyId = "AKIAEXAMPLE",
                BucketName = "my-bucket",
                Region = "auto",
                CustomEndpoint = provider == StorageProvider.SelfHosted ? "https://minio.example.com" : string.Empty,
            };

            using var form = new SettingsForm(settings);
            form.Show();
            Application.DoEvents();

            sb.AppendLine($"=== {provider} ===");
            sb.AppendLine($"  窗口 ClientSize = {form.ClientSize.Width} x {form.ClientSize.Height}");

            DumpColumn(sb, form, "显示前");

            form.Show();
            Application.DoEvents();

            DumpColumn(sb, form, "显示后");

            form.Close();
            Application.DoEvents();
        }

        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "r2m-uiprobe-layout.txt");
        System.IO.File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        return 0;
    }

    private static void DumpColumn(StringBuilder sb, Form form, string stage)
    {
        var left = GetField(form, "_leftColumn") as FlowLayoutPanel;
        var right = GetField(form, "_rightColumn") as FlowLayoutPanel;

        if (left is null)
        {
            sb.AppendLine($"  [{stage}] 找不到 _leftColumn");
            return;
        }

        sb.AppendLine($"  [{stage}] 左栏 高={left.ClientSize.Height} 宽={left.ClientSize.Width} " +
                      $"preferred={left.GetPreferredSize(new Size(312, 0)).Height}");

        int bottom = 0;

        foreach (Control child in left.Controls)
        {
            sb.AppendLine($"      {child.GetType().Name} vis={child.Visible} top={child.Top} h={child.Height} bottom={child.Bottom}");

            if (child.Visible)
            {
                bottom = Math.Max(bottom, child.Bottom);
            }
        }

        sb.AppendLine($"      → 实际内容底部 = {bottom}");

        if (right is not null)
        {
            sb.AppendLine($"  [{stage}] 右栏 高={right.ClientSize.Height} " +
                          $"preferred={right.GetPreferredSize(new Size(312, 0)).Height}");
        }
    }

    private static object? GetField(object target, string name) =>
        target.GetType().GetField(name,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(target);

    private static TableLayoutPanel? FindBody(Control root)
    {
        foreach (Control child in root.Controls)
        {
            if (child is TableLayoutPanel table && table.ColumnCount == 2 && table.RowCount == 1)
            {
                return table;
            }

            if (FindBody(child) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}
