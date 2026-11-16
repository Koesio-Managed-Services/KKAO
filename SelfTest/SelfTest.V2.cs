using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KKAO.Apps;
using KKAO.Setup;

namespace KKAO
{
    // Verifications de la v2 : protocole 2 (actions, reglages exposes, demarrage a la demande), menu rapide,
    // paquets d'applis, installation de KKAO (dossiers temporaires, registre en memoire), reglages, interface.
    // Rien n'est ecrit hors du dossier temporaire du test, aucune fenetre n'est visible.
    internal sealed partial class SelfTest
    {
        string _temp;

        async Task RunV2Async(ToolConfig nodeTool)
        {
            _temp = Path.Combine(Path.GetTempPath(), "kkao-selftest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_temp);
            try
            {
                CheckSettingsRoundTrip();
                CheckActionsParser();
                await CheckProtocolV2Async(nodeTool);
                await CheckPackagesAsync(nodeTool);
                CheckRejectedPackages();
                CheckAliases();
                await CheckRenamedAppAsync(nodeTool);
                CheckSelfInstall();
                await RunOnlineAsync(nodeTool);
                CheckInterface();
            }
            finally
            {
                AppInstaller.TryDeleteDirectory(_temp);
            }
        }

        // settings.json : toutes les valeurs relues, champs inconnus conserves
        void CheckSettingsRoundTrip()
        {
            var path = Path.Combine(_temp, "settings.json");
            File.WriteAllText(path, "{\"futur\": {\"x\": 42}, \"autostart\": {\"ancien\": true}}", new UTF8Encoding(false));
            var s = KkaoSettings.Load(path);
            s.Theme = "light";
            s.CompleteQuest(Quests.InstallApp);
            s.SetQuickMenu(new[] { new QuickEntry { ToolId = "kalepin", ActionId = "quickNote", Label = "Quicknote" }, QuickEntry.Separator() });
            s.SetToolIconMode("kalepin", "own");
            s.IconMode = "all";
            s.OnboardingStep = "theme";
            s.SetWindow(10, 20, 1000, 700, false);
            var again = KkaoSettings.Load(path);
            var quick = again.QuickMenu;
            var raw = File.ReadAllText(path);
            var ok = again.Theme == "light" && again.QuestDone(Quests.InstallApp) && !again.QuestDone(Quests.QuickAction)
                && quick != null && quick.Count == 2 && quick[0].Label == "Quicknote" && quick[1].IsSeparator
                && again.ToolIconMode("kalepin") == "own" && again.IconMode == "all" && again.ShowsOwnIcon("kalepin") && again.ShowsOwnIcon("autre")
                && again.OnboardingStep == "theme" && again.WindowBounds?[2] == 1000 && again.Autostart("ancien") == true && raw.Contains("\"futur\"");
            Check("réglages : sauvegarde et relecture", ok, "thème, missions, menu rapide, icônes, guide, fenêtre relus ; champ inconnu « futur » conservé");
        }

        void CheckActionsParser()
        {
            var json = Json.ParseObject("{\"items\":[{\"id\":\"a\",\"label\":\"A\",\"default\":true,\"pin\":true},{\"id\":\"a\",\"label\":\"doublon\"},"
                + "{\"id\":\"b c\",\"label\":\"id invalide\"},{\"id\":\"d\",\"label\":\"D\",\"default\":true,\"enabled\":false},{\"id\":\"e\"},{\"label\":\"sans id\"}]}");
            var actions = ActionNode.ParseList(Json.Get(json, "items"));
            var settingsJson = Json.ParseObject("{\"sections\":[{\"title\":\"T\",\"items\":[{\"id\":\"x\",\"label\":\"X\",\"type\":\"toggle\",\"value\":true},"
                + "{\"id\":\"y\",\"label\":\"Y\",\"type\":\"choice\",\"value\":\"1\"},{\"id\":\"z\",\"label\":\"Z\",\"type\":\"inconnu\"},{\"id\":\"x\",\"label\":\"doublon\",\"type\":\"text\"},"
                + "{\"id\":\"b\",\"label\":\"B\",\"type\":\"button\"}]},{\"title\":\"vide\",\"items\":[]}]}");
            var sections = SettingsSection.ParseList(Json.Get(settingsJson, "sections"));
            var items = sections.SelectMany(x => x.Items).ToList();
            var ok = actions.Count == 2 && actions[0].Id == "a" && actions[0].Default && actions[0].Pin && actions[1].Id == "d" && !actions[1].Default && !actions[1].Enabled
                && sections.Count == 1 && items.Count == 4 && items[0].ValueBool && items[1].Type == "info" && items[2].Type == "info" && items[3].Type == "info";
            Check("catalogue et réglages : lecture stricte", ok,
                "ids en double, invalides ou sans libellé ignorés ; une seule action par défaut ; choix sans options, type inconnu et bouton sans action en lecture seule ; section vide retirée");
        }

        // Faux outil v2 : hello avec icone, actions, reglages, setSetting, demarrage a la demande
        async Task CheckProtocolV2Async(ToolConfig nodeTool)
        {
            var settings = KkaoSettings.InMemory();
            var config = new ToolConfig
            {
                Id = "fakev2",
                Name = "fakev2",
                ConfigName = null,
                ExeRaw = nodeTool.Exe,
                Exe = nodeTool.Exe,
                Cwd = nodeTool.Cwd,
                Protocol = true,
            };
            config.Args.AddRange(new[] { nodeTool.Args[0], "--id", "fakev2", "--v2" });
            var manager = new ToolManager(new[] { config }, settings, SynchronizationContext.Current);
            var notes = new List<ToolNotification>();
            manager.Notification += n => notes.Add(n);
            var tool = manager.Tools[0];
            try
            {
                tool.Start(userInitiated: true);
                var ready = await WaitFor(() => tool.IsReady && tool.HasActionsCatalog && tool.HasSettings, 10000);
                var welcomeV2 = await WaitFor(() => LogHas("[fakev2] welcome:" + Branding.Name + ":2:"), 3000);
                var actions = tool.Actions;
                var cached = settings.CachedActions("fakev2").Count;
                Check("protocole 2 : hello, catalogue d'actions, réglages exposés",
                    ready && welcomeV2 && tool.ToolProtocol == 2 && tool.Name == "Fake fakev2" && tool.HelloIcon != null && tool.HelloIcon.Length > 50
                    && actions.Count == 5 && actions.Count(a => a.Default) == 1 && actions.First(a => a.Default).Id == "quickNote"
                    && actions.Count(a => a.Pin) == 2 && cached == 5 && tool.Settings.Count == 1 && tool.Settings[0].Items.Count == 5,
                    "nom du hello (" + tool.Name + "), icône " + (tool.HelloIcon?.Length ?? 0) + " octets, " + actions.Count + " actions (id invalide ignoré), "
                    + cached + " gardées pour quand l'appli est arrêtée, " + tool.Settings.Sum(x => x.Items.Count) + " réglages (section vide retirée)");

                // setSetting : valeur acceptee puis refusee, reglages toujours republies
                var revision = tool.SettingsRevision;
                tool.SendSetSetting("mode", "b");
                var accepted = await WaitFor(() => tool.SettingsRevision > revision && tool.Settings[0].Items[0].ValueText == "b", 3000);
                revision = tool.SettingsRevision;
                tool.SendSetSetting("mode", "zz");
                var refused = await WaitFor(() => tool.SettingsRevision > revision && tool.Settings[0].Items[0].Error != null, 3000);
                Check("réglages exposés : setSetting aller-retour", accepted && refused && tool.Settings[0].Items[0].ValueText == "b",
                    "« mode » = b accepté, « zz » refusé (ancienne valeur + message d'erreur de l'appli)");

                // Menu rapide par defaut : les "pin" du catalogue ; personnalise ; action disparue
                var quick = new QuickMenuService(manager, settings);
                var defaults = quick.Resolve();
                quick.Save(new[]
                {
                    new QuickEntry { ToolId = "fakev2", ActionId = "newPostIt", Label = "Post-it" },
                    QuickEntry.Separator(),
                    new QuickEntry { ToolId = "fakev2", ActionId = "disparue", KnownLabel = "Ancienne" },
                    new QuickEntry { ToolId = "inconnu", ActionId = "x", KnownLabel = "Appli désinstallée" },
                    QuickEntry.Separator(),
                });
                var custom = quick.Resolve();
                var later = quick.Resolve(new QuickEntry { ToolId = "fakev2", ActionId = "later" });
                Check("menu rapide : défaut, personnalisé, actions disparues",
                    defaults.Count == 2 && defaults[0].Label == "Nouvelle note…" && defaults[0].Shortcut == "Ctrl+Alt+F1" && defaults[0].Bold && defaults[1].Label == "Nouveau post-it"
                    && custom.Count == 4 && custom[0].Label == "Post-it" && custom[1].Entry.IsSeparator && custom[2].Unavailable && custom[3].Unavailable && !later.Enabled,
                    "défaut = 2 « pin » du catalogue ; personnalisé : libellé renommé, séparateur de fin retiré, action et appli disparues signalées ; action désactivée par l'appli grisée");

                // Demarrage a la demande : appli arretee, clic sur une action rapide
                var stopped = false;
                manager.StopAndWait(tool, 10000, ok => stopped = ok);
                await WaitFor(() => stopped, 11000);
                var starts = tool.StartCount;
                var queued = tool.RunAction("quickNote", "quick");
                var delivered = await WaitFor(() => LogHas("[fakev2] command:quickNote:quick"), 10000);
                Check("démarrage à la demande : commande envoyée après welcome",
                    stopped && queued && delivered && tool.StartCount == starts + 1 && tool.PendingCommandCount == 0,
                    "appli arrêtée, action rapide cliquée → démarrée (lancement n°" + tool.StartCount + "), commande reçue avec source « quick »");

                // Appli impossible a demarrer : la commande en attente est abandonnee, sans rester bloquee
                var broken = new ToolConfig { Id = "fakebroken", Name = "Appli cassée", Exe = Path.Combine(_temp, "absent.exe"), ExeRaw = "absent.exe", Protocol = true };
                var brokenHost = manager.AddOrReplace(broken);
                var before = notes.Count;
                brokenHost.RunAction("x", "quick");
                Check("démarrage impossible : commande abandonnée", brokenHost.PendingCommandCount == 0 && brokenHost.RunState == ToolRunState.Stopped && notes.Skip(before).Any(n => n.FromKkao),
                    "programme introuvable → bulle « n'a pas pu démarrer », rien ne reste en attente");
            }
            finally
            {
                var done = false;
                manager.QuitAll(() => done = true);
                await WaitFor(() => done, 12000);
            }
        }

        // Paquet d'appli : construction (comme --bundle), installation, lancement, mise a jour, desinstallation
        async Task CheckPackagesAsync(ToolConfig nodeTool)
        {
            var source = Path.Combine(_temp, "source");
            Directory.CreateDirectory(Path.Combine(source, "sub"));
            File.Copy(nodeTool.Args[0], Path.Combine(source, "fake-tool.js"));
            File.WriteAllText(Path.Combine(source, "sub", "garde.txt"), "x");
            File.WriteAllText(Path.Combine(source, "sub", "exclu.log"), "x");
            File.WriteAllText(Path.Combine(source, "package.json"), "{\"version\":\"1.2.3\"}");
            var recipe = new BundleRecipe { Id = "fakepkg", Name = "Appli paquet", SourceDir = source, VersionRule = "package.json", IconStandIn = "postit", Summary = "Résumé" };
            recipe.Include.AddRange(new[] { "fake-tool.js", "package.json", "sub" });
            recipe.Exclude.Add(@"sub\*.log");
            recipe.App = new AppManifest { Id = "fakepkg", Name = "Appli paquet", Exe = "node", Protocol = true, Autostart = false };
            recipe.App.Args.AddRange(new[] { @".\fake-tool.js", "--id", "fakepkg", "--v2" });
            var packages = Path.Combine(_temp, "packages");
            Directory.CreateDirectory(packages);
            var built = Bundler.BuildPackage(recipe, packages, null);
            var catalogPath = Path.Combine(_temp, AppCatalog.FileName);
            File.WriteAllText(catalogPath, AppCatalog.Serialize(new[] { built.App }, _temp), new UTF8Encoding(false));
            var catalog = AppCatalog.Load(catalogPath);
            var app = catalog.Find("fakepkg");
            List<string> entries;
            using (var zip = ZipFile.OpenRead(built.App.PackagePath)) entries = zip.Entries.Select(e => e.FullName).ToList();
            var buildOk = app != null && app.Version == "1.2.3" && app.HasPackage && app.Sha256?.Length == 64 && File.Exists(app.IconPath)
                && entries.Contains(AppManifest.FileName) && entries.Contains("kkao-icon.png") && entries.Contains("sub/garde.txt") && !entries.Contains("sub/exclu.log");
            Check("paquet d'appli construit", buildOk,
                built.App.PackagePath + " : " + entries.Count + " entrées (kkao-app.json, icône de remplacement, dossier inclus, *.log exclu), version lue dans package.json, catalogue relu");

            var appsRoot = Path.Combine(_temp, "apps");
            var installer = new AppInstaller(appsRoot);
            var manifest = installer.Install(app.PackagePath, app.Id, app.Sha256, null, CancellationToken.None);
            var installed = installer.ScanInstalled(null);
            var config = installed.FirstOrDefault();
            var installOk = manifest.Version == "1.2.3" && installed.Count == 1 && config.Source == ToolSource.Installed && config.ConfigName == "Appli paquet"
                && config.Args[0] == Path.Combine(appsRoot, "fakepkg", "fake-tool.js") && File.Exists(config.Exe) && File.Exists(config.IconPath)
                && !Directory.GetDirectories(appsRoot).Any(d => Path.GetFileName(d).StartsWith(".", StringComparison.Ordinal));
            Check("appli installée depuis son paquet", installOk,
                "dans " + Path.Combine(appsRoot, "fakepkg") + ", empreinte vérifiée, chemins relatifs au dossier de l'appli, node trouvé (" + config?.Exe + "), aucun dossier temporaire laissé");

            var manager = new ToolManager(installed, KkaoSettings.InMemory(), SynchronizationContext.Current);
            var host = manager.Tools[0];
            try
            {
                host.Start(userInitiated: true);
                var ready = await WaitFor(() => host.IsReady && host.HasActionsCatalog, 10000);
                // Mise a jour : refusee tant que l'appli tourne (fichiers utilises), acceptee une fois arretee
                var lockedRefused = false;
                try
                {
                    using (File.Open(Path.Combine(appsRoot, "fakepkg", "fake-tool.js"), FileMode.Open, FileAccess.Read, FileShare.None))
                        installer.Install(app.PackagePath, app.Id, app.Sha256, null, CancellationToken.None);
                }
                catch (InstallException)
                {
                    lockedRefused = true;
                }
                var stopped = false;
                manager.StopAndWait(host, 10000, ok => stopped = ok);
                await WaitFor(() => stopped, 11000);
                File.WriteAllText(Path.Combine(source, "package.json"), "{\"version\":\"1.3.0\"}");
                var built2 = Bundler.BuildPackage(recipe, packages, null);
                var updated = installer.Install(built2.App.PackagePath, "fakepkg", built2.App.Sha256, null, CancellationToken.None);
                host.ReplaceConfig(installer.ScanInstalled(null).First());
                host.Start(userInitiated: true);
                var readyAgain = await WaitFor(() => host.IsReady, 10000);
                Check("appli installée lancée, mise à jour", ready && lockedRefused && stopped && updated.Version == "1.3.0" && readyAgain && host.Config.Version == "1.3.0",
                    "hello reçu depuis le dossier d'installation ; mise à jour refusée tant qu'un fichier est utilisé (message clair), faite une fois l'appli arrêtée, relancée en 1.3.0");
                stopped = false;
                manager.StopAndWait(host, 10000, ok => stopped = ok);
                await WaitFor(() => stopped, 11000);
                installer.Uninstall("fakepkg");
                Check("appli désinstallée", stopped && !Directory.Exists(Path.Combine(appsRoot, "fakepkg")) && installer.ScanInstalled(null).Count == 0
                    && !Directory.GetDirectories(appsRoot).Any(),
                    "dossier de l'appli supprimé (ses données utilisateur ne sont jamais dans ce dossier)");
            }
            finally
            {
                var done = false;
                manager.QuitAll(() => done = true);
                await WaitFor(() => done, 12000);
            }
        }

        // Paquets refuses : chemin hors du dossier (zip slip), mauvais id, empreinte differente
        void CheckRejectedPackages()
        {
            var dir = Path.Combine(_temp, "refus");
            Directory.CreateDirectory(dir);
            string Make(string name, string id, string extraEntry)
            {
                var path = Path.Combine(dir, name);
                using (var stream = new FileStream(path, FileMode.Create))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    using (var writer = new StreamWriter(zip.CreateEntry(AppManifest.FileName).Open()))
                        writer.Write("{\"format\":1,\"id\":\"" + id + "\",\"name\":\"X\",\"version\":\"1.0.0\",\"exe\":\"x.exe\"}");
                    if (extraEntry != null)
                        using (var writer = new StreamWriter(zip.CreateEntry(extraEntry).Open())) writer.Write("x");
                }
                return path;
            }
            var installer = new AppInstaller(Path.Combine(dir, "apps"));
            var results = new List<string>();
            foreach (var test in new[]
            {
                Tuple.Create(Make("slip.zip", "slip", "../../evil.txt"), "slip", (string)null),
                Tuple.Create(Make("autre.zip", "autre", null), "attendu", (string)null),
                Tuple.Create(Make("sha.zip", "sha", null), "sha", new string('0', 64)),
            })
            {
                try
                {
                    installer.Install(test.Item1, test.Item2, test.Item3, null, CancellationToken.None);
                    results.Add(Path.GetFileName(test.Item1) + " accepté !");
                }
                catch (InstallException ex)
                {
                    results.Add(Path.GetFileName(test.Item1) + " refusé (" + ex.Message + ")");
                }
            }
            var ok = results.All(r => r.Contains("refusé")) && !File.Exists(Path.Combine(dir, "evil.txt")) && !File.Exists(Path.Combine(_temp, "evil.txt"))
                && (!Directory.Exists(Path.Combine(dir, "apps")) || !Directory.EnumerateFileSystemEntries(Path.Combine(dir, "apps")).Any());
            Check("paquets refusés (chemin interdit, mauvais id, empreinte)", ok, string.Join(" ; ", results));
        }

