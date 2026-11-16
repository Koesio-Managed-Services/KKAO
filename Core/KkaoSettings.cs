using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace KKAO
{
    // Choix de l'utilisateur : %APPDATA%\KKAO\settings.json (un seul fichier, champs inconnus conserves tels quels)
    //   autostart     { "procedurier": true }          remplace "autostart" de tools.json / du paquet
    //   theme         "system" | "dark" | "light"
    //   onboarding    { "done": true, "step": "theme", "tour": true }
    //   quests        { "install-app": "2026-11-06T10:00:00" }
    //   quickMenu     { "custom": true, "items": [ { "tool": "kalepin", "action": "quickNote", "label": "Quicknote" }, { "type": "separator" } ] }
    //   actionsCache  { "kalepin": [ …dernier catalogue "actions" recu… ] }
    //   icons         { "mode": "launcher" | "all", "tools": { "kalepin": "own" | "none" | "inherit" } }
    //   window        { "left", "top", "width", "height", "maximized" }
    internal sealed class KkaoSettings
    {
        readonly string _path; // null : en memoire (test automatique, rendus d'ecran)
        readonly Dictionary<string, object> _root;

        KkaoSettings(string path, Dictionary<string, object> root)
        {
            _path = path;
            _root = root ?? new Dictionary<string, object>();
        }

        public static string DefaultPath => Path.Combine(Log.DataDir, "settings.json");

        public static KkaoSettings InMemory() => new KkaoSettings(null, null);

        public static KkaoSettings Load(string path)
        {
            Dictionary<string, object> root = null;
            try
            {
                if (File.Exists(path))
                {
                    root = Json.ParseObject(File.ReadAllText(path, Encoding.UTF8));
                    if (root == null)
                    {
                        Log.Write("settings.json illisible : valeurs par defaut (copie gardee)");
                        FileUtil.KeepCorruptCopy(path);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Lecture de settings.json", ex);
            }
            return new KkaoSettings(path, root);
        }

        public bool IsFresh => _root.Count == 0;

        #region Demarrage des applis avec KKAO

        public bool? Autostart(string toolId) => Section("autostart").TryGetValue(toolId, out var value) && value is bool b ? b : (bool?)null;

        public void SetAutostart(string toolId, bool value)
        {
            Section("autostart", create: true)[toolId] = value;
            Save();
        }

        #endregion

        #region Apparence et fenetre

        public string Theme
        {
            get
            {
                var value = Json.GetString(_root, "theme");
                return value == "dark" || value == "light" ? value : "system";
            }
            set
            {
                _root["theme"] = value == "dark" || value == "light" ? value : "system";
                Save();
            }
        }

        public bool OpenAtStartup
        {
            get => Json.GetBool(_root, "openAtStartup") ?? false;
            set
            {
                _root["openAtStartup"] = value;
                Save();
            }
        }

        // Menu de l'icone : un sous-menu par appli (sinon : seulement les actions rapides)
        public bool ShowToolSubmenus
        {
            get => Json.GetBool(_root, "showToolSubmenus") ?? true;
            set
            {
                _root["showToolSubmenus"] = value;
                Save();
            }
        }

        public double[] WindowBounds
        {
            get
            {
                var w = Section("window");
                if (!w.ContainsKey("width")) return null;
                return new[] { Num(w, "left"), Num(w, "top"), Num(w, "width"), Num(w, "height") };
            }
        }

        public bool WindowMaximized => Json.GetBool(_root, "window", "maximized") ?? false;

        public void SetWindow(double left, double top, double width, double height, bool maximized)
        {
            var w = Section("window", create: true);
            w["left"] = Math.Round(left);
            w["top"] = Math.Round(top);
            w["width"] = Math.Round(width);
            w["height"] = Math.Round(height);
            w["maximized"] = maximized;
            Save();
        }

        #endregion

        #region Guide de demarrage et premiers pas

        public bool OnboardingDone
        {
            get => Json.GetBool(_root, "onboarding", "done") ?? false;
            set
            {
                Section("onboarding", create: true)["done"] = value;
                Save();
            }
        }

        // Etape ou reprendre le guide (apres l'installation de KKAO, qui le relance depuis son nouveau dossier)
        public string OnboardingStep
        {
            get => Json.GetString(_root, "onboarding", "step");
            set
            {
                var section = Section("onboarding", create: true);
                if (string.IsNullOrEmpty(value)) section.Remove("step");
                else section["step"] = value;
                Save();
            }
        }

        public bool TourDone
        {
            get => Json.GetBool(_root, "onboarding", "tour") ?? false;
            set
            {
                Section("onboarding", create: true)["tour"] = value;
                Save();
            }
        }

        public bool QuestDone(string id) => Section("quests").ContainsKey(id);

        // true : vient d'etre accompli (premiere fois)
        public bool CompleteQuest(string id)
        {
            var quests = Section("quests", create: true);
            if (quests.ContainsKey(id)) return false;
            quests[id] = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss");
            Save();
            return true;
        }

        public void ResetQuests()
        {
            _root.Remove("quests");
            Save();
        }

        #endregion

        #region Menu rapide

        // null : pas encore personnalise (menu par defaut : actions "pin" des applis)
        public List<QuickEntry> QuickMenu
        {
            get
            {
                if (!(Json.GetBool(_root, "quickMenu", "custom") ?? false)) return null;
                var list = new List<QuickEntry>();
                foreach (var node in Json.GetArray(_root, "quickMenu", "items"))
                {
                    var entry = QuickEntry.FromJson(node);
                    if (entry != null) list.Add(entry);
                }
                return list;
            }
        }

        public void SetQuickMenu(IEnumerable<QuickEntry> entries)
        {
            if (entries == null)
            {
                _root.Remove("quickMenu");
            }
            else
            {
                _root["quickMenu"] = new Dictionary<string, object>
                {
                    { "custom", true },
                    { "items", entries.Select(e => (object)e.ToJson()).ToArray() },
                };
            }
            Save();
        }

        #endregion

        #region Catalogues d'actions gardes (applis arretees)

        public IList<object> CachedActions(string toolId) => Json.GetArray(_root, "actionsCache", toolId);

        public void SetCachedActions(string toolId, IEnumerable<Dictionary<string, object>> actions)
        {
            var cache = Section("actionsCache", create: true);
            var array = actions.Select(a => (object)a).ToArray();
            if (cache.TryGetValue(toolId, out var previous) && Json.Serialize(previous) == Json.Serialize(array)) return;
            cache[toolId] = array;
            Save();
        }

        public void ForgetTool(string toolId)
        {
            Section("actionsCache").Remove(toolId);
            Section("autostart").Remove(toolId);
            if (Json.Get(_root, "icons", "tools") is Dictionary<string, object> icons) icons.Remove(toolId);
            Save();
        }

        // Appli renommee (ancien id dans "aliases") : ses reglages passent au nouvel id, sans ecraser ceux qui existent deja.
        // true : quelque chose a ete repris
        public bool MigrateToolId(string oldId, string newId)
        {
            if (string.IsNullOrEmpty(oldId) || string.IsNullOrEmpty(newId) || string.Equals(oldId, newId, StringComparison.OrdinalIgnoreCase)) return false;
            var changed = false;
            foreach (var section in new[] { Section("autostart"), Section("actionsCache"), Json.Get(_root, "icons", "tools") as Dictionary<string, object> })
            {
                if (section == null || !section.TryGetValue(oldId, out var value)) continue;
                if (!section.ContainsKey(newId)) section[newId] = value;
                section.Remove(oldId);
                changed = true;
            }
            var quick = QuickMenu;
            if (quick != null && quick.Any(e => string.Equals(e.ToolId, oldId, StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var entry in quick.Where(e => string.Equals(e.ToolId, oldId, StringComparison.OrdinalIgnoreCase))) entry.ToolId = newId;
                _root["quickMenu"] = new Dictionary<string, object>
                {
                    { "custom", true },
                    { "items", quick.Select(e => (object)e.ToJson()).ToArray() },
                };
                changed = true;
            }
            if (!changed) return false;
            Log.Write("Réglages de « " + oldId + " » repris sous « " + newId + " » (appli renommée)");
            Save();
            return true;
        }

        #endregion

        #region Icones de la zone de notification

        // "launcher" : une seule icone (KKAO) ; "all" : KKAO + une icone par appli en marche
        public string IconMode
        {
            get => Json.GetString(_root, "icons", "mode") == "all" ? "all" : "launcher";
            set
            {
                Section("icons", create: true)["mode"] = value == "all" ? "all" : "launcher";
                Save();
            }
        }

        // "inherit" (reglage general), "own" (toujours son icone), "none" (jamais)
        public string ToolIconMode(string toolId)
        {
            var value = Json.GetString(_root, "icons", "tools", toolId);
            return value == "own" || value == "none" ? value : "inherit";
        }

        public void SetToolIconMode(string toolId, string mode)
        {
            var icons = Section("icons", create: true);
            if (!(icons.TryGetValue("tools", out var raw) && raw is Dictionary<string, object> tools))
            {
                tools = new Dictionary<string, object>();
                icons["tools"] = tools;
            }
            if (mode == "own" || mode == "none") tools[toolId] = mode;
            else tools.Remove(toolId);
            Save();
        }

        public bool ShowsOwnIcon(string toolId)
        {
            var mode = ToolIconMode(toolId);
            return mode == "own" || (mode == "inherit" && IconMode == "all");
        }

        #endregion

        #region Mises a jour

        // Une bulle par nouvelle version, pas a chaque demarrage : "kkao" ou id d'appli -> derniere version signalee
        public bool MarkUpdateNotified(string id, string version)
        {
            var section = Section("updatesNotified", create: true);
            if (section.TryGetValue(id, out var known) && known as string == version) return false;
            section[id] = version;
            Save();
            return true;
        }

        #endregion

        #region Outils

        Dictionary<string, object> Section(string name, bool create = false)
        {
            if (_root.TryGetValue(name, out var value) && value is Dictionary<string, object> dict) return dict;
            var fresh = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            if (create) _root[name] = fresh;
            return fresh;
        }

        static double Num(Dictionary<string, object> dict, string key)
        {
            if (!dict.TryGetValue(key, out var value) || value == null) return double.NaN;
            try { return Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture); }
            catch { return double.NaN; }
        }

        void Save()
        {
            if (_path == null) return;
            try
            {
                FileUtil.WriteAllTextAtomic(_path, Json.Pretty(Json.Serialize(_root)) + "\n");
            }
            catch (Exception ex)
            {
                Log.Error("Sauvegarde de settings.json", ex);
            }
        }

        #endregion
    }
}
