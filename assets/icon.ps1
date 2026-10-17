$ErrorActionPreference = 'Stop'
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

public static class SessionPurgeIcon
{
    const double Edge = 10, Size = 236, Corner = 56.5;
    const double Badge = Edge + Size - Corner;
    static readonly int[] Blue = { 30, 78, 140 }, Red = { 192, 54, 44 }, White = { 255, 255, 255 };

    static bool RoundRect(double x, double y, double left, double top, double right, double bottom, double radius)
    {
        if (x < left || x > right || y < top || y > bottom) return false;
        double dx = Math.Max(Math.Max(left + radius - x, x - (right - radius)), 0);
        double dy = Math.Max(Math.Max(top + radius - y, y - (bottom - radius)), 0);
        return dx * dx + dy * dy <= radius * radius;
    }

    static bool Tail(double x, double y)
    {
        if (y < 150) return false;
        double left = 74 - 9 * (y - 160.5) / 28, right = 120 - 50 * (y - 160.5) / 28;
        return x >= left && x <= right;
    }

    static int[] Sample(double x, double y)
    {
        if (x >= 160 && x <= 219 && y >= 177 && y <= 202) return White;
        if ((x - Badge) * (x - Badge) + (y - Badge) * (y - Badge) <= Corner * Corner) return Red;
        if (RoundRect(x, y, 52, 50, 192, 160, 24) || Tail(x, y)) return White;
        if (RoundRect(x, y, Edge, Edge, Edge + Size, Edge + Size, Corner)) return Blue;
        return null;
    }

    public static Bitmap Render(int size)
    {
        const int grid = 16;
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        var bytes = new byte[size * data.Stride];
        double scale = 256.0 / size;
        for (int py = 0; py < size; py++)
            for (int px = 0; px < size; px++)
            {
                double r = 0, g = 0, b = 0, a = 0;
                for (int sy = 0; sy < grid; sy++)
                    for (int sx = 0; sx < grid; sx++)
                    {
                        int[] color = Sample((px + (sx + 0.5) / grid) * scale, (py + (sy + 0.5) / grid) * scale);
                        if (color == null) continue;
                        r += color[0]; g += color[1]; b += color[2]; a++;
                    }
                int i = py * data.Stride + px * 4;
                if (a > 0)
                {
                    bytes[i] = (byte)Math.Round(b / a);
                    bytes[i + 1] = (byte)Math.Round(g / a);
                    bytes[i + 2] = (byte)Math.Round(r / a);
                    bytes[i + 3] = (byte)Math.Round(255 * a / (grid * grid));
                }
            }
        Marshal.Copy(bytes, 0, data.Scan0, bytes.Length);
        bitmap.UnlockBits(data);
        return bitmap;
    }

    static byte[] Png(Bitmap bitmap)
    {
        using (var stream = new MemoryStream()) { bitmap.Save(stream, ImageFormat.Png); return stream.ToArray(); }
    }

    static byte[] Dib(Bitmap bitmap)
    {
        int size = bitmap.Width, maskStride = (size + 31) / 32 * 4;
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(40); writer.Write(size); writer.Write(size * 2); writer.Write((short)1); writer.Write((short)32);
            writer.Write(0); writer.Write(size * size * 4 + size * maskStride); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
            var mask = new byte[size * maskStride];
            for (int y = size - 1; y >= 0; y--)
                for (int x = 0; x < size; x++)
                {
                    Color c = bitmap.GetPixel(x, y);
                    writer.Write(c.B); writer.Write(c.G); writer.Write(c.R); writer.Write(c.A);
                    if (c.A == 0) mask[(size - 1 - y) * maskStride + x / 8] |= (byte)(0x80 >> (x % 8));
                }
            writer.Write(mask);
            return stream.ToArray();
        }
    }

    public static void Write(string ico, string png)
    {
        int[] sizes = { 16, 24, 32, 48, 64, 128, 256 };
        var frames = new byte[sizes.Length][];
        for (int i = 0; i < sizes.Length; i++)
            using (Bitmap bitmap = Render(sizes[i]))
            {
                frames[i] = sizes[i] >= 128 ? Png(bitmap) : Dib(bitmap);
                if (sizes[i] == 256) File.WriteAllBytes(png, frames[i]);
            }
        using (var stream = File.Create(ico))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write((short)0); writer.Write((short)1); writer.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                writer.Write((byte)(sizes[i] % 256)); writer.Write((byte)(sizes[i] % 256)); writer.Write((byte)0); writer.Write((byte)0);
                writer.Write((short)1); writer.Write((short)32); writer.Write(frames[i].Length); writer.Write(offset);
                offset += frames[i].Length;
            }
            foreach (byte[] frame in frames) writer.Write(frame);
        }
    }
}
'@
[SessionPurgeIcon]::Write((Join-Path $PSScriptRoot 'SessionPurge.ico'), (Join-Path $PSScriptRoot 'SessionPurge.png'))
Write-Output 'Icon written.'