        // Appli renommee (papernotes -> kalepin) : ses reglages, son catalogue garde et le menu rapide passent au nouvel id
        void CheckAliases()
        {
            var path = Path.Combine(_temp, "settings-alias.json");
            File.WriteAllText(path, "{\"autostart\":{\"papernotes\":false},\"actionsCache\":{\"papernotes\":[{\"id\":\"quickNote\",\"label\":\"Nouvelle note…\"}]},"
                + "\"icons\":{\"mode\":\"launcher\",\"tools\":{\"papernotes\":\"own\"}},"
                + "\"quickMenu\":{\"custom\":true,\"items\":[{\"tool\":\"papernotes\",\"action\":\"quickNote\",\"label\":\"Quicknote\"},{\"type\":\"separator\"},{\"tool\":\"instruktion\",\"action\":\"pause\"}]}}",
                new UTF8Encoding(false));
            var renamed = new ToolConfig { Id = "kalepin", Name = "Kalepin", ConfigName = "Kalepin", Exe = "x.exe", Protocol = true };
            renamed.Aliases.Add("papernotes");
            new ToolManager(new[] { renamed }, KkaoSettings.Load(path), SynchronizationContext.Current);
            var after = KkaoSettings.Load(path);
            var quick = after.QuickMenu;
            var catalogPath = Path.Combine(_temp, "catalog-alias.json");
            File.WriteAllText(catalogPath, "{\"format\":1,\"apps\":[{\"id\":\"kalepin\",\"name\":\"Kalepin\",\"aliases\":[\"papernotes\"]}]}", new UTF8Encoding(false));
            var catalog = AppCatalog.Load(catalogPath);
            var ok = after.Autostart("kalepin") == false && after.Autostart("papernotes") == null
                && after.CachedActions("kalepin").Count == 1 && after.CachedActions("papernotes").Count == 0
                && after.ToolIconMode("kalepin") == "own" && quick != null && quick.Count == 3 && quick[0].ToolId == "kalepin" && quick[0].Label == "Quicknote"
                && quick[2].ToolId == "instruktion" && !File.ReadAllText(path).Contains("papernotes")
                && catalog.FindByAlias("papernotes")?.Id == "kalepin" && catalog.FindByAlias("kalepin") == null;
            Check("appli renommée : réglages repris (aliases)", ok,
                "démarrage, catalogue gardé, icône et menu rapide de « papernotes » passés à « kalepin », plus aucune trace de l'ancien id dans settings.json ; le catalogue retrouve l'appli par son ancien id");
        }

