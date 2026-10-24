using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Amazon.S3;
using Amazon.S3.Model;
using CloudStore.Core;

namespace CloudStore.UI;

/// <summary>连接与传输参数设置窗口（双栏布局，无需滚动）。</summary>
public sealed class SettingsForm : Form
{
    private readonly StorageSettings _settings;

    private FlatComboBox _provider = null!;
    private FlatTextBox _accountId = null!;
    private FlatTextBox _region = null!;
    private FlatTextBox _accessKeyId = null!;
    private FlatTextBox _secretKey = null!;
    private FlatCheckBox _showSecret = null!;
    private FlatCheckBox _rememberSecret = null!;
    private FlatTextBox _endpoint = null!;
    private FlatTextBox _publicDomain = null!;
    private FlatComboBox _bucket = null!;
    private FlatButton _btnRefreshBuckets = null!;
    private FlatTextBox _concurrency = null!;
    private FlatTextBox _threshold = null!;
    private FlatTextBox _partSize = null!;
    private FlatComboBox _conflict = null!;
    private FlatCheckBox _autoConnect = null!;
    private Label _status = null!;

    /// <summary>需要随云厂商改文案 / 显隐的行。</summary>
    private Control _accountIdRow = null!;
    private Control _regionRow = null!;
    private Control _accessKeyRow = null!;
    private Control _secretKeyRow = null!;
    private Control _endpointRow = null!;
    private Control _publicDomainRow = null!;
    private Control _addressingRow = null!;
    private FlatComboBox _addressing = null!;

    /// <summary>左右两栏。用来量内容实际占多高，防止最后一行被裁掉。</summary>
    private FlowLayoutPanel _leftColumn = null!;
    private FlowLayoutPanel _rightColumn = null!;

    /// <summary>行容器 → 该行的标题 Label，用来动态改字段名。</summary>
    private readonly Dictionary<Control, Label> _captions = new();

    /// <summary>悬浮提示。字段名会随云厂商变，提示也得跟着换。</summary>
    private readonly ToolTip _tips = new();

    /// <summary>加载已保存配置时抑制厂商切换事件。</summary>
    private bool _suppressProviderEvents;

    private FlatButton _btnConsole = null!;

    private const int ColumnWidth = 312;
    private const int RowHeight = 48;
    private const int CaptionHeight = 18;
    private const int FooterHeight = 100;

    /// <summary>窗口的基础高度。内容装不下时按实际需要往上加，不裁内容。</summary>
    private const int BaseClientHeight = 540;

    /// <summary>body 表格上下留白合计。</summary>
    private const int BodyPaddingVertical = 18;

    /// <summary>列举桶只走一次控制面请求，给它一个明确的超时上限，别让界面看起来卡死。</summary>
    private static readonly TimeSpan BucketListTimeout = TimeSpan.FromSeconds(25);

    private CancellationTokenSource? _bucketCts;

    /// <summary>宽度依赖 Dock 布局、必须在窗口显示后统一执行的排版动作。</summary>
    private readonly List<Action> _layoutJobs = new();

    public SettingsForm(StorageSettings settings)
    {
        _settings = settings;

        Text = "云存储管家 — 设置";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = UiEx.Surface;
        Font = new Font("Microsoft YaHei UI", 9F);

        if (UiEx.AppIcon() is { } dialogIcon)
        {
            Icon = dialogIcon;
        }

        BuildUi();
        LoadValues();

        Load += (_, _) =>
        {
            foreach (Action job in _layoutJobs)
            {
                job();
            }

            FitHeightToContent();
        };

        Shown += async (_, _) =>
        {
            // 已有凭据但没记住桶名时，自动拉一次，省得每次都手动点。
            if (_settings.HasCredentials && string.IsNullOrWhiteSpace(_bucket.Text))
            {
                await RefreshBucketsAsync();
            }
        };
    }

    // ---------------------------------------------------------------- 布局

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

