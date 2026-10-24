using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;

namespace CloudStore.Logo;

/// <summary>
/// Logo 设计工具。
/// 所有图形按 100x100 的设计栅格绘制，再缩放到目标尺寸，所以任意分辨率都清晰。
/// </summary>
internal static class Program
{
    private const float G = 100f;

    // 品牌色（与 UI 保持一致）
    private static readonly Color Accent = Color.FromArgb(0xF7, 0x81, 0x1F);
    private static readonly Color AccentDeep = Color.FromArgb(0xDE, 0x66, 0x0D);
    private static readonly Color AccentLight = Color.FromArgb(0xFB, 0xAB, 0x63);

    private static int Main(string[] args)
    {
        string outDir = args.Length > 0
            ? args[0]
            : Path.Combine(AppContext.BaseDirectory, "out");

        Directory.CreateDirectory(outDir);

        if (args.Length > 1 && args[1] == "font")
        {
            WriteFontSheet(outDir);
            return 0;
        }

        if (args.Length > 2 && args[1] == "ico")
        {
            // uiprobe ico <variant> <输出路径>
            Action<Graphics, int>? pick = args[2].ToUpperInvariant() switch
            {
                "A" => DrawA,
                "B" => DrawB,
                "C" => DrawC,
                "D" => DrawD,
                _ => null,
            };

            if (pick is null)
            {
                Console.WriteLine("未知方案，只能是 A / B / C / D");
                return 1;
            }

            WriteIco(args[3], pick);
            Console.WriteLine($"已生成 {args[3]}（方案 {args[2]}）");
            return 0;
        }

        var variants = new (string Key, string Name, Action<Graphics, int> Draw)[]
        {
            ("A", "云 + 存储层", DrawA),
            ("B", "方块 + 云", DrawB),
            ("C", "双云（多云）", DrawC),
            ("D", "云字", DrawD),
        };

        foreach (var (key, name, draw) in variants)
        {
            foreach (int size in new[] { 1024, 256, 128, 64, 48, 32, 24, 16 })
            {
                using Bitmap bmp = Render(size, draw);
                bmp.Save(Path.Combine(outDir, $"logo-{key}-{size}.png"), ImageFormat.Png);
            }

            Console.WriteLine($"已生成 {key} {name}");
        }

        WriteSheet(outDir, variants);
        Console.WriteLine($"输出目录：{outDir}");
        return 0;
    }

    // ---------------------------------------------------------------- 画布