        // Appli installee sous un ancien id : proposee a la mise a jour, remplacee, ancien dossier supprime
        async Task CheckRenamedAppAsync(ToolConfig nodeTool)
        {
            var source = Path.Combine(_temp, "source-renommee");
            Directory.CreateDirectory(source);
            File.Copy(nodeTool.Args[0], Path.Combine(source, "fake-tool.js"));
            BundleRecipe Recipe(string id, string name)
            {
                var recipe = new BundleRecipe { Id = id, Name = name, SourceDir = source, VersionRule = "1.0.0" };
                recipe.Include.Add("fake-tool.js");
                recipe.App = new AppManifest { Id = id, Name = name, Exe = "node", Protocol = true };
                recipe.App.Args.AddRange(new[] { @".\fake-tool.js", "--id", id, "--v2" });
                return recipe;
            }
            var packages = Path.Combine(_temp, "packages-renommee");
            Directory.CreateDirectory(packages);
            var appsRoot = Path.Combine(_temp, "apps-renommee");
            var installer = new AppInstaller(appsRoot);
            var oldBuilt = Bundler.BuildPackage(Recipe("ancienpkg", "Ancien nom"), packages, null);
            installer.Install(oldBuilt.App.PackagePath, "ancienpkg", oldBuilt.App.Sha256, null, CancellationToken.None);
            var newRecipe = Recipe("nouveaupkg", "Nouveau nom");
            newRecipe.Aliases.Add("ancienpkg");
            var newBuilt = Bundler.BuildPackage(newRecipe, packages, null);
            var catalogPath = Path.Combine(_temp, "catalog-renommee.json");
            File.WriteAllText(catalogPath, AppCatalog.Serialize(new[] { newBuilt.App }, _temp), new UTF8Encoding(false));
            var catalog = AppCatalog.Load(catalogPath);

            var settings = KkaoSettings.InMemory();
            settings.SetAutostart("ancienpkg", true);
            var manager = new ToolManager(installer.ScanInstalled(null), settings, SynchronizationContext.Current);
            var layout = new InstallLayout { InstallRoot = Path.Combine(_temp, "kkao-renommee"), StartMenuDir = Path.Combine(_temp, "menu-renommee"), DesktopDir = Path.Combine(_temp, "bureau-renommee") };
            var controller = new Ui.KkaoController(manager, settings, catalog, installer, new SelfInstaller(layout, new MemoryRegistry()), false, null, SynchronizationContext.Current);
            try
            {
                var before = controller.Snapshot();
                var shown = before.Count == 1 && before[0].Id == "ancienpkg" && before[0].Name == "Nouveau nom" && before[0].UpdateAvailable;
                controller.InstallApp("ancienpkg", startAfter: false);
                var replaced = await WaitFor(() => manager.Find("nouveaupkg") != null && manager.Find("ancienpkg") == null && !Directory.Exists(Path.Combine(appsRoot, "ancienpkg")), 15000);
                var after = controller.Snapshot();
                Check("appli installée sous un ancien id : remplacée",
                    shown && replaced && after.Count == 1 && after[0].Id == "nouveaupkg" && !after[0].UpdateAvailable && settings.Autostart("nouveaupkg") == true
                    && Directory.Exists(Path.Combine(appsRoot, "nouveaupkg")),
                    "proposée sous son nouveau nom avec « Mettre à jour » ; après la mise à jour : nouvel id installé, ancien dossier supprimé, réglage « Démarrer avec KKAO » repris");
            }
            finally
            {
                var done = false;
                manager.QuitAll(() => done = true);
                await WaitFor(() => done, 12000);
            }
        }

