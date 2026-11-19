using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KKAO.Apps;
using KKAO.Setup;

namespace KKAO
{
    // Catalogue en ligne simule par des fichiers locaux (adresses file:///) : lecture, copie locale, telechargement + installation,
    // mise a jour, empreinte fausse refusee, choix embarque / en ligne, release GitHub, installation de KKAO depuis Telechargements.
    // Aucun acces a Internet, rien hors du dossier temporaire du test.
    internal sealed partial class SelfTest
    {
        async Task RunOnlineAsync(ToolConfig nodeTool)
        {
            await CheckOnlineCatalogAsync(nodeTool);
            CheckReleaseFolder(nodeTool);
            CheckSelfInstallFromDownload();
        }

        BuiltPackage BuildOnlinePackage(string source, string packages, string id, string version, bool broken = false)
        {
            var recipe = new BundleRecipe { Id = id, Name = "Appli en ligne", SourceDir = source, VersionRule = version, IconStandIn = "postit", Summary = "Téléchargée" };
            recipe.Include.Add("fake-tool.js");
            recipe.App = new AppManifest { Id = id, Name = "Appli en ligne", Exe = "node", Protocol = true };
            // broken : version qui s'arrete aussitot (script absent)
            recipe.App.Args.AddRange(broken ? new[] { @".\absent.js" } : new[] { @".\fake-tool.js", "--id", id, "--v2" });
            Directory.CreateDirectory(packages);
            return Bundler.BuildPackage(recipe, packages, null);
        }

        // Catalogue « publie » : adresses file:/// des paquets et icones, version de KKAO annoncee
        static string PublishLocally(string dir, IEnumerable<CatalogApp> apps, KkaoRelease kkao, Func<CatalogApp, string> sha = null)
        {
            var published = apps.Select(a =>
            {
                var copy = a.Clone();
                copy.Url = new Uri(a.PackagePath).AbsoluteUri;
                copy.IconUrl = a.IconPath != null ? new Uri(a.IconPath).AbsoluteUri : null;
                if (sha != null) copy.Sha256 = sha(a);
                return copy;
            }).ToList();
            var path = Path.Combine(dir, AppCatalog.FileName);
            File.WriteAllText(path, AppCatalog.Serialize(published, dir, kkao), new UTF8Encoding(false));
            return new Uri(path).AbsoluteUri;
        }

