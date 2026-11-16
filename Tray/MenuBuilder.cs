using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace KKAO
{
    // Menu de l'icone, reconstruit a chaque ouverture :
    //   Ouvrir KKAO / actions rapides (personnalisables) / un sous-menu par appli (option) / Reglages, Quitter
    internal sealed class MenuBuilder
    {
        readonly ToolManager _tools;
        readonly QuickMenuService _quick;
        Font _boldFont;

        public MenuBuilder(ToolManager tools, QuickMenuService quick)
        {
            _tools = tools;
            _quick = quick ?? (tools == null ? null : new QuickMenuService(tools, tools.Settings));
        }

        public Action OpenWindow { get; set; } = () => { };
        public Action OpenSettings { get; set; } = () => { };
        public Action Quit { get; set; } = () => { };
        // Clic sur une action rapide (outil, action) ; par defaut : envoi direct a l'appli
        public Action<ToolHost, string> RunQuick { get; set; }
        public Func<ToolHost, Image> ImageOf { get; set; }
        public bool ShowToolSubmenus { get; set; } = true;
        // tools.json introuvable ou illisible
        public string ProblemText { get; set; }

        // Texte affiche tel quel : & est le prefixe des raccourcis clavier des menus Windows
        public static string Escape(string text) => (text ?? "").Replace("&", "&&");

        public void Build(ToolStrip menu)
        {
            var old = menu.Items.Cast<ToolStripItem>().ToList();
            menu.SuspendLayout();
            menu.Items.Clear();
            foreach (var item in old) item.Dispose();
            if (_boldFont == null) _boldFont = new Font(menu.Font, FontStyle.Bold);

            var quitting = _tools?.Quitting == true;
            var items = new List<ToolStripItem>();
            var host = Escape(Branding.Name);

            // 1. Ouvrir KKAO
            var open = ActionItem("Ouvrir " + host, OpenWindow);
            open.Font = _boldFont;
            open.Enabled = !quitting;
            items.Add(open);
            items.Add(new ToolStripSeparator());

            // 2. Actions rapides
            if (_tools != null && _quick != null && !quitting)
            {
                var quick = _quick.Resolve().Where(r => r.Entry.IsSeparator || !r.Unavailable).ToList();
                var added = 0;
                foreach (var entry in quick)
                {
                    if (entry.Entry.IsSeparator)
                    {
                        if (added > 0 && !(items[items.Count - 1] is ToolStripSeparator)) items.Add(new ToolStripSeparator());
                        continue;
                    }
                    items.Add(RenderQuick(entry));
                    added++;
                }
                if (added > 0 && !(items[items.Count - 1] is ToolStripSeparator)) items.Add(new ToolStripSeparator());
            }

            // 3. Un sous-menu par appli (un simple element pour les lanceurs sans protocole)
            if (ShowToolSubmenus)
            {
                if (_tools == null || _tools.Tools.Count == 0)
                {
                    if (ProblemText != null) items.Add(new ToolStripMenuItem(Escape(ProblemText)) { Enabled = false });
                }
                else
                {
                    foreach (var tool in _tools.Tools)
                        items.Add(tool.Config.Protocol ? RenderTool(tool, quitting) : RenderLauncher(tool, quitting));
                }
                if (!(items[items.Count - 1] is ToolStripSeparator)) items.Add(new ToolStripSeparator());
            }

            // 4. KKAO
            var settings = ActionItem("Réglages…", OpenSettings);
            settings.Enabled = !quitting;
            items.Add(settings);
            var quit = ActionItem(quitting ? "Fermeture en cours…" : "Quitter " + host, Quit);
            quit.Enabled = !quitting;
            items.Add(quit);

            menu.Items.AddRange(items.ToArray());
            menu.ResumeLayout();
        }

        ToolStripMenuItem RenderQuick(ResolvedQuickEntry entry)
        {
            var item = new ToolStripMenuItem(Escape(entry.Label))
            {
                Enabled = entry.Enabled && !(_tools?.Quitting ?? false),
                Checked = entry.Checked,
                ToolTipText = entry.Tool?.Name,
            };
            if (!string.IsNullOrEmpty(entry.Shortcut))
            {
                item.ShortcutKeyDisplayString = entry.Shortcut;
                item.ShowShortcutKeys = true;
            }
            if (entry.Bold) item.Font = _boldFont;
            if (entry.Tool != null && ImageOf != null && !entry.Checked) item.Image = ImageOf(entry.Tool);
            var tool = entry.Tool;
            var actionId = entry.Entry.ActionId;
            item.Click += (s, e) =>
            {
                if (tool == null) return;
                if (RunQuick != null) RunQuick(tool, actionId);
                else tool.RunAction(actionId, "quick");
            };
            return item;
        }

        // Sous-menu d'une appli (aussi utilise par son icone propre)
        public ToolStripMenuItem RenderTool(ToolHost tool, bool quitting)
        {
            var item = new ToolStripMenuItem(Escape(tool.Name) + Suffix(tool));
            if (ImageOf != null) item.Image = ImageOf(tool);
            item.DropDownItems.AddRange(RenderToolItems(tool, quitting).ToArray());
            return item;
        }

        public List<ToolStripItem> RenderToolItems(ToolHost tool, bool quitting)
        {
            var sub = new List<ToolStripItem>();
            if (tool.IsReady && tool.Menu.Count > 0)
            {
                sub.AddRange(RenderNodes(tool, tool.Menu));
                if (sub.Count > 0) sub.Add(new ToolStripSeparator());
            }
            else if (tool.Crashed && !tool.IsAlive && !string.IsNullOrEmpty(tool.CrashSummary))
            {
                sub.Add(new ToolStripMenuItem(Escape(char.ToUpper(tool.CrashSummary[0]) + tool.CrashSummary.Substring(1))) { Enabled = false });
                sub.Add(new ToolStripSeparator());
            }

            var autostart = new ToolStripMenuItem("Démarrer avec " + Escape(Branding.Name)) { Checked = _tools.EffectiveAutostart(tool) };
            autostart.Click += (s, e) => _tools.SetAutostart(tool, !_tools.EffectiveAutostart(tool));
            sub.Add(autostart);

            if (tool.IsAlive)
            {
                var stopping = tool.RunState == ToolRunState.Stopping;
                var restart = ActionItem("Redémarrer", tool.Restart);
                restart.Enabled = !quitting && !stopping;
                var stop = ActionItem(stopping ? "Arrêt en cours…" : "Arrêter", tool.Stop);
                stop.Enabled = !quitting && !stopping;
                sub.Add(restart);
                sub.Add(stop);
            }
            else if (tool.RunState == ToolRunState.Waiting)
            {
                var now = ActionItem("Démarrer maintenant", () => tool.Start(userInitiated: true));
                now.Enabled = !quitting;
                sub.Add(now);
                sub.Add(ActionItem("Arrêter", tool.Stop));
            }
            else
            {
                var start = ActionItem("Démarrer", () => tool.Start(userInitiated: true));
                start.Enabled = !quitting;
                sub.Add(start);
            }
            return sub;
        }

        static string Suffix(ToolHost tool)
        {
            switch (tool.RunState)
            {
                case ToolRunState.Stopped: return " — arrêté";
                case ToolRunState.Starting:
                case ToolRunState.Waiting: return " — démarrage…";
                case ToolRunState.Stopping: return " — arrêt…";
                default: return tool.IsReady ? "" : " — sans réponse";
            }
        }

        ToolStripMenuItem RenderLauncher(ToolHost tool, bool quitting)
        {
            var item = ActionItem(Escape(tool.Name), tool.LaunchDetached);
            if (ImageOf != null) item.Image = ImageOf(tool);
            item.Enabled = !quitting;
            return item;
        }

        List<ToolStripItem> RenderNodes(ToolHost tool, IEnumerable<MenuNode> nodes)
        {
            var list = new List<ToolStripItem>();
            foreach (var node in nodes)
            {
                if (node.IsSeparator)
                {
                    if (list.Count > 0 && !(list[list.Count - 1] is ToolStripSeparator)) list.Add(new ToolStripSeparator());
                    continue;
                }
                if (node.IsSubmenu)
                {
                    var sub = new ToolStripMenuItem(Escape(node.Label)) { Enabled = node.Enabled };
                    var children = RenderNodes(tool, node.Items);
                    if (children.Count == 0) children.Add(new ToolStripMenuItem("(vide)") { Enabled = false });
                    sub.DropDownItems.AddRange(children.ToArray());
                    list.Add(sub);
                    continue;
                }
                list.Add(RenderLeaf(tool, node));
            }
            while (list.Count > 0 && list[list.Count - 1] is ToolStripSeparator)
            {
                list[list.Count - 1].Dispose();
                list.RemoveAt(list.Count - 1);
            }
            return list;
        }

        ToolStripMenuItem RenderLeaf(ToolHost tool, MenuNode node)
        {
            var item = new ToolStripMenuItem(Escape(node.Label))
            {
                Enabled = node.Enabled && !string.IsNullOrEmpty(node.Id),
                Checked = (node.Check || node.Radio) && node.Checked,
            };
            if (!string.IsNullOrEmpty(node.Shortcut))
            {
                item.ShortcutKeyDisplayString = node.Shortcut;
                item.ShowShortcutKeys = true;
            }
            if (node.Bold) item.Font = _boldFont;
            if (node.Radio && node.Checked) item.Image = IconFactory.RadioDot;
            // L'id est renvoye tel quel, meme si l'outil a publie un autre menu entre-temps
            var id = node.Id;
            if (id != null) item.Click += (s, e) => tool.SendCommand(id, "menu");
            return item;
        }

        static ToolStripMenuItem ActionItem(string escapedText, Action action)
        {
            var item = new ToolStripMenuItem(escapedText);
            item.Click += (s, e) => action();
            return item;
        }
    }
}
