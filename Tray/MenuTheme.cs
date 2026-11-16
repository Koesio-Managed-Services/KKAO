using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace KKAO
{
    // Menu de l'icone aux couleurs du theme de KKAO (sombre / clair), coins arrondis sur Windows 11
    internal sealed class MenuColors : ProfessionalColorTable
    {
        public MenuColors(bool dark)
        {
            Dark = dark;
            Back = dark ? Color.FromArgb(0x2b, 0x2b, 0x2b) : Color.White;
            Fore = dark ? Color.FromArgb(0xfa, 0xf9, 0xf5) : Color.FromArgb(0x3c, 0x3c, 0x3c);
            Faint = dark ? Color.FromArgb(0x86, 0x83, 0x7b) : Color.FromArgb(0x9a, 0x97, 0x8f);
            Line = dark ? Color.FromArgb(0x44, 0x44, 0x44) : Color.FromArgb(0xe6, 0xe6, 0xe6);
            Hover = dark ? Color.FromArgb(0x4a, 0x35, 0x58) : Color.FromArgb(0xee, 0xe6, 0xf3);
            Accent = dark ? Color.FromArgb(0xc9, 0xa3, 0xe6) : Color.FromArgb(0x6e, 0x39, 0x8e);
            UseSystemColors = false;
        }

        public bool Dark { get; }
        public Color Back { get; }
        public Color Fore { get; }
        public Color Faint { get; }
        public Color Line { get; }
        public Color Hover { get; }
        public Color Accent { get; }

        public override Color ToolStripDropDownBackground => Back;
        public override Color ImageMarginGradientBegin => Back;
        public override Color ImageMarginGradientMiddle => Back;
        public override Color ImageMarginGradientEnd => Back;
        public override Color MenuBorder => Line;
        public override Color MenuItemBorder => Hover;
        public override Color MenuItemSelected => Hover;
        public override Color MenuItemSelectedGradientBegin => Hover;
        public override Color MenuItemSelectedGradientEnd => Hover;
        public override Color SeparatorDark => Line;
        public override Color SeparatorLight => Back;
        public override Color CheckBackground => Back;
        public override Color CheckSelectedBackground => Hover;
        public override Color CheckPressedBackground => Hover;
    }

    internal sealed class MenuRenderer : ToolStripProfessionalRenderer
    {
        readonly MenuColors _colors;

        public MenuRenderer(MenuColors colors) : base(colors)
        {
            _colors = colors;
            RoundedEdges = true;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? _colors.Fore : _colors.Faint;
            // Raccourci affiche a droite en plus discret
            if (e.Item is ToolStripMenuItem item && !string.IsNullOrEmpty(item.ShortcutKeyDisplayString) && e.Text == item.ShortcutKeyDisplayString)
                e.TextColor = _colors.Faint;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = e.Item.Enabled ? _colors.Fore : _colors.Faint;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(4, 1, e.Item.Width - 8, e.Item.Height - 2);
            using (var path = Rounded(r, 5))
            using (var brush = new SolidBrush(_colors.Hover))
                g.FillPath(brush, path);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            var y = e.Item.Height / 2;
            using (var pen = new Pen(_colors.Line)) e.Graphics.DrawLine(pen, 10, y, e.Item.Width - 10, y);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = e.ImageRectangle;
            using (var pen = new Pen(_colors.Accent, 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                g.DrawLines(pen, new[] { new PointF(r.Left + r.Width * 0.18f, r.Top + r.Height * 0.52f), new PointF(r.Left + r.Width * 0.42f, r.Top + r.Height * 0.76f), new PointF(r.Left + r.Width * 0.84f, r.Top + r.Height * 0.26f) });
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using (var pen = new Pen(_colors.Line))
                e.Graphics.DrawRectangle(pen, 0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
        }

        static GraphicsPath Rounded(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            var d = radius * 2;
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        // Applique le theme a un menu (et a ses sous-menus quand ils s'ouvrent)
        public static void Apply(ToolStrip menu, bool dark)
        {
            var colors = new MenuColors(dark);
            menu.Renderer = new MenuRenderer(colors);
            menu.BackColor = colors.Back;
            menu.ForeColor = colors.Fore;
            menu.Font = MenuFont;
            foreach (ToolStripItem item in menu.Items) ApplyItem(item, colors);
        }

        static void ApplyItem(ToolStripItem item, MenuColors colors)
        {
            item.ForeColor = colors.Fore;
            if (!(item is ToolStripMenuItem menuItem)) return;
            menuItem.Padding = new Padding(2, 4, 2, 4);
            if (!menuItem.HasDropDownItems) return;
            var drop = menuItem.DropDown;
            drop.Renderer = new MenuRenderer(colors);
            drop.BackColor = colors.Back;
            drop.Font = MenuFont;
            drop.Opened -= RoundCorners;
            drop.Opened += RoundCorners;
            foreach (ToolStripItem child in menuItem.DropDownItems) ApplyItem(child, colors);
        }

        public static void RoundCorners(object sender, EventArgs e)
        {
            if (!(sender is Control control) || !control.IsHandleCreated) return;
            try
            {
                var preference = NativeMethods.DwmCornerRound;
                NativeMethods.DwmSetWindowAttribute(control.Handle, NativeMethods.DwmWindowCornerPreference, ref preference, 4);
            }
            catch (Exception)
            {
                // Windows 10 : coins droits
            }
        }

        static Font _font;
        static Font MenuFont => _font ?? (_font = new Font("Segoe UI", 9.75f));
    }

    // Images des applis pour les menus et leurs icones (fichier du paquet / tools.json, image du hello, sinon tuile a l'initiale)
    internal static class AppImages
    {
        static readonly Dictionary<string, Image> Cache = new Dictionary<string, Image>(StringComparer.OrdinalIgnoreCase);

        public static Image Get(ToolHost tool, string catalogIcon = null)
        {
            var path = File.Exists(tool.Config.IconPath ?? "") ? tool.Config.IconPath : File.Exists(catalogIcon ?? "") ? catalogIcon : null;
            var key = tool.Id + "|" + (path ?? (tool.HelloIcon != null ? "hello:" + tool.HelloIcon.Length : "tile:" + tool.Name));
            if (Cache.TryGetValue(key, out var cached)) return cached;
            Image image = null;
            try
            {
                if (path != null && path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
                {
                    using (var icon = new Icon(path, 256, 256)) image = icon.ToBitmap();
                }
                else if (path != null)
                {
                    using (var stream = new MemoryStream(File.ReadAllBytes(path))) image = new Bitmap(Image.FromStream(stream));
                }
                else if (tool.HelloIcon != null)
                {
                    using (var stream = new MemoryStream(tool.HelloIcon)) image = new Bitmap(Image.FromStream(stream));
                }
            }
            catch (Exception ex)
            {
                Log.Write("[" + tool.Id + "] image illisible : " + ex.Message);
                image = null;
            }
            image = image ?? IconFactory.LetterTile(tool.Name, 64);
            Cache[key] = image;
            return image;
        }

        public static Image Small(ToolHost tool, int size, string catalogIcon = null)
        {
            var source = Get(tool, catalogIcon);
            var key = "small:" + size + ":" + tool.Id + ":" + source.GetHashCode();
            if (Cache.TryGetValue(key, out var cached)) return cached;
            var bitmap = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(source, new Rectangle(0, 0, size, size));
            }
            Cache[key] = bitmap;
            return bitmap;
        }
    }
}