    private void BuildUi()
    {
        _provider = new FlatComboBox
        {
            Dock = DockStyle.None,
            AllowsFreeText = false,
            Placeholder = "选择云厂商",
        };
        _provider.SetItems(StorageProviders.All.Select(StorageProviders.DisplayName).ToArray());
        _provider.SelectedIndexChanged += (_, _) => OnProviderChanged();

        _accountId = Input();
        _region = Input();
        _accessKeyId = Input();
        _secretKey = Input(password: true);
        _endpoint = Input();
        _publicDomain = Input();

        _showSecret = Inline("显示");
        _showSecret.CheckedChanged += (_, _) => _secretKey.UsePassword = !_showSecret.Checked;

        _rememberSecret = Inline("在本机记住密钥（Windows DPAPI 加密）");

        _bucket = new FlatComboBox
        {
            Dock = DockStyle.None,
            Placeholder = "直接输入桶名称，或点右侧箭头选择",
        };

        _btnRefreshBuckets = UiEx.MakeButton("获取桶列表", ButtonStyle.Normal, 100);
        _btnRefreshBuckets.Dock = DockStyle.None;
        _btnRefreshBuckets.Height = 30;
        _btnRefreshBuckets.Click += async (_, _) => await RefreshBucketsAsync();

        _tips.SetToolTip(_btnRefreshBuckets,
            "列举桶需要账号级权限；对象级密钥会返回 403。" +
            "取不到时直接在左侧输入桶名称即可，不影响上传下载。");

        _concurrency = Input();
        _threshold = Input();
        _partSize = Input();

        _conflict = new FlatComboBox
        {
            Dock = DockStyle.None,
            AllowsFreeText = false,
            Placeholder = "同名对象处理",
        };
        _conflict.SetItems(new[]
        {
            "覆盖远端同名对象",
            "跳过远端同名对象",
            "自动改名为 “xxx (1).ext”",
        });

        _autoConnect = Inline("启动时自动连接");

        _addressing = new FlatComboBox
        {
            Dock = DockStyle.None,
            AllowsFreeText = false,
            Placeholder = "寻址方式",
        };
        _addressing.SetItems(new[] { "路径式（bucket 放在路径里）", "虚拟主机式（bucket 放在域名里）" });

        var left = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = UiEx.Surface,
            Margin = new Padding(0),
        };

        _leftColumn = left;

        left.Controls.Add(Section("云厂商"));
        left.Controls.Add(Field("服务商", _provider));
        left.Controls.Add(Spacer(6));
        left.Controls.Add(Section("账户信息"));
        _accountIdRow = Field("Cloudflare 账户 ID", _accountId);
        left.Controls.Add(_accountIdRow);
        _regionRow = Field("地域（Region）", _region);
        left.Controls.Add(_regionRow);
        _accessKeyRow = Field("Access Key ID", _accessKeyId);
        left.Controls.Add(_accessKeyRow);
        _secretKeyRow = Field("Secret Access Key", _secretKey, _showSecret);
        left.Controls.Add(_secretKeyRow);
        left.Controls.Add(_rememberSecret);
        _endpointRow = Field("Endpoint（留空自动生成）", _endpoint);
        left.Controls.Add(_endpointRow);
        _publicDomainRow = Field("公开域名（可选）", _publicDomain);
        left.Controls.Add(_publicDomainRow);
        _addressingRow = Field("寻址方式", _addressing);
        left.Controls.Add(_addressingRow);

