using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

// Keep this geometry in sync with assets/pureisp.svg. PNG-compressed ICO frames.
public static class IconBuilder
{
    public static void Main(string[] args)
    {
        int[] sizes = { 16, 20, 24, 32, 48, 64, 128, 256 };
        List<byte[]> frames = new List<byte[]>();
        foreach (int size in sizes)
        using (Bitmap bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb))
        {
            using (Graphics canvas = Graphics.FromImage(bitmap))
            {
                canvas.SmoothingMode = SmoothingMode.AntiAlias;
                canvas.PixelOffsetMode = PixelOffsetMode.HighQuality;
                canvas.ScaleTransform(size / 256f, size / 256f);
                using (GraphicsPath tile = new GraphicsPath())
                using (SolidBrush background = new SolidBrush(Color.FromArgb(17, 29, 48)))
                {
                    tile.AddArc(12, 12, 104, 104, 180, 90);
                    tile.AddArc(140, 12, 104, 104, 270, 90);
                    tile.AddArc(140, 140, 104, 104, 0, 90);
                    tile.AddArc(12, 140, 104, 104, 90, 90);
                    tile.CloseFigure();
                    canvas.FillPath(background, tile);
                }
                Color blue = Color.FromArgb(56, 201, 245);
                Color green = Color.FromArgb(57, 219, 171);
                using (Pen track = new Pen(blue, 22))
                using (GraphicsPath upper = new GraphicsPath())
                {
                    track.StartCap = track.EndCap = LineCap.Round;
                    track.LineJoin = LineJoin.Round;
                    upper.AddLine(74, 186, 74, 144);
                    upper.AddBezier(74, 144, 74, 108, 99, 78, 130, 78);
                    upper.AddLine(130, 78, 184, 78);
                    canvas.DrawPath(track, upper);
                    track.Color = green;
                    canvas.DrawLine(track, 74, 144, 184, 144);
                }
                using (SolidBrush dot = new SolidBrush(blue))
                {
                    canvas.FillEllipse(dot, 170, 64, 28, 28);
                    dot.Color = green;
                    canvas.FillEllipse(dot, 170, 130, 28, 28);
                    dot.Color = Color.FromArgb(237, 248, 255);
                    canvas.FillEllipse(dot, 60, 172, 28, 28);
                }
            }
            using (MemoryStream png = new MemoryStream())
            {
                bitmap.Save(png, ImageFormat.Png);
                frames.Add(png.ToArray());
            }
            if (size == 256) bitmap.Save(Path.Combine(args[0], "pureisp.png"), ImageFormat.Png);
        }
        using (BinaryWriter writer = new BinaryWriter(File.Create(Path.Combine(args[0], "pureisp.ico"))))
        {
            writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                writer.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                writer.Write((byte)0); writer.Write((byte)0);
                writer.Write((ushort)1); writer.Write((ushort)32);
                writer.Write(frames[i].Length); writer.Write(offset);
                offset += frames[i].Length;
            }
            foreach (byte[] frame in frames) writer.Write(frame);
        }
    }
}
