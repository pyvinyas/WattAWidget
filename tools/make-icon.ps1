# Generates src\app.ico (16..256) and docs\icon-preview.png from the "G" logo:
# dark rounded tile, green power-ring, amber bolt through the top gap.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

Add-Type -ReferencedAssemblies System.Drawing @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

public static class IconMaker
{
    static readonly Color Tile = Color.FromArgb(22, 22, 28);
    static readonly Color Ring = Color.FromArgb(90, 200, 120);
    static readonly Color Bolt = Color.FromArgb(240, 200, 80);

    // bolt offsets from ring center, in fractions of icon size
    static readonly float[,] B = {
        {0.04f,-0.31f},{-0.09f,-0.09f},{-0.01f,-0.09f},{-0.05f,0.07f},
        {0.10f,-0.15f},{0.01f,-0.15f},{0.06f,-0.31f}
    };

    static GraphicsPath RoundRect(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath();
        float d = r * 2;
        p.AddArc(x, y, d, d, 180, 90);
        p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    static PointF[] BoltPts(float s, float cx, float cy, float scale)
    {
        var pts = new PointF[7];
        for (int i = 0; i < 7; i++)
            pts[i] = new PointF(cx + B[i, 0] * scale * s, cy + B[i, 1] * scale * s);
        return pts;
    }

    public static byte[] RenderPng(int s)
    {
        using (var bmp = Render(s))
        using (var ms = new MemoryStream())
        {
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
    }

    // Classic uncompressed 32bpp DIB entry (BITMAPINFOHEADER + BGRA bottom-up + AND mask).
    // Windows only reliably decodes PNG entries at 256px; smaller sizes must be BMP.
    public static byte[] RenderBmpEntry(int s)
    {
        using (var bmp = Render(s))
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms))
        {
            int andStride = ((s + 31) / 32) * 4;
            w.Write(40); w.Write(s); w.Write(s * 2);
            w.Write((short)1); w.Write((short)32);
            w.Write(0); w.Write(s * s * 4 + andStride * s);
            w.Write(0); w.Write(0); w.Write(0); w.Write(0);
            var data = bmp.LockBits(new Rectangle(0, 0, s, s), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var row = new byte[s * 4];
            for (int y = s - 1; y >= 0; y--)
            {
                System.Runtime.InteropServices.Marshal.Copy(
                    new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride), row, 0, s * 4);
                w.Write(row);
            }
            bmp.UnlockBits(data);
            w.Write(new byte[andStride * s]);
            return ms.ToArray();
        }
    }

    static Bitmap Render(int s)
    {
        var bmp = new Bitmap(s, s, PixelFormat.Format32bppArgb);
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var tile = RoundRect(0, 0, s, s, 0.22f * s))
                using (var tb = new SolidBrush(Tile))
                    g.FillPath(tb, tile);

                if (s >= 32)
                {
                    float cx = 0.5f * s, cy = 0.54f * s, r = 0.33f * s;
                    using (var pen = new Pen(Ring, 0.085f * s))
                    {
                        pen.StartCap = LineCap.Round;
                        pen.EndCap = LineCap.Round;
                        g.DrawArc(pen, cx - r, cy - r, r * 2, r * 2, 310, 280);
                    }
                    using (var bb = new SolidBrush(Bolt))
                        g.FillPolygon(bb, BoltPts(s, cx, cy, 1.25f));
                }
                else
                {
                    // small sizes: tile + big bolt only, ring omitted for legibility
                    using (var bb = new SolidBrush(Bolt))
                        g.FillPolygon(bb, BoltPts(s, 0.5f * s, 0.764f * s, 2.2f));
                }
            }
        }
        return bmp;
    }

    public static void WriteIco(string path, int[] sizes)
    {
        var blobs = new List<byte[]>();
        foreach (int s in sizes) blobs.Add(s >= 256 ? RenderPng(s) : RenderBmpEntry(s));
        using (var fs = new FileStream(path, FileMode.Create))
        using (var w = new BinaryWriter(fs))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                int s = sizes[i];
                w.Write((byte)(s >= 256 ? 0 : s));
                w.Write((byte)(s >= 256 ? 0 : s));
                w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32);
                w.Write(blobs[i].Length);
                w.Write(offset);
                offset += blobs[i].Length;
            }
            foreach (var b in blobs) w.Write(b);
        }
    }

    public static void WritePreview(string path)
    {
        File.WriteAllBytes(path, RenderPng(256));
    }
}
'@

New-Item -ItemType Directory -Force (Join-Path $root 'docs') | Out-Null
[IconMaker]::WriteIco((Join-Path $root 'src\app.ico'), @(256,128,64,48,32,24,16))
[IconMaker]::WritePreview((Join-Path $root 'docs\icon-preview.png'))
Write-Host "Icon written: src\app.ico + docs\icon-preview.png"
