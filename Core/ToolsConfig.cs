using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace KKAO
{
    // D'ou vient une appli : tools.json (developpement, outils maison) ou installee par KKAO depuis un paquet
    internal enum ToolSource
    {
        ToolsFile,
        Installed,
    }

    // Un outil declare dans tools.json ou installe par KKAO (chemins deja resolus)
    internal sealed class ToolConfig
    {
        public string Id { get; set; }
        // Nom affiche : celui de tools.json / du paquet, sinon l'id (ToolHost prefere celui du hello si celui-ci manque)
        public string Name { get; set; }
        // Nom donne explicitement par tools.json ou le paquet (null : pas de nom impose)
        public string ConfigName { get; set; }
        public string ExeRaw { get; set; }
        public string Exe { get; set; }
        public string Cwd { get; set; }
        public List<string> Args { get; } = new List<string>();
        public bool Autostart { get; set; }
        public bool Protocol { get; set; } = true;
        // Image de l'appli (.png / .ico), prioritaire sur celle du hello
        public string IconPath { get; set; }
        // Anciens id : reglages repris
        public List<string> Aliases { get; } = new List<string>();
        public ToolSource Source { get; set; } = ToolSource.ToolsFile;
        // Applis installees : dossier, version et textes du paquet
        public string AppDir { get; set; }
        public string Version { get; set; }
        public string Publisher { get; set; }
        public string Summary { get; set; }
        public string Description { get; set; }
    }

    internal sealed class ToolsFile
    {
        public static readonly Regex ValidId = new Regex("^[A-Za-z0-9._-]{1,64}$");

        public string FilePath { get; private set; }
        public string Dir { get; private set; }
        public List<ToolConfig> Tools { get; } = new List<ToolConfig>();
        public List<string> Problems { get; } = new List<string>();

        // --tools <fichier>, sinon tools.json a cote de KKAO.exe, sinon dans le dossier parent
        public static string Locate(string explicitPath, string exeDir)
        {
            if (!string.IsNullOrWhiteSpace(explicitPath)) return Path.GetFullPath(explicitPath);
            var here = Path.Combine(exeDir, "tools.json");
            if (File.Exists(here)) return here;
            var parent = Directory.GetParent(exeDir)?.FullName;
            if (parent != null)
            {
                var above = Path.Combine(parent, "tools.json");
                if (File.Exists(above)) return above;
            }
            return null;
        }

        public static ToolsFile Load(string path)
        {
            var file = new ToolsFile { FilePath = path, Dir = Path.GetDirectoryName(path) };
            string text;
            try
            {
                text = File.ReadAllText(path, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                file.Problems.Add("lecture impossible : " + ex.Message);
                return file;
            }
            var root = Json.ParseObject(text);
            if (root == null)
            {
                file.Problems.Add("JSON invalide");
                return file;
            }

            foreach (var node in Json.GetArray(root, "tools"))
            {
                var id = Json.GetString(node, "id")?.Trim();
                if (string.IsNullOrEmpty(id) || !ValidId.IsMatch(id))
                {
                    file.Problems.Add("outil ignoré : id absent ou invalide (« " + id + " » ; lettres, chiffres, . _ -)");
                    continue;
                }
                if (file.Tools.Any(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase)))
                {
                    file.Problems.Add("outil ignoré : id « " + id + " » en double");
                    continue;
                }
                var name = Json.GetString(node, "name")?.Trim();
                var tool = new ToolConfig
                {
                    Id = id,
                    Name = string.IsNullOrEmpty(name) ? id : name,
                    ConfigName = string.IsNullOrEmpty(name) ? null : name,
                    ExeRaw = Json.GetString(node, "exe"),
                    Autostart = Json.GetBool(node, "autostart") ?? false,
                    Protocol = Json.GetBool(node, "protocol") ?? true,
                    Summary = Json.GetString(node, "summary"),
                    Description = Json.GetString(node, "description"),
                };
                var icon = Json.GetString(node, "icon");
                if (!string.IsNullOrWhiteSpace(icon)) tool.IconPath = PathResolver.ResolvePath(icon, file.Dir);
                foreach (var alias in Json.GetArray(node, "aliases"))
                {
                    var aliasText = alias as string;
                    if (!string.IsNullOrWhiteSpace(aliasText) && ValidId.IsMatch(aliasText.Trim())) tool.Aliases.Add(aliasText.Trim());
                }
                tool.Exe = PathResolver.ResolveExe(tool.ExeRaw, file.Dir);
                foreach (var arg in Json.GetArray(node, "args"))
                    if (arg != null) tool.Args.Add(PathResolver.ResolveArg(Convert.ToString(arg, System.Globalization.CultureInfo.InvariantCulture), file.Dir));
                var cwd = Json.GetString(node, "cwd");
                tool.Cwd = !string.IsNullOrWhiteSpace(cwd) ? PathResolver.ResolvePath(cwd, file.Dir)
                    : tool.Exe != null && Path.IsPathRooted(tool.Exe) ? Path.GetDirectoryName(tool.Exe)
                    : file.Dir;
                if (tool.Exe == null) file.Problems.Add(tool.Name + " : « exe » manquant");
                file.Tools.Add(tool);
            }
            if (file.Tools.Count == 0 && file.Problems.Count == 0) file.Problems.Add("aucun outil dans « tools »");
            return file;
        }
    }

    // Chemins de tools.json : relatifs au dossier de tools.json (exe, cwd, arguments qui commencent par .\ ou ..\)
    internal static class PathResolver
    {
        public static bool IsRelativeMarker(string value) =>
            value == "." || value == ".." ||
            value.StartsWith(@".\", StringComparison.Ordinal) || value.StartsWith(@"..\", StringComparison.Ordinal) ||
            value.StartsWith("./", StringComparison.Ordinal) || value.StartsWith("../", StringComparison.Ordinal);

        public static string ResolvePath(string raw, string baseDir)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var expanded = Environment.ExpandEnvironmentVariables(raw.Trim());
            try
            {
                return Path.GetFullPath(Path.IsPathRooted(expanded) ? expanded : Path.Combine(baseDir, expanded));
            }
            catch (Exception)
            {
                return expanded;
            }
        }

        public static string ResolveArg(string raw, string baseDir) => raw != null && IsRelativeMarker(raw) ? ResolvePath(raw, baseDir) : raw;

        // Un nom seul ("node") est d'abord cherche a cote de tools.json, puis dans le PATH
        public static string ResolveExe(string raw, string baseDir)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var expanded = Environment.ExpandEnvironmentVariables(raw.Trim());
            var local = ResolvePath(expanded, baseDir);
            var bareName = expanded.IndexOfAny(new[] { '\\', '/' }) < 0 && !Path.IsPathRooted(expanded);
            if (!bareName || File.Exists(local)) return local;
            return FindOnPath(expanded) ?? KnownLocation(expanded) ?? local;
        }

        public static string FindOnPath(string name)
        {
            var fileName = Path.HasExtension(name) ? name : name + ".exe";
            foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                var dir = entry.Trim().Trim('"');
                if (dir.Length == 0) continue;
                try
                {
                    var candidate = Path.Combine(Environment.ExpandEnvironmentVariables(dir), fileName);
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
                catch (Exception)
                {
                    // entree du PATH invalide : ignoree
                }
            }
            return null;
        }

        // Node installe pour tous les utilisateurs mais absent du PATH de la session
        static string KnownLocation(string name)
        {
            if (!name.Equals("node", StringComparison.OrdinalIgnoreCase) && !name.Equals("node.exe", StringComparison.OrdinalIgnoreCase)) return null;
            foreach (var root in new[] { Environment.GetEnvironmentVariable("ProgramW6432"), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
            {
                if (string.IsNullOrEmpty(root)) continue;
                var candidate = Path.Combine(root, "nodejs", "node.exe");
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }
    }

    // Ligne de commande Windows : regles de CommandLineToArgvW / CRT (guillemets et barres obliques inverses)
    internal static class CommandLine
    {
        public static string Quote(string arg)
        {
            if (arg == null) arg = "";
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0) return arg;
            var sb = new StringBuilder(arg.Length + 2);
            sb.Append('"');
            for (var i = 0; i < arg.Length; i++)
            {
                var backslashes = 0;
                while (i < arg.Length && arg[i] == '\\')
                {
                    backslashes++;
                    i++;
                }
                if (i == arg.Length)
                {
                    sb.Append('\\', backslashes * 2);
                    break;
                }
                if (arg[i] == '"') sb.Append('\\', backslashes * 2 + 1).Append('"');
                else sb.Append('\\', backslashes).Append(arg[i]);
            }
            sb.Append('"');
            return sb.ToString();
        }

        public static string Join(IEnumerable<string> args) => string.Join(" ", args.Select(Quote));
    }
}
