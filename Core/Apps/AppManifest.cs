using System;
using System.Collections.Generic;
using System.Globalization;

namespace KKAO.Apps
{
    // kkao-app.json : a la racine du zip d'une appli, et de son dossier une fois installee (PROTOCOLE.md, « Paquet d'appli »)
    internal sealed class AppManifest
    {
        public const string FileName = "kkao-app.json";
        public const int CurrentFormat = 1;

        public int Format { get; set; } = CurrentFormat;
        public string Id { get; set; }
        public string Name { get; set; }
        public string Version { get; set; }
        public string Publisher { get; set; }
        public string Exe { get; set; }
        public List<string> Args { get; } = new List<string>();
        public string Cwd { get; set; }
        public string Icon { get; set; }
        public bool Protocol { get; set; } = true;
        public bool Autostart { get; set; }
        public string Summary { get; set; }
        public string Description { get; set; }
        // Anciens id de l'appli (renommage) : reglages repris par KKAO
        public List<string> Aliases { get; } = new List<string>();

        public static AppManifest Parse(string json, out string problem)
        {
            problem = null;
            var root = Json.ParseObject(json);
            if (root == null)
            {
                problem = "kkao-app.json illisible";
                return null;
            }
            var manifest = new AppManifest
            {
                Format = Json.GetInt(root, "format") ?? CurrentFormat,
                Id = Json.GetString(root, "id")?.Trim(),
                Name = Json.GetString(root, "name")?.Trim(),
                Version = Json.GetString(root, "version")?.Trim(),
                Publisher = Json.GetString(root, "publisher")?.Trim(),
                Exe = Json.GetString(root, "exe")?.Trim(),
                Cwd = Json.GetString(root, "cwd")?.Trim(),
                Icon = Json.GetString(root, "icon")?.Trim(),
                Protocol = Json.GetBool(root, "protocol") ?? true,
                Autostart = Json.GetBool(root, "autostart") ?? false,
                Summary = Json.GetString(root, "summary"),
                Description = Json.GetString(root, "description"),
            };
            foreach (var arg in Json.GetArray(root, "args"))
                if (arg != null) manifest.Args.Add(Convert.ToString(arg, CultureInfo.InvariantCulture));
            foreach (var alias in Json.GetArray(root, "aliases"))
                if (alias is string a && ToolsFile.ValidId.IsMatch(a.Trim())) manifest.Aliases.Add(a.Trim());

            if (string.IsNullOrEmpty(manifest.Id) || !ToolsFile.ValidId.IsMatch(manifest.Id))
                problem = "id absent ou invalide (« " + manifest.Id + " »)";
            else if (string.IsNullOrEmpty(manifest.Exe))
                problem = "« exe » manquant";
            else if (manifest.Format > CurrentFormat)
                problem = "format " + manifest.Format + " trop récent pour ce " + Branding.Name + " (mettez " + Branding.Name + " à jour)";
            return problem == null ? manifest : null;
        }

        public Dictionary<string, object> ToJson()
        {
            var json = new Dictionary<string, object>
            {
                { "format", Format },
                { "id", Id },
                { "name", Name },
                { "version", Version },
                { "publisher", Publisher },
                { "exe", Exe },
                { "args", Args.ToArray() },
                { "protocol", Protocol },
                { "autostart", Autostart },
            };
            if (!string.IsNullOrEmpty(Cwd)) json["cwd"] = Cwd;
            if (!string.IsNullOrEmpty(Icon)) json["icon"] = Icon;
            if (!string.IsNullOrEmpty(Summary)) json["summary"] = Summary;
            if (!string.IsNullOrEmpty(Description)) json["description"] = Description;
            if (Aliases.Count > 0) json["aliases"] = Aliases.ToArray();
            return json;
        }

        // Chemins relatifs au dossier d'installation de l'appli (exe, cwd, icon, arguments . / .\…)
        public ToolConfig ToToolConfig(string appDir)
        {
            var tool = new ToolConfig
            {
                Id = Id,
                Name = string.IsNullOrEmpty(Name) ? Id : Name,
                ConfigName = string.IsNullOrEmpty(Name) ? null : Name,
                ExeRaw = Exe,
                Autostart = Autostart,
                Protocol = Protocol,
                Source = ToolSource.Installed,
                AppDir = appDir,
                Version = Version,
                Publisher = Publisher,
                Summary = Summary,
                Description = Description,
            };
            tool.Exe = PathResolver.ResolveExe(Exe, appDir);
            tool.Aliases.AddRange(Aliases);
            foreach (var arg in Args) tool.Args.Add(PathResolver.ResolveArg(arg, appDir));
            tool.Cwd = !string.IsNullOrWhiteSpace(Cwd) ? PathResolver.ResolvePath(Cwd, appDir) : appDir;
            tool.IconPath = string.IsNullOrWhiteSpace(Icon) ? null : PathResolver.ResolvePath(Icon, appDir);
            return tool;
        }
    }
}
