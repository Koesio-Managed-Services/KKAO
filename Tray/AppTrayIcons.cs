using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace KKAO
{
    // Une icone par appli pres de l'horloge (reglage « Une icone par appli », ou exception de l'appli) — dessinee par KKAO :
    // image de l'appli + pastille de son etat, son menu, clic gauche = son action par defaut. Les applis n'ont rien a coder.
    internal sealed class AppTrayIcons : IDisposable
    {
        readonly ToolManager _tools;
        readonly KkaoSettings _settings;
        readonly MenuBuilder _builder;
        readonly Func<ToolHost, string> _catalogIcon;
        readonly Dictionary<string, Entry> _icons = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        readonly int _size = Math.Max(16, SystemInformation.SmallIconSize.Width);

        sealed class Entry
        {
            public NotifyIcon Icon;
            public ContextMenuStrip Menu;
            public string Key;
            public string Text;
        }

        public AppTrayIcons(ToolManager tools, KkaoSettings settings, MenuBuilder builder, Func<ToolHost, string> catalogIcon)
        {
            _tools = tools;
            _settings = settings;
            _builder = builder;
            _catalogIcon = catalogIcon;
        }

        public bool Dark { get; set; } = true;

        // Apres chaque changement (etat d'une appli, reglage d'icones)
        public void Sync()
        {
            var wanted = _tools.Quitting ? new List<ToolHost>()
                : _tools.Tools.Where(t => t.Config.Protocol && t.IsReady && _settings.ShowsOwnIcon(t.Id)).ToList();
            foreach (var gone in _icons.Keys.Where(id => wanted.All(t => !string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase))).ToList())
            {
                var entry = _icons[gone];
                entry.Icon.Visible = false;
                entry.Icon.Dispose();
                entry.Menu.Dispose();
                _icons.Remove(gone);
            }
            foreach (var tool in wanted)
            {
                if (!_icons.TryGetValue(tool.Id, out var entry))
                {
                    entry = Create(tool);
                    _icons[tool.Id] = entry;
                }
                Update(tool, entry);
            }
        }

        Entry Create(ToolHost tool)
        {
            var entry = new Entry { Menu = new ContextMenuStrip() };
            entry.Menu.Opening += (s, e) =>
            {
                var current = _tools.Find(tool.Id);
                if (current == null) return;
                var old = entry.Menu.Items.Cast<ToolStripItem>().ToList();
                entry.Menu.Items.Clear();
                foreach (var item in old) item.Dispose();
                var title = new ToolStripMenuItem(MenuBuilder.Escape(current.Name)) { Enabled = false };
                entry.Menu.Items.Add(title);
                entry.Menu.Items.Add(new ToolStripSeparator());
                entry.Menu.Items.AddRange(_builder.RenderToolItems(current, _tools.Quitting).ToArray());
                MenuRenderer.Apply(entry.Menu, Dark);
                e.Cancel = false;
            };
            entry.Menu.Opened += MenuRenderer.RoundCorners;
            entry.Icon = new NotifyIcon { ContextMenuStrip = entry.Menu, Visible = false };
            entry.Icon.MouseClick += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                var current = _tools.Find(tool.Id);
                var action = current?.Actions.FirstOrDefault(a => a.Default);
                if (current != null && action != null) current.RunAction(action.Id, "icon");
                else ShowMenu(entry.Icon);
            };
            return entry;
        }

        void Update(ToolHost tool, Entry entry)
        {
            var image = AppImages.Get(tool, _catalogIcon?.Invoke(tool));
            var key = tool.Status + "|" + image.GetHashCode();
            if (entry.Key != key)
            {
                var old = entry.Icon.Icon;
                entry.Icon.Icon = IconFactory.AppTrayIcon(image, tool.Name, tool.Status, _size);
                old?.Dispose();
                entry.Key = key;
            }
            var text = ToolManager.Fit(string.IsNullOrWhiteSpace(tool.StatusTooltip) ? tool.Name : tool.StatusTooltip, 63).Replace("&", "＆");
            if (entry.Text != text)
            {
                entry.Icon.Text = text;
                entry.Text = text;
            }
            if (!entry.Icon.Visible) entry.Icon.Visible = true;
        }

        // Notification d'une appli qui a son icone : la bulle part de son icone
        public bool TryBalloon(ToolHost tool, string title, string text, ToolTipIcon icon)
        {
            if (tool == null || !_icons.TryGetValue(tool.Id, out var entry) || !entry.Icon.Visible) return false;
            try
            {
                entry.Icon.ShowBalloonTip(6000, ToolManager.Fit(string.IsNullOrWhiteSpace(title) ? tool.Name : title, 63), ToolManager.Fit(string.IsNullOrWhiteSpace(text) ? " " : text, 255), icon);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        static void ShowMenu(NotifyIcon icon)
        {
            try
            {
                var method = typeof(NotifyIcon).GetMethod("ShowContextMenu", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                method?.Invoke(icon, null);
            }
            catch (Exception ex)
            {
                Log.Error("Menu de l'icone d'une appli", ex);
            }
        }

        public void Dispose()
        {
            foreach (var entry in _icons.Values)
            {
                entry.Icon.Visible = false;
                entry.Icon.Dispose();
                entry.Menu.Dispose();
            }
            _icons.Clear();
        }
    }
}
