using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CloudStore.UI;

/// <summary>运行时用 GDI+ 绘制的 16x16 图标，避免额外的二进制资源文件。</summary>
internal static class UiIcons
{
    private const int Size = 16;

    // 注意：静态字段初始化按声明顺序执行，KeepAlive 必须先于 Images
    private static readonly List<Bitmap> KeepAlive = new();

    private static readonly ImageList Images = Build();

    private static readonly Dictionary<string, int> Index = new(StringComparer.OrdinalIgnoreCase)
    {
        ["folder"] = 0,
        ["file"] = 1,
        ["image"] = 2,
        ["text"] = 3,
        ["code"] = 4,
        ["doc"] = 5,
        ["archive"] = 6,
        ["video"] = 7,
        ["audio"] = 8,
        ["upload"] = 9,
        ["download"] = 10,
        ["delete"] = 11,
        ["refresh"] = 12,
        ["settings"] = 13,
        ["cloud"] = 14,
        ["warn"] = 15,
        ["search"] = 16,
        ["arrowUp"] = 17,
        ["chevronLeft"] = 18,
        ["chevronRight"] = 19,
        ["copy"] = 20,
        ["link"] = 21,
        ["preview"] = 22,
    };

    public static ImageList List => Images;

    public static int For(string? kind)
    {
        if (!string.IsNullOrEmpty(kind) && Index.TryGetValue(kind, out int index))
        {
            return index;
        }

        return 1;
    }

    private static ImageList Build()
    {
        var list = new ImageList
        {
            ImageSize = new System.Drawing.Size(Size, Size),
            ColorDepth = ColorDepth.Depth32Bit,
        };

        // ImageList 不会接管位图的生存期，这里保留引用避免被回收 / 提前释放
        foreach (var bitmap in CreateAll())
        {
            list.Images.Add(bitmap);
            KeepAlive.Add(bitmap);
        }

        return list;
    }


    private static IEnumerable<Bitmap> CreateAll()
    {
        yield return Draw(DrawFolder);
        yield return Draw(DrawPage);
        yield return Draw(g => { DrawPage(g); Decorate(g, Color.FromArgb(0x3B, 0x82, 0xF6), DrawLandscape); });
        yield return Draw(g => { DrawPage(g); DrawLines(g, Color.FromArgb(0x60, 0x6A, 0x76)); });
        yield return Draw(g => { DrawPage(g); DrawCodeBrackets(g); });
        yield return Draw(g => { DrawPage(g); Decorate(g, Color.FromArgb(0xD9, 0x34, 0x2B), DrawDocumentMark); });
        yield return Draw(g => { DrawPage(g); Decorate(g, Color.FromArgb(0xE8, 0x8B, 0x1F), DrawArchiveMark); });
        yield return Draw(g => { DrawPage(g); DrawPlayTriangle(g); });
        yield return Draw(g => { DrawPage(g); DrawNote(g); });
        yield return Draw(DrawUploadCloud);
        yield return Draw(DrawDownloadCloud);
        yield return Draw(DrawTrash);
        yield return Draw(DrawRefresh);
        yield return Draw(DrawGear);
        yield return Draw(g => DrawCloud(g, Color.FromArgb(0xF7, 0x81, 0x1F)));
        yield return Draw(DrawWarning);
        yield return Draw(DrawMagnifier);
        yield return Draw(DrawArrowUp);
        yield return Draw(DrawChevronLeft);
        yield return Draw(DrawChevronRight);
        yield return Draw(DrawCopy);
        yield return Draw(DrawLink);
        yield return Draw(g => { DrawPage(g); Decorate(g, Color.FromArgb(0x25, 0x63, 0xEB), DrawMagnifierInner); });
    }

    private static Bitmap Draw(Action<Graphics> painter)
    {
        var bitmap = new Bitmap(Size, Size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        painter(g);
        return bitmap;
    }

    private static Pen Stroke(Color color, float width = 1f) => new(color, width);

    private static GraphicsPath RoundedPath(float x, float y, float width, float height, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;

        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + width - d, y, d, d, 270, 90);
        path.AddArc(x + width - d, y + height - d, d, d, 0, 90);
        path.AddArc(x, y + height - d, d, d, 90, 90);
        path.CloseFigure();

        return path;
    }

    // ---------- 文件类型 ----------

