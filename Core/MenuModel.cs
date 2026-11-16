using System;
using System.Collections.Generic;
using System.Linq;

namespace KKAO
{
    // Etats d'un outil, du moins au plus important (l'icone montre le plus important)
    internal enum StatusState
    {
        Idle = 0,
        Active = 1,
        Warn = 2,
        Pause = 3,
        Error = 4,
        Rec = 5,
    }

    internal static class StatusStates
    {
        public static bool TryParse(string text, out StatusState state)
        {
            switch ((text ?? "").Trim().ToLowerInvariant())
            {
                case "idle": state = StatusState.Idle; return true;
                case "active": state = StatusState.Active; return true;
                case "warn": state = StatusState.Warn; return true;
                case "pause": state = StatusState.Pause; return true;
                case "error": state = StatusState.Error; return true;
                case "rec": state = StatusState.Rec; return true;
                default: state = StatusState.Idle; return false;
            }
        }

        public static StatusState Max(StatusState a, StatusState b) => (int)a >= (int)b ? a : b;

        public static string Label(StatusState state)
        {
            switch (state)
            {
                case StatusState.Active: return "en cours";
                case StatusState.Warn: return "à vérifier";
                case StatusState.Pause: return "en pause";
                case StatusState.Error: return "erreur";
                case StatusState.Rec: return "enregistrement";
                default: return "au repos";
            }
        }
    }

    // Element du menu publie par un outil (message "menu" du protocole)
    internal sealed class MenuNode
    {
        public const int MaxDepth = 8;
        public const int MaxItemsPerLevel = 400;
        public const int MaxIdLength = 4096;

        public bool IsSeparator { get; private set; }
        public string Id { get; private set; }
        public string Label { get; private set; } = "";
        public string Shortcut { get; private set; }
        public bool Enabled { get; private set; } = true;
        public bool Checked { get; private set; }
        public bool Check { get; private set; }
        public bool Radio { get; private set; }
        public bool Pin { get; private set; }
        public bool Bold { get; private set; }
        public List<MenuNode> Items { get; private set; }

        public bool IsSubmenu => Items != null;
        public bool IsClickable => !IsSeparator && !IsSubmenu && Enabled && !string.IsNullOrEmpty(Id);

        static readonly MenuNode SeparatorNode = new MenuNode { IsSeparator = true };

        // Champs et types inconnus ignores ; separateurs en trop retires
        public static List<MenuNode> ParseList(object items, int depth = 0)
        {
            var result = new List<MenuNode>();
            if (depth > MaxDepth) return result;
            foreach (var raw in Json.GetArray(items).Take(MaxItemsPerLevel))
            {
                if (!(raw is Dictionary<string, object>)) continue;
                var type = Json.GetString(raw, "type");
                if (type == "separator")
                {
                    if (result.Count > 0 && !result[result.Count - 1].IsSeparator) result.Add(SeparatorNode);
                    continue;
                }
                if (type != null && type != "item") continue;

                var node = new MenuNode
                {
                    Label = Json.GetString(raw, "label") ?? "",
                    Enabled = Json.GetBool(raw, "enabled") ?? true,
                };
                var children = Json.Get(raw, "items");
                if (children is object[] || children is System.Collections.ArrayList)
                {
                    node.Items = ParseList(children, depth + 1);
                }
                else
                {
                    var id = Json.GetString(raw, "id");
                    node.Id = string.IsNullOrEmpty(id) || id.Length > MaxIdLength ? null : id;
                    node.Shortcut = Json.GetString(raw, "shortcut");
                    node.Check = Json.GetBool(raw, "check") ?? false;
                    node.Radio = Json.GetBool(raw, "radio") ?? false;
                    node.Checked = Json.GetBool(raw, "checked") ?? false;
                    node.Pin = Json.GetBool(raw, "pin") ?? false;
                    node.Bold = Json.GetBool(raw, "bold") ?? false;
                    if (node.Id == null && node.Label.Length == 0) continue;
                }
                result.Add(node);
            }
            while (result.Count > 0 && result[result.Count - 1].IsSeparator) result.RemoveAt(result.Count - 1);
            return result;
        }

        // Tous les elements (sous-menus compris), en profondeur
        public static IEnumerable<MenuNode> Flatten(IEnumerable<MenuNode> nodes)
        {
            foreach (var node in nodes)
            {
                yield return node;
                if (node.IsSubmenu)
                    foreach (var child in Flatten(node.Items)) yield return child;
            }
        }
    }

    // Comptage (journal et test automatique)
    internal sealed class MenuStats
    {
        public int Leaves, Submenus, Separators, Pinned, Checks, Radios, CheckedCount, Disabled, Bold, WithShortcut;

        public static MenuStats Of(IEnumerable<MenuNode> nodes)
        {
            var stats = new MenuStats();
            foreach (var node in MenuNode.Flatten(nodes))
            {
                if (node.IsSeparator) stats.Separators++;
                else if (node.IsSubmenu) stats.Submenus++;
                else
                {
                    stats.Leaves++;
                    if (node.Pin) stats.Pinned++;
                    if (node.Check) stats.Checks++;
                    if (node.Radio) stats.Radios++;
                    if ((node.Check || node.Radio) && node.Checked) stats.CheckedCount++;
                    if (!node.Enabled) stats.Disabled++;
                    if (node.Bold) stats.Bold++;
                    if (!string.IsNullOrEmpty(node.Shortcut)) stats.WithShortcut++;
                }
            }
            return stats;
        }

        public override string ToString() =>
            Leaves + " éléments, " + Submenus + " sous-menus, " + Separators + " séparateurs, " + Pinned + " épinglés, "
            + Checks + " cases, " + Radios + " radios (" + CheckedCount + " cochés), " + Disabled + " désactivés, " + Bold + " en gras, "
            + WithShortcut + " raccourcis";
    }
}
