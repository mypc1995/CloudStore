using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CloudStore.UI;

internal enum ButtonStyle
{
    Normal,
    Primary,
    Danger,
    Ghost,
}

internal static class UiEx
{
    public static readonly Color Accent = Color.FromArgb(0xF7, 0x81, 0x1F);
    public static readonly Color AccentHover = Color.FromArgb(0xDF, 0x6C, 0x10);
    public static readonly Color AccentSoft = Color.FromArgb(0xFD, 0xF1, 0xE6);

    public static readonly Color Blue = Color.FromArgb(0x25, 0x63, 0xEB);
    public static readonly Color Green = Color.FromArgb(0x18, 0x9B, 0x62);
    public static readonly Color Red = Color.FromArgb(0xD9, 0x34, 0x2B);
    public static readonly Color Amber = Color.FromArgb(0xC9, 0x83, 0x0B);

    public static readonly Color Surface = Color.White;
    public static readonly Color SurfaceAlt = Color.FromArgb(0xF6, 0xF7, 0xF9);
    public static readonly Color SurfaceSunken = Color.FromArgb(0xFA, 0xFB, 0xFC);
    public static readonly Color Border = Color.FromArgb(0xE1, 0xE5, 0xEA);
    public static readonly Color TextMain = Color.FromArgb(0x1F, 0x24, 0x2B);
    public static readonly Color TextMuted = Color.FromArgb(0x74, 0x7D, 0x87);
    public static readonly Color StatusIdle = Color.FromArgb(0xA3, 0xAB, 0xB4);

    public static Font MonoFont { get; } = new("Consolas", 10F);
    public static Font MonoSmall { get; } = new("Consolas", 9F);

