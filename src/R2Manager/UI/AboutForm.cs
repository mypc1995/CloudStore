using System;
using System.Drawing;
using System.Windows.Forms;
using CloudStore.Core;

namespace CloudStore.UI;

/// <summary>
/// 关于窗口：展示应用标识、版本与作者信息，并提供一键环境自检。
/// 固定尺寸、不可缩放——和设置、重命名两个对话框保持一致。
/// </summary>
public sealed class AboutForm : Form
{
    private readonly StorageSettings? _settings;

    public AboutForm(StorageSettings? settings = null)
    {
        _settings = settings;

        Text = "关于 云存储管家";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(456, 326);
        BackColor = UiEx.Surface;
        Font = new Font("Microsoft YaHei UI", 9F);

        if (UiEx.AppIcon() is { } aboutIcon)
        {
            Icon = aboutIcon;
        }

        BuildUi();
    }

    private void BuildUi()
    {
        const int PadX = 24;
        int contentWidth = ClientSize.Width - PadX * 2;

        var logo = new PictureBox
        {
            Location = new Point(PadX, 24),
            Size = new Size(64, 64),
            BackColor = Color.Transparent,
            SizeMode = PictureBoxSizeMode.Zoom,
        };

        try
        {
            using Bitmap? mark = UiEx.AppLogoBitmap(96);
            logo.Image = mark is null ? null : new Bitmap(mark);
        }
        catch (Exception)
        {
            // 图标取不到就留空，不影响使用
        }

        var title = UiEx.MakeLabel(AppInfo.Name, 16F, true);
        title.Location = new Point(PadX + 64 + 18, 32);
        title.Size = new Size(contentWidth - 64 - 18, 30);

        var version = UiEx.MakeLabel($"版本 {AppInfo.Version}", 9F, false, UiEx.TextMuted);
        version.Location = new Point(PadX + 64 + 18, 62);
        version.Size = new Size(contentWidth - 64 - 18, 20);

        // 顶部信息与下方作者信息之间画一条分隔线。
        var divider = new Panel
        {
            Location = new Point(PadX, 108),
            Size = new Size(contentWidth, 1),
            BackColor = UiEx.Border,
        };

        var authorCaption = UiEx.MakeLabel("作者", 9F, false, UiEx.TextMuted);
        authorCaption.Location = new Point(PadX, 126);
        authorCaption.Size = new Size(60, 22);

        var authorValue = UiEx.MakeLabel(AppInfo.Author, 10F, true, UiEx.TextMain);
        authorValue.Location = new Point(PadX + 60, 125);
        authorValue.Size = new Size(contentWidth - 60, 22);

        var configCaption = UiEx.MakeLabel("配置位置", 9F, false, UiEx.TextMuted);
        configCaption.Location = new Point(PadX, 152);
        configCaption.Size = new Size(60, 22);

        var configValue = UiEx.MakeLabel(AppPaths.DataDirectory, 9F, false, UiEx.TextMain);
        configValue.Location = new Point(PadX + 60, 151);
        configValue.Size = new Size(contentWidth - 60, 22);
        configValue.AutoEllipsis = true;

        var pathTip = new ToolTip { AutoPopDelay = 200, InitialDelay = 150 };
        pathTip.SetToolTip(configValue, "配置与日志所在目录：" + AppPaths.DataDirectory);

        var publishCaption = UiEx.MakeLabel("首次发布", 9F, false, UiEx.TextMuted);
        publishCaption.Location = new Point(PadX, 178);
        publishCaption.Size = new Size(60, 22);

        var publishValue = UiEx.MakeLabel(AppInfo.FirstPublish, 10F, true, UiEx.TextMain);
        publishValue.Location = new Point(PadX + 60, 177);
        publishValue.Size = new Size(contentWidth - 60, 22);

        var note = UiEx.MakeLabel(
            "支持 Cloudflare R2、阿里云 OSS、腾讯云 COS 与自建 S3 兼容存储。\r\n" +
            "免费开源的个人作品，与各云厂商无隶属关系；相关商标归其所有者所有。",
            8.5F, false, UiEx.TextMuted);
        note.Location = new Point(PadX, 206);
        note.Size = new Size(contentWidth, 40);

        var diag = UiEx.MakeButton("复制诊断信息", ButtonStyle.Normal, 118);
        diag.Location = new Point(PadX, ClientSize.Height - 24 - 32);
        diag.Click += async (_, _) => await CopyDiagnosticsAsync();

        var close = UiEx.MakeButton("确定", ButtonStyle.Primary, 88);
        close.Location = new Point(ClientSize.Width - PadX - 88, ClientSize.Height - 24 - 32);
        close.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };

        Controls.AddRange(new Control[]
        {
            logo, title, version, divider,
            authorCaption, authorValue,
            configCaption, configValue,
            publishCaption, publishValue,
            note, diag, close,
        });

        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Escape && e.KeyCode != Keys.Enter)
            {
                return;
            }

            e.Handled = true;
            DialogResult = DialogResult.OK;
            Close();
        };
    }

    /// <summary>
    /// 收集环境自检信息并复制到剪贴板，同时写进日志。
    /// 「双击没反应」这类反馈光靠描述定位不了，让用户把这段信息发过来最快。
    /// </summary>
    private async Task CopyDiagnosticsAsync()
    {
        try
        {
            string text = await Diagnostics.CollectAsync(_settings).ConfigureAwait(true);

            try
            {
                Clipboard.SetText(text);
            }
            catch (Exception)
            {
                // 剪贴板被别的程序占用时会失败，忽略——日志里还有一份。
            }

            ErrorInfo.AppendLog("诊断信息", new Exception(text));

            MessageBox.Show(this,
                "诊断信息已复制到剪贴板，同时也写进了日志文件。\r\n\r\n" +
                "把它发给作者时，请一并说明「双击后无反应」的现象。",
                "诊断信息", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            ErrorInfo.Report("收集诊断信息失败", ex);
        }
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        try
        {
            UiEx.ApplyWindowIcons(this);
        }
        catch (Exception)
        {
            // 图标问题不影响使用
        }
    }
}
