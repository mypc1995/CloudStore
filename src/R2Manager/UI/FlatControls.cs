using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace CloudStore.UI;

/// <summary>圆角输入框：内嵌无边框 TextBox，外面画圆角边框、聚焦高亮和占位提示。</summary>
internal sealed class FlatTextBox : UserControl
{
    private readonly TextBox _inner;
    private string _placeholder = string.Empty;
    private bool _focused;

    public FlatTextBox(bool password = false)
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);

        BackColor = UiEx.Surface;
        Font = new Font("Microsoft YaHei UI", 9.5F);

        _inner = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = UiEx.Surface,
            Font = new Font("Microsoft YaHei UI", 9.5F),
            UseSystemPasswordChar = password,
            TextAlign = HorizontalAlignment.Left,
        };

        _inner.GotFocus += (_, _) => { _focused = true; Invalidate(); };
        _inner.LostFocus += (_, _) => { _focused = false; Invalidate(); };
        _inner.TextChanged += (_, _) =>
        {
            Invalidate();
            InputChanged?.Invoke(this, EventArgs.Empty);
        };

        // 占位提示交给内部 TextBox 自己绘制（EM_SETCUEBANNER），
        // 否则容器画的提示会被子控件的白底盖住。
        _inner.HandleCreated += (_, _) => NativeMethods.SetCueBanner(_inner, _placeholder);

        Controls.Add(_inner);

        Height = 30;
    }

    /// <summary>内部文本框内容变化时触发（<see cref="Control.TextChanged"/> 不会从内部控件冒泡上来）。</summary>
    public event EventHandler? InputChanged;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Placeholder
    {
        get => _placeholder;
        set
        {
            _placeholder = value;

            if (_inner.IsHandleCreated)
            {
                NativeMethods.SetCueBanner(_inner, value);
            }
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool UsePassword
    {
        get => _inner.UseSystemPasswordChar;
        set => _inner.UseSystemPasswordChar = value;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ReadOnly
    {
        get => _inner.ReadOnly;
        set => _inner.ReadOnly = value;
    }

    [AllowNull]
    public override string Text
    {
        get => _inner.Text;
        set => _inner.Text = value ?? string.Empty;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int MaxLength
    {
        get => _inner.MaxLength;
        set => _inner.MaxLength = value;
    }

    public bool FocusInput() => _inner.Focus();

    public void SelectAll() => _inner.SelectAll();

    public void Clear() => _inner.Clear();

    public new bool Focus()
    {
        _inner.Focus();
        return true;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutInner();
    }

    private void LayoutInner()
    {
        if (_inner is null || _inner.IsDisposed)
        {
            return;
        }

        const int padX = 10;
        int textHeight = _inner.PreferredHeight;

        _inner.Bounds = new Rectangle(
            padX,
            Math.Max(2, (Height - textHeight) / 2),
            Math.Max(10, Width - padX * 2),
            Math.Min(Height - 4, textHeight + 2));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);

        using (GraphicsPath path = UiEx.RoundedPath(rect, 6))
        {
            using var fill = new SolidBrush(UiEx.Surface);
            g.FillPath(fill, path);

            using var border = new Pen(_focused ? UiEx.Accent : UiEx.Border);
            g.DrawPath(border, path);
        }
    }
}

/// <summary>自绘勾选框：圆角方框 + 橙色对勾，替代系统 CheckBox 的立体边框。</summary>
internal sealed class FlatCheckBox : Control
{
    private bool _checked;
    private bool _hover;

    public FlatCheckBox(string text = "")
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw |
            ControlStyles.SupportsTransparentBackColor,
            true);

        Text = text;
        BackColor = Color.Transparent;
        Font = new Font("Microsoft YaHei UI", 8.5F);
        ForeColor = UiEx.TextMuted;
        Cursor = Cursors.Hand;
        Size = new Size(Math.Max(24, TextRenderer.MeasureText(text, Font, Size.Empty,
            TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width + 26), 22);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value)
            {
                return;
            }

            _checked = value;
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? CheckedChanged;

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Size = new Size(Math.Max(24, TextRenderer.MeasureText(Text, Font, Size.Empty,
            TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine).Width + 26), 22);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        int boxSize = 16;
        int top = (Height - boxSize) / 2;
        var box = new Rectangle(0, top, boxSize, boxSize);

        Color fill = _checked ? UiEx.Accent : Color.Transparent;
        Color border = _checked ? UiEx.Accent : _hover ? UiEx.Accent : Color.FromArgb(0xC3, 0xCA, 0xD3);

        using (GraphicsPath path = UiEx.RoundedPath(box, 4))
        {
            if (fill != Color.Transparent)
            {
                using var brush = new SolidBrush(fill);
                g.FillPath(brush, path);
            }

            using var pen = new Pen(border);
            g.DrawPath(pen, path);
        }

        if (_checked)
        {
            using var pen = new Pen(Color.White, 1.8f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };

            g.DrawLines(pen, new[]
            {
                new PointF(box.X + 3.8f, box.Y + 8.2f),
                new PointF(box.X + 6.6f, box.Y + 11f),
                new PointF(box.X + 12.2f, box.Y + 5f),
            });
        }

        TextRenderer.DrawText(g, Text, Font, new Rectangle(boxSize + 7, 0, Width - boxSize - 7, Height), ForeColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnClick(EventArgs e)
    {
        Checked = !Checked;
        base.OnClick(e);
    }

    protected override bool IsInputKey(Keys keyData) => keyData == Keys.Space || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Space)
        {
            Checked = !Checked;
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }
}

/// <summary>自绘下拉框：可以像输入框一样直接键入，也可以点右侧箭头从候选里挑。</summary>
internal sealed class FlatComboBox : Control
{
    private const int ArrowWidth = 28;

    private readonly TextBox _inner;
    private readonly List<string> _items = new();
    private int _selectedIndex = -1;
    private bool _hover;
    private bool _arrowHover;
    private bool _focused;
    private bool _allowsFreeText = true;
    private string _placeholder = string.Empty;

    public FlatComboBox()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint |
            ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw,
            true);

        BackColor = UiEx.Surface;
        Font = new Font("Microsoft YaHei UI", 9.5F);
        ForeColor = UiEx.TextMain;
        Cursor = Cursors.IBeam;
        TabStop = true;
        Height = 30;
        Width = 200;

        _inner = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = UiEx.Surface,
            ForeColor = UiEx.TextMain,
            Font = new Font("Microsoft YaHei UI", 9.5F),
            TextAlign = HorizontalAlignment.Left,
        };

        _inner.TextChanged += (_, _) =>
        {
            string current = _inner.Text;
            _selectedIndex = _items.FindIndex(i => string.Equals(i, current, StringComparison.Ordinal));
            OnTextChanged(EventArgs.Empty);
            Invalidate();
        };

        _inner.GotFocus += (_, _) => { _focused = true; Invalidate(); };
        _inner.LostFocus += (_, _) => { _focused = false; _arrowHover = false; Invalidate(); };
        _inner.MouseEnter += (_, _) => { _hover = true; Invalidate(); };
        _inner.MouseLeave += (_, _) => { _hover = false; Invalidate(); };
        _inner.HandleCreated += (_, _) => ApplyPlaceholder(this, EventArgs.Empty);

        // 只读（下拉选择）模式下，文字区归内部 TextBox，点击不会冒泡到父控件，这里补上。
        _inner.Click += (_, _) =>
        {
            if (!_allowsFreeText && Enabled)
            {
                ShowMenu();
            }
        };

        Controls.Add(_inner);
    }

    public event EventHandler? SelectedIndexChanged;

    /// <summary>是否允许自由键入。false 时只能从候选里挑（用于「同名对象处理」这类固定枚举）。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool AllowsFreeText
    {
        get => _allowsFreeText;
        set
        {
            _allowsFreeText = value;
            _inner.ReadOnly = !value;
            Cursor = value ? Cursors.IBeam : Cursors.Hand;
            Invalidate();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Placeholder
    {
        get => _placeholder;
        set
        {
            _placeholder = value;

            if (_inner.IsHandleCreated)
            {
                NativeMethods.SetCueBanner(_inner, value);
            }
        }
    }

    /// <summary>句柄还没建时占位提示设不进去，等控件建好再补一次。</summary>
    private void ApplyPlaceholder(object? sender, EventArgs e) =>
        NativeMethods.SetCueBanner(_inner, _placeholder);

    [AllowNull]
    public override string Text
    {
        get => _inner.Text;
        set => _inner.Text = value ?? string.Empty;
    }

    public string SelectedText => _inner.Text;

    /// <summary>
    /// 选中第 index 项：更新显示文本、记录选中下标、抛事件。
    /// 下拉菜单点击和代码设值共用这一条路径——曾经只改显示不改下标，
    /// 导致「下拉选了 A，保存的却是 B」。
    /// </summary>
    internal void SelectIndex(int index)
    {
        if (index < 0 || index >= _items.Count)
        {
            return;
        }

        _selectedIndex = index;
        _inner.Text = _items[index];
        Invalidate();
        SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _selectedIndex;
        set => SelectIndex(value >= 0 && value < _items.Count ? value : -1);
    }

    public IReadOnlyList<string> Items => _items;

    public void SetItems(IEnumerable<string>? items)
    {
        _items.Clear();

        if (items is not null)
        {
            _items.AddRange(items.Where(i => !string.IsNullOrWhiteSpace(i)));
        }

        Invalidate();
    }

    public void FocusInput() => _inner.Focus();

    public new bool Focus()
    {
        _inner.Focus();
        return true;
    }

    private Rectangle ArrowBounds => new(Width - ArrowWidth, 0, ArrowWidth, Height);

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutInner();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        _inner.BackColor = Enabled ? UiEx.Surface : Color.FromArgb(0xF4, 0xF5, 0xF7);
        Invalidate();
    }

    private void LayoutInner()
    {
        if (_inner is null || _inner.IsDisposed)
        {
            return;
        }

        const int padX = 10;
        int textHeight = _inner.PreferredHeight;

        _inner.Bounds = new Rectangle(
            padX,
            Math.Max(2, (Height - textHeight) / 2),
            Math.Max(10, Width - padX - ArrowWidth - 2),
            Math.Min(Height - 4, textHeight + 2));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        bool highlight = Enabled && (_focused || _arrowHover);
        Color border = !Enabled ? UiEx.Border : highlight ? UiEx.Accent : UiEx.Border;

        using (GraphicsPath path = UiEx.RoundedPath(rect, 6))
        {
            using var fill = new SolidBrush(Enabled ? UiEx.Surface : Color.FromArgb(0xF4, 0xF5, 0xF7));
            g.FillPath(fill, path);

            using var pen = new Pen(border);
            g.DrawPath(pen, path);
        }

        // 箭头区域的高亮底色
        if (Enabled && _arrowHover)
        {
            using var wash = new SolidBrush(Color.FromArgb(0x14, UiEx.Accent));
            using GraphicsPath arrowPath = UiEx.RoundedPath(new Rectangle(Width - ArrowWidth - 3, 1, ArrowWidth + 2, Height - 2), 6);
            g.FillPath(wash, arrowPath);
        }

        Color glyph = Enabled ? (_hover || _focused ? UiEx.Accent : UiEx.TextMuted) : UiEx.StatusIdle;
        int arrowX = Width - ArrowWidth + 8;

        using (var brush = new SolidBrush(glyph))
        {
            g.FillPolygon(brush, new[]
            {
                new Point(arrowX, Height / 2 - 2),
                new Point(arrowX + 9, Height / 2 - 2),
                new Point(arrowX + 4, Height / 2 + 3),
            });
        }
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        _arrowHover = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        bool overArrow = Enabled && ArrowBounds.Contains(e.Location);

        if (overArrow != _arrowHover)
        {
            _arrowHover = overArrow;
            Invalidate();
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Button == MouseButtons.Left && Enabled && ArrowBounds.Contains(e.Location))
        {
            ShowMenu();
        }
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);

        if (!Enabled)
        {
            return;
        }

        if (!_allowsFreeText)
        {
            ShowMenu();
        }
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Alt or Keys.F4 || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Down && e.Alt || e.KeyCode == Keys.F4)
        {
            ShowMenu();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private void ShowMenu()
    {
        if (_items.Count == 0)
        {
            new ToolTip().SetToolTip(this, AllowsFreeText
                ? "还没有候选桶，直接键入桶名称即可"
                : "暂无可选项");
            return;
        }

        var menu = new ContextMenuStrip
        {
            Renderer = new ToolStripProfessionalRenderer(new FlatMenuColors()),
            Font = new Font("Microsoft YaHei UI", 9F),
            ShowImageMargin = false,
        };

        for (int i = 0; i < _items.Count; i++)
        {
            int index = i;
            var item = new ToolStripMenuItem(_items[i])
            {
                Checked = i == _selectedIndex,
                AutoSize = true,
                Padding = new Padding(8, 4, 16, 4),
            };

            item.Click += (_, _) => SelectIndex(index);

            menu.Items.Add(item);
        }

        menu.Show(PointToScreen(new Point(0, Height)));
    }

    private sealed class FlatMenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => UiEx.Surface;

        public override Color MenuBorder => UiEx.Border;

        public override Color MenuItemBorder => UiEx.AccentSoft;

        public override Color MenuItemSelected => UiEx.AccentSoft;

        public override Color MenuItemSelectedGradientBegin => UiEx.AccentSoft;

        public override Color MenuItemSelectedGradientEnd => UiEx.AccentSoft;

        public override Color MenuItemPressedGradientBegin => UiEx.AccentSoft;

        public override Color MenuItemPressedGradientEnd => UiEx.AccentSoft;

        public override Color ImageMarginGradientBegin => UiEx.Surface;

        public override Color ImageMarginGradientMiddle => UiEx.Surface;

        public override Color ImageMarginGradientEnd => UiEx.Surface;
    }
}