    /// <summary>圆角矩形路径，供按钮 / 状态胶囊 / 进度条共用。</summary>
    public static GraphicsPath RoundedPath(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        int diameter = Math.Max(1, radius * 2);

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

    private const int WM_SETICON = 0x0080;
    private const int ICON_SMALL = 0;
    private const int ICON_BIG = 1;
    private const int SM_CXICON = 11;
    private const int SM_CXSMICON = 49;
    private const string IconResourceName = "CloudStore.app.ico";

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    private static Icon? _appIcon;
    private static byte[]? _icoBytes;

    /// <summary>读取嵌入的 app.ico 原始字节。</summary>
    private static byte[]? IcoBytes
    {
        get
        {
            if (_icoBytes is not null)
            {
                return _icoBytes;
            }

            using Stream? stream = typeof(UiEx).Assembly.GetManifestResourceStream(IconResourceName);

            if (stream is not null)
            {
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                _icoBytes = ms.ToArray();
            }

            return _icoBytes;
        }
    }

    /// <summary>应用图标（任务栏 / Alt-Tab / 标题栏大图标槽位）。</summary>
    public static Icon? AppIcon()
    {
        if (_appIcon is not null)
        {
            return _appIcon;
        }

        byte[]? bytes = IcoBytes;

        if (bytes is null)
        {
            return null;
        }

        using var ms = new MemoryStream(bytes, writable: false);
        _appIcon = new Icon(ms);
        return _appIcon;
    }

    /// <summary>
    /// 把大小两个图标槽位都按系统度量塞给窗口。
    ///
    /// 必须这么做：WinForms 的 Form.Icon 只会设 ICON_BIG（32），标题栏要的是
    /// ICON_SMALL（本机 SM_CXSMICON = 16），于是 Windows 把 32 缩到 16，
    /// 图标发糊。这里按系统度量各取一帧原生尺寸的图标，1:1 零缩放。
    /// </summary>
    public static void ApplyWindowIcons(Form form)
    {
        if (form.IsDisposed)
        {
            return;
        }

        byte[]? bytes = IcoBytes;

        if (bytes is null)
        {
            return;
        }

        int smallSize = Math.Max(1, GetSystemMetrics(SM_CXSMICON));
        int largeSize = Math.Max(1, GetSystemMetrics(SM_CXICON));

        IntPtr? smallHandle = CreateIconHandle(bytes, smallSize);
        IntPtr? largeHandle = smallSize == largeSize ? smallHandle : CreateIconHandle(bytes, largeSize);

        try
        {
            if (smallHandle is { } sh)
            {
                _ = SendMessage(form.Handle, WM_SETICON, new IntPtr(ICON_SMALL), sh);
            }

            if (largeHandle is { } lh)
            {
                _ = SendMessage(form.Handle, WM_SETICON, new IntPtr(ICON_BIG), lh);
            }
        }
        finally
        {
            // SendMessage 只是把句柄交出去，系统自己会复制一份，这里必须自己销毁，
            // 否则每次重建窗口都漏一个 GDI 句柄。
            if (largeHandle is { } l && l != (smallHandle ?? IntPtr.Zero))
            {
                _ = DestroyIcon(l);
            }

            if (smallHandle is { } s)
            {
                _ = DestroyIcon(s);
            }
        }
    }

    /// <summary>取 app.ico 中指定尺寸的一帧，返回可直接绘制或转 HICON 的位图。</summary>
    public static Bitmap? AppLogoBitmap(int preferredSize)
    {
        byte[]? bytes = IcoBytes;

        if (bytes is null || bytes.Length < 6)
        {
            return null;
        }

        int count = BitConverter.ToUInt16(bytes, 4);
        int best = -1;
        int bestSize = int.MaxValue;

        for (int i = 0; i < count; i++)
        {
            int o = 6 + i * 16;
            int w = bytes[o] == 0 ? 256 : bytes[o];

            if (w < bestSize)
            {
                bestSize = w;
                best = i;
            }
        }

        // 取「不小于目标尺寸的最小一帧」，避免放大；没有就退回最小帧
        best = -1;
        bestSize = int.MaxValue;
        for (int i = 0; i < count; i++)
        {
            int o = 6 + i * 16;
            int w = bytes[o] == 0 ? 256 : bytes[o];

            if (w >= preferredSize && w < bestSize)
            {
                bestSize = w;
                best = i;
            }
        }

        if (best < 0)
        {
            best = 0;
            for (int i = 0; i < count; i++)
            {
                int o = 6 + i * 16;
                int w = bytes[o] == 0 ? 256 : bytes[o];

                if (w < bestSize)
                {
                    bestSize = w;
                    best = i;
                }
            }
        }

        return best < 0 ? null : ReadFrame(bytes, best);
    }

    private static IntPtr? CreateIconHandle(byte[] ico, int size)
    {
        Bitmap? bitmap = FindFrame(ico, size);

        if (bitmap is null)
        {
            return null;
        }

        IntPtr handle = bitmap.GetHicon();
        bitmap.Dispose();
        return handle;
    }

    private static Bitmap? FindFrame(byte[] ico, int size)
    {
        if (ico.Length < 6)
        {
            return null;
        }

        int count = BitConverter.ToUInt16(ico, 4);

        for (int i = 0; i < count; i++)
        {
            int o = 6 + i * 16;
            int w = ico[o] == 0 ? 256 : ico[o];
            int h = ico[o + 1] == 0 ? 256 : ico[o + 1];

            if (w == size && h == size)
            {
                return ReadFrame(ico, i);
            }
        }

        return null;
    }

    private static Bitmap? ReadFrame(byte[] ico, int index)
    {
        if (ico.Length < 6)
        {
            return null;
        }

        int count = BitConverter.ToUInt16(ico, 4);

        if (index < 0 || index >= count)
        {
            return null;
        }

        int o = 6 + index * 16;
        int width = ico[o] == 0 ? 256 : ico[o];
        int height = ico[o + 1] == 0 ? 256 : ico[o + 1];
        int length = BitConverter.ToInt32(ico, o + 8);
        int offset = BitConverter.ToInt32(ico, o + 12);

        if (length <= 0 || offset < 0 || offset + length > ico.Length)
        {
            return null;
        }

        // PNG 帧（大尺寸常用）
        if (offset + 8 <= ico.Length && ico[offset] == 0x89 && ico[offset + 1] == 0x50)
        {
            try
            {
                using var ms = new MemoryStream(ico, offset, length);
                using var png = new Bitmap(ms);
                return new Bitmap(png);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        return DecodeDib(ico, offset, width, height);
    }

    /// <summary>解析 .ico 里的 32 位 DIB 帧（BITMAPINFOHEADER + 自底向上 BGRA + AND 掩码）。</summary>
    private static Bitmap? DecodeDib(byte[] data, int offset, int width, int height)
    {
        if (offset + 40 > data.Length)
        {
            return null;
        }

        int headerSize = BitConverter.ToInt32(data, offset);
        int bitCount = BitConverter.ToUInt16(data, offset + 14);

        // 只处理 32 位；24 位及以下需要调色板解析，app.ico 用不到
        if (headerSize < 40 || bitCount != 32)
        {
            return null;
        }

        int stride = width * 4;
        int baseOffset = offset + headerSize;

        if (baseOffset + stride * height > data.Length)
        {
            return null;
        }

        var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var row = new byte[stride];

        for (int y = 0; y < height; y++)
        {
            int src = baseOffset + (height - 1 - y) * stride;
            Array.Copy(data, src, row, 0, stride);

            for (int x = 0; x < width; x++)
            {
                byte b = row[x * 4 + 0];
                byte g = row[x * 4 + 1];
                byte r = row[x * 4 + 2];
                byte a = row[x * 4 + 3];
                bitmap.SetPixel(x, y, Color.FromArgb(a, r, g, b));
            }
        }

        return bitmap;
    }

    public static void EnableDoubleBuffer(Control control)
    {
        typeof(Control)
            .GetProperty("DoubleBuffered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.SetValue(control, true);
    }

    public static FlatButton MakeButton(string text, ButtonStyle style = ButtonStyle.Normal, int width = 88)
    {
        return new FlatButton
        {
            Text = text,
            Kind = style,
            Size = new Size(width, 32),
            Margin = new Padding(0, 0, 8, 0),
            Font = new Font("Microsoft YaHei UI", 9F),
        };
    }

    public static Label MakeLabel(string text, float size = 9F, bool bold = false, Color? color = null)
    {
        return new Label
        {
            Text = text,
            AutoSize = false,
            ForeColor = color ?? TextMain,
            Font = new Font("Microsoft YaHei UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleLeft,
        };
    }

    public static Panel MakeCard(int padding = 14)
    {
        var panel = new Panel
        {
            BackColor = Surface,
            Padding = new Padding(padding),
            Margin = new Padding(0),
        };

        panel.Paint += (_, e) =>
        {
            using var pen = new Pen(Border);
            e.Graphics.DrawRectangle(pen, 0, 0, panel.Width - 1, panel.Height - 1);
        };

        return panel;
    }

    public static void StyleGrid(DataGridView grid)
    {
        grid.BackgroundColor = Surface;
        grid.BorderStyle = BorderStyle.None;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.GridColor = Border;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        grid.EnableHeadersVisualStyles = false;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.AllowUserToResizeRows = false;
        grid.AllowUserToOrderColumns = false;
        grid.ReadOnly = true;
        grid.MultiSelect = true;
        grid.RowHeadersVisible = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        grid.ColumnHeadersHeight = 36;
        grid.RowTemplate.Height = 30;
        grid.ScrollBars = ScrollBars.Both;
        grid.StandardTab = false;
        grid.Cursor = Cursors.Default;

        grid.ColumnHeadersDefaultCellStyle.BackColor = SurfaceAlt;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = TextMuted;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = SurfaceAlt;
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = TextMuted;
        grid.ColumnHeadersDefaultCellStyle.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular);
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(4, 0, 4, 0);
        grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleLeft;

        grid.DefaultCellStyle.BackColor = Surface;
        grid.DefaultCellStyle.ForeColor = TextMain;
        grid.DefaultCellStyle.SelectionBackColor = AccentSoft;
        grid.DefaultCellStyle.SelectionForeColor = TextMain;
        grid.DefaultCellStyle.Font = new Font("Microsoft YaHei UI", 9F);
        grid.DefaultCellStyle.Padding = new Padding(4, 0, 4, 0);
        grid.DefaultCellStyle.WrapMode = DataGridViewTriState.False;
    }

    public static string Ellipsize(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
        {
            return text;
        }

        if (maxLength <= 3)
        {
            return text[..maxLength];
        }

        return text[..(maxLength - 3)] + "...";
    }
}

/// <summary>左侧色点 + 文本的连接状态指示器（胶囊形）。</summary>
internal sealed class StatusPill : Control
{
    private Color _dotColor = UiEx.StatusIdle;

    public StatusPill()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = UiEx.Surface;
        ForeColor = UiEx.TextMuted;
        Font = new Font("Microsoft YaHei UI", 9F);
        Size = new Size(120, 26);
    }

    public void SetStatus(string text, Color? dot = null, Color? textColor = null)
    {
        Text = text;
        _dotColor = dot ?? UiEx.StatusIdle;
        ForeColor = textColor ?? UiEx.TextMuted;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);

        using (GraphicsPath path = UiEx.RoundedPath(rect, Height / 2))
        using (var background = new SolidBrush(Color.FromArgb(0xF2, 0xF4, 0xF7)))
        using (var border = new Pen(Color.FromArgb(0xE4, 0xE8, 0xED)))
        {
            g.FillPath(background, path);
            g.DrawPath(border, path);
        }

        float cy = rect.Y + rect.Height / 2f;
        using (var brush = new SolidBrush(_dotColor))
        {
            g.FillEllipse(brush, 10, cy - 3.5f, 7, 7);
        }

        var textBounds = new Rectangle(23, rect.Y, Math.Max(0, rect.Right - 33), rect.Height);

        TextRenderer.DrawText(g, Text, Font, textBounds, ForeColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}

/// <summary>带双缓冲的 ListView。</summary>
internal sealed class BufferedListView : ListView
{
    public BufferedListView()
    {
        DoubleBuffered = true;
    }
}

/// <summary>分段标签样式的小节标题。</summary>
internal sealed class SectionTitle : Label
{
    public SectionTitle(string text)
    {
        AutoSize = false;
        Height = 22;
        ForeColor = UiEx.TextMuted;
        Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Bold);
        TextAlign = ContentAlignment.MiddleLeft;
        BackColor = Color.Transparent;
        Text = text;
    }
}