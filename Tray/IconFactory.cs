using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace KKAO
{
    // Icone dessinee a la volee : carre arrondi violet Koesio, "K" blanc, pastille de l'etat le plus important
    internal static class IconFactory
    {
        public static readonly Color Violet = Color.FromArgb(0x6e, 0x39, 0x8e);
        static readonly Dictionary<string, Icon> Cache = new Dictionary<string, Icon>();
        static Bitmap _radioDot;

        public static Color? DotColor(StatusState state)
        {
            switch (state)
            {
                case StatusState.Rec: return Color.FromArgb(0xe9, 0x4b, 0x57);    // corail
                case StatusState.Error: return Color.FromArgb(0xd3, 0x2f, 0x2f);  // rouge
                case StatusState.Pause: return Color.FromArgb(0xf2, 0x8c, 0x28);  // orange
                case StatusState.Warn: return Color.FromArgb(0xf5, 0xb3, 0x01);   // ambre
                case StatusState.Active: return Color.FromArgb(0x41, 0xc0, 0xf0); // bleu clair
                default: return null;
            }
        }

        public static Icon Get(StatusState state, int size)
        {
            var key = state + ":" + size;
            if (Cache.TryGetValue(key, out var icon)) return icon;
            using (var bitmap = Render(state, size))
            {
                var handle = bitmap.GetHicon();
                try
                {
                    using (var temp = Icon.FromHandle(handle)) icon = (Icon)temp.Clone();
                }
                finally
                {
                    NativeMethods.DestroyIcon(handle);
                }
            }
            Cache[key] = icon;
            return icon;
        }

        // Dessin sur une grille de 32 x 32 mise a l'echelle
        public static Bitmap Render(StatusState state, int size)
        {
            var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);
                g.ScaleTransform(size / 32f, size / 32f);

                using (var body = RoundedRect(1f, 1f, 30f, 30f, 7f))
                using (var fill = new SolidBrush(Violet))
                    g.FillPath(fill, body);

                // "K" : fut vertical + deux bras obliques coupes a l'horizontale par la zone de detourage
                var clip = g.Clip;
                g.SetClip(new RectangleF(8.5f, 7f, 17.5f, 18f));
                using (var white = new SolidBrush(Color.White))
                using (var pen = new Pen(Color.White, 4.4f) { StartCap = LineCap.Flat, EndCap = LineCap.Flat })
                {
                    g.FillRectangle(white, 8.5f, 7f, 4.6f, 18f);
                    g.DrawLine(pen, 12.4f, 17.4f, 25.6f, 3.6f);
                    g.DrawLine(pen, 16.0f, 13.6f, 27.0f, 28.6f);
                }
                g.Clip = clip;

                var dot = DotColor(state);
                if (dot.HasValue)
                {
                    using (var ring = new SolidBrush(Color.White)) g.FillEllipse(ring, 17.6f, 17.6f, 14f, 14f);
                    using (var fill = new SolidBrush(dot.Value)) g.FillEllipse(fill, 19.6f, 19.6f, 10f, 10f);
                }
            }
            return bitmap;
        }

        // Icone d'une appli pour la zone de notification : son image (ou une tuile a l'initiale) + pastille de son etat
        public static Icon AppTrayIcon(Image image, string name, StatusState state, int size)
        {
            using (var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(bitmap))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.Clear(Color.Transparent);
                    if (image != null) g.DrawImage(image, new Rectangle(0, 0, size, size));
                    else
                    {
                        using (var tile = LetterTile(name, size)) g.DrawImage(tile, 0, 0, size, size);
                    }
                    var dot = DotColor(state);
                    if (dot.HasValue)
                    {
                        g.ScaleTransform(size / 32f, size / 32f);
                        using (var ring = new SolidBrush(Color.White)) g.FillEllipse(ring, 17.6f, 17.6f, 14f, 14f);
                        using (var fill = new SolidBrush(dot.Value)) g.FillEllipse(fill, 19.6f, 19.6f, 10f, 10f);
                    }
                }
                var handle = bitmap.GetHicon();
                try
                {
                    using (var temp = Icon.FromHandle(handle)) return (Icon)temp.Clone();
                }
                finally
                {
                    NativeMethods.DestroyIcon(handle);
                }
            }
        }

        // Tuile a l'initiale du nom (appli sans image), couleur stable par nom dans la palette Koesio
        public static Bitmap LetterTile(string name, int size)
        {
            var palette = new[] { Violet, Color.FromArgb(0xe9, 0x4b, 0x57), Color.FromArgb(0x41, 0xc0, 0xf0), Color.FromArgb(0x3c, 0x3c, 0x3c) };
            var text = string.IsNullOrWhiteSpace(name) ? "?" : name.Trim().Substring(0, 1).ToUpperInvariant();
            var hash = 0;
            foreach (var c in name ?? "") hash = (hash * 31 + c) & 0x7fffffff;
            var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.Clear(Color.Transparent);
                g.ScaleTransform(size / 32f, size / 32f);
                using (var body = RoundedRect(1f, 1f, 30f, 30f, 7f))
                using (var fill = new SolidBrush(palette[hash % palette.Length]))
                    g.FillPath(fill, body);
                using (var font = new Font("Segoe UI", 17f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var white = new SolidBrush(Color.White))
                using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString(text, font, white, new RectangleF(0, 0.5f, 32, 32), format);
            }
            return bitmap;
        }

        // Icone de remplacement d'une appli qui n'en fournit pas encore (assemblage du paquet)
        public static Bitmap RenderStandIn(string kind, int size)
        {
            if (!string.Equals(kind, "postit", StringComparison.OrdinalIgnoreCase)) return LetterTile(kind, size);
            var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);
                g.ScaleTransform(size / 32f, size / 32f);
                // Post-it jaune a coin plie, trois lignes d'ecriture bleues
                var body = new[] { new PointF(3, 3), new PointF(29, 3), new PointF(29, 21), new PointF(21, 29), new PointF(3, 29) };
                using (var fill = new SolidBrush(Color.FromArgb(255, 213, 74))) g.FillPolygon(fill, body);
                using (var outline = new Pen(Color.FromArgb(150, 110, 0), 1.2f)) g.DrawPolygon(outline, body);
                var fold = new[] { new PointF(29, 21), new PointF(21, 21), new PointF(21, 29) };
                using (var foldFill = new SolidBrush(Color.FromArgb(230, 170, 20))) g.FillPolygon(foldFill, fold);
                using (var line = new Pen(Color.FromArgb(0, 97, 254), 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                {
                    g.DrawLine(line, 8, 10, 24, 10);
                    g.DrawLine(line, 8, 15.5f, 24, 15.5f);
                    g.DrawLine(line, 8, 21, 16, 21);
                }
            }
            return bitmap;
        }

        // Fichier .ico multi-tailles (16 a 256 px) de l'icone KKAO : KKAO.exe --export-icon <fichier>
        public static void WriteIco(string path, Func<int, Bitmap> render, int[] sizes)
        {
            var images = new List<byte[]>();
            foreach (var size in sizes)
            {
                using (var bitmap = render(size))
                    images.Add(size >= 128 ? Png(bitmap) : Dib(bitmap));
            }
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write((short)0);
                writer.Write((short)1);
                writer.Write((short)sizes.Length);
                var offset = 6 + 16 * sizes.Length;
                for (var i = 0; i < sizes.Length; i++)
                {
                    var s = sizes[i];
                    writer.Write((byte)(s >= 256 ? 0 : s));
                    writer.Write((byte)(s >= 256 ? 0 : s));
                    writer.Write((byte)0);
                    writer.Write((byte)0);
                    writer.Write((short)1);
                    writer.Write((short)32);
                    writer.Write(images[i].Length);
                    writer.Write(offset);
                    offset += images[i].Length;
                }
                foreach (var image in images) writer.Write(image);
            }
        }

        static byte[] Png(Bitmap bitmap)
        {
            using (var memory = new MemoryStream())
            {
                bitmap.Save(memory, ImageFormat.Png);
                return memory.ToArray();
            }
        }

        // Image d'icone classique : BITMAPINFOHEADER (hauteur doublee) + pixels BGRA de bas en haut + masque ET
        static byte[] Dib(Bitmap bitmap)
        {
            var size = bitmap.Width;
            using (var memory = new MemoryStream())
            using (var writer = new BinaryWriter(memory))
            {
                writer.Write(40);
                writer.Write(size);
                writer.Write(size * 2);
                writer.Write((short)1);
                writer.Write((short)32);
                writer.Write(0);
                writer.Write(0);
                writer.Write(0);
                writer.Write(0);
                writer.Write(0);
                writer.Write(0);
                for (var y = size - 1; y >= 0; y--)
                    for (var x = 0; x < size; x++)
                    {
                        var c = bitmap.GetPixel(x, y);
                        writer.Write(c.B);
                        writer.Write(c.G);
                        writer.Write(c.R);
                        writer.Write(c.A);
                    }
                var maskStride = ((size + 31) / 32) * 4;
                for (var y = size - 1; y >= 0; y--)
                {
                    var row = new byte[maskStride];
                    for (var x = 0; x < size; x++)
                        if (bitmap.GetPixel(x, y).A == 0) row[x / 8] |= (byte)(0x80 >> (x % 8));
                    writer.Write(row);
                }
                return memory.ToArray();
            }
        }

        // Puce des elements "radio" coches du menu
        public static Image RadioDot
        {
            get
            {
                if (_radioDot != null) return _radioDot;
                const int size = 16;
                var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bitmap))
                using (var brush = new SolidBrush(SystemColors.MenuText))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    g.FillEllipse(brush, 5f, 5f, 6f, 6f);
                }
                return _radioDot = bitmap;
            }
        }

        static GraphicsPath RoundedRect(float x, float y, float width, float height, float radius)
        {
            var path = new GraphicsPath();
            var d = radius * 2;
            path.AddArc(x, y, d, d, 180, 90);
            path.AddArc(x + width - d, y, d, d, 270, 90);
            path.AddArc(x + width - d, y + height - d, d, d, 0, 90);
            path.AddArc(x, y + height - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