        async Task CheckOnlineCatalogAsync(ToolConfig nodeTool)
        {
            var root = Path.Combine(_temp, "en-ligne");
            var source = Path.Combine(root, "source");
            Directory.CreateDirectory(source);
            File.Copy(nodeTool.Args[0], Path.Combine(source, "fake-tool.js"));
            var v1 = BuildOnlinePackage(source, Path.Combine(root, "release-1"), "enligne", "1.0.0");
            var kkao = new KkaoRelease { Version = "99.0.0", Url = "https://example.invalid/KKAO.exe", Sha256 = new string('a', 64), Size = 1 };
            var url = PublishLocally(Path.Combine(root, "release-1"), new[] { v1.App }, kkao);
            var cacheDir = Path.Combine(root, "cache");

            // 1. Lecture : appli telechargeable, icone mise en cache, version de KKAO lue ; copie locale relue hors ligne
            var remote = new RemoteCatalogService(url, cacheDir);
            remote.Apply(remote.Fetch());
            var fetched = remote.Catalog.Find("enligne");
            var offline = new RemoteCatalogService(url, cacheDir);
            offline.LoadCache();
            var cached = offline.Catalog.Find("enligne");
            Check("catalogue en ligne lu, copie locale",
                fetched != null && fetched.NeedsDownload && fetched.PackagePath == null && fetched.IconPath != null && File.Exists(fetched.IconPath)
                && remote.Kkao?.Version == "99.0.0" && cached != null && cached.IconPath == fetched.IconPath && offline.Kkao?.Version == "99.0.0" && offline.LastSuccess != null,
                "appli à télécharger (adresse + empreinte), icône récupérée dans " + Path.GetFileName(cacheDir) + "\\icones, " + Branding.Name + " 99.0.0 annoncé ; relu depuis la copie locale sans réseau");

            // 2. Installation par telechargement, puis mise a jour annoncee et installee
            var appsRoot = Path.Combine(root, "apps");
            var installer = new AppInstaller(appsRoot);
            var settings = KkaoSettings.InMemory();
            // Ici : le parcours a la main (bulle « disponible », bouton « Mettre a jour ») ; l'automatique a son propre test
            settings.AutoUpdate = false;
            var manager = new ToolManager(new List<ToolConfig>(), settings, SynchronizationContext.Current);
            var layout = new InstallLayout { InstallRoot = Path.Combine(root, "kkao"), StartMenuDir = Path.Combine(root, "menu"), DesktopDir = Path.Combine(root, "bureau") };
            var controller = new Ui.KkaoController(manager, settings, AppCatalog.Empty(), installer, new SelfInstaller(layout, new MemoryRegistry()), false, null,
                SynchronizationContext.Current, remote);
            var messages = new List<string>();
            controller.Message += (title, text, error) => messages.Add((error ? "ERREUR " : "") + title + " : " + text);
            try
            {
                var before = controller.Snapshot().FirstOrDefault(a => a.Id == "enligne");
                controller.InstallApp("enligne", startAfter: false);
                var installed = await WaitFor(() => manager.Find("enligne") != null && !controller.IsInstalling("enligne"), 15000);
                var downloads = Path.Combine(appsRoot, ".telechargements");
                var leftovers = Directory.Exists(downloads) ? Directory.GetFiles(downloads).Length : 0;
                Check("appli téléchargée puis installée",
                    before != null && before.HasPackage && before.Download && before.DownloadSize == v1.App.Size && installed
                    && manager.Find("enligne")?.Config.Version == "1.0.0" && leftovers == 0,
                    "proposée « à télécharger » (" + Bundler.FormatSize(v1.App.Size) + "), téléchargée, empreinte vérifiée, installée en 1.0.0, fichier téléchargé supprimé"
                    + (messages.Count > 0 ? " ; messages : " + string.Join(" / ", messages) : ""));

                var v2 = BuildOnlinePackage(source, Path.Combine(root, "release-2"), "enligne", "1.1.0");
                PublishLocally(Path.Combine(root, "release-2"), new[] { v2.App }, kkao);
                // Le catalogue « latest » pointe maintenant vers la release 2 (meme adresse qu'avant pour le test : on remplace le fichier)
                File.Copy(Path.Combine(root, "release-2", AppCatalog.FileName), Path.Combine(root, "release-1", AppCatalog.FileName), true);
                controller.CheckForUpdates(userInitiated: false);
                var checkedOk = await WaitFor(() => !controller.CheckingUpdates && controller.Snapshot().Any(a => a.Id == "enligne" && a.UpdateAvailable), 10000);
                controller.InstallApp("enligne", startAfter: false);
                var updated = await WaitFor(() => manager.Find("enligne")?.Config.Version == "1.1.0" && !controller.IsInstalling("enligne"), 15000);
                var notified = messages.Any(m => m.StartsWith("Appli en ligne 1.1.0 est disponible", StringComparison.Ordinal));
                Check("mise à jour en ligne annoncée et installée", checkedOk && updated && notified && controller.KkaoUpdate == null,
                    "nouvelle lecture du catalogue → « Mettre à jour » + bulle (une fois par version) → 1.1.0 installée ; "
                    + Branding.Name + " 99.0.0 annoncé mais pas proposé (copie non installée)");

                // 3. Empreinte fausse : refusee, l'appli installee reste intacte, rien ne traine
                var v3 = BuildOnlinePackage(source, Path.Combine(root, "release-3"), "enligne", "1.2.0");
                PublishLocally(Path.Combine(root, "release-3"), new[] { v3.App }, kkao, a => new string('0', 64));
                File.Copy(Path.Combine(root, "release-3", AppCatalog.FileName), Path.Combine(root, "release-1", AppCatalog.FileName), true);
                controller.CheckForUpdates(userInitiated: false);
                await WaitFor(() => !controller.CheckingUpdates && controller.Snapshot().Any(a => a.Id == "enligne" && a.AvailableVersion == "1.2.0"), 10000);
                var errorsBefore = messages.Count(m => m.StartsWith("ERREUR", StringComparison.Ordinal));
                controller.InstallApp("enligne", startAfter: false);
                var refused = await WaitFor(() => !controller.IsInstalling("enligne") && messages.Count(m => m.StartsWith("ERREUR", StringComparison.Ordinal)) > errorsBefore, 15000);
                leftovers = Directory.Exists(downloads) ? Directory.GetFiles(downloads).Length : 0;
                var error = controller.Snapshot().FirstOrDefault(a => a.Id == "enligne")?.InstallError;
                Check("paquet téléchargé à l'empreinte fausse : refusé", refused && manager.Find("enligne")?.Config.Version == "1.1.0" && leftovers == 0 && error != null,
                    "« " + error + " » ; 1.1.0 toujours installée, fichier téléchargé supprimé");
            }
            finally
            {
                var done = false;
                manager.QuitAll(() => done = true);
                await WaitFor(() => done, 12000);
            }

            // 4. Embarque ou en ligne : la version la plus recente ; a egalite, le paquet embarque (pas de telechargement)
            var localDir = Path.Combine(root, "embarque");
            var local = BuildOnlinePackage(source, Path.Combine(localDir, AppCatalog.PackagesDir), "enligne", "1.1.0");
            File.WriteAllText(Path.Combine(localDir, AppCatalog.FileName), AppCatalog.Serialize(new[] { local.App }, localDir), new UTF8Encoding(false));
            var localCatalog = AppCatalog.Load(Path.Combine(localDir, AppCatalog.FileName));
            AppCatalog Remote(string version)
            {
                var app = local.App.Clone();
                app.Version = version;
                app.PackagePath = null;
                app.Url = "https://example.invalid/enligne-" + version + ".zip";
                var catalog = AppCatalog.Empty();
                catalog.Apps.Add(app);
                return catalog;
            }
            var same = AppCatalog.Merge(localCatalog, Remote("1.1.0")).Find("enligne");
            var newer = AppCatalog.Merge(localCatalog, Remote("1.2.0")).Find("enligne");
            var older = AppCatalog.Merge(localCatalog, Remote("1.0.0")).Find("enligne");
            Check("embarqué ou en ligne : la plus récente, sinon l'embarquée",
                same.HasPackage && !same.NeedsDownload && newer.NeedsDownload && newer.Version == "1.2.0" && newer.IconPath != null && older.HasPackage && older.Version == "1.1.0",
                "1.1.0 embarquée vs 1.1.0 en ligne → embarquée ; vs 1.2.0 → téléchargement (icône embarquée gardée) ; vs 1.0.0 → embarquée");
        }

