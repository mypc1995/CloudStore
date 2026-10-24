using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using CloudStore.Core;

namespace CloudStore.UI;

/// <summary>底部传输队列面板：逐条显示进度、速度、状态，支持取消 / 打开目录 / 清理。</summary>
internal sealed class TransferPanel : Panel
{
    private const int ProgressColumn = 1;

    private readonly TransferManager _manager;
    private readonly ListView _list;
    private readonly Label _title;
    private readonly Dictionary<Guid, ListViewItem> _map = new();
    private Label _emptyHint = null!;

    public TransferPanel(TransferManager manager)
    {
        _manager = manager;

        BackColor = UiEx.Surface;
        Padding = new Padding(0);

        _list = new BufferedListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
            BorderStyle = BorderStyle.None,
            OwnerDraw = true,
            BackColor = UiEx.Surface,
            SmallImageList = UiIcons.List,
            Font = new Font("Microsoft YaHei UI", 9F),
        };

        _list.Columns.Add("名称", 340);
        _list.Columns.Add("进度", 260);
        _list.Columns.Add("大小", 120, HorizontalAlignment.Right);
        _list.Columns.Add("速度", 110, HorizontalAlignment.Right);
        _list.Columns.Add("状态", 90);
        _list.Columns.Add("路径", 400);

