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
    // Mises a jour automatiques (2.3) avec un catalogue en ligne simule (file:///) et une fausse appli Node :
    // telechargement en arriere-plan, appli occupee (status.busy) jamais fermee, PC inactif → mise a jour + relance,
    // version qui ne demarre pas → retour arriere et version ecartee, applis retenues au demarrage de KKAO.
    internal sealed partial class SelfTest
    {
        async Task CheckAutoUpdateAsync(ToolConfig nodeTool)
        {
            var root = Path.Combine(_temp, "auto");
            var source = Path.Combine(root, "source");
            Directory.CreateDirectory(source);
            File.Copy(nodeTool.Args[0], Path.Combine(source, "fake-tool.js"));
            var latest = Path.Combine(root, "latest");
            Directory.CreateDirectory(latest);
            var catalogPath = Path.Combine(latest, AppCatalog.FileName);

            // « Publie » une version : son paquet + le catalogue « latest » qui pointe dessus
            CatalogApp Publish(string version, bool broken = false)
            {
                var dir = Path.Combine(root, "release-" + version);
                var built = BuildOnlinePackage(source, dir, "auto", version, broken);
                PublishLocally(dir, new[] { built.App }, null);
                File.Copy(Path.Combine(dir, AppCatalog.FileName), catalogPath, true);
                return built.App;
            }
            Publish("1.0.0");

            var remote = new RemoteCatalogService(new Uri(catalogPath).AbsoluteUri, Path.Combine(root, "cache"));
            remote.Apply(remote.Fetch());
            var appsRoot = Path.Combine(root, "apps");
            var installer = new AppInstaller(appsRoot);
            var settings = KkaoSettings.InMemory();
            var manager = new ToolManager(new List<ToolConfig>(), settings, SynchronizationContext.Current);
            var layout = new InstallLayout { InstallRoot = Path.Combine(root, "kkao"), StartMenuDir = Path.Combine(root, "menu"), DesktopDir = Path.Combine(root, "bureau") };
            var controller = new Ui.KkaoController(manager, settings, AppCatalog.Empty(), installer, new SelfInstaller(layout, new MemoryRegistry()), false, null,
                SynchronizationContext.Current, remote);
            var idle = TimeSpan.Zero;
            controller.IdleProbe = () => idle;
            controller.VerifyTimeoutMs = 15000;
            var messages = new List<string>();
            controller.Message += (title, text, error) => messages.Add((error ? "ERREUR " : "") + title + " : " + text);
            var downloads = Path.Combine(appsRoot, AppInstaller.DownloadsDirName);
            Func<string> version = () => manager.Find("auto")?.Config.Version;
            Func<ToolHost> tool = () => manager.Find("auto");

            async Task<bool> Refresh(string expected)
            {
                controller.CheckForUpdates(userInitiated: false);
                return await WaitFor(() => !controller.CheckingUpdates && controller.Catalog.Find("auto")?.Version == expected, 10000);
            }

            try
            {
                Check("mise à jour automatique activée par défaut", settings.AutoUpdate && controller.AutoUpdate,
                    "réglage « autoUpdate » absent de settings.json → activé");

                controller.InstallApp("auto", startAfter: true);
                await WaitFor(() => tool()?.IsReady == true && !controller.IsInstalling("auto"), 15000);

                // 1. Occupee : status.busy lu ; le telechargement se fait, l'installation attend (meme PC inactif)
                tool().SendCommand("busy");
                var busy = await WaitFor(() => tool().IsBusy, 5000);
                Publish("1.1.0");
                await Refresh("1.1.0");
                var downloaded = await WaitFor(() => File.Exists(Path.Combine(downloads, "auto-1.1.0.zip")), 10000);
                idle = TimeSpan.FromHours(1);
                controller.AutoUpdateTick();
                await Task.Delay(1500);
                var waited = version() == "1.0.0" && tool().IsReady && !controller.IsInstalling("auto");
                var plan = controller.AutoUpdatePlan();
                Check("appli occupée : téléchargée, jamais fermée", busy && downloaded && waited && !messages.Any(m => m.Contains("est disponible"))
                    && plan.Any(l => l.Contains("1.1.0") && l.Contains("prête")),
                    "status.busy → occupée ; 1.1.0 téléchargée en arrière-plan, appli laissée ouverte malgré 1 h d'inactivité, pas de bulle « disponible » ; « " + string.Join(" / ", plan) + " »");

                // 2. Plus occupee + PC inactif : fermee, mise a jour, relancee, verifiee (ancienne version supprimee)
                tool().SendCommand("free");
                await WaitFor(() => !tool().IsBusy, 5000);
                idle = TimeSpan.Zero;
                controller.AutoUpdateTick();
                await Task.Delay(800);
                var notWhileActive = version() == "1.0.0";
                idle = TimeSpan.FromHours(1);
                controller.AutoUpdateTick();
                var updated = await WaitFor(() => version() == "1.1.0" && tool()?.IsReady == true, 20000);
                var verified = await WaitFor(() => settings.VerifyPending("auto") == null && !installer.HasPrevious("auto"), 10000);
                Check("PC inactif : fermée, mise à jour, relancée", notWhileActive && updated && verified
                    && messages.Any(m => m.StartsWith("Appli en ligne 1.1.0 est installée", StringComparison.Ordinal)) && !File.Exists(Path.Combine(downloads, "auto-1.1.0.zip")),
                    "rien tant que l'utilisateur est actif ; après 30 min d'inactivité : quit, 1.1.0 installée, relancée, hello reçu → ancienne version supprimée, une bulle « installée »");

                // 3. Version qui ne demarre pas : installee appli arretee, retour arriere au premier lancement, puis ecartee
                var stopped = false;
                manager.StopAndWait(tool(), 12000, ok => stopped = ok);
                await WaitFor(() => stopped, 13000);
                idle = TimeSpan.Zero;
                Publish("1.2.0", broken: true);
                await Refresh("1.2.0");
                var brokenInstalled = await WaitFor(() => version() == "1.2.0" && !controller.IsInstalling("auto"), 20000);
                var keptOld = installer.HasPrevious("auto") && settings.VerifyPending("auto") == "1.2.0" && tool()?.IsAlive == false;
                tool().Start(userInitiated: true);
                var rolledBack = await WaitFor(() => version() == "1.1.0" && tool()?.IsReady == true, 20000);
                controller.AutoUpdateTick();
                await Task.Delay(1500);
                var skipped = settings.SkippedVersion("auto") == "1.2.0" && version() == "1.1.0" && !controller.IsInstalling("auto")
                    && controller.AutoUpdatePlan().Any(l => l.Contains("1.2.0") && l.Contains("ignorée"));
                Check("version qui ne démarre pas : retour arrière", brokenInstalled && keptOld && rolledBack && skipped
                    && messages.Any(m => m.Contains("1.2.0 ne démarre pas") && m.Contains("1.1.0")),
                    "1.2.0 installée appli arrêtée (1.1.0 gardée) ; au lancement elle s'arrête → 1.1.0 remise et relancée, bulle d'erreur, 1.2.0 plus jamais installée automatiquement");

                // 4. Demarrage de KKAO : paquet deja telecharge → appli retenue, installee, puis lancee
                stopped = false;
                manager.StopAndWait(tool(), 12000, ok => stopped = ok);
                await WaitFor(() => stopped, 13000);
                settings.AutoUpdate = false;
                Publish("1.3.0");
                await Refresh("1.3.0");
                var app = controller.Catalog.Find("auto");
                Downloader.DownloadFile(app.Url, Path.Combine(downloads, "auto-1.3.0.zip"), app.Size, null, CancellationToken.None);
                settings.AutoUpdate = true;
                var held = controller.InstallReadyUpdatesAtStartup(new[] { "auto" });
                manager.StartAutostart(t => held.Contains(t.Id));
                var heldOk = held.Contains("auto") && tool()?.IsAlive == false;
                var startedAfter = await WaitFor(() => version() == "1.3.0" && tool()?.IsReady == true, 20000);
                Check("démarrage de KKAO : mise à jour avant le lancement", heldOk && startedAfter,
                    "1.3.0 déjà téléchargée → appli retenue au lancement, installée, puis démarrée");
            }
            finally
            {
                var done = false;
                manager.QuitAll(() => done = true);
                await WaitFor(() => done, 12000);
            }
        }
    }
}
