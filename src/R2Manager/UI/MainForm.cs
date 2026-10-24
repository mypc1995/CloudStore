using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Amazon.S3.Model;
using CloudStore.Core;

namespace CloudStore.UI;

public sealed class MainForm : Form
{
    private const int PageSize = 1000;
    private const int NavBarHeight = 46;

    /// <summary>首次连通对象的等待上限。超过就明确报错，不让界面长时间停在「正在连接」。</summary>
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(30);

    private sealed class Row
    {
        public required string Key { get; init; }

        public required string Display { get; init; }

        public bool IsFolder { get; init; }

        public long Size { get; init; }

        public DateTimeOffset LastModified { get; init; }

        public string? ETag { get; init; }

        public string StorageClass { get; init; } = "STANDARD";

        public string ContentType { get; init; } = string.Empty;

        public string IconKind => IsFolder ? "folder" : Humanize.IconKind(Key, ContentType);

        public static Row Folder(string prefix) => new()
        {
            Key = prefix,
            Display = Humanize.LeafName(prefix.TrimEnd('/')),
            IsFolder = true,
            StorageClass = "-",
            ContentType = "文件夹",
        };

        public static Row Object(S3Object o) => new()
        {
            Key = o.Key,
            Display = Humanize.LeafName(o.Key),
            Size = o.Size,
            LastModified = o.LastModified,
            ETag = o.ETag,
            StorageClass = string.IsNullOrWhiteSpace(o.StorageClass) ? "STANDARD" : o.StorageClass,
            ContentType = Humanize.GuessMime(o.Key),
        };
    }

    private readonly StorageSettings _settings;
    private readonly TransferManager _transfers = TransferManager.Instance;

    private StorageService? _service;
    private CancellationTokenSource? _listCts;
    private CancellationTokenSource? _deleteCts;
    private PreviewForm? _previewForm;
    private SplitContainer? _split;
    private TransferPanel _transfersPanel = null!;
    private bool _needsRefresh;

    private readonly List<Row> _rows = new();
    private readonly List<S3Object> _pageObjects = new();
    private readonly List<string> _pagePrefixes = new();
    private readonly List<string?> _pageTokens = new();

    private int _pageIndex;
    private string _prefix;
    private string _search = string.Empty;
    private string _sortColumn = "name";
    private bool _sortAscending = true;
    private bool _busy;

    private StatusPill _pill = null!;
    private Label _bucketLabel = null!;
    private Label _endpointLabel = null!;
    private FlatTextBox _prefixBox = null!;
    private FlatTextBox _searchBox = null!;
    private DataGridView _grid = null!;
    private Panel _detailsHost = null!;
    private Label _summaryLabel = null!;
    private Label _pageLabel = null!;
    private FlatButton _btnPrevPage = null!;
    private FlatButton _btnNextPage = null!;
    private FlatButton _btnUp = null!;
    private FlatButton _btnRefresh = null!;
    private FlatButton _btnDownload = null!;
    private FlatButton _btnPreview = null!;
    private FlatButton _btnDelete = null!;
    private FlatButton _btnCopy = null!;
    private FlatButton _btnRename = null!;
    private ToolStripStatusLabel _statusLabel = null!;
    private ToolStripProgressBar _statusProgress = null!;
    private ToolStripButton _statusCancel = null!;
    private ToolStripButton _statusTransfers = null!;
    private ToolStripStatusLabel _creditsLabel = null!;
    private ToolStripButton _statusAbout = null!;
    private ContextMenuStrip _gridMenu = null!;

    public MainForm()
    {
        _settings = SettingsStore.Load(out string? warning);
        _settings.Warning = warning;
        _prefix = _settings.LastPrefix ?? string.Empty;

        Text = "云存储管家 — R2 / OSS / COS 存储桶管理";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1040, 640);
        Size = new Size(Math.Max(1040, _settings.WindowWidth), Math.Max(640, _settings.WindowHeight));
        KeyPreview = true;
        AllowDrop = true;

        try
        {
            Icon = UiEx.AppIcon();
        }
        catch (Exception)
        {
            // 图标生成失败不影响使用
        }

        BuildUi();
        WireEvents();