    private static void DrawPage(Graphics g)
    {
        using var fill = new SolidBrush(Color.White);
        using var pen = Stroke(Color.FromArgb(0xA9, 0xB2, 0xBD));

        g.FillRectangle(fill, 3.5f, 1.5f, 9f, 13f);
        g.DrawRectangle(pen, 3.5f, 1.5f, 9f, 13f);

        using var fold = new SolidBrush(Color.FromArgb(0xE2, 0xE7, 0xEC));
        g.FillPolygon(fold, new[]
        {
            new PointF(8.5f, 1.5f),
            new PointF(12.5f, 5.5f),
            new PointF(8.5f, 5.5f),
        });

        using var foldPen = Stroke(Color.FromArgb(0xA9, 0xB2, 0xBD));
        g.DrawLines(foldPen, new[]
        {
            new PointF(8.5f, 1.5f),
            new PointF(8.5f, 5.5f),
            new PointF(12.5f, 5.5f),
        });
    }

    private static void DrawFolder(Graphics g)
    {
        GraphicsPath back = RoundedPath(1.5f, 3.5f, 13f, 5f, 1.5f);
        using (var fill = new SolidBrush(Color.FromArgb(0xF0, 0xA7, 0x1F)))
        using (var pen = Stroke(Color.FromArgb(0xC8, 0x87, 0x0C)))
        {
            g.FillPath(fill, back);
            g.DrawPath(pen, back);
        }

        GraphicsPath front = RoundedPath(1.5f, 6.5f, 13f, 8f, 1.5f);
        using (var fill = new SolidBrush(Color.FromArgb(0xFF, 0xC9, 0x4A)))
        using (var pen = Stroke(Color.FromArgb(0xC8, 0x87, 0x0C)))
        {
            g.FillPath(fill, front);
            g.DrawPath(pen, front);
        }
    }

    private static void Decorate(Graphics g, Color accent, Action<Graphics> body)
    {
        using var background = new SolidBrush(Color.FromArgb(0xF7, 0xF9, 0xFB));
        g.FillRectangle(background, 4.5f, 6.5f, 7f, 7f);
        body(g);
    }

    private static void DrawLandscape(Graphics g)
    {
        using var sky = new SolidBrush(Color.FromArgb(0xBA, 0xDC, 0xFB));
        g.FillRectangle(sky, 4.5f, 6.5f, 7f, 7f);

        using var sun = new SolidBrush(Color.FromArgb(0xF5, 0xB1, 0x3B));
        g.FillEllipse(sun, 8.5f, 7.5f, 2f, 2f);

        using var hill = new SolidBrush(Color.FromArgb(0x33, 0x9B, 0x62));
        g.FillPolygon(hill, new[]
        {
            new PointF(4.5f, 13.5f),
            new PointF(7.5f, 9.5f),
            new PointF(10f, 13.5f),
        });

        using var hill2 = new SolidBrush(Color.FromArgb(0x16, 0x8A, 0x58));
        g.FillPolygon(hill2, new[]
        {
            new PointF(8f, 13.5f),
            new PointF(11f, 10.5f),
            new PointF(13.5f, 13.5f),
        });
    }

    private static void DrawLines(Graphics g, Color color)
    {
        using var pen = Stroke(color, 1.2f);
        g.DrawLines(pen, new[]
        {
            new PointF(5f, 8f), new PointF(11f, 8f),
        });
        g.DrawLines(pen, new[]
        {
            new PointF(5f, 10.25f), new PointF(11f, 10.25f),
        });
        g.DrawLines(pen, new[]
        {
            new PointF(5f, 12.5f), new PointF(9f, 12.5f),
        });
    }

    private static void DrawCodeBrackets(Graphics g)
    {
        using var pen = Stroke(Color.FromArgb(0x25, 0x63, 0xEB), 1.4f);

        g.DrawLines(pen, new[]
        {
            new PointF(7f, 8.5f), new PointF(5f, 10.5f), new PointF(7f, 12.5f),
        });
        g.DrawLines(pen, new[]
        {
            new PointF(10f, 8.5f), new PointF(12f, 10.5f), new PointF(10f, 12.5f),
        });
    }

    private static void DrawDocumentMark(Graphics g)
    {
        using var bar = new SolidBrush(Color.FromArgb(0xD9, 0x34, 0x2B));
        g.FillRectangle(bar, 5f, 7.5f, 6f, 1.2f);
        g.FillRectangle(bar, 5f, 10f, 6f, 1.2f);
        g.FillRectangle(bar, 5f, 12.5f, 3.5f, 1.2f);
    }