        _list.DrawItem += OnDrawItem;
        _list.DrawSubItem += OnDrawSubItem;
        _list.MouseDoubleClick += (_, e) =>
        {
            var hit = _list.HitTest(e.Location);

            if (hit.Item is not null)
            {
                RevealItem(hit.Item);
            }
        };
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Delete && _list.SelectedItems.Count > 0)
            {
                CancelSelected();
                e.Handled = true;
            }
        };

        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 34,
            BackColor = UiEx.SurfaceAlt,
        };

        header.Paint += (_, e) =>
        {
            using var pen = new Pen(UiEx.Border);
            e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1);
        };

        _title = UiEx.MakeLabel("传输队列", 9F, true);
        _title.Location = new Point(12, 0);
        _title.Size = new Size(260, 34);

        var btnCancelAll = UiEx.MakeButton("全部取消", ButtonStyle.Normal, 84);
        btnCancelAll.Click += (_, _) => _manager.CancelAll();

        var btnClear = UiEx.MakeButton("清除已完成", ButtonStyle.Normal, 96);
        btnClear.Click += (_, _) =>
        {
            _manager.ClearFinished();
            Sync();
        };

        var btnHide = UiEx.MakeButton("收起", ButtonStyle.Normal, 64);
        btnHide.Click += (_, _) => VisibilityToggled?.Invoke(this, EventArgs.Empty);

        header.Controls.AddRange(new Control[] { _title, btnCancelAll, btnClear, btnHide });

        void PositionHeader()
        {
            int x = header.ClientSize.Width - 10;
            x -= btnHide.Width; btnHide.Location = new Point(x, 6); x -= 4;
            x -= btnClear.Width; btnClear.Location = new Point(x, 6); x -= 4;
            x -= btnCancelAll.Width; btnCancelAll.Location = new Point(x, 6);
        }

        header.Resize += (_, _) => PositionHeader();
        PositionHeader();

        var menu = new ContextMenuStrip { Font = new Font("Microsoft YaHei UI", 9F) };

        var menuCancel = new ToolStripMenuItem("取消该任务");
        menuCancel.Click += (_, _) => CancelSelected();

        var menuRetry = new ToolStripMenuItem("重试（重新加入队列）");
        menuRetry.Click += (_, _) => RetrySelected();

        var menuReveal = new ToolStripMenuItem("打开所在文件夹");
        menuReveal.Click += (_, _) =>
        {
            if (_list.SelectedItems.Count > 0)
            {
                RevealItem(_list.SelectedItems[0]);
            }
        };

        var menuCopy = new ToolStripMenuItem("复制路径");
        menuCopy.Click += (_, _) =>
        {
            if (_list.SelectedItems.Count > 0 && _list.SelectedItems[0].Tag is TransferItem item)
            {
                try
                {
                    Clipboard.SetText(item.Description);
                }
                catch (Exception ex)
                {
                    ErrorInfo.Report("复制失败", ex);
                }
            }
        };

        menu.Items.AddRange(new ToolStripItem[] { menuCancel, menuRetry, new ToolStripSeparator(), menuReveal, menuCopy });
        _list.ContextMenuStrip = menu;

        _emptyHint = new Label
        {
            Text = "暂无传输任务　·　把文件或文件夹拖到窗口即可上传，或选中对象后点「下载」",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = UiEx.TextMuted,
            BackColor = UiEx.Surface,
            Font = new Font("Microsoft YaHei UI", 9F),
        };

        _list.MouseDown += (_, _) => DismissHint();
        _list.MouseMove += (_, _) => DismissHint();
        _emptyHint.MouseDown += (_, _) => DismissHint();

        Controls.Add(_list);
        Controls.Add(_emptyHint);
        Controls.Add(header);

        _manager.Items.CollectionChanged += (_, _) => Sync();
        _manager.Changed += (_, _) => RefreshRows();
        _manager.Finished += (_, _) => RefreshRows();

        foreach (TransferItem item in _manager.Items)
        {
            AddRow(item);
        }

        UpdateHeader();
        UpdateEmptyHint();
    }

    private bool _hintDismissed;

    private void DismissHint()
    {
        _hintDismissed = true;
        UpdateEmptyHint();
    }

    private void UpdateEmptyHint()
    {
        if (_emptyHint is null)
        {
            return;
        }

        bool empty = _map.Count == 0;

        if (!empty)
        {
            _hintDismissed = false;
        }

        bool showHint = empty && !_hintDismissed;

        _emptyHint.Visible = showHint;
        _list.Visible = !showHint;

        if (showHint)
        {
            _emptyHint.BringToFront();
        }
    }

    private TransferItem? SelectedItem =>
        _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as TransferItem : null;

    /// <summary>用户点击“重试”时抛出，由主窗体重新入队。</summary>
    public event EventHandler<TransferItem>? RetryRequested;

    private void CancelSelected()
    {
        if (SelectedItem is { } item)
        {
            _manager.Cancel(item.Id);
        }
    }

    private void RetrySelected()
    {
        if (SelectedItem is { } item && item.State is TransferState.Failed or TransferState.Canceled)
        {
            RetryRequested?.Invoke(this, item);
        }
    }

    private static void RevealItem(ListViewItem listItem)
    {
        if (listItem.Tag is not TransferItem item)
        {
            return;
        }

        string? path = item.Kind == TransferKind.Download ? item.Description : null;

        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        try
        {
            string? directory = Path.GetDirectoryName(path);

            if (directory is null || !Directory.Exists(directory))
            {
                return;
            }

            if (File.Exists(path))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "/select,\"" + path + "\"",
                    UseShellExecute = true,
                });
            }
            else
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = directory,
                    UseShellExecute = true,
                });
            }
        }
        catch (Exception ex)
        {
            ErrorInfo.Report("打开文件夹失败", ex);
        }
    }

    private void AddRow(TransferItem item)
    {
        var row = new ListViewItem(item.Title)
        {
            Tag = item,
            ImageIndex = UiIcons.For(item.Kind == TransferKind.Upload ? "upload" : "download"),
        };

        row.SubItems.Add(string.Empty);
        row.SubItems.Add(Humanize.Bytes(item.Size));
        row.SubItems.Add(string.Empty);
        row.SubItems.Add(item.StatusText);
        row.SubItems.Add(item.Description);

        _list.Items.Add(row);
        _map[item.Id] = row;
    }

    private void Sync()
    {
        if (!IsHandleCreated || InvokeRequired)
        {
            try
            {
                BeginInvoke(Sync);
            }
            catch (ObjectDisposedException)
            {
                // 忽略
            }

            return;
        }

        foreach (var pair in _map.ToArray())
        {
            if (_manager.Items.All(i => i.Id != pair.Key))
            {
                _list.Items.Remove(pair.Value);
                _map.Remove(pair.Key);
            }
        }

        foreach (TransferItem item in _manager.Items.Where(i => !_map.ContainsKey(i.Id)))
        {
            AddRow(item);
        }

        RefreshRows();
        UpdateHeader();
    }

    private void RefreshRows()
    {
        if (!IsHandleCreated || InvokeRequired)
        {
            try
            {
                BeginInvoke(RefreshRows);
            }
            catch (ObjectDisposedException)
            {
                // 忽略
            }

            return;
        }

        foreach (TransferItem item in _manager.Items)
        {
            if (!_map.TryGetValue(item.Id, out ListViewItem? row))
            {
                continue;
            }

            row.ForeColor = item.State switch
            {
                TransferState.Failed => UiEx.Red,
                TransferState.Completed => UiEx.Green,
                TransferState.Canceled => UiEx.TextMuted,
                _ => UiEx.TextMain,
            };

            row.SubItems[2].Text = Humanize.Bytes(item.Size);
            row.SubItems[3].Text = item.SpeedText;
            row.SubItems[4].Text = item.StatusText;
            row.SubItems[5].Text = item.Description;

            if (item.Result is not null)
            {
                row.SubItems[5].Text = item.Description + " · " + item.Result;
            }
        }

        UpdateHeader();
        UpdateEmptyHint();
    }

    private void UpdateHeader()
    {
        TransferStats stats = _manager.Stats;
        _title.Text = $"传输队列　进行中 {stats.Running} · 等待 {stats.Waiting} · 完成 {stats.Completed}" +
                      (stats.Failed > 0 ? $" · 失败 {stats.Failed}" : string.Empty) +
                      (stats.Canceled > 0 ? $" · 取消 {stats.Canceled}" : string.Empty);
    }

    /// <summary>面板内的「收起」按钮触发，由主窗统一同步状态栏上的显隐开关。</summary>
    public event EventHandler? VisibilityToggled;

    private void OnDrawItem(object? sender, DrawListViewItemEventArgs e)
    {
        e.DrawDefault = true;
    }

    private void OnDrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (e.ColumnIndex != ProgressColumn || e.Item?.Tag is not TransferItem item)
        {
            e.DrawDefault = true;
            return;
        }

        var g = e.Graphics;
        Rectangle bounds = e.Bounds;

        using (var back = new SolidBrush(bounds.Height > 2 ? UiEx.SurfaceAlt : UiEx.Surface))
        {
            g.FillRectangle(back, bounds);
        }

        int pad = 8;
        var track = new Rectangle(bounds.X + pad, bounds.Y + (bounds.Height - 8) / 2, Math.Max(10, bounds.Width - pad * 2), 8);

        using (var trackBrush = new SolidBrush(Color.FromArgb(0xE6, 0xE9, 0xEE)))
        using (var trackPath = GraphicsPathRounded(track, 4))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.FillPath(trackBrush, trackPath);
        }

        int percent = item.Percent;
        if (percent > 0)
        {
            var fill = new Rectangle(track.X, track.Y, (int)Math.Round(track.Width * percent / 100d), track.Height);
            Color color = item.State switch
            {
                TransferState.Failed => UiEx.Red,
                TransferState.Canceled => UiEx.TextMuted,
                _ => UiEx.Blue,
            };

            using var fillBrush = new SolidBrush(color);
            using var fillPath = GraphicsPathRounded(fill, 4);
            g.FillPath(fillBrush, fillPath);
        }

        string text = item.Size > 0
            ? $"{percent}%  ({Humanize.Bytes(item.Transferred)} / {Humanize.Bytes(item.Size)})"
            : Humanize.Bytes(item.Transferred);

        if (!string.IsNullOrEmpty(item.EtaText))
        {
            text += "　剩余 " + item.EtaText.Replace("剩余 ", string.Empty);
        }

        TextRenderer.DrawText(g, text, new Font("Microsoft YaHei UI", 8.5F), bounds, UiEx.TextMuted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
    }

    private static System.Drawing.Drawing2D.GraphicsPath GraphicsPathRounded(Rectangle rect, int radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        int diameter = radius * 2;

        if (rect.Width <= diameter || rect.Height <= diameter)
        {
            path.AddRectangle(rect);
            return path;
        }

        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();

        return path;
    }
}
