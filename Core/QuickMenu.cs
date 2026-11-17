using System;
using System.Collections.Generic;
using System.Linq;

namespace KKAO
{
    // Une entree du menu rapide (haut du menu de l'icone KKAO, page « Actions rapides »)
    internal sealed class QuickEntry
    {
        public bool IsSeparator { get; set; }
        public string ToolId { get; set; }
        public string ActionId { get; set; }
        // Libelle choisi par l'utilisateur (null : celui de l'appli)
        public string Label { get; set; }
        // Dernier libelle connu de l'appli (affiche quand elle est arretee et que son catalogue est inconnu)
        public string KnownLabel { get; set; }

        public static QuickEntry Separator() => new QuickEntry { IsSeparator = true };

        public static QuickEntry FromJson(object node)
        {
            if (!(node is Dictionary<string, object>)) return null;
            if (Json.GetString(node, "type") == "separator") return Separator();
            var tool = Json.GetString(node, "tool");
            var action = Json.GetString(node, "action");
            if (string.IsNullOrEmpty(tool) || string.IsNullOrEmpty(action)) return null;
            return new QuickEntry
            {
                ToolId = tool,
                ActionId = action,
                Label = Blank(Json.GetString(node, "label")),
                KnownLabel = Blank(Json.GetString(node, "knownLabel")),
            };
        }

        public Dictionary<string, object> ToJson()
        {
            if (IsSeparator) return new Dictionary<string, object> { { "type", "separator" } };
            var json = new Dictionary<string, object> { { "tool", ToolId }, { "action", ActionId } };
            if (!string.IsNullOrEmpty(Label)) json["label"] = Label;
            if (!string.IsNullOrEmpty(KnownLabel)) json["knownLabel"] = KnownLabel;
            return json;
        }

        public QuickEntry Clone() => new QuickEntry { IsSeparator = IsSeparator, ToolId = ToolId, ActionId = ActionId, Label = Label, KnownLabel = KnownLabel };

        static string Blank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    // Entree resolue contre l'appli : libelle, raccourci, etat
    internal sealed class ResolvedQuickEntry
    {
        public QuickEntry Entry { get; set; }
        public ToolHost Tool { get; set; }
        public ActionNode Action { get; set; }
        public string Label { get; set; }
        public string Shortcut { get; set; }
        public bool Enabled { get; set; }
        public bool Checked { get; set; }
        public bool Bold { get; set; }
        // L'appli est desinstallee ou l'action n'existe plus dans son catalogue
        public bool Unavailable { get; set; }
    }

    // Menu rapide : personnalise par l'utilisateur, sinon les actions "pin" des applis
    internal sealed class QuickMenuService
    {
        readonly ToolManager _tools;
        readonly KkaoSettings _settings;

        public QuickMenuService(ToolManager tools, KkaoSettings settings)
        {
            _tools = tools;
            _settings = settings;
        }

        public bool IsCustom => _settings.QuickMenu != null;

        public List<QuickEntry> Entries => _settings.QuickMenu ?? Defaults();

        // Par defaut : les "pin" du catalogue de chaque appli (applis v1 : les "pin" du menu, seulement quand elles tournent)
        public List<QuickEntry> Defaults()
        {
            var list = new List<QuickEntry>();
            foreach (var tool in _tools.Tools.Where(t => t.Config.Protocol))
            {
                var pins = _tools.ActionsOf(tool).Where(a => a.Pin).ToList();
                foreach (var action in pins)
                    list.Add(new QuickEntry { ToolId = tool.Id, ActionId = action.Id, KnownLabel = action.Label });
            }
            return list;
        }

        // Enregistre la liste telle que l'utilisateur l'a placee, separateurs compris : un separateur ajoute en fin
        // doit rester pour qu'on puisse le monter a sa place. Le menu (Resolve, MenuBuilder) ignore ceux en tete, en fin ou doubles.
        public void Save(IEnumerable<QuickEntry> entries) => _settings.SetQuickMenu(entries.Where(e => e != null).ToList());

