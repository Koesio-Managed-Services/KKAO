using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace KKAO.Apps
{
    // Une appli proposee par un catalogue : embarquee dans le dossier de KKAO (paquet local) ou en ligne (a telecharger)
    internal sealed class CatalogApp
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Version { get; set; }
        public string Publisher { get; set; }
        public string Summary { get; set; }
        public string Description { get; set; }
        public List<string> Highlights { get; } = new List<string>();
        // Fichiers locaux (chemins complets) : icone (embarquee ou mise en cache) et paquet embarque
        public string IconPath { get; set; }
        public string PackagePath { get; set; }
        // En ligne : adresse du paquet et de l'icone (release GitHub)
        public string Url { get; set; }
        public string IconUrl { get; set; }
        public string Sha256 { get; set; }
        public long Size { get; set; }
        public long InstalledSize { get; set; }
        // Anciens id (appli renommee) : une appli installee sous un de ces id est proposee a la mise a jour
        public List<string> Aliases { get; } = new List<string>();

        public bool HasPackage => !string.IsNullOrEmpty(PackagePath) && File.Exists(PackagePath);

        // Telechargeable : adresse et empreinte connues (jamais d'installation sans empreinte a verifier)
        public bool HasDownload => !string.IsNullOrEmpty(Url) && !string.IsNullOrEmpty(Sha256);

        public bool CanInstall => HasPackage || HasDownload;

        public bool NeedsDownload => !HasPackage && HasDownload;

        public bool IsAlias(string id) => Aliases.Any(a => string.Equals(a, id, StringComparison.OrdinalIgnoreCase));

        public CatalogApp Clone()
        {
            var fresh = new CatalogApp
            {
                Id = Id, Name = Name, Version = Version, Publisher = Publisher, Summary = Summary, Description = Description,
                IconPath = IconPath, PackagePath = PackagePath, Url = Url, IconUrl = IconUrl, Sha256 = Sha256, Size = Size, InstalledSize = InstalledSize,
            };
            fresh.Highlights.AddRange(Highlights);
            fresh.Aliases.AddRange(Aliases);
            return fresh;
        }
    }

    // catalog.json : a cote de KKAO.exe (applis embarquees) ou dans la release GitHub (applis en ligne + version de KKAO)
    //   { "format": 1, "kkao": { "version", "url", "sha256", "size" },
    //     "apps": [ { "id", "name", "version", "summary", "description", "highlights": [], "icon", "package", "url", "iconUrl", "sha256", "size" } ] }
    internal sealed class AppCatalog
    {
        public const string FileName = "catalog.json";
        public const string PackagesDir = "packages";

        public string FilePath { get; private set; }
        public string Dir { get; private set; }
        public List<CatalogApp> Apps { get; } = new List<CatalogApp>();
        public List<string> Problems { get; } = new List<string>();

        public bool IsEmpty => Apps.Count == 0;

        public static AppCatalog Empty() => new AppCatalog();

        // catalog.json du dossier, ou catalogue vide
        public static AppCatalog LoadFrom(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return Empty();
            var path = Path.Combine(dir, FileName);
            return File.Exists(path) ? Load(path) : Empty();
        }

        public static AppCatalog Load(string path)
        {
            string text;
            try
            {
                text = File.ReadAllText(path, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                var broken = new AppCatalog { FilePath = path, Dir = Path.GetDirectoryName(path) };
                broken.Problems.Add("catalogue illisible : " + ex.Message);
                return broken;
            }
            var catalog = Parse(text, Path.GetDirectoryName(path));
            catalog.FilePath = path;
            return catalog;
        }

        // dir : dossier du catalogue local (icone et paquet relatifs a lui) ; null : catalogue en ligne (seules les adresses comptent)
        public static AppCatalog Parse(string text, string dir)
        {
            var catalog = new AppCatalog { Dir = dir };
            var root = Json.ParseObject(text);
            if (root == null)
            {
                catalog.Problems.Add("catalogue : JSON invalide");
                return catalog;
            }
            foreach (var node in Json.GetArray(root, "apps"))
            {
                var id = Json.GetString(node, "id")?.Trim();
                if (string.IsNullOrEmpty(id) || !ToolsFile.ValidId.IsMatch(id))
                {
                    catalog.Problems.Add("appli ignorée : id invalide (« " + id + " »)");
                    continue;
                }
                if (catalog.Apps.Any(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase)))
                {
                    catalog.Problems.Add("appli ignorée : id « " + id + " » en double");
                    continue;
                }
                var app = new CatalogApp
                {
                    Id = id,
                    Name = Json.GetString(node, "name")?.Trim() ?? id,
                    Version = Json.GetString(node, "version")?.Trim(),
                    Publisher = Json.GetString(node, "publisher")?.Trim(),
                    Summary = Json.GetString(node, "summary"),
                    Description = Json.GetString(node, "description"),
                    Url = WebAddress(Json.GetString(node, "url")),
                    IconUrl = WebAddress(Json.GetString(node, "iconUrl")),
                    Sha256 = Json.GetString(node, "sha256"),
                    Size = ToLong(Json.Get(node, "size")),
                    InstalledSize = ToLong(Json.Get(node, "installedSize")),
                };
                foreach (var highlight in Json.GetArray(node, "highlights"))
                    if (highlight is string h && h.Trim().Length > 0) app.Highlights.Add(h.Trim());
                foreach (var alias in Json.GetArray(node, "aliases"))
                    if (alias is string a && ToolsFile.ValidId.IsMatch(a.Trim())) app.Aliases.Add(a.Trim());
                if (dir != null)
                {
                    var icon = Json.GetString(node, "icon");
                    if (!string.IsNullOrWhiteSpace(icon)) app.IconPath = PathResolver.ResolvePath(icon, dir);
                    var package = Json.GetString(node, "package");
                    if (!string.IsNullOrWhiteSpace(package)) app.PackagePath = PathResolver.ResolvePath(package, dir);
                }
                catalog.Apps.Add(app);
            }
            return catalog;
        }

        // Seules les adresses https:// (GitHub) et file:/// (test) sont suivies
        static string WebAddress(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            value = value.Trim();
            return Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeFile) ? value : null;
        }

        public CatalogApp Find(string id) => Apps.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase));

        // L'appli du catalogue qui remplace un ancien id (renommage)
        public CatalogApp FindByAlias(string oldId) => Apps.FirstOrDefault(a => a.IsAlias(oldId));

        // Applis embarquees + applis en ligne : pour chaque appli, la version la plus recente ;
        // a version egale, le paquet embarque (pas de telechargement)
        public static AppCatalog Merge(AppCatalog local, AppCatalog remote)
        {
            var merged = new AppCatalog { FilePath = local?.FilePath, Dir = local?.Dir };
            var all = (local?.Apps ?? new List<CatalogApp>()).Concat(remote?.Apps ?? new List<CatalogApp>());
            foreach (var id in all.Select(a => a.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
            {
                var l = local?.Find(id);
                var r = remote?.Find(id);
                CatalogApp pick;
                if (l == null) pick = r.Clone();
                else if (r == null) pick = l.Clone();
                else
                {
                    var newer = VersionUtil.Compare(r.Version, l.Version);
                    var useRemote = r.CanInstall && (newer > 0 || (newer == 0 && !l.CanInstall));
                    pick = (useRemote ? r : l).Clone();
                    var other = useRemote ? l : r;
                    if (pick.IconPath == null || !File.Exists(pick.IconPath)) pick.IconPath = other.IconPath;
                    foreach (var alias in other.Aliases)
                        if (!pick.IsAlias(alias)) pick.Aliases.Add(alias);
                }
                merged.Apps.Add(pick);
            }
            return merged;
        }

        // Ecriture (assemblage du paquet, release) : chemins relatifs au dossier du catalogue, adresses si connues
        public static string Serialize(IEnumerable<CatalogApp> apps, string dir, KkaoRelease kkao = null)
        {
            var list = new List<object>();
            foreach (var app in apps)
            {
                var node = new Dictionary<string, object>
                {
                    { "id", app.Id },
                    { "name", app.Name },
                    { "version", app.Version },
                    { "publisher", app.Publisher },
                    { "summary", app.Summary },
                    { "description", app.Description },
                    { "highlights", app.Highlights.ToArray() },
                    { "icon", Relative(app.IconPath, dir) },
                    { "package", Relative(app.PackagePath, dir) },
                };
                if (!string.IsNullOrEmpty(app.Url)) node["url"] = app.Url;
                if (!string.IsNullOrEmpty(app.IconUrl)) node["iconUrl"] = app.IconUrl;
                node["sha256"] = app.Sha256;
                node["size"] = app.Size;
                node["installedSize"] = app.InstalledSize;
                if (app.Aliases.Count > 0) node["aliases"] = app.Aliases.ToArray();
                list.Add(node);
            }
            var root = new Dictionary<string, object>
            {
                { "format", 1 },
                { "generated", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") },
                { "host", Branding.Name + " " + Program.Version },
            };
            if (kkao != null) root["kkao"] = kkao.ToJson();
            root["apps"] = list;
            return Json.Pretty(Json.Serialize(root)) + "\n";
        }

        static string Relative(string path, string dir)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var full = Path.GetFullPath(path);
            var root = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full.Substring(root.Length) : full;
        }

        public static long ToLong(object value)
        {
            switch (value)
            {
                case int i: return i;
                case long l: return l;
                case decimal d: return (long)d;
                case double f: return (long)f;
                case string s when long.TryParse(s, out var parsed): return parsed;
                default: return 0;
            }
        }
    }
}
