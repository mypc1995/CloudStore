using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using System.Runtime.InteropServices;

namespace CloudStore.UI;

/// <summary>
/// 自绘的扁平圆角按钮。默认 WinForms 按钮的灰色立体边框很廉价，
/// 这里统一用 GDI+ 画圆角 + 纯色填充，视觉上更接近现代桌面应用。
/// </summary>
internal sealed class FlatButton : Control
{
    private bool _hover;
    private bool _pressed;
    private Image? _icon;
    private int _measureWidth;

    public FlatButton()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);

        Font = new Font("Microsoft YaHei UI", 9F);
        Size = new Size(88, 32);
        Cursor = Cursors.Hand;
        TabStop = false;
        Kind = ButtonStyle.Normal;
        _measureWidth = 0;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public ButtonStyle Kind { get; set; } = ButtonStyle.Normal;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Image? Icon
    {
        get => _icon;
        set
        {
            _icon = value;
            _measureWidth = MeasureContent();
            Invalidate();
        }
    }

    /// <summary>左右内边距，默认 12。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int HorizontalPadding { get; set; } = 12;

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        _measureWidth = MeasureContent();
    }

    private int MeasureContent()
    {
        int text = TextRenderer.MeasureText(Text, Font, Size.Empty,
            TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width;

        return text + HorizontalPadding * 2 + (_icon is null ? 0 : 16 + 6);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        Color fill;
        Color border;
        Color text;

        if (Kind == ButtonStyle.Primary)
        {
            fill = !Enabled ? Color.FromArgb(0xE7, 0xD3, 0xBE) : _pressed ? AccentDark : _hover ? UiEx.AccentHover : UiEx.Accent;
            border = fill;
            text = Color.White;
        }
        else if (Kind == ButtonStyle.Danger)
        {
            fill = !Enabled ? UiEx.Surface : _pressed ? Color.FromArgb(0xF9, 0xD3, 0xD1) : _hover ? Color.FromArgb(0xFD, 0xEC, 0xEB) : UiEx.Surface;
            border = !Enabled ? UiEx.Border : Color.FromArgb(0xF0, 0xC0, 0xBC);
            text = !Enabled ? UiEx.TextMuted : UiEx.Red;
        }
        else if (Kind == ButtonStyle.Ghost)
        {
            fill = !Enabled ? Color.Transparent : _hover ? UiEx.SurfaceAlt : Color.Transparent;
            border = Color.Transparent;
            text = !Enabled ? UiEx.StatusIdle : _hover ? UiEx.TextMain : UiEx.TextMuted;
        }
        else
        {
            fill = !Enabled ? UiEx.Surface : _pressed ? Color.FromArgb(0xFA, 0xE6, 0xD5) : _hover ? UiEx.AccentSoft : UiEx.Surface;
            border = !Enabled ? UiEx.Border : _hover ? Color.FromArgb(0xF0, 0xC9, 0x9F) : UiEx.Border;
            text = !Enabled ? UiEx.TextMuted : UiEx.TextMain;
        }

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);

        if (fill != Color.Transparent)
        {
            using GraphicsPath path = UiEx.RoundedPath(rect, 6);
            using var brush = new SolidBrush(fill);
            g.FillPath(brush, path);
        }

        if (border != Color.Transparent)
        {
            using GraphicsPath path = UiEx.RoundedPath(rect, 6);
            using var pen = new Pen(border);
            g.DrawPath(pen, path);
        }

        Rectangle textBounds = rect;

        if (_icon is not null && Enabled)
        {
            int iconSize = Math.Min(16, Height - 10);
            var iconRect = new Rectangle(
                rect.X + Math.Max(8, (Width - _measureWidth) / 2),
                rect.Y + (Height - iconSize) / 2,
                iconSize, iconSize);

            g.DrawImage(_icon, iconRect);
            textBounds = new Rectangle(
                iconRect.Right + 6, rect.Y, Math.Max(0, rect.Right - iconRect.Right - 6), rect.Height);
        }

        TextRenderer.DrawText(g, Text, Font, textBounds, text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
            TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
    }

    private static Color AccentDark => Color.FromArgb(0xC9, 0x5E, 0x0C);

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        _pressed = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        _pressed = true;
        Invalidate();
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        _pressed = false;
        Invalidate();
        base.OnMouseUp(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        Invalidate();
        base.OnEnabledChanged(e);
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Space or Keys.Enter || base.IsInputKey(keyData);

    /// <summary>供键盘（回车）触发表定点击动作。</summary>
    public void PerformClick() => OnClick(EventArgs.Empty);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter)
        {
            OnClick(e);
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }
}

internal static class NativeMethods
{
    private const int EM_SETCUEBANNER = 0x1501;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);

    /// <summary>给 TextBox 加灰色占位提示。</summary>
    public static void SetCueBanner(TextBox box, string text)
    {
        if (box.IsHandleCreated)
        {
            SendMessage(box.Handle, EM_SETCUEBANNER, IntPtr.Zero, text);
        }
        else
        {
            box.HandleCreated += (_, _) => SendMessage(box.Handle, EM_SETCUEBANNER, IntPtr.Zero, text);
        }
    }
}