        // Release GitHub preparee a partir d'un paquet assemble : fichiers a plat, adresses de la release, empreintes
        void CheckReleaseFolder(ToolConfig nodeTool)
        {
            var root = Path.Combine(_temp, "release-test");
            var bundle = Path.Combine(root, Branding.Name + "-" + Program.Version);
            var source = Path.Combine(root, "source");
            Directory.CreateDirectory(source);
            File.Copy(nodeTool.Args[0], Path.Combine(source, "fake-tool.js"));
            var built = BuildOnlinePackage(source, Path.Combine(bundle, AppCatalog.PackagesDir), "relpkg", "2.0.0");
            File.Copy(Paths.ExePath, Path.Combine(bundle, Paths.TechName + ".exe"));
            File.WriteAllText(Path.Combine(bundle, AppCatalog.FileName), AppCatalog.Serialize(new[] { built.App }, bundle), new UTF8Encoding(false));

            var logPath = Log.FilePath;
            int code;
            try
            {
                code = Bundler.Release(bundle);
            }
            finally
            {
                Log.FilePath = logPath;
            }
            var releaseDir = Path.Combine(root, "release");
            var text = File.Exists(Path.Combine(releaseDir, AppCatalog.FileName)) ? File.ReadAllText(Path.Combine(releaseDir, AppCatalog.FileName), Encoding.UTF8) : "";
            var catalog = AppCatalog.Parse(text, null);
            var kkao = KkaoRelease.Parse(Json.ParseObject(text));
            var app = catalog.Find("relpkg");
            var tag = Distribution.TagFor(Program.Version);
            var baseUrl = Distribution.ReleaseBaseUrl(tag);
            var exe = Path.Combine(releaseDir, Paths.TechName + ".exe");
            var notes = Path.Combine(root, "release-notes.md");
            var ok = code == 0 && app != null && app.Url == baseUrl + "relpkg-2.0.0.zip" && app.IconUrl == baseUrl + "relpkg.png" && app.Sha256 == built.App.Sha256
                && File.Exists(Path.Combine(releaseDir, "relpkg-2.0.0.zip")) && File.Exists(Path.Combine(releaseDir, "relpkg.png")) && File.Exists(exe)
                && kkao != null && kkao.Version == Program.Version && kkao.Url == baseUrl + Paths.TechName + ".exe"
                && kkao.Sha256 == AppInstaller.Sha256Of(exe, null, CancellationToken.None) && kkao.Size == new FileInfo(exe).Length
                && File.Exists(notes) && File.ReadAllText(notes, Encoding.UTF8).Contains(Distribution.LatestExeUrl)
                && File.ReadAllText(Path.Combine(root, "release-tag.txt")).Trim() == tag
                && Directory.GetFiles(releaseDir).Length == 4;
            Check("release GitHub préparée", ok,
                "out\\release : " + Paths.TechName + ".exe, catalog.json, paquet et icône à plat ; adresses " + baseUrl + "… ; empreintes et taille de l'exe dans le catalogue ; texte et tag " + tag);
        }