        // Installation de KKAO pour l'utilisateur : dans des dossiers temporaires, registre en memoire
        void CheckSelfInstall()
        {
            var source = Path.Combine(_temp, "extrait");
            Directory.CreateDirectory(Path.Combine(source, AppCatalog.PackagesDir));
            var exeName = Path.GetFileName(Paths.ExePath);
            File.Copy(Paths.ExePath, Path.Combine(source, exeName));
            File.WriteAllText(Path.Combine(source, AppCatalog.FileName), "{\"format\":1,\"apps\":[]}");
            File.WriteAllText(Path.Combine(source, AppCatalog.PackagesDir, "x-1.0.0.zip"), "zip");
            var layout = new InstallLayout
            {
                InstallRoot = Path.Combine(_temp, "Programs", Paths.TechName),
                StartMenuDir = Path.Combine(_temp, "Menu Démarrer"),
                DesktopDir = Path.Combine(_temp, "Bureau"),
            };
            // Ancien paquet d'une version precedente : retire
            Directory.CreateDirectory(Path.Combine(layout.InstallRoot, AppCatalog.PackagesDir));
            File.WriteAllText(Path.Combine(layout.InstallRoot, AppCatalog.PackagesDir, "ancien-0.9.zip"), "old");
            var registry = new MemoryRegistry();
            var self = new SelfInstaller(layout, registry);
            self.Install(source, new InstallOptions { StartWithWindows = true, DesktopShortcut = true }, null);
            var exe = self.InstalledExe;
            var run = registry.GetString(StartupManager.RunKey, StartupManager.ValueName);
            var target = File.Exists(self.StartMenuShortcut) ? SelfInstaller.ReadShortcutTarget(self.StartMenuShortcut) : null;
            var ok = File.Exists(exe) && File.Exists(Path.Combine(layout.InstallRoot, AppCatalog.FileName))
                && File.Exists(Path.Combine(layout.InstallRoot, AppCatalog.PackagesDir, "x-1.0.0.zip"))
                && !File.Exists(Path.Combine(layout.InstallRoot, AppCatalog.PackagesDir, "ancien-0.9.zip"))
                && File.Exists(Path.Combine(layout.InstallRoot, SelfInstaller.MarkerFile))
                && string.Equals(target, exe, StringComparison.OrdinalIgnoreCase) && File.Exists(self.DesktopShortcut)
                && run == "\"" + exe + "\" " + StartupManager.AutostartArg && StartupManager.IsEnabledFor(exe, registry)
                && registry.GetString(SelfInstaller.UninstallKey, "UninstallString") == "\"" + exe + "\" --uninstall"
                && registry.GetString(SelfInstaller.UninstallKey, "DisplayVersion") == Program.Version
                && self.IsInstalled && self.InstalledVersion == Program.Version;
            self.RemoveIntegration();
            var removed = !File.Exists(self.StartMenuShortcut) && !File.Exists(self.DesktopShortcut)
                && registry.GetString(StartupManager.RunKey, StartupManager.ValueName) == null && !registry.KeyExists(SelfInstaller.UninstallKey);
            Check("installation de KKAO (dossiers temporaires, registre en mémoire)", ok && removed,
                "exe, catalogue et paquets copiés (ancien paquet retiré), raccourcis Démarrer et Bureau vers " + exe + ", démarrage avec Windows « --autostart », "
                + "entrée « Applications installées » (version " + Program.Version + ") ; puis tout retiré par la désinstallation");
        }

        // Interface : chaque page et chaque etape du guide dessinees en memoire, dans les deux themes
        void CheckInterface()
        {
            try
            {
                var renders = Ui.Screenshots.Render(null).ToList();
                Check("interface : pages et guide dessinés (sombre et clair)", renders.Count == 38,
                    renders.Count + " rendus hors écran (accueil, applis, fiche, actions rapides, missions, réglages haut et bas, succès, visite guidée, 7 étapes du guide, menu de l'icône)");
            }
            catch (Exception ex)
            {
                Check("interface : pages et guide dessinés (sombre et clair)", false, ex.GetBaseException().Message);
            }
        }
    }
}
