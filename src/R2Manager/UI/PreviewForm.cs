using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CloudStore.Core;

namespace CloudStore.UI;

/// <summary>对象查看窗口：支持在选中的多个对象之间前后翻阅，显示图片 / 文本 / 元信息。</summary>
public sealed class PreviewForm : Form
{
    private StorageService _service;
    private IReadOnlyList<string> _keys;
    private int _index;

    private CancellationTokenSource? _cts;
    private Label _header = null!;
    private TabControl _tabs = null!;
    private PictureBox _picture = null!;
    private TextBox _text = null!;
    private Label _placeholder = null!;
    private Panel _contentHost = null!;
    private FlatButton _btnPrev = null!;
    private FlatButton _btnNext = null!;
    private Label _counter = null!;
    private PreviewContent? _current;

    public PreviewForm(StorageService service, IReadOnlyList<string> keys, int index)
    {
        _service = service;
        _keys = keys;
        _index = Math.Clamp(index, 0, Math.Max(0, keys.Count - 1));

        Text = "预览 — 云存储管家";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(1000, 700);
        MinimumSize = new Size(640, 480);
        BackColor = UiEx.SurfaceAlt;

        if (UiEx.AppIcon() is { } previewIcon)
        {
            Icon = previewIcon;
        }

        BuildUi();
        KeyPreview = true;
        KeyDown += OnKeyDown;
        FormClosed += (_, _) => _cts?.Cancel();

        LoadCurrent();
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

    public void SetSource(StorageService service, IReadOnlyList<string> keys, int index)
    {
        _cts?.Cancel();
        _service = service;
        _keys = keys;
        _index = Math.Clamp(index, 0, Math.Max(0, keys.Count - 1));
        LoadCurrent();
    }

    private void BuildUi()
    {
        var top = new Panel { Dock = DockStyle.Top, Height = 48, BackColor = UiEx.Surface };
        top.Paint += (_, e) =>
        {
            using var pen = new Pen(UiEx.Border);
            e.Graphics.DrawLine(pen, 0, top.Height - 1, top.Width, top.Height - 1);
        };

        _header = UiEx.MakeLabel(string.Empty, 10F, true);
        _header.AutoSize = false;
        _header.Location = new Point(12, 0);
        _header.Size = new Size(600, 48);

        _btnPrev = UiEx.MakeButton("上一项", ButtonStyle.Normal, 78);
        _btnPrev.Click += (_, _) => Step(-1);

        _btnNext = UiEx.MakeButton("下一项", ButtonStyle.Normal, 78);
        _btnNext.Click += (_, _) => Step(1);

        _counter = UiEx.MakeLabel(string.Empty, 9F, false, UiEx.TextMuted);
        _counter.AutoSize = false;
        _counter.TextAlign = ContentAlignment.MiddleCenter;
        _counter.Size = new Size(90, 32);

        top.Controls.AddRange(new Control[] { _header, _btnPrev, _counter, _btnNext });

        void PositionTop()
        {
            _btnNext.Location = new Point(top.ClientSize.Width - 12 - _btnNext.Width, 8);
            _btnPrev.Location = new Point(_btnNext.Left - 6 - _btnPrev.Width, 8);
            _counter.Location = new Point(_btnPrev.Left - 4 - _counter.Width, 8);
            _header.Width = Math.Max(120, _counter.Left - 20);
        }

        top.Resize += (_, _) => PositionTop();
        PositionTop();

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 50, BackColor = UiEx.Surface };
        bottom.Paint += (_, e) =>
        {
            using var pen = new Pen(UiEx.Border);
            e.Graphics.DrawLine(pen, 0, 0, bottom.Width, 0);
        };

        var btnDownload = UiEx.MakeButton("下载", ButtonStyle.Primary, 84);
        btnDownload.Click += (_, _) => Download();

        var btnOpen = UiEx.MakeButton("用默认程序打开", ButtonStyle.Normal, 140);
        btnOpen.Click += (_, _) => OpenExternally();

        var btnCopyKey = UiEx.MakeButton("复制 Key", ButtonStyle.Ghost, 88);
        btnCopyKey.Click += (_, _) => CopyKey();

        var btnCopyLink = UiEx.MakeButton("复制链接", ButtonStyle.Ghost, 88);
        btnCopyLink.Click += (_, _) => CopyLink();

        var btnRefresh = UiEx.MakeButton("刷新", ButtonStyle.Ghost, 68);
        btnRefresh.Click += (_, _) => LoadCurrent();

        bottom.Controls.AddRange(new Control[] { btnDownload, btnOpen, btnCopyKey, btnCopyLink, btnRefresh });

        void PositionBottom()
        {
            int x = 12;
            foreach (Control control in new Control[] { btnDownload, btnOpen, btnCopyKey, btnCopyLink, btnRefresh })
            {
                control.Location = new Point(x, 9);
                x += control.Width + 8;
            }
        }

        bottom.Resize += (_, _) => PositionBottom();
        PositionBottom();

        _contentHost = new Panel { Dock = DockStyle.Fill, BackColor = UiEx.SurfaceSunken };

        _picture = new PictureBox
        {
            Dock = DockStyle.Fill,
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.FromArgb(0xE9, 0xEC, 0xF0),
            Visible = false,
        };

        _text = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = UiEx.MonoFont,
            BackColor = UiEx.Surface,
            BorderStyle = BorderStyle.None,
            Visible = false,
        };