    private static void DrawArchiveMark(Graphics g)
    {
        using var fill = new SolidBrush(Color.FromArgb(0xE8, 0x8B, 0x1F));
        g.FillRectangle(fill, 4.5f, 8.5f, 7f, 5f);

        using var pen = Stroke(Color.FromArgb(0xA8, 0x62, 0x0C));
        g.DrawLines(pen, new[]
        {
            new PointF(7.5f, 8.5f), new PointF(8.5f, 13.5f),
        });
    }

    private static void DrawPlayTriangle(Graphics g)
    {
        using var fill = new SolidBrush(Color.FromArgb(0x7C, 0x4D, 0xE8));
        g.FillPolygon(fill, new[]
        {
            new PointF(6.5f, 7.5f),
            new PointF(12f, 10.5f),
            new PointF(6.5f, 13.5f),
        });
    }

    private static void DrawNote(Graphics g)
    {
        using var fill = new SolidBrush(Color.FromArgb(0xE0, 0x8B, 0x1F));
        g.FillEllipse(fill, 5f, 10f, 3f, 2.4f);
        g.FillRectangle(fill, 7.4f, 7f, 0.9f, 4.4f);
        g.FillRectangle(fill, 7.4f, 7f, 2.6f, 0.9f);
    }

    // ---------- 操作类 ----------

    private static void DrawCloud(Graphics g, Color color)
    {
        using var fill = new SolidBrush(color);
        using var pen = Stroke(Color.FromArgb(0xC0, 0xC6, 0xCE));

        g.FillEllipse(fill, 3f, 6f, 5f, 5f);
        g.FillEllipse(fill, 7f, 4.5f, 6f, 6f);
        g.FillRectangle(fill, 3f, 8.5f, 10f, 2.5f);
        g.DrawEllipse(pen, 3f, 6f, 5f, 5f);
    }

    private static void DrawUploadCloud(Graphics g)
    {
        DrawCloud(g, Color.FromArgb(0xF7, 0x81, 0x1F));

        using var arrow = new SolidBrush(Color.White);
        using var pen = Stroke(Color.White, 1.6f);

        g.DrawLines(pen, new[]
        {
            new PointF(8f, 13.2f), new PointF(8f, 8.2f),
        });
        g.DrawLines(pen, new[]
        {
            new PointF(5.8f, 10.4f), new PointF(8f, 8.2f), new PointF(10.2f, 10.4f),
        });
    }

    private static void DrawDownloadCloud(Graphics g)
    {
        DrawCloud(g, Color.FromArgb(0x25, 0x63, 0xEB));

        using var pen = Stroke(Color.White, 1.6f);
        g.DrawLines(pen, new[]
        {
            new PointF(8f, 7.8f), new PointF(8f, 12.8f),
        });
        g.DrawLines(pen, new[]
        {
            new PointF(5.8f, 10.6f), new PointF(8f, 12.8f), new PointF(10.2f, 10.6f),
        });
    }

    private static void DrawTrash(Graphics g)
    {
        using var pen = Stroke(Color.FromArgb(0xD9, 0x34, 0x2B), 1.2f);

        g.DrawLines(pen, new[]
        {
            new PointF(4f, 4.5f), new PointF(12f, 4.5f),
        });
        g.DrawLines(pen, new[]
        {
            new PointF(6.5f, 2.8f), new PointF(9.5f, 2.8f),
        });
        g.DrawLines(pen, new[]
        {
            new PointF(5f, 4.5f), new PointF(5.7f, 13f), new PointF(10.3f, 13f), new PointF(11f, 4.5f),
        });
    }

    private static void DrawRefresh(Graphics g)
    {
        using var pen = Stroke(Color.FromArgb(0x25, 0x63, 0xEB), 1.6f);
        g.DrawArc(pen, 3.2f, 3.2f, 9.6f, 9.6f, 40, 280);

        using var fill = new SolidBrush(Color.FromArgb(0x25, 0x63, 0xEB));
        g.FillPolygon(fill, new[]
        {
            new PointF(11.4f, 1.6f),
            new PointF(13.4f, 4.8f),
            new PointF(9.7f, 4.0f),
        });
    }

