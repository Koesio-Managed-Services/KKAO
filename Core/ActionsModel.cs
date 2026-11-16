using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace KKAO
{
    // Action du catalogue d'une appli (message "actions" du protocole v2) : id stable, utilisable dans le menu rapide
    internal sealed class ActionNode
    {
        public const int MaxActions = 200;

        public string Id { get; private set; }
        public string Label { get; private set; } = "";
        public string Shortcut { get; private set; }
        public string ShortcutError { get; private set; }
        public string Description { get; private set; }
        public bool Enabled { get; private set; } = true;
        public bool Check { get; private set; }
        public bool Checked { get; private set; }
        public bool Default { get; private set; }
        public bool Pin { get; private set; }

        public static List<ActionNode> ParseList(object items)
        {
            var result = new List<ActionNode>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var hasDefault = false;
            foreach (var raw in Json.GetArray(items).Take(MaxActions))
            {
                var node = Parse(raw);
                if (node == null || !seen.Add(node.Id)) continue;
                // Une seule action par defaut (la premiere)
                if (node.Default && hasDefault) node.Default = false;
                hasDefault |= node.Default;
                result.Add(node);
            }
            return result;
        }

        public static ActionNode Parse(object raw)
        {
            if (!(raw is Dictionary<string, object>)) return null;
            var id = Json.GetString(raw, "id")?.Trim();
            var label = Json.GetString(raw, "label")?.Trim();
            if (string.IsNullOrEmpty(id) || !ToolsFile.ValidId.IsMatch(id) || string.IsNullOrEmpty(label)) return null;
            return new ActionNode
            {
                Id = id,
                Label = Log.Truncate(label, 120),
                Shortcut = Blank(Json.GetString(raw, "shortcut")),
                ShortcutError = Blank(Json.GetString(raw, "shortcutError")),
                Description = Blank(Json.GetString(raw, "description")),
                Enabled = Json.GetBool(raw, "enabled") ?? true,
                Check = Json.GetBool(raw, "check") ?? false,
                Checked = Json.GetBool(raw, "checked") ?? false,
                Default = Json.GetBool(raw, "default") ?? false,
                Pin = Json.GetBool(raw, "pin") ?? false,
            };
        }

        // Pour le cache de settings.json (catalogue visible quand l'appli est arretee)
        public Dictionary<string, object> ToJson()
        {
            var json = new Dictionary<string, object> { { "id", Id }, { "label", Label } };
            if (Shortcut != null) json["shortcut"] = Shortcut;
            if (Description != null) json["description"] = Description;
            if (Check) json["check"] = true;
            if (Default) json["default"] = true;
            if (Pin) json["pin"] = true;
            return json;
        }

        // Action deduite d'un element "pin" du menu d'une appli v1 (sans catalogue) ; "bold" = action principale
        public static ActionNode FromMenu(MenuNode node) => new ActionNode
        {
            Id = node.Id,
            Label = node.Label,
            Shortcut = node.Shortcut,
            Enabled = node.Enabled,
            Check = node.Check,
            Checked = node.Checked,
            Pin = node.Pin,
            Default = node.Bold,
        };

        static string Blank(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    // Reglages exposes par une appli (message "settings" du protocole v2)
    internal sealed class SettingsSection
    {
        public string Title { get; set; }
        public List<SettingItem> Items { get; } = new List<SettingItem>();

        public const int MaxItems = 300;

        public static List<SettingsSection> ParseList(object sections)
        {
            var result = new List<SettingsSection>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var count = 0;
            foreach (var raw in Json.GetArray(sections))
            {
                if (!(raw is Dictionary<string, object>)) continue;
                var section = new SettingsSection { Title = Log.Truncate(Json.GetString(raw, "title") ?? "", 80) };
                foreach (var rawItem in Json.GetArray(raw, "items"))
                {
                    if (count >= MaxItems) break;
                    var item = SettingItem.Parse(rawItem);
                    if (item == null || !seen.Add(item.Id)) continue;
                    section.Items.Add(item);
                    count++;
                }
                if (section.Items.Count > 0) result.Add(section);
            }
            return result;
        }
    }

    internal sealed class SettingOption
    {
        public string Value { get; set; }
        public string Label { get; set; }
    }

    internal sealed class SettingItem
    {
        public static readonly string[] KnownTypes = { "toggle", "text", "number", "choice", "hotkey", "folder", "info", "button" };

        public string Id { get; private set; }
        public string Label { get; private set; }
        // toggle, text, number, choice, hotkey, folder, info, button ; type inconnu -> info (lecture seule)
        public string Type { get; private set; }
        public object Value { get; private set; }
        public string Description { get; private set; }
        public bool Enabled { get; private set; } = true;
        public string Error { get; private set; }
        public string Placeholder { get; private set; }
        public string Action { get; private set; }
        public double? Min { get; private set; }
        public double? Max { get; private set; }
        public double? Step { get; private set; }
        public List<SettingOption> Options { get; } = new List<SettingOption>();

        public bool IsReadOnly => Type == "info" || !Enabled;

        public string ValueText => Value == null ? "" : Value is bool b ? (b ? "true" : "false") : Convert.ToString(Value, CultureInfo.InvariantCulture);

        public bool ValueBool => Value is bool b && b;

        public static SettingItem Parse(object raw)
        {
            if (!(raw is Dictionary<string, object>)) return null;
            var id = Json.GetString(raw, "id")?.Trim();
            var label = Json.GetString(raw, "label")?.Trim();
            if (string.IsNullOrEmpty(id) || !ToolsFile.ValidId.IsMatch(id) || string.IsNullOrEmpty(label)) return null;
            var type = (Json.GetString(raw, "type") ?? "info").Trim().ToLowerInvariant();
            if (!KnownTypes.Contains(type)) type = "info";
            var item = new SettingItem
            {
                Id = id,
                Label = Log.Truncate(label, 120),
                Type = type,
                Value = Json.Get(raw, "value"),
                Description = Json.GetString(raw, "description"),
                Enabled = Json.GetBool(raw, "enabled") ?? true,
                Error = Json.GetString(raw, "error"),
                Placeholder = Json.GetString(raw, "placeholder"),
                Action = Json.GetString(raw, "action"),
                Min = ToDouble(Json.Get(raw, "min")),
                Max = ToDouble(Json.Get(raw, "max")),
                Step = ToDouble(Json.Get(raw, "step")),
            };
            if (item.Value is Dictionary<string, object> || item.Value is object[]) item.Value = null;
            foreach (var option in Json.GetArray(raw, "options"))
            {
                var value = Json.GetString(option, "value");
                if (value == null) continue;
                item.Options.Add(new SettingOption { Value = value, Label = Json.GetString(option, "label") ?? value });
            }
            if (type == "choice" && item.Options.Count == 0) item.Type = "info";
            if (type == "button" && string.IsNullOrEmpty(item.Action)) item.Type = "info";
            return item;
        }

        static double? ToDouble(object value)
        {
            if (value == null) return null;
            try { return Convert.ToDouble(value, CultureInfo.InvariantCulture); }
            catch { return null; }
        }
    }
}