        _placeholder = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = UiEx.TextMuted,
            Font = new Font("Microsoft YaHei UI", 9.5F),
            BackColor = UiEx.SurfaceSunken,
            Visible = false,
        };

        _contentHost.Controls.Add(_placeholder);
        _contentHost.Controls.Add(_text);
        _contentHost.Controls.Add(_picture);

        _tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Font = new Font("Microsoft YaHei UI", 9F),
        };

        var contentPage = new TabPage("内容");
        contentPage.BackColor = UiEx.SurfaceSunken;
        contentPage.Controls.Add(_contentHost);

        var infoHost = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = UiEx.Surface, Padding = new Padding(14) };
        var infoPage = new TabPage("信息") { BackColor = UiEx.Surface };
        infoPage.Controls.Add(infoHost);

        _tabs.TabPages.Add(contentPage);
        _tabs.TabPages.Add(infoPage);
        _tabs.Tag = infoHost;

        Controls.Add(_tabs);
        Controls.Add(bottom);
        Controls.Add(top);
    }

    private async void Step(int delta)
    {
        if (_keys.Count == 0)
        {
            return;
        }

        int target = _index + delta;

        if (target < 0 || target >= _keys.Count)
        {
            return;
        }

        _index = target;
        await LoadCurrentAsync();
    }

    private async void LoadCurrent()
    {
        await LoadCurrentAsync();
    }

    private async Task LoadCurrentAsync()
    {
        if (_keys.Count == 0)
        {
            return;
        }

        string key = _keys[_index];

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        CancellationToken ct = _cts.Token;

        _header.Text = Humanize.LeafName(key);
        _counter.Text = $"{_index + 1} / {_keys.Count}";
        _btnPrev.Enabled = _index > 0;
        _btnNext.Enabled = _index < _keys.Count - 1;
        _tabs.Enabled = false;
        ShowPlaceholder("正在加载预览…");

        try
        {
            PreviewContent content = await PreviewLoader.LoadAsync(_service, key, ct);

            if (ct.IsCancellationRequested)
            {
                return;
            }

            _current = content;
            Render(content);
            RenderInfo(content, key);
        }
        catch (Exception ex) when (ErrorInfo.IsCanceled(ex))
        {
            // 已切换到其它对象
        }
        catch (Exception ex)
        {
            _current = null;
            ShowPlaceholder(ErrorInfo.Describe(ex));
        }
        finally
        {
            _tabs.Enabled = true;
        }
    }

    private void Render(PreviewContent content)
    {
        _text.Clear();

        switch (content.Kind)
        {
            case PreviewKind.Image:
                _picture.Image?.Dispose();
                _picture.Image = content.Image;
                _picture.Visible = true;
                _text.Visible = false;
                _placeholder.Visible = false;
                break;

            case PreviewKind.Text:
                _picture.Visible = false;
                _picture.Image?.Dispose();
                _picture.Image = null;
                _text.Visible = true;
                _text.Text = content.Text;
                break;

            default:
                _picture.Visible = false;
                _text.Visible = false;
                ShowPlaceholder(content.Message ?? "无法预览该对象", content.HexDump);
                break;
        }
    }

    private void ShowPlaceholder(string message, string? hexDump = null)
    {
        _picture.Visible = false;
        _text.Visible = false;
        _placeholder.Visible = true;

        _placeholder.Text = hexDump is null
            ? message
            : message + Environment.NewLine + Environment.NewLine + hexDump;
    }

    private void RenderInfo(PreviewContent content, string key)
    {
        if (_tabs.Tag is not Panel host)
        {
            return;
        }

        host.Controls.Clear();

        var layout = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
        };

        void Row(string name, string value) => layout.Controls.Add(InfoLine(name, value));

        layout.Controls.Add(new SectionTitle("对象") { Width = 700 });
        Row("Key", key);
        Row("文件名", Humanize.LeafName(key));
        Row("大小", $"{Humanize.Bytes(content.Length)}（{content.Length:N0} 字节）");
        Row("Content-Type", content.ContentType ?? "-");
        Row("最后修改", Humanize.Date(content.LastModified));
        Row("ETag", content.ETag ?? "-");
        Row("存储类别", content.StorageClass ?? "-");
        Row("访问链接", _service.BuildPublicUrl(key));

        layout.Controls.Add(new SectionTitle("响应头") { Width = 700 });
        foreach ((string name, string value) in content.Headers)
        {
            Row(name, value);
        }

        if (content.Metadata.Count > 0)
        {
            layout.Controls.Add(new SectionTitle("自定义元数据") { Width = 700 });
            foreach ((string name, string value) in content.Metadata)
            {
                Row(name, value);
            }
        }

        if (content.EncodingName is not null)
        {
            layout.Controls.Add(new SectionTitle("预览") { Width = 700 });
            Row("文本编码", content.EncodingName);
            Row("内容截断", content.Truncated ? "是（仅显示部分内容）" : "否");
        }

        host.Controls.Add(layout);
    }

    private static Control InfoLine(string name, string value)
    {
        var panel = new Panel { Width = 700, Height = 22, Margin = new Padding(0, 0, 0, 6) };

        panel.Controls.Add(new Label
        {
            Text = name,
            Dock = DockStyle.Left,
            Width = 150,
            ForeColor = UiEx.TextMuted,
            Font = new Font("Microsoft YaHei UI", 8.5F),
            TextAlign = ContentAlignment.MiddleLeft,
        });

        panel.Controls.Add(new Label
        {
            Text = value,
            Dock = DockStyle.Fill,
            ForeColor = UiEx.TextMain,
            Font = new Font("Microsoft YaHei UI", 9F),
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
        });

        return panel;
    }

    private string CurrentKey => _keys.Count == 0 ? string.Empty : _keys[_index];

    private void CopyKey()
    {
        try
        {
            Clipboard.SetText(CurrentKey);
            MessageBox.Show(this, "已复制 Key。", "云存储管家", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            ErrorInfo.Report("复制失败", ex);
        }
    }

    private void CopyLink()
    {
        try
        {
            Clipboard.SetText(_service.BuildPublicUrl(CurrentKey));
            MessageBox.Show(this, "已复制链接。", "云存储管家", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            ErrorInfo.Report("复制失败", ex);
        }
    }

    private void Download()
    {
        if (_current is null || _current.Length <= 0)
        {
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Title = "保存到本地",
            FileName = Humanize.LeafName(CurrentKey),
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _ = DownloadAsync(dialog.FileName);
    }

    private async Task DownloadAsync(string target)
    {
        try
        {
            var options = new DownloadOptions(16L * 1024 * 1024, 16L * 1024 * 1024, 3);

            await ParallelDownloader.DownloadAsync(
                _service, CurrentKey, target, null, CancellationToken.None, options);

            MessageBox.Show(this, $"已保存到：\n{target}", "下载完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            ErrorInfo.Report("下载失败", ex);
        }
    }

    private void OpenExternally()
    {
        string temp = Path.Combine(Path.GetTempPath(), "r2preview-" + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(temp, Humanize.LeafName(CurrentKey));

        _ = OpenExternallyAsync(file, temp);
    }

    private async Task OpenExternallyAsync(string file, string tempFolder)
    {
        try
        {
            Directory.CreateDirectory(tempFolder);

            var options = new DownloadOptions(16L * 1024 * 1024, 16L * 1024 * 1024, 3);
            await ParallelDownloader.DownloadAsync(_service, CurrentKey, file, null, CancellationToken.None, options);

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = file,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ErrorInfo.Report("用默认程序打开失败", ex);
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.Left or Keys.PageUp:
                e.SuppressKeyPress = true;
                Step(-1);
                break;

            case Keys.Right or Keys.PageDown:
                e.SuppressKeyPress = true;
                Step(1);
                break;

            case Keys.Escape:
                Close();
                break;
        }
    }
}