        // KKAO telecharge seul (« KKAO (1).exe » dans Telechargements) : installe sous KKAO.exe, ancien catalogue et paquets retires
        void CheckSelfInstallFromDownload()
        {
            var downloads = Path.Combine(_temp, "Téléchargements");
            Directory.CreateDirectory(Path.Combine(downloads, AppCatalog.PackagesDir));
            File.Copy(Paths.ExePath, Path.Combine(downloads, "KKAO (1).exe"));
            // Dossier quelconque : un « packages » sans catalog.json n'est pas a KKAO
            File.WriteAllText(Path.Combine(downloads, AppCatalog.PackagesDir, "autre.zip"), "x");
            var layout = new InstallLayout
            {
                InstallRoot = Path.Combine(_temp, "Programs-2", Paths.TechName),
                StartMenuDir = Path.Combine(_temp, "Menu-2"),
                DesktopDir = Path.Combine(_temp, "Bureau-2"),
            };
            Directory.CreateDirectory(Path.Combine(layout.InstallRoot, AppCatalog.PackagesDir));
            File.WriteAllText(Path.Combine(layout.InstallRoot, AppCatalog.FileName), "{\"format\":1,\"apps\":[]}");
            File.WriteAllText(Path.Combine(layout.InstallRoot, AppCatalog.PackagesDir, "ancien-1.0.zip"), "old");
            var self = new SelfInstaller(layout, new MemoryRegistry());
            self.Install(downloads, new InstallOptions { StartWithWindows = false, DesktopShortcut = false }, null, "KKAO (1).exe");
            var ok = File.Exists(Path.Combine(layout.InstallRoot, Paths.TechName + ".exe")) && !File.Exists(Path.Combine(layout.InstallRoot, "KKAO (1).exe"))
                && !File.Exists(Path.Combine(layout.InstallRoot, AppCatalog.FileName))
                && !File.Exists(Path.Combine(layout.InstallRoot, AppCatalog.PackagesDir, "ancien-1.0.zip"))
                && !File.Exists(Path.Combine(layout.InstallRoot, AppCatalog.PackagesDir, "autre.zip"))
                && self.InstalledVersion == Program.Version;
            self.RemoveIntegration();
            Check("installation de KKAO depuis Téléchargements", ok,
                "« KKAO (1).exe » installé sous " + Paths.TechName + ".exe ; ancien catalogue et paquets retirés (les applis viennent du catalogue en ligne) ; "
                + "dossier « packages » étranger ignoré");
        }
    }
}