        public void Reset() => _settings.SetQuickMenu(null);

        public List<ResolvedQuickEntry> Resolve()
        {
            var result = new List<ResolvedQuickEntry>();
            foreach (var entry in Entries)
            {
                if (entry.IsSeparator)
                {
                    if (result.Count > 0 && result[result.Count - 1].Entry.IsSeparator == false) result.Add(new ResolvedQuickEntry { Entry = entry });
                    continue;
                }
                result.Add(Resolve(entry));
            }
            while (result.Count > 0 && result[result.Count - 1].Entry.IsSeparator) result.RemoveAt(result.Count - 1);
            return result;
        }

        public ResolvedQuickEntry Resolve(QuickEntry entry)
        {
            var tool = _tools.Find(entry.ToolId);
            var resolved = new ResolvedQuickEntry { Entry = entry, Tool = tool };
            if (tool == null)
            {
                resolved.Label = entry.Label ?? entry.KnownLabel ?? entry.ActionId;
                resolved.Unavailable = true;
                return resolved;
            }
            var actions = _tools.ActionsOf(tool);
            var action = actions.FirstOrDefault(a => a.Id == entry.ActionId);
            // Appli v1 en marche : l'action peut etre un element du menu non epingle
            if (action == null)
            {
                var menuNode = MenuNode.Flatten(tool.Menu).FirstOrDefault(n => n.IsClickable && n.Id == entry.ActionId);
                if (menuNode != null) action = ActionNode.FromMenu(menuNode);
            }
            resolved.Action = action;
            resolved.Label = entry.Label ?? action?.Label ?? entry.KnownLabel ?? entry.ActionId;
            resolved.Shortcut = action?.Shortcut;
            resolved.Checked = action != null && action.Check && action.Checked;
            resolved.Bold = action != null && action.Default;
            // Catalogue connu et action absente : elle n'existe plus ; sinon cliquable (demarrage a la demande)
            var catalogKnown = tool.HasActionsCatalog || _settings.CachedActions(tool.Id).Count > 0;
            resolved.Unavailable = action == null && catalogKnown;
            resolved.Enabled = !resolved.Unavailable && tool.Config.Protocol && (action == null || !tool.IsReady || action.Enabled);
            return resolved;
        }

        // Actions proposables pour une appli (page « Actions rapides »)
        public List<ActionNode> AvailableActions(ToolHost tool)
        {
            var list = _tools.ActionsOf(tool);
            if (!tool.HasActionsCatalog && tool.IsReady)
            {
                // Appli v1 : tous les elements cliquables de son menu (ids stables a verifier par l'appli)
                foreach (var node in MenuNode.Flatten(tool.Menu).Where(n => n.IsClickable))
                    if (list.All(a => a.Id != node.Id)) list.Add(ActionNode.FromMenu(node));
            }
            return list;
        }

        public bool Contains(string toolId, string actionId) =>
            Entries.Any(e => !e.IsSeparator && string.Equals(e.ToolId, toolId, StringComparison.OrdinalIgnoreCase) && e.ActionId == actionId);

        public void Toggle(ToolHost tool, ActionNode action, bool include)
        {
            var entries = Entries.Select(e => e.Clone()).ToList();
            var index = entries.FindIndex(e => !e.IsSeparator && string.Equals(e.ToolId, tool.Id, StringComparison.OrdinalIgnoreCase) && e.ActionId == action.Id);
            if (include && index < 0) entries.Add(new QuickEntry { ToolId = tool.Id, ActionId = action.Id, KnownLabel = action.Label });
            else if (!include && index >= 0) entries.RemoveAt(index);
            else return;
            Save(entries);
        }

        // Desinstallation : les entrees de l'appli disparaissent (un menu personnalise reste personnalise)
        public void ForgetTool(string toolId)
        {
            if (!IsCustom) return;
            Save(Entries.Where(e => e.IsSeparator || !string.Equals(e.ToolId, toolId, StringComparison.OrdinalIgnoreCase)));
        }
    }
}