    private static void DrawGear(Graphics g)
    {
        using var fill = new SolidBrush(Color.FromArgb(0x6E, 0x77, 0x81));
        GraphicsPath path = RoundedPath(3f, 3f, 10f, 10f, 2f);

        for (int i = 0; i < 8; i++)
        {
            double angle = i * Math.PI / 4;
            float cx = 8f + (float)Math.Cos(angle) * 7.2f;
            float cy = 8f + (float)Math.Sin(angle) * 7.2f;
            path.AddEllipse(cx - 1.4f, cy - 1.4f, 2.8f, 2.8f);
        }

        g.FillPath(fill, path);

        using var hole = new SolidBrush(Color.Transparent);
        using var pen = Stroke(Color.White, 1.6f);
        g.DrawEllipse(pen, 5.8f, 5.8f, 4.4f, 4.4f);
    }

    private static void DrawWarning(Graphics g)
    {
        using var fill = new SolidBrush(Color.FromArgb(0xF5, 0xB1, 0x3B));
        g.FillPolygon(fill, new[]
        {
            new PointF(8f, 1.8f),
            new PointF(15f, 13.6f),
            new PointF(1f, 13.6f),
        });

        using var pen = Stroke(Color.White, 1.6f);
        g.DrawLines(pen, new[]
        {
            new PointF(8f, 6f), new PointF(8f, 9.6f),
        });
        g.FillEllipse(new SolidBrush(Color.White), 7.2f, 10.8f, 1.6f, 1.6f);
    }

    private static void DrawMagnifier(Graphics g)
    {
        using var pen = Stroke(Color.FromArgb(0x6E, 0x77, 0x81), 1.6f);
        g.DrawEllipse(pen, 3f, 3f, 8f, 8f);
        g.DrawLines(pen, new[]
        {
            new PointF(10.4f, 10.4f), new PointF(14f, 14f),
        });
    }

    private static void DrawMagnifierInner(Graphics g)
    {
        using var pen = Stroke(Color.FromArgb(0x25, 0x63, 0xEB), 1.1f);
        g.DrawEllipse(pen, 5.6f, 7.4f, 4.4f, 4.4f);
        g.DrawLines(pen, new[]
        {
            new PointF(9.4f, 11.2f), new PointF(11.6f, 13.4f),
        });
    }

    private static void DrawArrowUp(Graphics g)
    {
        using var pen = Stroke(Color.FromArgb(0x25, 0x63, 0xEB), 1.7f);
        g.DrawLines(pen, new[]
        {
            new PointF(8f, 13.5f), new PointF(8f, 3f),
        });
        g.DrawLines(pen, new[]
        {
            new PointF(4.2f, 6.8f), new PointF(8f, 3f), new PointF(11.8f, 6.8f),
        });
    }

    private static void DrawChevronLeft(Graphics g)
    {
        using var pen = Stroke(Color.FromArgb(0x6E, 0x77, 0x81), 1.7f);
        g.DrawLines(pen, new[]
        {
            new PointF(10f, 3f), new PointF(5f, 8f), new PointF(10f, 13f),
        });
    }

    private static void DrawChevronRight(Graphics g)
    {
        using var pen = Stroke(Color.FromArgb(0x6E, 0x77, 0x81), 1.7f);
        g.DrawLines(pen, new[]
        {
            new PointF(6f, 3f), new PointF(11f, 8f), new PointF(6f, 13f),
        });
    }

    private static void DrawCopy(Graphics g)
    {
        using var fill = new SolidBrush(Color.White);
        using var pen = Stroke(Color.FromArgb(0x6E, 0x77, 0x81), 1.2f);

        g.FillRectangle(fill, 2.5f, 2.5f, 8f, 8f);
        g.DrawRectangle(pen, 2.5f, 2.5f, 8f, 8f);

        g.FillRectangle(fill, 5.5f, 5.5f, 8f, 8f);
        g.DrawRectangle(pen, 5.5f, 5.5f, 8f, 8f);
    }

    private static void DrawLink(Graphics g)
    {
        using var pen = Stroke(Color.FromArgb(0x25, 0x63, 0xEB), 1.6f);

        g.DrawArc(pen, 1.5f, 6f, 7f, 5f, 90, 180);
        g.DrawArc(pen, 7.5f, 5f, 7f, 5f, 270, 180);
        g.DrawLines(pen, new[]
        {
            new PointF(5.4f, 8.2f), new PointF(10.6f, 8.2f),
        });
    }
}