    private static Bitmap Render(int size, Action<Graphics, int> draw)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);

        using Graphics g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        draw(g, size);
        return bmp;
    }

    // ---------------------------------------------------------------- 方案

    /// <summary>A：一朵云 + 白色双向箭头（传上 / 取回）。</summary>
    private static void DrawA(Graphics g, int size)
    {
        g.ScaleTransform(size / G, size / G);

        using (GraphicsPath cloud = Cloud(4, 10, 92, 60))
        using (var brush = new SolidBrush(Accent))
        {
            g.FillPath(brush, cloud);
        }

        // 白色竖向双箭头
        using (var white = new SolidBrush(Color.White))
        {
            g.FillPath(white, RoundRect(46, 26, 8, 28, 4));
            g.FillPolygon(white, new[]
            {
                new PointF(38, 34), new PointF(62, 34), new PointF(50, 16),
            });
            g.FillPolygon(white, new[]
            {
                new PointF(38, 46), new PointF(62, 46), new PointF(50, 64),
            });
        }
    }

    /// <summary>B：橙色圆角方块 + 白色云。</summary>
    private static void DrawB(Graphics g, int size)
    {
        g.ScaleTransform(size / G, size / G);

        using (var path = RoundRect(4, 4, 92, 92, 23))
        using (var tileBrush = new LinearGradientBrush(
                   new RectangleF(4, 4, 92, 92), AccentLight, AccentDeep, LinearGradientMode.ForwardDiagonal))
        {
            g.FillPath(tileBrush, path);
        }

        using (GraphicsPath cloud = Cloud(20, 27, 60, 46))
        using (var cloudBrush = new SolidBrush(Color.White))
        {
            g.FillPath(cloudBrush, cloud);
        }
    }

    /// <summary>C：两朵云叠放，代表「同时管多家云」。</summary>
    private static void DrawC(Graphics g, int size)
    {
        g.ScaleTransform(size / G, size / G);

        using GraphicsPath big = Cloud(0, 2, 70, 50);
        using GraphicsPath small = Cloud(38, 46, 62, 46);

        // 小云外扩一圈，从大云里挖掉，避免糊成一坨
        using (var bigRegion = new Region(big))
        {
            using Region gap = Dilate(small, 5f, 1.5f);
            bigRegion.Exclude(gap);

            using var bodyBrush = new SolidBrush(Accent);
            g.FillRegion(bodyBrush, bigRegion);
        }

        using (var smallBrush = new SolidBrush(AccentDeep))
        {
            g.FillPath(smallBrush, small);
        }
    }

    /// <summary>D：橙色圆角方块 + 白色「云」字。</summary>
    private static void DrawD(Graphics g, int size)
    {
        g.ScaleTransform(size / G, size / G);

        using (var path = RoundRect(4, 4, 92, 92, 23))
        using (var tileBrush = new LinearGradientBrush(
                   new RectangleF(4, 4, 92, 92), AccentLight, AccentDeep, LinearGradientMode.ForwardDiagonal))
        {
            g.FillPath(tileBrush, path);
        }

        g.ResetTransform();

        using var font = new Font(CloudFont, size * 0.52f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var format = new StringFormat(StringFormat.GenericTypographic);

        SizeF ink = g.MeasureString("云", font, int.MaxValue, format);
        float x = (size - ink.Width) / 2f;
        float y = (size - ink.Height) / 2f - size * 0.02f;

        using var textBrush = new SolidBrush(Color.White);
        g.DrawString("云", font, textBrush, new PointF(x, y), format);
    }

    /// <summary>「云」字用哪种字体——雅黑 UI 偏细，黑体更厚重。</summary>
    private static string CloudFont = "SimHei";

    // ---------------------------------------------------------------- 图形

    /// <summary>
    /// 云：三个相交的圆并集——两侧小鼓包 + 中间大鼓包。
    /// 关键是三个圆必须真的相交：按比例算半径、再用「中心点 + 半径」定位，
    /// 保证左圆右缘 > 顶圆左缘、右圆左缘 < 顶圆右缘，否则中间会裂开一条缝。
    /// </summary>
    private static GraphicsPath Cloud(float x, float y, float w, float h)
    {
        float rL = h * 0.30f;   // 左鼓包
        float rT = h * 0.39f;   // 顶鼓包
        float rR = h * 0.27f;   // 右鼓包

        var path = new GraphicsPath { FillMode = FillMode.Winding };
        path.AddEllipse(x, y + h - rL * 2f, rL * 2f, rL * 2f);
        path.AddEllipse(x + w * 0.5f - rT, y, rT * 2f, rT * 2f);
        path.AddEllipse(x + w - rR * 2f, y + h - rR * 2f, rR * 2f, rR * 2f);
        return path;
    }

    /// <summary>把路径向外扩一圈（用一堆偏移副本求并集近似）。</summary>
    private static Region Dilate(GraphicsPath path, float amount, float step)
    {
        var region = new Region(path);

        for (float dx = -amount; dx <= amount + 0.001f; dx += step)
        {
            for (float dy = -amount; dy <= amount + 0.001f; dy += step)
            {
                if (dx == 0f && dy == 0f)
                {
                    continue;
                }

                var clone = (GraphicsPath)path.Clone();
                var matrix = new Matrix();
                matrix.Translate(dx, dy);
                clone.Transform(matrix);
                region.Union(clone);
                clone.Dispose();
            }
        }

        return region;
    }

    private static GraphicsPath RoundRect(float x, float y, float w, float h, float r)
    {
        float d = r * 2f;

        var path = new GraphicsPath();
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // ---------------------------------------------------------------- 字体对比

    private static void WriteFontSheet(string outDir)
    {
        string[] fonts =
        {
            "Microsoft YaHei UI",
            "Microsoft YaHei",
            "SimHei",
            "DengXian",
            "SimSun",
            "Microsoft JhengHei UI",
        };

        const int Cell = 200;
        using var sheet = new Bitmap(Cell * fonts.Length, Cell + 40, PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(sheet);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.FromArgb(0xF2, 0xF4, 0xF7));

        for (int i = 0; i < fonts.Length; i++)
        {
            using (Bitmap bmp = Render(Cell, (gg, size) =>
                   {
                       gg.ScaleTransform(size / G, size / G);
                       using var path = RoundRect(6, 6, 88, 88, 22);
                       using var tile = new LinearGradientBrush(
                           new RectangleF(6, 6, 88, 88), AccentLight, AccentDeep, LinearGradientMode.ForwardDiagonal);
                       gg.FillPath(tile, path);
                       gg.ResetTransform();

                       using var font = new Font(fonts[i], size * 0.56f, FontStyle.Bold, GraphicsUnit.Pixel);
                       using var format = new StringFormat(StringFormat.GenericTypographic);
                       SizeF ink = gg.MeasureString("云", font, int.MaxValue, format);
                       using var tb = new SolidBrush(Color.White);
                       gg.DrawString("云", font, tb,
                           new PointF((size - ink.Width) / 2f, (size - ink.Height) / 2f - size * 0.02f), format);
                   }))
            {
                g.DrawImage(bmp, i * Cell, 0);
            }

            using var labelFont = new Font("Microsoft YaHei UI", 9F);
            using var labelBrush = new SolidBrush(Color.FromArgb(0x74, 0x7D, 0x87));
            g.DrawString(fonts[i], labelFont, labelBrush, new PointF(i * Cell + 8, Cell + 10));
        }

        sheet.Save(Path.Combine(outDir, "font-compare.png"), ImageFormat.Png);
        Console.WriteLine("已生成 font-compare.png");
    }

    // ---------------------------------------------------------------- 预览

    private static void WriteSheet(string outDir, (string Key, string Name, Action<Graphics, int> Draw)[] variants)
    {
        const int CellW = 300;
        const int CellH = 470;
        const int Pad = 16;

        int width = (CellW + Pad) * variants.Length + Pad;
        int height = CellH + Pad * 2;

        using var sheet = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using Graphics g = Graphics.FromImage(sheet);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        using (var back = new SolidBrush(Color.FromArgb(0xF2, 0xF4, 0xF7)))
        {
            g.FillRectangle(back, 0, 0, width, height);
        }

        for (int i = 0; i < variants.Length; i++)
        {
            var (key, name, draw) = variants[i];
            int x0 = Pad + i * (CellW + Pad);
            int y0 = Pad;

            using (var card = new SolidBrush(Color.White))
            using (var border = new Pen(Color.FromArgb(0xE1, 0xE5, 0xEA)))
            {
                g.FillRectangle(card, x0, y0, CellW, CellH);
                g.DrawRectangle(border, x0, y0, CellW, CellH);
            }

            using (var font = new Font("Microsoft YaHei UI", 11F, FontStyle.Bold))
            using (var labelBrush = new SolidBrush(Color.FromArgb(0x1F, 0x24, 0x2B)))
            {
                g.DrawString($"{key} · {name}", font, labelBrush, new PointF(x0 + 14, y0 + 12));
            }

            using (Bitmap big = Render(256, draw))
            {
                g.DrawImage(big, new Rectangle(x0 + (CellW - 256) / 2, y0 + 44, 256, 256));
            }

            using (var strip = new SolidBrush(Color.FromArgb(0xF6, 0xF7, 0xF9)))
            {
                g.FillRectangle(strip, x0 + 1, y0 + 312, CellW - 1, 92);
            }

            int sx = x0 + 16;
            foreach (int size in new[] { 64, 48, 32, 24, 16 })
            {
                using Bitmap small = Render(size, draw);
                g.DrawImage(small, new Rectangle(sx, y0 + 358 - size, size, size));
                sx += size + 22;
            }

            using (var strip = new SolidBrush(Color.FromArgb(0x1F, 0x24, 0x2B)))
            {
                g.FillRectangle(strip, x0 + 1, y0 + 405, CellW - 1, 64);
            }

            int dx = x0 + 16;
            foreach (int size in new[] { 48, 32, 16 })
            {
                using Bitmap small = Render(size, draw);
                g.DrawImage(small, new Rectangle(dx, y0 + 437 - size, size, size));
                dx += size + 22;
            }
        }

        sheet.Save(Path.Combine(outDir, "logo-variants.png"), ImageFormat.Png);
    }

    // ---------------------------------------------------------------- ICO

    /// <summary>
    /// 写多尺寸 ico。
    /// 尺寸集合与现有 app.ico 保持一致（小尺寸用 BMP 帧、256 用 PNG 帧），
    /// 免得改了容器格式之后标题栏那条 16px 路径出问题。
    /// </summary>
    public static void WriteIco(string path, Action<Graphics, int> draw)
    {
        int[] sizes = { 16, 24, 32, 48, 64, 72, 80, 96, 128, 256 };
        var entries = new List<(int Size, byte[] Data)>();

        foreach (int size in sizes)
        {
            using Bitmap bmp = Render(size, draw);

            if (size >= 128)
            {
                using var ms = new MemoryStream();
                bmp.Save(ms, ImageFormat.Png);
                entries.Add((size, ms.ToArray()));
            }
            else
            {
                entries.Add((size, EncodeDib(bmp)));
            }
        }

        using var fs = new FileStream(path, FileMode.Create);
        using var bw = new BinaryWriter(fs);

        bw.Write((ushort)0);
        bw.Write((ushort)1);
        bw.Write((ushort)entries.Count);

        int offset = 6 + 16 * entries.Count;

        foreach (var (size, data) in entries)
        {
            bw.Write((byte)(size >= 256 ? 0 : size));
            bw.Write((byte)(size >= 256 ? 0 : size));
            bw.Write((byte)0);
            bw.Write((byte)0);
            bw.Write((ushort)1);
            bw.Write((ushort)32);
            bw.Write(data.Length);
            bw.Write(offset);
            offset += data.Length;
        }

        foreach (var (_, data) in entries)
        {
            bw.Write(data);
        }
    }

    /// <summary>位图转 ICO 用的 DIB（BITMAPINFOHEADER + 自下而上 BGRA + AND 掩码）。</summary>
    private static byte[] EncodeDib(Bitmap bmp)
    {
        int w = bmp.Width;
        int h = bmp.Height;
        int xorSize = w * h * 4;
        int maskStride = ((w + 31) / 32) * 4;
        int maskSize = maskStride * h;

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(40);                    // biSize
        bw.Write(w);                     // biWidth
        bw.Write(h * 2);                 // biHeight = XOR + AND
        bw.Write((short)1);              // biPlanes
        bw.Write((short)32);             // biBitCount
        bw.Write((int)0);                // biCompression = BI_RGB
        bw.Write(xorSize + maskSize);    // biSizeImage
        bw.Write(0);                     // biXPelsPerMeter
        bw.Write(0);                     // biYPelsPerMeter
        bw.Write(0);                     // biClrUsed
        bw.Write(0);                     // biClrImportant

        BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        try
        {
            var row = new byte[w * 4];

            for (int y = h - 1; y >= 0; y--)
            {
                IntPtr line = data.Scan0 + (nint)(y * data.Stride);
                System.Runtime.InteropServices.Marshal.Copy(line, row, 0, w * 4);
                bw.Write(row);
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        // 32 位图靠 alpha 表达透明，AND 掩码全 0（完全不透明）即可
        bw.Write(new byte[maskSize]);

        return ms.ToArray();
    }
}