        var right = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = UiEx.Surface,
            Margin = new Padding(0),
        };

        _rightColumn = right;

        right.Controls.Add(Section("存储桶"));
        right.Controls.Add(Field("桶名称", _bucket, _btnRefreshBuckets));
        right.Controls.Add(Spacer(12));
        right.Controls.Add(Section("传输"));
        right.Controls.Add(Field("最大并发数（1 - 16）", _concurrency));
        right.Controls.Add(Field("分片阈值 (MB)", _threshold));
        right.Controls.Add(Field("分片大小 (MB)", _partSize));
        right.Controls.Add(Field("同名对象处理", _conflict));
        right.Controls.Add(_autoConnect);

        var body = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = UiEx.Surface,
            Padding = new Padding(20, 14, 20, 4),
        };

        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ColumnWidth));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, ColumnWidth));
        body.Controls.Add(left, 0, 0);
        body.Controls.Add(right, 1, 0);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = FooterHeight, BackColor = UiEx.SurfaceAlt };

        footer.Paint += (_, e) =>
        {
            using var pen = new Pen(UiEx.Border);
            e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
        };

        _status = new Label
        {
            Text = string.Empty,
            Location = new Point(20, 10),
            Size = new Size(560, 34),
            ForeColor = UiEx.TextMuted,
            Font = new Font("Microsoft YaHei UI", 8.5F),
            AutoEllipsis = true,
        };

        _btnConsole = UiEx.MakeButton(StorageProviders.ConsoleButtonText(CurrentProvider), ButtonStyle.Normal, 190);
        _btnConsole.Click += (_, _) => OpenConsole();

        var btnAbout = UiEx.MakeButton("关于", ButtonStyle.Normal, 76);
        btnAbout.Click += (_, _) =>
        {
            using var dialog = new AboutForm();
            dialog.ShowDialog(this);
        };

        var btnTest = UiEx.MakeButton("测试连接", ButtonStyle.Normal, 100);
        btnTest.Click += async (_, _) => await TestAsync();

        var btnCancel = UiEx.MakeButton("取消", ButtonStyle.Normal, 84);
        btnCancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        var btnSave = UiEx.MakeButton("保存", ButtonStyle.Primary, 92);
        btnSave.Click += (_, _) => Save();

        footer.Controls.AddRange(new Control[] { _status, _btnConsole, btnAbout, btnTest, btnCancel, btnSave });

        void PositionFooter()
        {
            // 状态提示独占整行，按钮排在其下方——提示语经常比较长，挤在按钮左边会被截断。
            int statusWidth = Math.Max(120, footer.ClientSize.Width - 40);
            _status.SetBounds(20, 8, statusWidth, 46);
            _status.MaximumSize = new Size(statusWidth, 46);

            int x = footer.ClientSize.Width - 20;
            int y = footer.Height - 32 - 10;

            foreach (FlatButton button in new[] { btnSave, btnCancel, btnTest, btnAbout, _btnConsole })
            {
                x -= button.Width;
                button.Location = new Point(x, y);
                x -= 8;
            }
        }

        footer.Resize += (_, _) => PositionFooter();
        PositionFooter();

        // 状态文字可能比可视区域长，悬停给出完整内容。
        var statusTip = new ToolTip { AutoPopDelay = 200, InitialDelay = 150 };
        _status.MouseEnter += (_, _) => statusTip.SetToolTip(_status, _status.Text);

        Controls.Add(body);
        Controls.Add(footer);

        // 左栏在自建存储下会有 8 行（服务商 + 地域 + 密钥两行 + 记住 + Endpoint + 寻址方式），
        // 高度必须留够，否则最后一行会被 FlowLayoutPanel 裁掉。
        // 实际高度由 FitHeightToContent 按内容校正，这里只给个基准值。
        ClientSize = new Size(20 + ColumnWidth * 2 + 36 + 20, BaseClientHeight);
    }

    private static FlatTextBox Input(bool password = false)
    {
        var box = new FlatTextBox(password) { Dock = DockStyle.Fill };
        box.Height = 30;
        return box;
    }

    /// <summary>
    /// 量一栏内容实际要占多高。FlowLayoutPanel 装不下时是「直接裁掉」，
    /// 既不报错也不出滚动条——最后一行会缺一截边框，只能靠量高度提前发现。
    ///
    /// 这里只取 GetPreferredSize：它是按当前可见行算出来的，不受布局时机影响。
    /// 早前还把 child.Bottom 一起算进去，结果拿到的是「上一轮布局」的旧坐标
    /// （那时行还没隐藏），自建存储下会凭空多出一行的高度，把窗口撑高 33px。
    /// </summary>
    private static int MeasureColumn(FlowLayoutPanel panel)
    {
        if (panel is null)
        {
            return 0;
        }

        return panel.GetPreferredSize(new Size(ColumnWidth, 0)).Height;
    }

    /// <summary>
    /// 按内容把窗口撑到刚好装得下。以前是手写固定高度，
    /// 字段一多就把最后一行裁掉（R2 的「公开域名」就是这么被切掉的）。
    /// </summary>
    private void FitHeightToContent()
    {
        int needed = Math.Max(MeasureColumn(_leftColumn), MeasureColumn(_rightColumn));
        int baseAvailable = BaseClientHeight - FooterHeight - BodyPaddingVertical;
        int target = BaseClientHeight + Math.Max(0, needed - baseAvailable);

        if (ClientSize.Height != target)
        {
            ClientSize = new Size(ClientSize.Width, target);
        }
    }

    /// <summary>行内或行下的勾选框，统一边距与高度，保证两栏节奏一致。</summary>
    private static FlatCheckBox Inline(string text) => new(text)
    {
        Dock = DockStyle.None,
        Height = 22,
        Margin = new Padding(0, 3, 0, 0),
    };

    private static Control Section(string text) => new SectionTitle(text)
    {
        Width = ColumnWidth,
        Height = 22,
        Margin = new Padding(0, 0, 0, 4),
    };

    private static Control Spacer(int height) => new Panel
    {
        Width = ColumnWidth,
        Height = height,
        Margin = new Padding(0),
        BackColor = Color.Transparent,
    };

    /// <summary>一个「标题 + 控件」的行，extra 为行内右侧附加控件（如「显示」「获取桶列表」）。</summary>
    private Control Field(string label, Control input, Control? extra = null)
    {
        const int gap = 10;

        var row = new Panel
        {
            Width = ColumnWidth,
            Height = RowHeight,
            Margin = new Padding(0, 0, 0, 6),
            BackColor = Color.Transparent,
        };

        var caption = new Label
        {
            Text = label,
            Dock = DockStyle.Top,
            Height = CaptionHeight,
            ForeColor = UiEx.TextMuted,
            Font = new Font("Microsoft YaHei UI", 8.5F),
            TextAlign = ContentAlignment.MiddleLeft,
            BackColor = Color.Transparent,
        };

        var host = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };

        input.Dock = DockStyle.None;
        host.Controls.Add(input);

        if (extra is not null)
        {
            extra.Dock = DockStyle.None;
            host.Controls.Add(extra);
        }

        row.Controls.Add(host);
        row.Controls.Add(caption);

        _captions[row] = caption;

        void LayoutHost()
        {
            int width = host.ClientSize.Width;
            int height = host.ClientSize.Height;

            if (width <= 0 || height <= 0)
            {
                return;
            }

            if (extra is null)
            {
                input.SetBounds(0, 0, width, height);
                return;
            }

            int extraWidth = Math.Min(Math.Max(extra.Width, 44), width / 2);
            extra.SetBounds(width - extraWidth, Math.Max(0, (height - extra.Height) / 2), extraWidth, extra.Height);
            input.SetBounds(0, 0, Math.Max(120, width - extraWidth - gap), height);
        }

        host.Resize += (_, _) => LayoutHost();
        _layoutJobs.Add(LayoutHost);

        return row;
    }

    // ---------------------------------------------------------------- 数据

    /// <summary>当前下拉框选中的云厂商。</summary>
    private StorageProvider CurrentProvider
    {
        get
        {
            int index = _provider.SelectedIndex;
            if (index < 0 || index >= StorageProviders.All.Length)
            {
                return StorageProvider.CloudflareR2;
            }

            return StorageProviders.All[index];
        }
    }

    /// <summary>
    /// 切换云厂商后跟着变的东西：字段名、哪些字段要填、端点怎么算。
    /// R2 要账户 ID，阿里云 / 腾讯云要地域——两者的区别都在这里体现。
    /// </summary>
    private void ApplyProvider()
    {
        StorageProvider provider = CurrentProvider;

        bool needsAccountId = StorageProviders.RequiresAccountId(provider);
        bool needsEndpoint = StorageProviders.RequiresCustomEndpoint(provider);

        _accountIdRow.Visible = needsAccountId;
        _regionRow.Visible = !needsAccountId;
        _addressingRow.Visible = StorageProviders.ShowsAddressingMode(provider);

        // 「公开域名」是 R2 独有的概念（r2.dev 那种免配置公开地址）。
        // 阿里云 / 腾讯云 / 自建都直接由桶名和端点推导公开链接，摆出来只会让人困惑。
        _publicDomainRow.Visible = StorageProviders.ShowsPublicDomain(provider);

        SetCaption(_accountIdRow, StorageProviders.AccountIdLabel(provider));
        SetCaption(_regionRow, StorageProviders.RegionLabel(provider));
        SetCaption(_accessKeyRow, StorageProviders.AccessKeyLabel(provider));
        SetCaption(_secretKeyRow, StorageProviders.SecretKeyLabel(provider));
        SetCaption(_endpointRow, needsEndpoint
            ? "Endpoint（必填，例如 https://minio.example.com）"
            : "Endpoint（留空按地域自动生成）");

        _region.Placeholder = StorageProviders.RegionHint(provider);
        _bucket.Placeholder = StorageProviders.BucketHint(provider);
        _btnConsole.Text = StorageProviders.ConsoleButtonText(provider);

        _tips.SetToolTip(_btnRefreshBuckets, StorageProviders.CredentialHint(provider));
    }

    /// <summary>用户主动换了云厂商：文案跟着换，地域也换成新厂商的默认值。</summary>
    private void OnProviderChanged()
    {
        if (_suppressProviderEvents)
        {
            return;
        }

        ApplyProvider();
        _region.Text = StorageProviders.DefaultRegion(CurrentProvider);

        // 换厂商会换掉可见的行（账户 ID / 地域 / 公开域名 / 寻址方式），
        // 高度得跟着重新量一次，免得换到行数多的厂商时最后一行又被裁掉。
        FitHeightToContent();
    }

    private void SetCaption(Control row, string text)
    {
        if (_captions.TryGetValue(row, out Label? caption))
        {
            caption.Text = text;
        }
    }

    private void LoadValues()
    {
        int providerIndex = Array.IndexOf(StorageProviders.All, _settings.Provider);

        // 程序化设置选中项也会触发 SelectedIndexChanged，先挡一下，
        // 否则会把已保存的地域覆盖成新厂商的默认值。
        _suppressProviderEvents = true;
        _provider.SelectedIndex = providerIndex >= 0 ? providerIndex : 0;
        _suppressProviderEvents = false;

        _accountId.Text = _settings.AccountId;
        _region.Text = StorageProviders.NormalizeRegion(CurrentProvider, _settings.Region);
        _addressing.SelectedIndex = _settings.Addressing == S3Addressing.VirtualHosted ? 1 : 0;
        _accessKeyId.Text = _settings.AccessKeyId;
        _secretKey.Text = _settings.SecretAccessKey;
        _rememberSecret.Checked = _settings.RememberSecret;
        _endpoint.Text = _settings.CustomEndpoint;
        _publicDomain.Text = _settings.PublicDomain;

        ApplyProvider();

        if (!string.IsNullOrWhiteSpace(_settings.BucketName))
        {
            _bucket.Text = _settings.BucketName;
        }

        _conflict.SelectedIndex = _settings.OverwriteExisting ? 0 : _settings.SkipExisting ? 1 : 2;
        _autoConnect.Checked = _settings.AutoConnect;

        _concurrency.Text = _settings.MaxConcurrency.ToString();
        _threshold.Text = _settings.MultipartThresholdMb.ToString();
        _partSize.Text = _settings.PartSizeMb.ToString();
    }

    private StorageSettings Collect()
    {
        var result = _settings.Clone();

        result.Provider = CurrentProvider;
        result.AccountId = _accountId.Text.Trim();
        result.Region = StorageProviders.NormalizeRegion(CurrentProvider, _region.Text);
        result.AccessKeyId = _accessKeyId.Text.Trim();
        result.SecretAccessKey = _secretKey.Text.Trim();
        result.RememberSecret = _rememberSecret.Checked;
        result.BucketName = _bucket.Text.Trim();
        result.CustomEndpoint = _endpoint.Text.Trim();
        result.Addressing = _addressing.SelectedIndex == 1 ? S3Addressing.VirtualHosted : S3Addressing.PathStyle;
        result.PublicDomain = _publicDomain.Text.Trim();
        result.MaxConcurrency = ParseInt(_concurrency.Text, 3, 1, 16);
        result.MultipartThresholdMb = ParseInt(_threshold.Text, 16, 5, 4096);
        result.PartSizeMb = ParseInt(_partSize.Text, 16, StorageSettings.MinPartSizeMb, StorageSettings.MaxPartSizeMb);
        result.OverwriteExisting = _conflict.SelectedIndex == 0;
        result.SkipExisting = _conflict.SelectedIndex == 1;
        result.AutoConnect = _autoConnect.Checked;

        return result;
    }

    private static int ParseInt(string text, int fallback, int min, int max)
    {
        return int.TryParse(text, out int value) ? Math.Clamp(value, min, max) : fallback;
    }

    private void Save()
    {
        StorageSettings result = Collect();
        string? error = result.Validate();

        if (error is not null)
        {
            MessageBox.Show(this, error, "设置有误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        foreach (PropertyInfo property in typeof(StorageSettings).GetProperties())
        {
            if (property.CanRead && property.CanWrite)
            {
                property.SetValue(_settings, property.GetValue(result));
            }
        }

        SettingsStore.Save(_settings);

        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>R2 要账户 ID，阿里云 / 腾讯云要地域——必填项按厂商不同。</summary>
    private static bool HasRequiredCredentials(StorageSettings s)
    {
        if (StorageProviders.RequiresAccountId(s.Provider))
        {
            if (string.IsNullOrWhiteSpace(s.AccountId))
            {
                return false;
            }
        }
        else if (string.IsNullOrWhiteSpace(s.NormalizedRegion))
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(s.AccessKeyId) &&
               !string.IsNullOrWhiteSpace(s.SecretAccessKey);
    }

    private async Task TestAsync()
    {
        StorageSettings result = Collect();

        if (!HasRequiredCredentials(result))
        {
            MessageBox.Show(this,
                $"请先填写{StorageProviders.AccessKeyLabel(result.Provider)}与{StorageProviders.SecretKeyLabel(result.Provider)}" +
                (StorageProviders.RequiresAccountId(result.Provider) ? "、以及账户 ID。" : "、以及地域。"),
                "测试连接", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (string.IsNullOrWhiteSpace(result.BucketName))
        {
            MessageBox.Show(this, "请先选择或填写桶名称。", "测试连接", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        SetStatus("正在测试连接…", UiEx.TextMuted);

        try
        {
            using StorageService service = StorageService.Create(result);
            await service.TestAsync(CancellationToken.None);

            SetStatus($"连接成功：{result.ResolveEndpoint()}", UiEx.Green);
        }
        catch (Exception ex)
        {
            SetStatus("连接失败：" + ex.Message, UiEx.Red);
            ErrorInfo.Report("测试连接失败", ex);
        }
    }

    /// <summary>拉取桶列表。再次点击按钮即取消；失败时仍然可以手动输入桶名。</summary>
    private async Task RefreshBucketsAsync()
    {
        if (_bucketCts is not null)
        {
            _bucketCts.Cancel();
            return;
        }

        StorageSettings result = Collect();

        if (!HasRequiredCredentials(result))
        {
            MessageBox.Show(this,
                $"请先填写{StorageProviders.AccessKeyLabel(result.Provider)}与{StorageProviders.SecretKeyLabel(result.Provider)}" +
                (StorageProviders.RequiresAccountId(result.Provider) ? "、以及账户 ID。" : "、以及地域。"),
                "获取桶列表", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        result.BucketName = "list-probe";

        _bucketCts = new CancellationTokenSource(BucketListTimeout);
        _btnRefreshBuckets.Text = "取消";
        _btnRefreshBuckets.Enabled = false;
        _bucket.Enabled = false;
        _bucket.Placeholder = $"正在获取桶列表（最长 {BucketListTimeout.TotalSeconds:0} 秒）…";
        SetStatus($"正在获取桶列表…  {_settings.ResolveEndpoint()}", UiEx.TextMuted);

        try
        {
            using StorageService service = StorageService.Create(result, BucketListTimeout);
            IReadOnlyList<string> buckets = await service
                .ListBucketNamesAsync(_bucketCts.Token)
                .ConfigureAwait(true);

            string current = _bucket.Text.Trim();
            _bucket.SetItems(buckets);

            if (buckets.Count == 0)
            {
                SetStatus("该账户下没有找到存储桶，请手动输入桶名称。", UiEx.Amber);
            }
            else if (buckets.Contains(current))
            {
                _bucket.Text = current;
                SetStatus($"共 {buckets.Count} 个桶，已保持当前选择。", UiEx.Green);
            }
            else
            {
                _bucket.Text = buckets[0];
                SetStatus($"共 {buckets.Count} 个桶，已选中第一个；点右侧箭头可切换。", UiEx.Green);
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus(_bucketCts?.IsCancellationRequested == true
                    ? "已取消获取桶列表，可手动输入桶名称。"
                    : $"获取桶列表超过 {BucketListTimeout.TotalSeconds:0} 秒仍未响应，请检查网络或代理设置。",
                UiEx.Amber);
        }
        catch (Exception ex)
        {
            SetStatus(DescribeBucketFailure(ex), UiEx.Red);
            ErrorInfo.AppendLog("获取桶列表失败", ex);
        }
        finally
        {
            _btnRefreshBuckets.Text = "获取桶列表";
            _btnRefreshBuckets.Enabled = true;
            _bucket.Enabled = true;
            _bucket.Placeholder = StorageProviders.BucketHint(CurrentProvider);

            _bucketCts?.Dispose();
            _bucketCts = null;
        }
    }

    /// <summary>
    /// 列举桶需要账号级权限；对象级 / 限定桶的密钥会 403。
    /// 注意「密钥填错」和「密钥有效但没权限」是两回事，给的整改动作完全不同。
    /// </summary>
    private string DescribeBucketFailure(Exception ex)
    {
        string detail = ErrorInfo.Describe(ex).Replace("\r\n", " ");

        if (ErrorInfo.IsBadCredentials(ex))
        {
            return $"{StorageProviders.AccessKeyLabel(CurrentProvider)} / {StorageProviders.SecretKeyLabel(CurrentProvider)} 不正确或已失效，请核对后重试。";
        }

        bool denied =
            ex is AmazonS3Exception { StatusCode: HttpStatusCode.Forbidden } ||
            detail.Contains("AccessDenied", StringComparison.OrdinalIgnoreCase);

        if (denied)
        {
            return "当前密钥没有列举存储桶的权限（列举桶需要账号级权限）。" +
                   "请直接在输入框手动填写桶名称，不影响上传下载。";
        }

        return $"获取桶列表失败：{detail}　可直接手动填写桶名称。";
    }

    private void SetStatus(string message, Color color)
    {
        _status.Text = message;
        _status.ForeColor = color;
    }

    private void OpenConsole()
    {
        try
        {
            string url = StorageProviders.ConsoleUrl(CurrentProvider);

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ErrorInfo.Report("无法打开浏览器", ex);
        }
    }
}

/// <summary>简单的重命名输入框。</summary>
public sealed class RenameDialog : Form
{
    public RenameDialog(string currentName)
    {
        Text = "重命名对象";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(460, 156);
        BackColor = UiEx.Surface;
        Font = new Font("Microsoft YaHei UI", 9F);

        if (UiEx.AppIcon() is { } renameIcon)
        {
            Icon = renameIcon;
        }

        var title = new Label
        {
            Text = "新的名称（不含路径）：",
            Location = new Point(20, 20),
            Size = new Size(420, 20),
            ForeColor = UiEx.TextMain,
            BackColor = Color.Transparent,
        };

        var input = new FlatTextBox
        {
            Location = new Point(20, 46),
            Size = new Size(420, 30),
            Text = currentName,
        };

        var hint = new Label
        {
            Text = "改名会在桶内完成：先复制到新名称，再删除原对象（内容与元数据保持不变）。",
            Location = new Point(20, 84),
            Size = new Size(420, 34),
            ForeColor = UiEx.TextMuted,
            Font = new Font("Microsoft YaHei UI", 8.5F),
            BackColor = Color.Transparent,
        };

        var ok = UiEx.MakeButton("确定", ButtonStyle.Primary, 88);
        ok.Location = new Point(266, 118);
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(input.Text))
            {
                MessageBox.Show(this, "名称不能为空。", "重命名", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            NewName = input.Text.Trim();
            DialogResult = DialogResult.OK;
            Close();
        };

        var cancel = UiEx.MakeButton("取消", ButtonStyle.Normal, 82);
        cancel.Location = new Point(358, 118);
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        Controls.AddRange(new Control[] { title, input, hint, ok, cancel });

        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                ok.PerformClick();
                e.Handled = e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                cancel.PerformClick();
                e.Handled = e.SuppressKeyPress = true;
            }
        };

        Shown += (_, _) => { input.Focus(); input.SelectAll(); };
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

    public string NewName { get; private set; } = string.Empty;
}
