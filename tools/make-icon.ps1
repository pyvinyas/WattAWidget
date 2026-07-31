# Generates src\app.ico (16..256) and docs\icon-preview.png from the "E3" logo:
# dark rounded tile, bar chart whose peak bar is an amber lightning bolt
# (green bar, amber bolt, red bar - the widget's load colors).
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
    static readonly Color Green = Color.FromArgb(90, 200, 120);
    static readonly Color Amber = Color.FromArgb(240, 200, 80);
    static readonly Color Red = Color.FromArgb(240, 110, 90);

    // flat-top bolt vertices, in fractions of icon size
    static readonly float[,] B = {
        {0.589f,0.144f},{0.411f,0.522f},{0.511f,0.522f},{0.444f,0.844f},
        {0.656f,0.444f},{0.556f,0.444f},{0.633f,0.144f}
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

    static PointF[] BoltPts(float s)
    {
        var pts = new PointF[7];
        for (int i = 0; i < 7; i++)
            pts[i] = new PointF(B[i, 0] * s, B[i, 1] * s);
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

                float rad = Math.Max(0.75f, 0.039f * s);
                using (var br = new SolidBrush(Green))
                using (var p = RoundRect(0.178f * s, 0.544f * s, 0.167f * s, 0.267f * s, rad))
                    g.FillPath(br, p);
                using (var br = new SolidBrush(Amber))
                    g.FillPolygon(br, BoltPts(s));
                using (var br = new SolidBrush(Red))
                using (var p = RoundRect(0.667f * s, 0.422f * s, 0.167f * s, 0.389f * s, rad))
                    g.FillPath(br, p);
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