        if (_settings.WindowMaximized)
        {
            WindowState = FormWindowState.Maximized;
        }
    }

    // ================================================================= UI 构建

    private void BuildUi()
    {
        _split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel2,
            Orientation = Orientation.Vertical,
            SplitterWidth = 8,
            BackColor = UiEx.SurfaceAlt,
        };

        var status = BuildStatusStrip();
        var transfers = BuildTransferPanel();
        var commandBar = BuildCommandBar();
        var navBar = BuildNavBar();
        var connBar = BuildConnectionBar();

        Controls.Add(_split);
        Controls.Add(commandBar);
        Controls.Add(navBar);
        Controls.Add(connBar);
        Controls.Add(transfers);
        Controls.Add(status);

        BuildGrid(_split.Panel1);
        BuildDetailsPanel(_split.Panel2);
        BuildContextMenu();

        _split.HandleCreated += (_, _) => ApplySplitterDistance();
        _split.SplitterMoved += (_, _) => _settings.SplitterDistance = _split.SplitterDistance;

        UpdateConnectionUi();
        UpdateActions();
    }

    private void ApplySplitterDistance()
    {
        if (_split is null || _split.Width <= 0)
        {
            return;
        }

        int min = Math.Max(420, _split.Width - DetailWidth - 40);
        _split.SplitterDistance = Math.Clamp(_settings.SplitterDistance, min, _split.Width - 120);
    }

    private Control BuildConnectionBar()
    {
        var bar = new Panel
        {
            Dock = DockStyle.Top,
            Height = 58,
            BackColor = UiEx.Surface,
        };

        bar.Paint += (_, e) =>
        {
            using var pen = new Pen(UiEx.Border);
            e.Graphics.DrawLine(pen, 0, bar.Height - 1, bar.Width, bar.Height - 1);
        };

        // 左侧标识改用 app.ico 里的图，不再是手写文字。
        var logo = new PictureBox
        {
            Location = new Point(14, 11),
            Size = new Size(36, 36),
            BackColor = Color.Transparent,
            SizeMode = PictureBoxSizeMode.Zoom,
            TabStop = false,
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

        _bucketLabel = UiEx.MakeLabel("尚未配置", 11.5F, true);
        _bucketLabel.AutoSize = false;
        _bucketLabel.Location = new Point(70, 7);
        _bucketLabel.Size = new Size(520, 22);

        _endpointLabel = UiEx.MakeLabel(string.Empty, 8.5F, false, UiEx.TextMuted);
        _endpointLabel.AutoSize = false;
        _endpointLabel.Location = new Point(70, 30);
        _endpointLabel.Size = new Size(520, 20);

        _pill = new StatusPill
        {
            Location = new Point(600, 16),
            Size = new Size(120, 26),
        };

        var btnSettings = UiEx.MakeButton("设置", ButtonStyle.Normal, 72);
        btnSettings.Click += (_, _) => ShowSettings();

        bar.Controls.AddRange(new Control[] { logo, _bucketLabel, _endpointLabel, _pill, btnSettings });

        void Position()
        {
            int right = bar.ClientSize.Width - 14;
            btnSettings.Location = new Point(right - btnSettings.Width, (bar.Height - 32) / 2);
            _pill.Location = new Point(btnSettings.Left - 10 - _pill.Width, (bar.Height - 26) / 2);

            int width = Math.Max(120, _pill.Left - 80);
            _bucketLabel.Width = width;
            _endpointLabel.Width = width;
        }

        bar.Resize += (_, _) => Position();
        Position();

        return bar;
    }

    private Control BuildNavBar()
    {
        var bar = new Panel
        {
            Dock = DockStyle.Top,
            Height = NavBarHeight,
            BackColor = UiEx.Surface,
        };

        bar.Paint += (_, e) =>
        {
            using var pen = new Pen(UiEx.Border);
            e.Graphics.DrawLine(pen, 0, bar.Height - 1, bar.Width, bar.Height - 1);
        };

        _btnUp = UiEx.MakeButton("上一级", ButtonStyle.Normal, 78);
        _btnUp.Click += (_, _) => Navigate(Humanize.ParentPrefix(_prefix));

        _prefixBox = new FlatTextBox
        {
            Location = new Point(0, 8),
            Size = new Size(240, 30),
            Placeholder = "根目录 / 输入对象前缀后回车",
        };

        _prefixBox.Text = _prefix;
        _prefixBox.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter)
            {
                return;
            }

            e.SuppressKeyPress = true;
            Navigate(_prefixBox.Text.Trim());
        };

        var btnGo = UiEx.MakeButton("转到", ButtonStyle.Normal, 62);
        btnGo.Click += (_, _) => Navigate(_prefixBox.Text.Trim());

        var searchIcon = new PictureBox
        {
            Image = UiIcons.List.Images[UiIcons.For("search")],
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Transparent,
            Size = new Size(16, 16),
        };

        _searchBox = new FlatTextBox
        {
            Location = new Point(0, 8),
            Size = new Size(200, 30),
            Placeholder = "搜索当前页的名称或 Key",
        };

        _searchBox.InputChanged += (_, _) =>
        {
            _search = _searchBox.Text.Trim();
            RebuildRows();
        };

        _btnPrevPage = UiEx.MakeButton("上一页", ButtonStyle.Normal, 72);
        _btnPrevPage.Click += (_, _) => ChangePage(-1);

        _btnNextPage = UiEx.MakeButton("下一页", ButtonStyle.Normal, 72);
        _btnNextPage.Click += (_, _) => ChangePage(1);

        _pageLabel = UiEx.MakeLabel("第 1 / 1 页", 9F, false, UiEx.TextMuted);
        _pageLabel.AutoSize = false;
        _pageLabel.TextAlign = ContentAlignment.MiddleCenter;
        _pageLabel.Size = new Size(80, 30);

        bar.Controls.AddRange(new Control[]
        {
            _btnUp, _prefixBox, btnGo, searchIcon, _searchBox,
            _btnPrevPage, _pageLabel, _btnNextPage,
        });

        void Position()
        {
            // 按钮 32px 高、输入框 30px 高，统一按中心线对齐
            const int buttonY = (NavBarHeight - 32) / 2;
            const int boxY = (NavBarHeight - 30) / 2;

            int x = 14;

            _btnUp.Location = new Point(x, buttonY);
            x = _btnUp.Right + 8;

            // 右侧分页与搜索先定位
            _btnNextPage.Location = new Point(bar.ClientSize.Width - 14 - _btnNextPage.Width, buttonY);
            _btnPrevPage.Location = new Point(_btnNextPage.Left - 6 - _btnPrevPage.Width, buttonY);
            _pageLabel.Location = new Point(_btnPrevPage.Left - 6 - _pageLabel.Width, buttonY);

            int searchRight = _pageLabel.Left - 18;
            _searchBox.Width = Math.Clamp(searchRight / 3, 150, 300);
            _searchBox.Location = new Point(searchRight - _searchBox.Width, boxY);
            searchIcon.Location = new Point(_searchBox.Left - 22, boxY + 7);

            // 「转到」紧挨搜索框左侧，路径输入框填满中间剩余空间
            int goLeft = Math.Max(x + 140, _searchBox.Left - 22 - btnGo.Width);
            btnGo.Location = new Point(goLeft, buttonY);
            _prefixBox.Location = new Point(x, boxY);
            _prefixBox.Width = Math.Max(140, btnGo.Left - 8 - x);
        }

        bar.Resize += (_, _) => Position();
        Position();

        return bar;
    }

    private Control BuildCommandBar()
    {
        var bar = new Panel
        {
            Dock = DockStyle.Top,
            Height = 54,
            BackColor = UiEx.Surface,
        };

        bar.Paint += (_, e) =>
        {
            using var pen = new Pen(UiEx.Border);
            e.Graphics.DrawLine(pen, 0, bar.Height - 1, bar.Width, bar.Height - 1);
        };

        var uploadFile = UiEx.MakeButton("上传文件", ButtonStyle.Primary, 88);
        uploadFile.Click += (_, _) => PickAndUploadFiles();

        var uploadFolder = UiEx.MakeButton("上传文件夹", ButtonStyle.Normal, 96);
        uploadFolder.Click += (_, _) => PickAndUploadFolder();

        _btnDownload = UiEx.MakeButton("下载", ButtonStyle.Normal, 78);
        _btnDownload.Click += (_, _) => DownloadSelection();

        _btnPreview = UiEx.MakeButton("预览", ButtonStyle.Normal, 78);
        _btnPreview.Click += (_, _) => PreviewSelection();

        _btnRename = UiEx.MakeButton("重命名", ButtonStyle.Normal, 78);
        _btnRename.Click += (_, _) => _ = RenameSelectionAsync();

        _btnDelete = UiEx.MakeButton("删除", ButtonStyle.Danger, 78);
        _btnDelete.Click += (_, _) => _ = DeleteSelectionAsync();

        _btnCopy = UiEx.MakeButton("复制链接", ButtonStyle.Normal, 84);
        _btnCopy.Click += (_, _) => CopySelection();

        _btnRefresh = UiEx.MakeButton("刷新", ButtonStyle.Normal, 72);
        _btnRefresh.Click += (_, _) => _ = LoadAsync(false);

        var btnCsv = UiEx.MakeButton("导出清单", ButtonStyle.Normal, 84);
        btnCsv.Click += (_, _) => ExportCsv();

        _summaryLabel = UiEx.MakeLabel(string.Empty, 9F, false, UiEx.TextMuted);
        _summaryLabel.AutoSize = false;
        _summaryLabel.TextAlign = ContentAlignment.MiddleRight;

        bar.Controls.AddRange(new Control[]
        {
            uploadFile, uploadFolder, _btnDownload, _btnPreview, _btnRename,
            _btnDelete, _btnCopy, _btnRefresh, btnCsv, _summaryLabel,
        });

        void Position()
        {
            int x = 14;
            foreach (Control control in new Control[]
            {
                uploadFile, uploadFolder, _btnDownload, _btnPreview, _btnRename,
                _btnDelete, _btnCopy, _btnRefresh, btnCsv,
            })
            {
                control.Location = new Point(x, 11);
                x += control.Width + 8;
            }

            _summaryLabel.Location = new Point(bar.ClientSize.Width - 14 - 330, 12);
            _summaryLabel.Size = new Size(330, 30);
        }

        bar.Resize += (_, _) => Position();
        Position();

        return bar;
    }

    private void BuildGrid(Control host)
    {
        _grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            VirtualMode = true,
        };

        _gridHint = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = UiEx.TextMuted,
            BackColor = UiEx.Surface,
            Font = new Font("Microsoft YaHei UI", 9.5F),
        };

        UiEx.StyleGrid(_grid);

        _grid.Columns.Add(new DataGridViewImageColumn
        {
            Width = 30,
            HeaderText = string.Empty,
            ImageLayout = DataGridViewImageCellLayout.Zoom,
            Resizable = DataGridViewTriState.False,
        });

        _grid.Columns.Add(Column("name", "名称", 360, false, fill: true));
        _grid.Columns.Add(Column("size", "大小", 110, true));
        _grid.Columns.Add(Column("type", "类型", 170, false));
        _grid.Columns.Add(Column("date", "最后修改", 155, false));
        _grid.Columns.Add(Column("etag", "ETag", 110, false));
        _grid.Columns.Add(Column("class", "存储类别", 96, false));

        host.Controls.Add(_grid);
        host.Controls.Add(_gridHint);

        _grid.RowCount = 0;
        UpdateGridHint();
    }

    private Label _gridHint = null!;

    private void UpdateGridHint()
    {
        if (_gridHint is null)
        {
            return;
        }

        bool empty = _rows.Count == 0;

        _gridHint.Text = _service is null
            ? "尚未连接　·　点击右上角「设置」填写凭据后开始"
            : _search.Length > 0
                ? $"没有匹配「{_search}」的对象"
                : _prefix.Length == 0
                    ? "桶根目录下没有对象　·　把文件或文件夹拖进窗口即可上传"
                    : $"目录 {_prefix} 下没有对象";

        _gridHint.Visible = empty;
        _grid.Visible = !empty;
        _gridHint.BringToFront();
    }

    private static DataGridViewTextBoxColumn Column(string name, string header, int width, bool rightAlign, bool fill = false)
    {
        return new DataGridViewTextBoxColumn
        {
            Name = name,
            HeaderText = header,
            Width = width,
            MinimumWidth = 70,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            AutoSizeMode = fill
                ? DataGridViewAutoSizeColumnMode.Fill
                : DataGridViewAutoSizeColumnMode.None,
            FillWeight = fill ? 100 : 10,
            DefaultCellStyle = rightAlign
                ? new DataGridViewCellStyle { Alignment = DataGridViewContentAlignment.MiddleRight }
                : new DataGridViewCellStyle(),
        };
    }

    private void BuildDetailsPanel(Control host)
    {
        host.BackColor = UiEx.SurfaceAlt;

        _detailsHost = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = UiEx.Surface,
            AutoScroll = true,
            Padding = new Padding(14, 12, 10, 12),
        };

        _detailsHost.Paint += (_, e) =>
        {
            using var pen = new Pen(UiEx.Border);
            e.Graphics.DrawRectangle(pen, 0, 0, _detailsHost.Width - 1, _detailsHost.Height - 1);
        };

        host.Controls.Add(_detailsHost);
        RenderDetails();
    }

    private Control BuildTransferPanel()
    {
        _transfersPanel = new TransferPanel(_transfers)
        {
            Dock = DockStyle.Bottom,
            Height = Math.Clamp(_settings.TransferPanelHeight, 120, 620),
            Visible = _settings.TransferPanelVisible,
        };

        _transfersPanel.RetryRequested += (_, item) => RetryTransfer(item);
        _transfersPanel.VisibilityToggled += (_, _) => ShowTransferPanel(false);

        return _transfersPanel;
    }

    private void RetryTransfer(TransferItem item)
    {
        if (_service is null)
        {
            SetStatus("未连接，无法重试", UiEx.Red);
            return;
        }

        if (item.Source is null || item.RemoteKey is null)
        {
            SetStatus("该任务缺少重试所需信息，请手动重新上传 / 下载", UiEx.Amber);
            return;
        }

        TransferItem retry = item.Kind == TransferKind.Upload
            ? TransferQueue.CreateUpload(_service, _settings, item.Source, item.RemoteKey, item.Size)
            : TransferQueue.CreateDownload(_service, _settings, item.RemoteKey, item.Source, item.Size);

        TransferManager.Instance.Add(retry);
        ShowTransferPanel(true);
        SetStatus($"已重新加入队列：{item.Title}", UiEx.Blue);
    }

    private StatusStrip BuildStatusStrip()
    {
        var strip = new StatusStrip
        {
            BackColor = UiEx.SurfaceAlt,
            SizingGrip = false,
        };

        _statusLabel = new ToolStripStatusLabel
        {
            Text = "就绪",
            ForeColor = UiEx.TextMain,
            Spring = true,
            TextAlign = ContentAlignment.MiddleLeft,
        };

        _statusProgress = new ToolStripProgressBar
        {
            Visible = false,
            Width = 180,
            Style = ProgressBarStyle.Continuous,
        };

        _statusCancel = new ToolStripButton("取消")
        {
            Visible = false,
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            ForeColor = UiEx.Red,
        };
        _statusCancel.Click += (_, _) => _deleteCts?.Cancel();

        _statusTransfers = new ToolStripButton("隐藏传输队列")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            ForeColor = UiEx.Blue,
            ToolTipText = "折叠底部传输队列面板",
        };
        _statusTransfers.Click += (_, _) => ToggleTransferPanel();

        // 作者署名常驻状态栏右侧，顺带把「关于」窗口的入口放在旁边。
        _creditsLabel = new ToolStripStatusLabel(AppInfo.Credits)
        {
            ForeColor = UiEx.TextMuted,
            TextAlign = ContentAlignment.MiddleRight,
        };

        _statusAbout = new ToolStripButton("关于")
        {
            DisplayStyle = ToolStripItemDisplayStyle.Text,
            ForeColor = UiEx.Blue,
            ToolTipText = "版本、作者与首发信息",
        };
        _statusAbout.Click += (_, _) => ShowAbout();

        strip.Items.Add(_statusLabel);
        strip.Items.Add(new ToolStripStatusLabel("　"));
        strip.Items.Add(_statusProgress);
        strip.Items.Add(_statusCancel);
        strip.Items.Add(_statusTransfers);
        strip.Items.Add(new ToolStripSeparator());
        strip.Items.Add(_creditsLabel);
        strip.Items.Add(_statusAbout);

        foreach (ToolStripItem item in strip.Items)
        {
            item.BackColor = UiEx.SurfaceAlt;
        }

        return strip;
    }

    private void BuildContextMenu()
    {
        _gridMenu = new ContextMenuStrip { Font = new Font("Microsoft YaHei UI", 9F) };

        AddMenu("preview", "预览", (_, _) => PreviewSelection());
        AddMenu("download", "下载到本地", (_, _) => DownloadSelection());
        _gridMenu.Items.Add(new ToolStripSeparator());
        AddMenu("copy", "复制 Key", (_, _) => CopyKey());
        AddMenu("link", "复制链接", (_, _) => CopySelection());
        AddMenu("rename", "重命名…", (_, _) => _ = RenameSelectionAsync());
        _gridMenu.Items.Add(new ToolStripSeparator());
        AddMenu("delete", "删除", (_, _) => _ = DeleteSelectionAsync());

        _gridMenu.Opening += (_, _) =>
        {
            bool connected = _service is not null;
            foreach (ToolStripItem item in _gridMenu.Items)
            {
                item.Enabled = connected && SelectedRows.Count > 0;
            }

            if (_gridMenu.Items["rename"] is ToolStripItem rename)
            {
                rename.Enabled = connected && SelectedRows.Count == 1 && !SelectedRows[0].IsFolder;
            }
        };

        _grid.ContextMenuStrip = _gridMenu;

        void AddMenu(string icon, string text, EventHandler handler)
        {
            var item = new ToolStripMenuItem(text)
            {
                Name = icon,
                Image = UiIcons.List.Images[UiIcons.For(icon)],
                ImageScaling = ToolStripItemImageScaling.SizeToFit,
            };

            item.Click += handler;
            _gridMenu.Items.Add(item);
        }
    }

    private void WireEvents()
    {
        DragEnter += OnDragEnter;
        DragDrop += OnDragDrop;

        _grid.SelectionChanged += (_, _) =>
        {
            RenderDetails();
            UpdateActions();
        };

        _grid.CellValueNeeded += OnCellValueNeeded;

        _grid.CellDoubleClick += (_, e) =>
        {
            if (e.RowIndex < 0 || e.RowIndex >= _rows.Count)
            {
                return;
            }

            Row row = _rows[e.RowIndex];
            if (row.IsFolder)
            {
                Navigate(row.Key);
            }
            else
            {
                PreviewSelection();
            }
        };

        _grid.ColumnHeaderMouseClick += (_, e) => ToggleSort(e.ColumnIndex);

        KeyDown += OnKeyDown;
        FormClosing += OnFormClosing;
        Shown += OnShown;
    }

    private void OnCellValueNeeded(object? sender, DataGridViewCellValueEventArgs e)
    {
        if (e.RowIndex < 0 || e.RowIndex >= _rows.Count || e.ColumnIndex < 0)
        {
            e.Value = null;
            return;
        }

        Row row = _rows[e.RowIndex];

        e.Value = e.ColumnIndex switch
        {
            0 => UiIcons.List.Images[UiIcons.For(row.IconKind)],
            1 => row.Display,
            2 => row.IsFolder ? string.Empty : Humanize.Bytes(row.Size),
            3 => row.ContentType,
            4 => row.IsFolder ? string.Empty : Humanize.Date(row.LastModified),
            5 => row.IsFolder ? string.Empty : Humanize.ETag(row.ETag),
            6 => row.StorageClass,
            _ => null,
        };
    }

    // ================================================================= 生命周期

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        // 句柄建好后再投递图标：Form.Icon 只会设 ICON_BIG，标题栏的小图标槽位
        // 得自己按系统度量补一份原生尺寸，否则 Windows 会把 32 缩到 16，图标发糊。
        try
        {
            UiEx.ApplyWindowIcons(this);
        }
        catch (Exception)
        {
            // 图标问题不影响使用
        }
    }

    private async void OnShown(object? sender, EventArgs e)
    {
        if (_settings.Warning is not null)
        {
            MessageBox.Show(this, _settings.Warning, "云存储管家",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _settings.Warning = null;
        }

        _transfers.Changed += OnTransfersChanged;
        _transfers.Finished += OnTransferFinished;

        TransferManager.Instance.MaxConcurrency = _settings.Concurrency;

        if (_settings.IsReady && _settings.AutoConnect)
        {
            _service ??= StorageService.Create(_settings);
            await ConnectAsync();
        }
        else if (!_settings.HasCredentials)
        {
            ShowSettings();
        }
        else
        {
            SetStatus("请点击「刷新」连接对象存储", UiEx.Amber);
        }
    }

    private void OnTransfersChanged(object? sender, EventArgs e)
    {
        if (!IsDisposed && !Disposing)
        {
            UpdateTransferSummary();
        }
    }

    private void OnTransferFinished(object? sender, TransferItem item)
    {
        if (item.Kind == TransferKind.Upload && item.State == TransferState.Completed)
        {
            _needsRefresh = true;
        }

        if (item.State == TransferState.Failed && item.Error is not null)
        {
            _statusLabel.Text = "传输失败：" + item.Title;
            _statusLabel.ForeColor = UiEx.Red;
        }

        if (_needsRefresh && !_busy && _service is not null)
        {
            _needsRefresh = false;
            _ = LoadAsync(false);
        }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        TransferStats stats = _transfers.Stats;

        if (stats.HasActive)
        {
            var answer = MessageBox.Show(this,
                $"还有 {stats.Running + stats.Waiting} 个传输任务未完成，退出将被中断。是否继续退出？",
                "确认退出", MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);

            if (answer != DialogResult.OK)
            {
                e.Cancel = true;
                return;
            }

            _transfers.CancelAll();
        }

        if (WindowState == FormWindowState.Normal)
        {
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
        }

        _settings.WindowMaximized = WindowState == FormWindowState.Maximized;
        _settings.TransferPanelVisible = _transfersPanel.Visible;
        _settings.TransferPanelHeight = _transfersPanel.Height;
        _settings.LastPrefix = _prefix;

        if (_split is not null)
        {
            _settings.SplitterDistance = _split.SplitterDistance;
        }

        try
        {
            SettingsStore.Save(_settings);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("保存配置失败：" + ex.Message);
        }

        _listCts?.Cancel();
        _deleteCts?.Cancel();
        _service?.Dispose();
        _service = null;
    }

    // ================================================================= 连接

    private async Task ConnectAsync()
    {
        if (_service is null)
        {
            ShowSettings();
            return;
        }

        SetPill("正在连接…", UiEx.Amber);
        SetStatus($"正在连接 {_settings.BucketName} @ {_settings.EndpointHost}…");

        _listCts?.Cancel();
        _listCts = new CancellationTokenSource();

        // SDK 自身的超时是 30 分钟（要给大文件上传留时间），
        // 但「连不上」和「传得慢」必须区分开：这里给首次连通单独一个上限，
        // 否则网络不通时界面会长时间停在「正在连接」，看着像卡死。
        using var timeoutCts = new CancellationTokenSource(ConnectTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_listCts.Token, timeoutCts.Token);

        try
        {
            await _service.TestAsync(linkedCts.Token);
            TransferManager.Instance.MaxConcurrency = _settings.Concurrency;

            SetPill("已连接", UiEx.Green);
            UpdateConnectionUi();
            UpdateGridHint();

            await LoadAsync(true);
        }
        catch (Exception ex) when (ErrorInfo.IsCanceled(ex))
        {
            bool timedOut = timeoutCts.IsCancellationRequested && !_listCts.IsCancellationRequested;

            if (timedOut)
            {
                SetPill("连接超时", UiEx.Red);
                SetStatus($"连接 {_settings.EndpointHost} 超时（{ConnectTimeout.TotalSeconds:F0} 秒无响应）");
                UpdateConnectionUi();

                ErrorInfo.Report("连接超时", new TimeoutException(
                    $"在 {ConnectTimeout.TotalSeconds:F0} 秒内无法连接到 {_settings.EndpointHost}。\r\n" +
                    "请检查网络是否通畅、是否需要走代理，以及防火墙是否拦截了该端口。"));
            }
            else
            {
                SetPill("已取消", UiEx.StatusIdle);
                SetStatus("连接已取消");
            }
        }
        catch (Exception ex)
        {
            SetPill("连接失败", UiEx.Red);
            SetStatus("连接失败：" + ErrorInfo.Describe(ex));
            UpdateConnectionUi();

            if (ErrorInfo.IsAuthFailure(ex))
            {
                ShowSettings();
            }
            else
            {
                ErrorInfo.Report("连接对象存储失败", ex);
            }
        }
    }

    private void ShowAbout()
    {
        using var dialog = new AboutForm(_settings);
        dialog.ShowDialog(this);
    }

    private void ShowSettings()
    {
        bool wasConnected = _service is not null;

        using var dialog = new SettingsForm(_settings);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            if (!wasConnected)
            {
                SetStatus("尚未配置连接信息", UiEx.Amber);
            }

            return;
        }

        try
        {
            _service?.Dispose();
            _service = _settings.IsReady ? StorageService.Create(_settings) : null;
            TransferManager.Instance.MaxConcurrency = _settings.Concurrency;
            UpdateConnectionUi();

            if (_service is not null)
            {
                _ = ConnectAsync();
            }
            else
            {
                SetPill("未配置", UiEx.StatusIdle);
                SetStatus("请填写 Account ID、密钥与桶名");
            }
        }
        catch (Exception ex)
        {
            ErrorInfo.Report("配置无效", ex);
        }
    }

    // ================================================================= 列表

    private async Task LoadAsync(bool resetPage)
    {
        if (_service is null)
        {
            return;
        }

        _listCts?.Cancel();
        _listCts = new CancellationTokenSource();
        CancellationToken ct = _listCts.Token;

        if (resetPage)
        {
            _pageIndex = 0;
            _pageTokens.Clear();
            _settings.LastPrefix = _prefix;
        }

        _busy = true;
        UpdateActions();
        SetStatus("正在加载对象列表…");

        try
        {
            string? token = _pageIndex < _pageTokens.Count ? _pageTokens[_pageIndex] : null;
            ListObjectsPage page = await _service.ListAsync(_prefix, token, PageSize, ct);

            _pageObjects.Clear();
            _pageObjects.AddRange(page.Objects);
            _pagePrefixes.Clear();
            _pagePrefixes.AddRange(page.Prefixes);

            if (_pageIndex + 1 < _pageTokens.Count)
            {
                _pageTokens.RemoveRange(_pageIndex + 1, _pageTokens.Count - _pageIndex - 1);
            }

            if (page.NextContinuationToken is not null)
            {
                if (_pageTokens.Count == _pageIndex + 1)
                {
                    _pageTokens.Add(null);
                }

                _pageTokens[_pageIndex + 1] = page.NextContinuationToken;
            }

            _prefixBox.Text = _prefix;
            _grid.ClearSelection();
            RebuildRows();

            SetStatus(page.IsTruncated
                ? $"已加载 {_pageObjects.Count} 个对象 / {_pagePrefixes.Count} 个目录（还有更多，请翻页）"
                : $"已加载 {_pageObjects.Count} 个对象 / {_pagePrefixes.Count} 个目录");
        }
        catch (Exception ex) when (ErrorInfo.IsCanceled(ex))
        {
            SetStatus("加载已取消");
        }
        catch (Exception ex)
        {
            SetStatus("加载失败：" + ErrorInfo.Describe(ex), UiEx.Red);
        }
        finally
        {
            _busy = false;
            UpdateActions();
        }
    }

    private void RebuildRows()
    {
        _grid.SuspendLayout();

        var all = new List<Row>(_pagePrefixes.Count + _pageObjects.Count);

        foreach (string prefix in _pagePrefixes)
        {
            all.Add(Row.Folder(prefix));
        }

        foreach (S3Object item in _pageObjects)
        {
            all.Add(Row.Object(item));
        }

        if (_search.Length > 0)
        {
            all = all
                .Where(r => r.Display.Contains(_search, StringComparison.OrdinalIgnoreCase) ||
                            r.Key.Contains(_search, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        all.Sort(CompareRows);

        _rows.Clear();
        _rows.AddRange(all);

        _grid.RowCount = _rows.Count;
        _grid.ResumeLayout();
        _grid.Invalidate();

        UpdateGridHint();
        RenderDetails();
        UpdateSummary();
    }

    private int CompareRows(Row x, Row y)
    {
        if (x.IsFolder != y.IsFolder)
        {
            return x.IsFolder ? -1 : 1;
        }

        int result = _sortColumn switch
        {
            "size" => x.Size.CompareTo(y.Size),
            "date" => x.LastModified.CompareTo(y.LastModified),
            "type" => string.Compare(x.ContentType, y.ContentType, StringComparison.OrdinalIgnoreCase),
            "etag" => string.Compare(x.ETag ?? string.Empty, y.ETag ?? string.Empty, StringComparison.OrdinalIgnoreCase),
            "class" => string.Compare(x.StorageClass, y.StorageClass, StringComparison.OrdinalIgnoreCase),
            _ => string.Compare(x.Display, y.Display, StringComparison.OrdinalIgnoreCase),
        };

        return _sortAscending ? result : -result;
    }

    private void ToggleSort(int columnIndex)
    {
        string column = columnIndex switch
        {
            2 => "size",
            3 => "type",
            4 => "date",
            5 => "etag",
            6 => "class",
            _ => "name",
        };

        if (_sortColumn == column)
        {
            _sortAscending = !_sortAscending;
        }
        else
        {
            _sortColumn = column;
            _sortAscending = column is not "size";
        }

        RebuildRows();
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        long size = _rows.Where(r => !r.IsFolder).Sum(r => r.Size);
        int folders = _rows.Count(r => r.IsFolder);
        string search = _search.Length > 0 ? $"，已筛选 “{_search}”" : string.Empty;

        _summaryLabel.Text = $"{_rows.Count} 项 · {folders} 个目录 · {Humanize.Bytes(size)}{search}";
    }

    private void ChangePage(int delta)
    {
        int target = _pageIndex + delta;
        if (target < 0 || target >= _pageTokens.Count || _busy)
        {
            return;
        }

        _pageIndex = target;
        _ = LoadAsync(false);
    }

    private void Navigate(string prefix)
    {
        prefix = prefix.Trim();
        if (prefix.Length > 0 && !prefix.EndsWith('/'))
        {
            prefix += "/";
        }

        if (prefix == _prefix)
        {
            return;
        }

        _prefix = prefix;
        _searchBox.Clear();
        _ = LoadAsync(true);
    }

    // ================================================================= 状态与按钮

    private void SetStatus(string message, Color? color = null)
    {
        _statusLabel.Text = message;
        _statusLabel.ForeColor = color ?? UiEx.TextMain;
    }

    private void SetPill(string text, Color? dot = null)
    {
        _pill.SetStatus(text, dot);
    }

    private void UpdateConnectionUi()
    {
        bool ready = _settings.IsReady;
        bool connected = _service is not null;

        _bucketLabel.Text = ready ? _settings.BucketName : "尚未配置";
        _bucketLabel.ForeColor = ready ? UiEx.TextMain : UiEx.TextMuted;

        string provider = StorageProviders.DisplayName(_settings.Provider);
        _endpointLabel.Text = ready
            ? $"{provider}　·　{_settings.EndpointHost}" + (connected ? string.Empty : "（未连接）")
            : "点击右上角「设置」填写凭据后开始";

        if (!connected && _pill.Text != "正在连接…")
        {
            _pill.SetStatus("未连接", UiEx.StatusIdle);
        }

        UpdateActions();
    }

    private void UpdateActions()
    {
        bool hasSelection = SelectedRows.Count > 0;
        bool single = SelectedRows.Count == 1;
        bool connected = _service is not null && !_busy;

        _btnDownload.Enabled = connected && hasSelection;
        _btnPreview.Enabled = connected && hasSelection;
        _btnDelete.Enabled = connected && hasSelection;
        _btnRename.Enabled = connected && single && !SelectedRows[0].IsFolder;
        _btnCopy.Enabled = connected && hasSelection;
        _btnRefresh.Enabled = _service is not null && !_busy;
        _btnUp.Enabled = _prefix.Length > 0 && connected;
        _btnPrevPage.Enabled = connected && _pageIndex > 0;
        _btnNextPage.Enabled = connected && _pageIndex + 1 < _pageTokens.Count;

        _pageLabel.Text = $"第 {_pageIndex + 1} / {Math.Max(1, _pageTokens.Count)} 页";
    }

    private List<Row> SelectedRows =>
        _grid.SelectedRows
            .Cast<DataGridViewRow>()
            .Where(r => r.Index >= 0 && r.Index < _rows.Count)
            .Select(r => _rows[r.Index])
            .ToList();

    private List<Row> SelectedObjects => SelectedRows.Where(r => !r.IsFolder).ToList();

    // ================================================================= 详情面板

    private void RenderDetails()
    {
        if (_detailsHost is null)
        {
            return;
        }

        foreach (Control control in _detailsHost.Controls.Cast<Control>().ToArray())
        {
            _detailsHost.Controls.Remove(control);
            control.Dispose();
        }

        var layout = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Margin = new Padding(0),
        };

        List<Row> selection = SelectedRows;

        if (_service is null)
        {
            layout.Controls.Add(Title("连接"));
            layout.Controls.Add(Body("还没有连接到 R2。\r\n\r\n在右上角「设置」中填写 Account ID、Access Key ID 与 Secret Access Key，即可开始管理对象。", 96));
        }
        else if (selection.Count == 0)
        {
            layout.Controls.Add(Title("当前目录"));
            layout.Controls.Add(DetailLine("路径", _prefix.Length == 0 ? "/" : _prefix, mono: true));
            layout.Controls.Add(DetailLine("对象数", _pageObjects.Count.ToString(CultureInfo.InvariantCulture)));
            layout.Controls.Add(DetailLine("子目录", _pagePrefixes.Count.ToString(CultureInfo.InvariantCulture)));
            layout.Controls.Add(DetailLine("合计大小", Humanize.Bytes(_pageObjects.Sum(o => o.Size))));
            layout.Controls.Add(Body(
                "提示：双击目录进入，双击文件预览；\r\n直接把文件或文件夹拖进窗口即可上传。\r\nCtrl+U 上传 · F5 刷新 · Delete 删除。", 84));
        }
        else if (selection.Count == 1)
        {
            Row row = selection[0];

            layout.Controls.Add(Title(row.IsFolder ? "目录详情" : "对象详情"));
            layout.Controls.Add(DetailLine("名称", row.Display, mono: true));
            layout.Controls.Add(DetailLine("完整 Key", row.Key, mono: true));

            if (!row.IsFolder)
            {
                layout.Controls.Add(DetailLine("大小", Humanize.Bytes(row.Size) + $"（{row.Size:N0} 字节）"));
                layout.Controls.Add(DetailLine("类型", row.ContentType));
                layout.Controls.Add(DetailLine("最后修改", Humanize.Date(row.LastModified)));
                layout.Controls.Add(DetailLine("ETag", Humanize.ETag(row.ETag, 20)));
                layout.Controls.Add(DetailLine("存储类别", row.StorageClass));
                layout.Controls.Add(DetailLine("链接", _service.BuildPublicUrl(row.Key), mono: true));
            }

            layout.Controls.Add(BuildDetailButtons(row));
        }
        else
        {
            List<Row> objects = selection.Where(r => !r.IsFolder).ToList();

            layout.Controls.Add(Title($"已选择 {selection.Count} 项"));
            layout.Controls.Add(DetailLine("文件数", objects.Count.ToString(CultureInfo.InvariantCulture)));
            layout.Controls.Add(DetailLine("目录数", (selection.Count - objects.Count).ToString(CultureInfo.InvariantCulture)));
            layout.Controls.Add(DetailLine("合计大小", Humanize.Bytes(objects.Sum(r => r.Size))));
            layout.Controls.Add(Body("批量操作：下载、删除、复制链接。", 24));
        }

        _detailsHost.Controls.Add(layout);
    }

    private static Control Title(string text) => new SectionTitle(text) { Width = DetailWidth - 28 };

    private static Control Body(string text, int height) => new Label
    {
        Text = text,
        Width = DetailWidth - 28,
        Height = height,
        ForeColor = UiEx.TextMuted,
        Font = new Font("Microsoft YaHei UI", 8.5F),
        BackColor = Color.Transparent,
    };

    private const int DetailWidth = 330;

    /// <summary>详情字段：上方是小标题，下方是可读性更好的值（长 Key 自动换行）。</summary>
    private static Control DetailLine(string name, string value, bool mono = false)
    {
        Font font = mono ? UiEx.MonoSmall : new Font("Microsoft YaHei UI", 9F);
        int width = DetailWidth - 28;
        int lines = mono && value.Length > 34 ? 3 : 1;

        var panel = new Panel
        {
            Width = width,
            Height = 18 + lines * 17 + 12,
            Margin = new Padding(0, 0, 0, 6),
            BackColor = Color.Transparent,
        };

        var nameLabel = new Label
        {
            Text = name,
            Dock = DockStyle.Top,
            Height = 17,
            ForeColor = UiEx.TextMuted,
            Font = new Font("Microsoft YaHei UI", 8.5F),
            BackColor = Color.Transparent,
        };

        var valueLabel = new Label
        {
            Text = value,
            Dock = DockStyle.Fill,
            ForeColor = UiEx.TextMain,
            Font = font,
            BackColor = Color.Transparent,
            AutoEllipsis = !mono,
            UseMnemonic = false,
            Cursor = Cursors.IBeam,
        };

        valueLabel.DoubleClick += (_, _) =>
        {
            try
            {
                Clipboard.SetText(value);
            }
            catch (Exception ex)
            {
                ErrorInfo.Report("复制失败", ex);
            }
        };

        if (mono && value.Length > 0)
        {
            new ToolTip().SetToolTip(valueLabel, $"{value}\r\n（双击可复制）");
        }

        panel.Controls.Add(valueLabel);
        panel.Controls.Add(nameLabel);

        return panel;
    }

    private Control BuildDetailButtons(Row row)
    {
        var panel = new FlowLayoutPanel
        {
            Width = DetailWidth - 28,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = true,
            Margin = new Padding(0, 14, 0, 0),
        };

        if (!row.IsFolder)
        {
            var preview = UiEx.MakeButton("预览", ButtonStyle.Primary, 76);
            preview.Click += (_, _) => PreviewSelection();

            var download = UiEx.MakeButton("下载", ButtonStyle.Normal, 76);
            download.Click += (_, _) => DownloadSelection();

            panel.Controls.Add(preview);
            panel.Controls.Add(download);
        }

        var copy = UiEx.MakeButton("复制 Key", ButtonStyle.Ghost, 86);
        copy.Click += (_, _) => CopyKey();
        panel.Controls.Add(copy);

        return panel;
    }

    private void UpdateTransferSummary()
    {
        TransferStats stats = _transfers.Stats;

        UpdateTransferToggle();

        if (stats.HasActive)
        {
            _statusLabel.Text = $"传输中：{stats.Running} 进行中，{stats.Waiting} 等待" +
                                (stats.Completed > 0 ? $"，{stats.Completed} 已完成" : string.Empty) +
                                (stats.Failed > 0 ? $"，{stats.Failed} 失败" : string.Empty);
            _statusLabel.ForeColor = UiEx.TextMain;
        }
    }

    private void ShowTransferPanel(bool visible)
    {
        if (_transfersPanel.Visible != visible)
        {
            _transfersPanel.Visible = visible;
        }

        UpdateTransferToggle();
    }

    private void ToggleTransferPanel() => ShowTransferPanel(!_transfersPanel.Visible);

    private void UpdateTransferToggle()
    {
        bool visible = _transfersPanel.Visible;
        TransferStats stats = _transfers.Stats;

        _statusTransfers.Text = visible ? "隐藏传输队列" : "显示传输队列";
        _statusTransfers.ForeColor = !visible && stats.HasActive ? UiEx.Amber : UiEx.Blue;
        _statusTransfers.ToolTipText = visible ? "折叠底部传输队列面板" : "展开底部传输队列面板";
    }

    // ================================================================= 上传

    private void EnsureConnected()
    {
        if (_service is null)
        {
            ShowSettings();
        }
    }

    private void PickAndUploadFiles()
    {
        EnsureConnected();

        if (_service is null)
        {
            return;
        }

        using var dialog = new OpenFileDialog
        {
            Title = "选择要上传的文件（按住 Ctrl / Shift 可多选）",
            Multiselect = true,
            CheckFileExists = true,
            InitialDirectory = ResolveDownloadDirectory(),
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var files = dialog.FileNames
            .Select(path => (LocalPath: path, Key: _prefix + Path.GetFileName(path)))
            .ToList();

        EnqueueUploads(files);
    }

    private void PickAndUploadFolder()
    {
        EnsureConnected();

        if (_service is null)
        {
            return;
        }

        using var dialog = new FolderBrowserDialog
        {
            Description = "选择要上传的文件夹（递归上传并保留目录结构）",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(_settings.DownloadDirectory) ? _settings.DownloadDirectory : null,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        string selected = dialog.SelectedPath ?? string.Empty;

        if (selected.Length == 0)
        {
            return;
        }

        string folderName = new DirectoryInfo(selected).Name;
        ScanAndEnqueue(selected, _prefix + folderName + "/");
    }

    private void ScanAndEnqueue(string root, string keyPrefix)
    {
        SetStatus("正在扫描本地文件夹…");

        Task.Run(() => FolderScanner.Scan(root, keyPrefix, CancellationToken.None))
            .ContinueWith(task => InvokeOnUi(() =>
            {
                if (task.IsFaulted)
                {
                    SetStatus("扫描失败", UiEx.Red);
                    ErrorInfo.Report("扫描文件夹失败", task.Exception!.GetBaseException());
                    return;
                }

                var files = task.Result;

                if (files.Count == 0)
                {
                    SetStatus("该文件夹内没有可上传的文件", UiEx.Amber);
                    return;
                }

                EnqueueUploads(files);
            }), TaskScheduler.Default);
    }

    private void EnqueueUploads(List<(string LocalPath, string Key)> files)
    {
        if (_service is null || files.Count == 0)
        {
            return;
        }

        ShowTransferPanel(true);

        int count = TransferQueue.EnqueueUploads(_service, _settings, files);
        SetStatus($"已加入 {count} 个上传任务", UiEx.Blue);
    }

    private void OnDragEnter(object? sender, DragEventArgs e)
    {
        if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true && _service is not null && !_busy)
        {
            e.Effect = DragDropEffects.Copy;
            SetStatus("松开鼠标即可上传到当前目录", UiEx.Blue);
        }
        else
        {
            e.Effect = DragDropEffects.None;
        }
    }

    private void OnDragDrop(object? sender, DragEventArgs e)
    {
        if (_service is null || e.Data?.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return;
        }

        var files = new List<(string LocalPath, string Key)>();

        foreach (string path in paths)
        {
            if (Directory.Exists(path))
            {
                string name = new DirectoryInfo(path).Name;
                files.AddRange(FolderScanner.Scan(path, _prefix + name + "/", CancellationToken.None));
            }
            else if (File.Exists(path))
            {
                files.Add((path, _prefix + Path.GetFileName(path)));
            }
        }

        if (files.Count > 0)
        {
            EnqueueUploads(files);
        }
        else
        {
            SetStatus("没有可上传的文件", UiEx.Amber);
        }
    }

    // ================================================================= 下载 / 预览 / 复制

    private string ResolveDownloadDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_settings.DownloadDirectory) && Directory.Exists(_settings.DownloadDirectory))
        {
            return _settings.DownloadDirectory;
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    private void DownloadSelection()
    {
        List<Row> rows = SelectedObjects;

        if (rows.Count == 0 || _service is null)
        {
            return;
        }

        string destination;

        if (rows.Count == 1)
        {
            using var dialog = new SaveFileDialog
            {
                Title = "保存到本地",
                FileName = Humanize.LeafName(rows[0].Key),
                InitialDirectory = ResolveDownloadDirectory(),
                OverwritePrompt = true,
            };

            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            destination = dialog.FileName;
        }
        else
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = $"选择保存 {rows.Count} 个对象的目录（会保留对象路径结构）",
                UseDescriptionForTitle = true,
                SelectedPath = ResolveDownloadDirectory(),
            };

            if (dialog.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            destination = dialog.SelectedPath;
        }

        var jobs = rows
            .Select(row => rows.Count == 1
                ? (row.Key, destination, row.Size)
                : (row.Key, Path.Combine(destination, Humanize.ToSafeRelativePath(row.Key)), row.Size))
            .ToList();

        ShowTransferPanel(true);
        int count = TransferQueue.EnqueueDownloads(_service, _settings, jobs);

        _settings.DownloadDirectory = rows.Count == 1
            ? Path.GetDirectoryName(destination) ?? string.Empty
            : destination;

        SetStatus($"已加入 {count} 个下载任务", UiEx.Blue);
    }

    private void PreviewSelection()
    {
        List<Row> rows = SelectedRows;

        if (rows.Count == 0 || _service is null)
        {
            return;
        }

        if (rows.Count == 1 && rows[0].IsFolder)
        {
            Navigate(rows[0].Key);
            return;
        }

        var keys = rows.Select(r => r.Key).ToList();
        int index = keys.IndexOf(rows[0].Key);

        if (_previewForm is { IsDisposed: false })
        {
            _previewForm.SetSource(_service, keys, index);
            _previewForm.Activate();
            return;
        }

        _previewForm = new PreviewForm(_service, keys, index);
        _previewForm.FormClosed += (_, _) => _previewForm = null;
        _previewForm.Show(this);
    }

    private void CopySelection()
    {
        List<Row> rows = SelectedRows;

        if (rows.Count == 0 || _service is null)
        {
            return;
        }

        string text = rows.Count == 1
            ? _service.BuildPublicUrl(rows[0].Key)
            : string.Join(Environment.NewLine, rows.Select(r => _service.BuildPublicUrl(r.Key)));

        SetClipboard(text, rows.Count == 1 ? "已复制链接" : $"已复制 {rows.Count} 条链接");
    }

    private void CopyKey()
    {
        List<Row> rows = SelectedRows;

        if (rows.Count == 0)
        {
            return;
        }

        string text = rows.Count == 1 ? rows[0].Key : string.Join(Environment.NewLine, rows.Select(r => r.Key));
        SetClipboard(text, rows.Count == 1 ? "已复制 Key" : $"已复制 {rows.Count} 个 Key");
    }

    private void SetClipboard(string text, string message)
    {
        try
        {
            Clipboard.SetText(text);
            SetStatus(message, UiEx.Green);
        }
        catch (Exception ex)
        {
            ErrorInfo.Report("写入剪贴板失败", ex);
        }
    }

    // ================================================================= 删除 / 重命名 / 导出

    private async Task DeleteSelectionAsync()
    {
        List<Row> rows = SelectedRows;

        if (rows.Count == 0 || _service is null)
        {
            return;
        }

        long totalSize = rows.Where(r => !r.IsFolder).Sum(r => r.Size);
        int folderCount = rows.Count(r => r.IsFolder);

        var message = new StringBuilder();
        message.AppendLine($"确定要删除 {rows.Count} 项吗？");
        message.AppendLine($"文件：{rows.Count - folderCount} 个（{Humanize.Bytes(totalSize)}）");

        if (folderCount > 0)
        {
            message.AppendLine($"目录前缀：{folderCount} 个（将删除该前缀下的所有对象）");
        }

        message.AppendLine();
        message.AppendLine("删除后不可恢复。");

        if (MessageBox.Show(this, message.ToString(), "确认删除",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK)
        {
            return;
        }

        var keys = new List<string>();

        foreach (Row row in rows)
        {
            if (row.IsFolder)
            {
                keys.AddRange(await CollectPrefixKeysAsync(row.Key));
            }
            else
            {
                keys.Add(row.Key);
            }
        }

        keys = keys.Distinct(StringComparer.Ordinal).ToList();

        if (keys.Count == 0)
        {
            SetStatus("没有可删除的对象", UiEx.Amber);
            return;
        }

        _deleteCts = new CancellationTokenSource();
        _statusProgress.Visible = true;
        _statusCancel.Visible = true;
        _statusProgress.Minimum = 0;
        _statusProgress.Maximum = Math.Max(1, keys.Count);
        _statusProgress.Value = 0;

        try
        {
            var progress = new Progress<int>(done =>
            {
                _statusProgress.Value = Math.Clamp(done, 0, keys.Count);
                _statusLabel.Text = $"正在删除 {done} / {keys.Count}…";
            });

            DeleteSummary summary = await _service.DeleteAsync(keys, progress, _deleteCts.Token);

            if (summary.Errors.Count == 0)
            {
                SetStatus($"已删除 {summary.Deleted} 个对象", UiEx.Green);
            }
            else
            {
                SetStatus($"已删除 {summary.Deleted} 个，{summary.Errors.Count} 个失败", UiEx.Red);
                MessageBox.Show(this,
                    string.Join(Environment.NewLine, summary.Errors.Take(25)) +
                    (summary.Errors.Count > 25 ? $"\r\n…共 {summary.Errors.Count} 条失败" : string.Empty),
                    "部分对象删除失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            await LoadAsync(false);
        }
        catch (Exception ex) when (ErrorInfo.IsCanceled(ex))
        {
            SetStatus("删除已取消", UiEx.Amber);
        }
        catch (Exception ex)
        {
            SetStatus("删除失败", UiEx.Red);
            ErrorInfo.Report("删除对象失败", ex);
        }
        finally
        {
            _statusProgress.Visible = false;
            _statusCancel.Visible = false;
            _deleteCts?.Dispose();
            _deleteCts = null;
        }
    }

    private async Task<List<string>> CollectPrefixKeysAsync(string prefix)
    {
        var keys = new List<string>();
        string? token = null;

        do
        {
            ListObjectsPage page = await _service!
                .ListAsync(prefix, token, 1000, CancellationToken.None, useDelimiter: false);

            keys.AddRange(page.Objects.Select(o => o.Key));
            token = page.NextContinuationToken;
        }
        while (token is not null);

        keys.Add(prefix.TrimEnd('/'));
        return keys;
    }

    private async Task RenameSelectionAsync()
    {
        List<Row> rows = SelectedRows;

        if (rows.Count != 1 || rows[0].IsFolder || _service is null)
        {
            return;
        }

        Row row = rows[0];

        using var dialog = new RenameDialog(row.Display);
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        string newName = dialog.NewName.Trim();

        if (string.Equals(newName, row.Display, StringComparison.Ordinal))
        {
            return;
        }

        if (newName.Length == 0 || newName.Contains('/') || newName.Contains('\\'))
        {
            SetStatus("新名称不能为空，也不能包含路径分隔符", UiEx.Amber);
            return;
        }

        int slash = row.Key.LastIndexOf('/');
        string newKey = slash >= 0 ? row.Key[..(slash + 1)] + newName : newName;

        SetStatus($"正在把 {row.Display} 重命名为 {newName}…");

        try
        {
            if (await _service.TryServerSideCopyAsync(row.Key, newKey, CancellationToken.None))
            {
                await _service.DeleteAsync(new[] { row.Key }, null, CancellationToken.None);
            }
            else
            {
                SetStatus("服务端复制不可用，改用下载 / 上传完成重命名（大文件可能较慢）…", UiEx.Amber);

                string tempFile = Path.Combine(Path.GetTempPath(), "r2-rename-" + Guid.NewGuid().ToString("N"));

                try
                {
                    await ParallelDownloader.DownloadAsync(
                        _service, row.Key, tempFile, null, CancellationToken.None,
                        new DownloadOptions(_settings.MultipartThresholdBytes, _settings.PartSizeBytes, _settings.Concurrency));

                    await MultipartUploader.UploadAsync(
                        _service, tempFile, newKey, null, CancellationToken.None,
                        new UploadOptions(_settings.MultipartThresholdBytes, _settings.PartSizeBytes,
                            _settings.Concurrency, true, false));
                }
                finally
                {
                    try
                    {
                        File.Delete(tempFile);
                    }
                    catch (IOException)
                    {
                        // 交给系统清理临时目录
                    }
                }

                await _service.DeleteAsync(new[] { row.Key }, null, CancellationToken.None);
            }

            SetStatus($"已重命名为 {newName}", UiEx.Green);
            await LoadAsync(false);
        }
        catch (Exception ex)
        {
            SetStatus("重命名失败", UiEx.Red);
            ErrorInfo.Report("重命名失败", ex);
        }
    }

    private void ExportCsv()
    {
        if (_rows.Count == 0)
        {
            SetStatus("当前没有可导出的对象", UiEx.Amber);
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Title = "导出当前页清单为 CSV",
            Filter = "CSV 文件 (*.csv)|*.csv",
            FileName = $"r2-listing-{DateTime.Now:yyyyMMdd-HHmm}.csv",
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            var csv = new StringBuilder();
            csv.AppendLine("类型,名称,大小(字节),大小,最后修改,ETag,存储类别,Key");

            foreach (Row row in _rows)
            {
                csv.Append(row.IsFolder ? "目录," : "文件,");
                csv.Append(Csv(row.Display)).Append(',');
                csv.Append(row.IsFolder ? "0" : row.Size.ToString(CultureInfo.InvariantCulture)).Append(',');
                csv.Append(Csv(row.IsFolder ? string.Empty : Humanize.Bytes(row.Size))).Append(',');
                csv.Append(Csv(Humanize.Date(row.LastModified))).Append(',');
                csv.Append(Csv(Humanize.ETag(row.ETag, 64))).Append(',');
                csv.Append(Csv(row.StorageClass)).Append(',');
                csv.AppendLine(Csv(row.Key));
            }

            File.WriteAllText(dialog.FileName, csv.ToString(), new UTF8Encoding(true));
            SetStatus($"已导出 {_rows.Count} 条记录", UiEx.Green);
        }
        catch (Exception ex)
        {
            ErrorInfo.Report("导出清单失败", ex);
        }
    }

    private static string Csv(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Contains('"') || value.Contains(',') || value.Contains('\n')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
    }

    // ================================================================= 键盘 / 杂项

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.KeyCode)
        {
            case Keys.F5:
                e.SuppressKeyPress = true;
                _ = LoadAsync(false);
                return;

            case Keys.Delete:
                if (SelectedRows.Count > 0)
                {
                    e.SuppressKeyPress = true;
                    _ = DeleteSelectionAsync();
                }

                return;

            case Keys.Enter:
                if (_grid.Focused && SelectedRows.Count > 0)
                {
                    e.SuppressKeyPress = true;
                    PreviewSelection();
                }

                return;

            case Keys.Back:
                if (_prefix.Length > 0 && !_grid.Focused && !_searchBox.Focused)
                {
                    e.SuppressKeyPress = true;
                    Navigate(Humanize.ParentPrefix(_prefix));
                }

                return;

            case Keys.Escape:
                if (_searchBox.Focused && _searchBox.Text.Length > 0)
                {
                    e.SuppressKeyPress = true;
                    _searchBox.Clear();
                }

                return;
        }

        if (!e.Control)
        {
            return;
        }

        switch (e.KeyCode)
        {
            case Keys.U:
                e.SuppressKeyPress = true;
                PickAndUploadFiles();
                break;

            case Keys.F:
                e.SuppressKeyPress = true;
                _searchBox.Focus();
                _searchBox.SelectAll();
                break;

            case Keys.R:
                e.SuppressKeyPress = true;
                _ = LoadAsync(false);
                break;

            case Keys.L:
                e.SuppressKeyPress = true;
                CopySelection();
                break;
        }
    }

    private void InvokeOnUi(Action action)
    {
        if (IsDisposed || Disposing)
        {
            return;
        }

        if (InvokeRequired)
        {
            try
            {
                BeginInvoke(action);
            }
            catch (ObjectDisposedException)
            {
                // 窗口已关闭
            }

            return;
        }

        action();
    }
}
