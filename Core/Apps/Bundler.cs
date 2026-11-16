using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace KKAO.Apps
{
    // Recette d'un paquet d'appli (KKAO\bundle\apps\<id>.json) : quels fichiers du dossier de l'appli embarquer, et comment la lancer
    internal sealed class BundleRecipe
    {
        public string FilePath { get; set; }
        public string Id { get; set; }
        public string Name { get; set; }
        public string Publisher { get; set; }
        public string Summary { get; set; }
        public string Description { get; set; }
        public List<string> Highlights { get; } = new List<string>();
        public string SourceDir { get; set; }
        public string VersionRule { get; set; }
        public List<string> Include { get; } = new List<string>();
        public List<string> Exclude { get; } = new List<string>();
        public List<string> Keep { get; } = new List<string>();
        // Anciens id de l'appli (renommage) : repris dans le catalogue et le manifeste
        public List<string> Aliases { get; } = new List<string>();
        // Icone de remplacement dessinee par KKAO quand l'appli n'en fournit pas encore ("postit")
        public string IconStandIn { get; set; }
        public AppManifest App { get; set; }

        public static BundleRecipe Load(string path, out string problem)
        {
            problem = null;
            var root = Json.ParseObject(File.ReadAllText(path, Encoding.UTF8));
            if (root == null)
            {
                problem = "JSON invalide";
                return null;
            }
            var dir = Path.GetDirectoryName(path);
            var recipe = new BundleRecipe
            {
                FilePath = path,
                Id = Json.GetString(root, "id")?.Trim(),
                Name = Json.GetString(root, "name")?.Trim(),
                Publisher = Json.GetString(root, "publisher")?.Trim() ?? Branding.Publisher,
                Summary = Json.GetString(root, "summary"),
                Description = Json.GetString(root, "description"),
                VersionRule = Json.GetString(root, "version") ?? "0.0.0",
                IconStandIn = Json.GetString(root, "iconStandIn"),
            };
            var source = Json.GetString(root, "source");
            recipe.SourceDir = string.IsNullOrWhiteSpace(source) ? null : PathResolver.ResolvePath(source, dir);
            foreach (var h in Json.GetArray(root, "highlights")) if (h is string s && s.Trim().Length > 0) recipe.Highlights.Add(s.Trim());
            foreach (var i in Json.GetArray(root, "include")) if (i is string s && s.Trim().Length > 0) recipe.Include.Add(Normalize(s));
            foreach (var e in Json.GetArray(root, "exclude")) if (e is string s && s.Trim().Length > 0) recipe.Exclude.Add(Normalize(s));
            foreach (var k in Json.GetArray(root, "keep")) if (k is string s && s.Trim().Length > 0) recipe.Keep.Add(Normalize(s));
            foreach (var a in Json.GetArray(root, "aliases")) if (a is string s && ToolsFile.ValidId.IsMatch(s.Trim())) recipe.Aliases.Add(s.Trim());

            var app = Json.Get(root, "app");
            recipe.App = app == null ? null : AppManifest.Parse(Json.Serialize(MergeIdentity(app, recipe)), out problem);
            if (string.IsNullOrEmpty(recipe.Id) || !ToolsFile.ValidId.IsMatch(recipe.Id)) problem = "id invalide";
            else if (recipe.SourceDir == null || !Directory.Exists(recipe.SourceDir)) problem = "dossier source introuvable : " + (recipe.SourceDir ?? "(« source » manquant)");
            else if (recipe.Include.Count == 0) problem = "« include » vide";
            else if (recipe.App == null && problem == null) problem = "« app » manquant";
            return problem == null ? recipe : null;
        }

        static Dictionary<string, object> MergeIdentity(object app, BundleRecipe recipe)
        {
            var copy = new Dictionary<string, object>();
            if (app is Dictionary<string, object> dict) foreach (var pair in dict) copy[pair.Key] = pair.Value;
            copy["id"] = recipe.Id;
            if (!copy.ContainsKey("name")) copy["name"] = recipe.Name;
            if (!copy.ContainsKey("publisher")) copy["publisher"] = recipe.Publisher;
            return copy;
        }

        public static string Normalize(string path) => path.Trim().Replace('/', '\\').Trim('\\');
    }

    internal sealed class BuiltPackage
    {
        public CatalogApp App { get; set; }
        public int FileCount { get; set; }
    }

    // KKAO.exe --bundle <dossier de sortie> [--recipes <dossier>] : prepare le dossier a partager aux collegues
    //   <sortie>\KKAO.exe, catalog.json, packages\<id>-<version>.zip + <id>.png, LISEZ-MOI.txt, puis <sortie>.zip
    internal static class Bundler
    {
        public static int Run(string outDir, string recipesDir)
        {
            var watch = Stopwatch.StartNew();
            // Par defaut : a cote du dossier de l'exe (out\build\KKAO.exe -> out\KKAO-<version>)
            outDir = Path.GetFullPath(string.IsNullOrWhiteSpace(outDir)
                ? Path.Combine(Path.GetDirectoryName(Paths.ExeDir) ?? Paths.ExeDir, Branding.Name + "-" + Program.Version) : outDir);
            Log.FilePath = outDir.TrimEnd('\\') + ".log";
            try { File.Delete(Log.FilePath); }
            catch { }
            Log.Write("Assemblage du paquet " + Branding.Name + " " + Program.Version + " dans " + outDir);
            try
            {
                recipesDir = recipesDir != null ? Path.GetFullPath(recipesDir) : FindUp(Path.Combine("bundle", "apps"));
                if (recipesDir == null || !Directory.Exists(recipesDir)) throw new InstallException("dossier des recettes introuvable (bundle\\apps du projet KKAO)");
                Log.Write("Recettes : " + recipesDir);

                PrepareOutput(outDir);
                var packagesDir = Path.Combine(outDir, AppCatalog.PackagesDir);
                Directory.CreateDirectory(packagesDir);

                var apps = new List<CatalogApp>();
                foreach (var file in Directory.GetFiles(recipesDir, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    var recipe = BundleRecipe.Load(file, out var problem);
                    if (recipe == null) throw new InstallException(Path.GetFileName(file) + " : " + problem);
                    var built = BuildPackage(recipe, packagesDir, Log.Write);
                    apps.Add(built.App);
                }

                foreach (var name in new[] { Path.GetFileName(Paths.ExePath), Path.GetFileName(Paths.ExePath) + ".config" })
                {
                    var from = Path.Combine(Paths.ExeDir, name);
                    if (File.Exists(from)) File.Copy(from, Path.Combine(outDir, name), true);
                }
                File.WriteAllText(Path.Combine(outDir, AppCatalog.FileName), AppCatalog.Serialize(apps, outDir), new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(outDir, "LISEZ-MOI.txt"), ReadMe(apps), new UTF8Encoding(true));
                Verify(outDir);

                var zipPath = outDir.TrimEnd('\\') + ".zip";
                ZipFolder(outDir, zipPath);
                var size = new FileInfo(zipPath).Length;
                Log.Write("Paquet prêt : " + zipPath + " (" + FormatSize(size) + ", " + apps.Count + " appli(s) : "
                    + string.Join(", ", apps.Select(a => a.Name + " " + a.Version)) + ") en " + watch.Elapsed.TotalSeconds.ToString("0.0") + " s");
                Log.Flush();
                return 0;
            }
            catch (Exception ex)
            {
                Log.Write("ECHEC de l'assemblage : " + (ex is InstallException ? ex.Message : ex.ToString()));
                Log.Flush();
                return 1;
            }
        }

        // KKAO.exe --release : a partir du paquet out\KKAO-<version> deja assemble, prepare une release GitHub
        //   out\release\ (a plat : KKAO.exe, catalog.json avec les adresses de telechargement, paquets, icones),
        //   out\release-notes.md (texte de la release) et out\release-tag.txt (v<version>)
        public static int Release(string bundleDir)
        {
            var watch = Stopwatch.StartNew();
            var parent = Path.GetDirectoryName(Paths.ExeDir) ?? Paths.ExeDir;
            bundleDir = Path.GetFullPath(string.IsNullOrWhiteSpace(bundleDir) ? Path.Combine(parent, Branding.Name + "-" + Program.Version) : bundleDir);
            var outRoot = Path.GetDirectoryName(bundleDir.TrimEnd('\\'));
            var releaseDir = Path.Combine(outRoot, "release");
            Log.FilePath = Path.Combine(outRoot, "release.log");
            try { File.Delete(Log.FilePath); }
            catch { }
            var tag = Distribution.TagFor(Program.Version);
            var baseUrl = Distribution.ReleaseBaseUrl(tag);
            Log.Write("Release " + tag + " de " + Branding.Name + " depuis " + bundleDir);
            try
            {
                var catalog = AppCatalog.Load(Path.Combine(bundleDir, AppCatalog.FileName));
                if (catalog.FilePath == null || catalog.Problems.Count > 0 || catalog.Apps.Count == 0)
                    throw new InstallException("paquet introuvable ou illisible : lancez d'abord build-bundle.cmd (" + bundleDir + ")");
                var exe = Path.Combine(bundleDir, Paths.TechName + ".exe");
                if (!File.Exists(exe)) throw new InstallException(exe + " introuvable");
                var exeVersion = VersionUtil.Short(FileVersionInfo.GetVersionInfo(exe).ProductVersion);
                if (VersionUtil.Compare(exeVersion, Program.Version) != 0)
                    throw new InstallException("le paquet contient " + Branding.Name + " " + exeVersion + ", pas " + Program.Version + " : relancez build-bundle.cmd");

                PrepareOutput(releaseDir);
                var exeCopy = Path.Combine(releaseDir, Paths.TechName + ".exe");
                File.Copy(exe, exeCopy);
                var kkao = new KkaoRelease
                {
                    Version = Program.Version,
                    Url = baseUrl + Paths.TechName + ".exe",
                    Sha256 = AppInstaller.Sha256Of(exeCopy, null, CancellationToken.None),
                    Size = new FileInfo(exeCopy).Length,
                };

                var apps = new List<CatalogApp>();
                foreach (var source in catalog.Apps)
                {
                    if (!source.HasPackage) throw new InstallException(source.Id + " : paquet absent de " + bundleDir);
                    var app = source.Clone();
                    app.PackagePath = Path.Combine(releaseDir, Path.GetFileName(source.PackagePath));
                    File.Copy(source.PackagePath, app.PackagePath);
                    if (AppInstaller.Sha256Of(app.PackagePath, null, CancellationToken.None) != source.Sha256)
                        throw new InstallException(source.Id + " : empreinte du paquet différente de celle du catalogue");
                    app.Url = baseUrl + Path.GetFileName(app.PackagePath);
                    if (source.IconPath != null && File.Exists(source.IconPath))
                    {
                        app.IconPath = Path.Combine(releaseDir, source.Id + Path.GetExtension(source.IconPath));
                        File.Copy(source.IconPath, app.IconPath);
                        app.IconUrl = baseUrl + Path.GetFileName(app.IconPath);
                    }
                    apps.Add(app);
                    Log.Write("  " + app.Name + " " + app.Version + " → " + app.Url + " (" + FormatSize(app.Size) + ")");
                }
                File.WriteAllText(Path.Combine(releaseDir, AppCatalog.FileName), AppCatalog.Serialize(apps, releaseDir, kkao), new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(outRoot, "release-notes.md"), ReleaseNotes(apps), new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(outRoot, "release-tag.txt"), tag, new UTF8Encoding(false));
                Log.Write("Release prête : " + releaseDir + " (" + (apps.Count * 2 + 2) + " fichiers, " + Branding.Name + ".exe " + FormatSize(kkao.Size)
                    + ") en " + watch.Elapsed.TotalSeconds.ToString("0.0") + " s ; texte : release-notes.md");
                Log.Flush();
                return 0;
            }
            catch (Exception ex)
            {
                Log.Write("ECHEC de la release : " + (ex is InstallException ? ex.Message : ex.ToString()));
                Log.Flush();
                return 1;
            }
        }

        static string ReleaseNotes(List<CatalogApp> apps)
        {
            var b = new StringBuilder();
            b.AppendLine("## Installer " + Branding.Name);
            b.AppendLine();
            b.AppendLine("1. Téléchargez **[" + Paths.TechName + ".exe](" + Distribution.LatestExeUrl + ")** (aussi dans « Assets » ci-dessous). "
                + "Si le navigateur indique que le fichier n'est pas couramment téléchargé : « … » puis **Conserver**.");
            b.AppendLine("2. Double-cliquez dessus. Si Windows affiche « Windows a protégé votre ordinateur » : **Informations complémentaires**, puis **Exécuter quand même**.");
            b.AppendLine("3. Laissez-vous guider : " + Branding.Name + " s'installe pour vous seul, sans droits administrateur, puis vous cochez vos applis. "
                + "Elles se téléchargent et s'installent d'un clic.");
            b.AppendLine();
            b.AppendLine("## Applis proposées");
            b.AppendLine();
            b.AppendLine("| Appli | Version | En bref | Téléchargement |");
            b.AppendLine("|---|---|---|---|");
            foreach (var app in apps)
                b.AppendLine("| " + app.Name + " | " + app.Version + " | " + (app.Summary ?? "") + " | " + FormatSize(app.Size) + " |");
            b.AppendLine();
            b.AppendLine("Les autres fichiers (catalog.json, paquets .zip, icônes) servent à " + Branding.Name + " lui-même : inutile de les télécharger.");
            b.AppendLine();
            b.AppendLine("## Mises à jour");
            b.AppendLine();
            b.AppendLine(Branding.Name + " consulte cette page au démarrage puis toutes les 6 heures, et propose d'un clic les nouvelles versions de lui-même et des applis. "
                + "Vos réglages et vos documents sont conservés.");
            return b.ToString();
        }

        // Installation d'essai de chaque appli du catalogue produit (dossier temporaire, rien n'est lance) :
        // empreinte, extraction, exe et icone presents la ou le manifeste les annonce
        static void Verify(string outDir)
        {
            var catalog = AppCatalog.Load(Path.Combine(outDir, AppCatalog.FileName));
            if (catalog.Problems.Count > 0) throw new InstallException("catalogue produit illisible : " + catalog.Problems[0]);
            var temp = Path.Combine(Path.GetTempPath(), "kkao-verif-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                var installer = new AppInstaller(temp);
                foreach (var app in catalog.Apps)
                {
                    var watch = Stopwatch.StartNew();
                    installer.Install(app.PackagePath, app.Id, app.Sha256, null, CancellationToken.None);
                    var tool = installer.ScanInstalled(null).FirstOrDefault(t => t.Id == app.Id);
                    if (tool == null) throw new InstallException(app.Id + " : installation d'essai illisible");
                    if (!File.Exists(tool.Exe)) throw new InstallException(app.Id + " : exe absent après installation (" + tool.Exe + ")");
                    if (tool.IconPath != null && !File.Exists(tool.IconPath)) throw new InstallException(app.Id + " : icône absente après installation (" + tool.IconPath + ")");
                    Log.Write("  vérifié : " + app.Name + " s'installe en " + watch.Elapsed.TotalSeconds.ToString("0.0") + " s → " + tool.Exe.Substring(temp.Length));
                    installer.Uninstall(app.Id);
                }
            }
            finally
            {
                AppInstaller.TryDeleteDirectory(temp);
            }
        }

        // Dossier du projet KKAO cherche en remontant depuis l'exe (dist\, out\build\, bin\Release\net48\)
        static string FindUp(string relative)
        {
            var dir = Paths.ExeDir;
            for (var i = 0; i < 5 && dir != null; i++)
            {
                var candidate = Path.Combine(dir, relative);
                if (Directory.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }

        // Le dossier de sortie est vide ou a deja ete produit par KKAO : jamais un dossier quelconque
        static void PrepareOutput(string outDir)
        {
            if (Directory.Exists(outDir))
            {
                var ours = File.Exists(Path.Combine(outDir, AppCatalog.FileName)) || !Directory.EnumerateFileSystemEntries(outDir).Any();
                if (!ours) throw new InstallException("le dossier de sortie existe et n'a pas été produit par " + Branding.Name + " : " + outDir);
                AppInstaller.TryDeleteDirectory(outDir);
            }
            var zip = outDir.TrimEnd('\\') + ".zip";
            if (File.Exists(zip)) File.Delete(zip);
            Directory.CreateDirectory(outDir);
        }

        public static BuiltPackage BuildPackage(BundleRecipe recipe, string packagesDir, Action<string> log)
        {
            var files = CollectFiles(recipe.SourceDir, recipe.Include, recipe.Exclude, recipe.Keep, out var missing);
            if (missing.Count > 0) throw new InstallException(recipe.Id + " : introuvable(s) dans " + recipe.SourceDir + " : " + string.Join(", ", missing));

            var manifest = recipe.App;
            manifest.Version = ResolveVersion(recipe);
            foreach (var alias in recipe.Aliases)
                if (!manifest.Aliases.Contains(alias)) manifest.Aliases.Add(alias);
            if (string.IsNullOrEmpty(manifest.Summary)) manifest.Summary = recipe.Summary;
            if (string.IsNullOrEmpty(manifest.Description)) manifest.Description = recipe.Description;

            // Icone : celle de l'appli si elle existe, sinon le dessin de remplacement de KKAO
            byte[] standIn = null;
            var iconSource = string.IsNullOrEmpty(manifest.Icon) ? null : Path.Combine(recipe.SourceDir, manifest.Icon);
            if (iconSource == null || !File.Exists(iconSource))
            {
                if (!string.IsNullOrEmpty(recipe.IconStandIn))
                {
                    using (var bitmap = IconFactory.RenderStandIn(recipe.IconStandIn, 256))
                    using (var memory = new MemoryStream())
                    {
                        bitmap.Save(memory, ImageFormat.Png);
                        standIn = memory.ToArray();
                    }
                    manifest.Icon = "kkao-icon.png";
                }
                else
                {
                    manifest.Icon = null;
                }
                iconSource = null;
            }

            var zipName = recipe.Id + "-" + manifest.Version + ".zip";
            var zipPath = Path.Combine(packagesDir, zipName);
            long installed = 0;
            using (var stream = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var entry = zip.CreateEntry(AppManifest.FileName, CompressionLevel.Optimal);
                using (var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
                    writer.Write(Json.Pretty(Json.Serialize(manifest.ToJson())) + "\n");
                if (standIn != null)
                {
                    var iconEntry = zip.CreateEntry("kkao-icon.png", CompressionLevel.NoCompression);
                    using (var output = iconEntry.Open()) output.Write(standIn, 0, standIn.Length);
                    installed += standIn.Length;
                }
                foreach (var relative in files)
                {
                    var full = Path.Combine(recipe.SourceDir, relative);
                    zip.CreateEntryFromFile(full, relative.Replace('\\', '/'), CompressionLevel.Optimal);
                    installed += new FileInfo(full).Length;
                }
            }

            var iconOut = Path.Combine(packagesDir, recipe.Id + ".png");
            if (iconSource != null) File.Copy(iconSource, iconOut, true);
            else if (standIn != null) File.WriteAllBytes(iconOut, standIn);
            else iconOut = null;

            var app = new CatalogApp
            {
                Id = recipe.Id,
                Name = manifest.Name ?? recipe.Id,
                Version = manifest.Version,
                Publisher = manifest.Publisher,
                Summary = recipe.Summary,
                Description = recipe.Description,
                IconPath = iconOut,
                PackagePath = zipPath,
                Size = new FileInfo(zipPath).Length,
                InstalledSize = installed,
                Sha256 = AppInstaller.Sha256Of(zipPath, null, CancellationToken.None),
            };
            app.Highlights.AddRange(recipe.Highlights);
            app.Aliases.AddRange(manifest.Aliases);
            log?.Invoke("  " + app.Name + " " + app.Version + " : " + files.Count + " fichiers, " + FormatSize(installed) + " installés, paquet " + FormatSize(app.Size));
            return new BuiltPackage { App = app, FileCount = files.Count };
        }

        static string ResolveVersion(BundleRecipe recipe)
        {
            var rule = (recipe.VersionRule ?? "").Trim();
            try
            {
                if (rule.Equals("package.json", StringComparison.OrdinalIgnoreCase))
                {
                    var json = Json.ParseObject(File.ReadAllText(Path.Combine(recipe.SourceDir, "package.json"), Encoding.UTF8));
                    return VersionUtil.Short(Json.GetString(json, "version"));
                }
                if (rule.StartsWith("exe:", StringComparison.OrdinalIgnoreCase))
                {
                    var info = FileVersionInfo.GetVersionInfo(Path.Combine(recipe.SourceDir, rule.Substring(4)));
                    return VersionUtil.Short(info.ProductVersion ?? info.FileVersion);
                }
            }
            catch (Exception ex)
            {
                throw new InstallException(recipe.Id + " : version illisible (" + rule + ") : " + ex.Message);
            }
            return VersionUtil.Short(rule);
        }

        // include : fichiers ou dossiers (recursifs) ; exclude puis keep : motifs * ? ** sur le chemin relatif
        public static List<string> CollectFiles(string sourceDir, IList<string> include, IList<string> exclude, IList<string> keep, out List<string> missing)
        {
            missing = new List<string>();
            var excludeRx = exclude.Select(GlobToRegex).ToList();
            var keepRx = keep.Select(GlobToRegex).ToList();
            var result = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var root = Path.GetFullPath(sourceDir).TrimEnd('\\') + "\\";
            foreach (var item in include)
            {
                var full = Path.GetFullPath(Path.Combine(sourceDir, item));
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    missing.Add(item + " (hors du dossier)");
                    continue;
                }
                IEnumerable<string> candidates;
                if (File.Exists(full)) candidates = new[] { full };
                else if (Directory.Exists(full)) candidates = Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories);
                else
                {
                    missing.Add(item);
                    continue;
                }
                foreach (var file in candidates)
                {
                    var relative = file.Substring(root.Length);
                    var excluded = excludeRx.Any(r => r.IsMatch(relative)) && !keepRx.Any(r => r.IsMatch(relative));
                    if (!excluded) result.Add(relative);
                }
            }
            return result.ToList();
        }

        public static Regex GlobToRegex(string glob)
        {
            var sb = new StringBuilder("^");
            var pattern = BundleRecipe.Normalize(glob);
            for (var i = 0; i < pattern.Length; i++)
            {
                var c = pattern[i];
                if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
                {
                    sb.Append(".*");
                    i++;
                }
                else if (c == '*') sb.Append(@"[^\\]*");
                else if (c == '?') sb.Append(@"[^\\]");
                else sb.Append(Regex.Escape(c.ToString()));
            }
            // Un dossier exclu emporte son contenu
            sb.Append(@"(\\.*)?$");
            return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        // Le dossier entier dans un zip (dossier de tete = nom du dossier) ; les paquets deja compresses sont stockes tels quels
        public static void ZipFolder(string dir, string zipPath)
        {
            var top = Path.GetFileName(dir.TrimEnd('\\'));
            var root = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
            using (var stream = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    var relative = file.Substring(root.Length).Replace('\\', '/');
                    var level = file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                        ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
                    zip.CreateEntryFromFile(file, top + "/" + relative, level);
                }
            }
        }

        public static string FormatSize(long bytes)
        {
            if (bytes >= 1L << 30) return (bytes / (double)(1L << 30)).ToString("0.0") + " Go";
            if (bytes >= 1L << 20) return (bytes / (double)(1L << 20)).ToString("0") + " Mo";
            if (bytes >= 1L << 10) return (bytes / (double)(1L << 10)).ToString("0") + " Ko";
            return bytes + " o";
        }

        static string ReadMe(List<CatalogApp> apps)
        {
            var sb = new StringBuilder();
            sb.AppendLine(Branding.Name + " " + Program.Version + " — " + Branding.LongName);
            sb.AppendLine(Branding.Tagline);
            sb.AppendLine(new string('=', 60));
            sb.AppendLine();
            sb.AppendLine("Pour commencer (2 minutes, sans droits administrateur) :");
            sb.AppendLine();
            sb.AppendLine("  1. Extrayez ce dossier (clic droit sur le zip > « Extraire tout… »).");
            sb.AppendLine("     Ne lancez pas " + Branding.Name + " depuis l'intérieur du zip.");
            sb.AppendLine("  2. Double-cliquez sur " + Path.GetFileName(Paths.ExePath) + ".");
            sb.AppendLine("     Si Windows affiche « Windows a protégé votre ordinateur » :");
            sb.AppendLine("     cliquez sur « Informations complémentaires », puis « Exécuter quand même ».");
            sb.AppendLine("  3. Laissez-vous guider : " + Branding.Name + " s'installe pour vous seul, puis vous");
            sb.AppendLine("     choisissez les applis à installer d'un clic.");
            sb.AppendLine();
            sb.AppendLine("Applis embarquées (installées seulement si vous le demandez) :");
            foreach (var app in apps)
                sb.AppendLine("  - " + app.Name + " " + app.Version + (string.IsNullOrEmpty(app.Summary) ? "" : " : " + app.Summary));
            sb.AppendLine();
            sb.AppendLine("Ensuite :");
            sb.AppendLine("  - L'icône " + Branding.Name + " est en bas à droite, près de l'horloge (sous « ^ » au début).");
            sb.AppendLine("    Clic gauche : ouvrir " + Branding.Name + ". Clic droit : vos actions rapides.");
            sb.AppendLine("  - Désinstaller : Paramètres Windows > Applications > " + Branding.Name + ",");
            sb.AppendLine("    ou " + Branding.Name + " > Réglages > Désinstaller. Vos données sont conservées.");
            sb.AppendLine();
            sb.AppendLine("Emplacements : " + Branding.Name + " et ses applis dans %LOCALAPPDATA%\\Programs\\" + Paths.TechName + ",");
            sb.AppendLine("réglages et journal dans %APPDATA%\\" + Paths.TechName + ".");
            sb.AppendLine();
            sb.AppendLine(Branding.PublisherLong);
            return sb.ToString();
        }
    }
}
