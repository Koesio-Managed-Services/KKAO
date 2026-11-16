using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace KKAO
{
    // KKAO.exe --selftest <rapport.json> [--tools <fichier>] : verifie le coeur de KKAO sans aucune interface
    // (pas d'icone, pas de fenetre, notifications captees) contre de faux outils Node (tests\fake-tool.js).
    // Seuls les outils dont l'id commence par "fake" sont lances : jamais Kalepin ni InstruKtion.
    internal sealed partial class SelfTest
    {
        const int GlobalTimeoutMs = 180000;
        // Nom affiche de KKAO tel qu'il apparait dans les menus (Branding.cs)
        static readonly string Host = MenuBuilder.Escape(Branding.Name);

        readonly string _reportPath;
        readonly string _toolsArg;
        readonly List<Dictionary<string, object>> _steps = new List<Dictionary<string, object>>();
        readonly List<string> _log = new List<string>();
        readonly object _logGate = new object();
        readonly List<ToolNotification> _notes = new List<ToolNotification>();
        readonly Stopwatch _clock = Stopwatch.StartNew();
        ToolManager _tools;
        ToolsFile _config;
        string _marker;
        string _fatal;
        bool _finished;

        SelfTest(string reportPath, string toolsArg)
        {
            _reportPath = reportPath;
            _toolsArg = toolsArg;
        }

        bool AllOk => _fatal == null && _steps.Count > 0 && _steps.All(s => (bool)s["ok"]);

        public static int Run(string reportPath, string toolsArg)
        {
            reportPath = Path.GetFullPath(string.IsNullOrWhiteSpace(reportPath) ? "kkao-selftest.json" : reportPath);
            Log.FilePath = Path.ChangeExtension(reportPath, ".log");
            try { File.Delete(Log.FilePath); }
            catch { }
            var test = new SelfTest(reportPath, toolsArg);
            Log.Tap = test.OnLog;
            Log.Write("Test automatique de KKAO " + Program.Version);

            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            var ui = SynchronizationContext.Current;
            var context = new ApplicationContext();
            var watchdog = new System.Windows.Forms.Timer { Interval = GlobalTimeoutMs };
            watchdog.Tick += (s, e) =>
            {
                watchdog.Stop();
                if (test._finished) return;
                test._fatal = "délai global de " + GlobalTimeoutMs / 1000 + " s dépassé";
                test.Finish();
                context.ExitThread();
            };
            ui.Post(async _ =>
            {
                try
                {
                    await test.RunAsync();
                }
                catch (Exception ex)
                {
                    test._fatal = ex.ToString();
                }
                if (test._finished) return;
                test.Finish();
                context.ExitThread();
            }, null);
            watchdog.Start();
            Application.Run(context);
            watchdog.Dispose();
            Log.Flush();
            return test.AllOk ? 0 : 1;
        }

        #region Scenario

        async Task RunAsync()
        {
            // --- Verifications sans processus
            CheckPriorityTable();
            CheckQuoting();
            CheckPaths();
            CheckMenuParser();
            CheckIcons();
            CheckFit();

            // --- tools.json de test
            var exeDir = Path.GetDirectoryName(Application.ExecutablePath);
            var toolsPath = _toolsArg != null ? Path.GetFullPath(_toolsArg) : LocateSelfTestTools(exeDir);
            if (toolsPath == null || !File.Exists(toolsPath))
            {
                Check("tools.json de test", false, "introuvable : " + (toolsPath ?? @"tests\selftest-tools.json"));
                return;
            }
            _config = ToolsFile.Load(toolsPath);
            var ignored = _config.Tools.Where(t => !IsFake(t)).Select(t => t.Id).ToList();
            _tools = new ToolManager(_config, KkaoSettings.InMemory(), SynchronizationContext.Current, IsFake);
            _tools.Notification += n => _notes.Add(n);
            var fake1 = Tool("fake1");
            var fake2 = Tool("fake2");
            var fake3 = Tool("fake3");
            var launcher = Tool("fake-launcher");
            var configOk = fake1 != null && fake2 != null && fake3 != null && launcher != null
                && File.Exists(fake1.Config.Exe) && fake1.Config.Args.Count > 0 && File.Exists(fake1.Config.Args[0])
                && fake1.Config.Protocol && !launcher.Config.Protocol && fake1.Config.Autostart && fake2.Config.Autostart && !fake3.Config.Autostart
                && _config.Problems.Count == 0;
            Check("tools.json de test", configOk,
                toolsPath + " ; node = " + fake1?.Config.Exe + " ; script = " + fake1?.Config.Args.FirstOrDefault()
                + (ignored.Count > 0 ? " ; ignorés (id sans « fake ») : " + string.Join(", ", ignored) : "")
                + (_config.Problems.Count > 0 ? " ; problèmes : " + string.Join(" / ", _config.Problems) : ""));
            if (!configOk) return;

            _marker = ArgAfter(fake2.Config.Args, "--retry-once");
            DeleteMarker();

            // --- Demarrage automatique
            _tools.StartAutostart();
            Check("démarrage automatique (autostart)", fake1.IsAlive && fake2.IsAlive && !fake3.IsAlive && launcher.StartCount == 0,
                "fake1 et fake2 lancés, fake3 (autostart false) et le lanceur simple non lancés ; tube de fake1 : " + fake1.PipePath);

            // --- hello -> welcome
            var helloOk = await WaitFor(() => fake1.RunState == ToolRunState.Running, 10000);
            var welcomeOk = helloOk && await WaitFor(() => LogHas("[fake1] welcome:" + Branding.Name + ":" + ToolHost.ProtocolVersion + ":" + Program.Version), 5000);
            Check("hello → welcome", helloOk && welcomeOk,
                "hello reçu = " + helloOk + " (version de l'outil " + fake1.ToolVersion + "), welcome reçu et relu par l'outil = " + welcomeOk);

            // --- menu
            await WaitFor(() => fake1.Menu.Count > 0, 5000);
            var stats = MenuStats.Of(fake1.Menu);
            Check("menu reçu et lu", stats.Leaves == 8 && stats.Submenus == 2 && stats.Separators == 2 && stats.Pinned == 1 && stats.Checks == 1
                && stats.Radios == 2 && stats.CheckedCount == 2 && stats.Disabled == 1 && stats.Bold == 1 && stats.WithShortcut == 1,
                stats + " (attendu : 8 éléments, 2 sous-menus, 2 séparateurs, 1 épinglé, 1 case, 2 radios, 2 cochés, 1 désactivé, 1 en gras, 1 raccourci ; l'élément de type inconnu est ignoré)");

            // --- notification
            var notifyOk = await WaitFor(() => _notes.Any(n => n.Tool == fake1), 5000);
            var note = _notes.FirstOrDefault(n => n.Tool == fake1);
            Check("notification captée (pas affichée)",
                notifyOk && note.Title == "Notification fake1" && note.Text == "Bonjour é ✓ & <b>" && note.Kind == "warn" && !note.FromKkao,
                note == null ? "aucune notification" : "titre « " + note.Title + " », texte « " + note.Text + " », type " + note.Kind);

            // --- lignes illisibles / types inconnus
            await WaitFor(() => fake1.IgnoredLines >= 1 && fake1.UnknownMessages >= 1, 3000);
            Check("ligne illisible et type inconnu ignorés", fake1.IgnoredLines == 1 && fake1.UnknownMessages == 1 && fake1.RunState == ToolRunState.Running,
                fake1.IgnoredLines + " ligne(s) illisible(s), " + fake1.UnknownMessages + " message(s) de type inconnu, l'outil reste connecté");

            // --- stdout / stderr
            var outputOk = await WaitFor(() => LogHas("[fake1] stdout : fake fake1 stdout ok") && LogHas("[fake1] stderr : fake fake1 stderr ok"), 5000);
            await WaitFor(() => LogCount("[fake1] stdout : fake fake1 bruit ") >= 200, 3000);
            var noise = LogCount("[fake1] stdout : fake fake1 bruit ");
            Check("stdout/stderr lus en continu vers le journal", outputOk && noise == 200,
                noise + " lignes de 1 Ko écrites avant la connexion puis journalisées (l'outil n'a pas été bloqué), stdout et stderr présents");

            // --- retry -> relance
            var respawnOk = await WaitFor(() => fake2.StartCount == 2 && fake2.RunState == ToolRunState.Running, 15000);
            Check("retry → relance après « after »",
                respawnOk && fake2.ExitCodes.FirstOrDefault() == 75 && !KkaoNotes().Any(n => n.Tool == fake2) && LogHas("[fake2] retry dans 500 ms"),
                "lancements : " + fake2.StartCount + ", codes de sortie : " + string.Join(", ", fake2.ExitCodes) + ", aucune bulle « s'est arrêté »");

            // --- priorite des etats sur de vrais outils
            await WaitFor(() => fake1.Status == StatusState.Warn && fake2.Status == StatusState.Active, 5000);
            var warnActive = _tools.AggregateState();
            fake2.SendCommand("status:rec");
            await WaitFor(() => _tools.AggregateState() == StatusState.Rec, 3000);
            var warnRec = _tools.AggregateState();
            var tooltipRec = _tools.BuildTooltip();
            fake2.SendCommand("status:idle");
            await WaitFor(() => fake2.Status == StatusState.Idle, 3000);
            var warnIdle = _tools.AggregateState();
            Check("priorité des états (outils en marche)",
                warnActive == StatusState.Warn && warnRec == StatusState.Rec && warnIdle == StatusState.Warn
                && tooltipRec.StartsWith(Branding.Name + "\n", StringComparison.Ordinal) && tooltipRec.Length <= 63,
                "warn + active → " + warnActive + " ; warn + rec → " + warnRec + " ; warn + idle → " + warnIdle
                + " ; infobulle « " + tooltipRec.Replace("\n", " | ") + " » (" + tooltipRec.Length + " car.)");

            // --- commande aller-retour
            var watch = Stopwatch.StartNew();
            var sent = fake1.SendCommand("zone-é");
            var roundTrip = sent && await WaitFor(() => LogHas("[fake1] command:zone-é"), 3000);
            Check("aller-retour d'une commande", roundTrip, "id « zone-é » envoyé, renvoyé par l'outil en " + watch.ElapsedMilliseconds + " ms");

            // --- menu KKAO construit en memoire (jamais affiche)
            CheckMenuRendering(fake1, fake2, fake3, launcher);

            // --- fin inattendue
            var before = KkaoNotes().Count;
            fake2.SendCommand("crash:3");
            var crashed = await WaitFor(() => fake2.RunState == ToolRunState.Stopped, 5000) && await WaitFor(() => KkaoNotes().Count > before, 2000);
            var crashNote = KkaoNotes().Skip(before).FirstOrDefault();
            var aggregateAfterCrash = _tools.AggregateState();
            Check("fin inattendue (code ≠ 0) → notification",
                crashed && crashNote != null && crashNote.Title == "Faux outil 2 s'est arrêté (code 3)" && crashNote.Kind == "error"
                && fake2.Crashed && aggregateAfterCrash == StatusState.Error,
                crashNote == null ? "aucune notification" : "« " + crashNote.Title + " » / « " + crashNote.Text + " », pastille " + aggregateAfterCrash);

            // --- Demarrer (choix de l'utilisateur)
            fake2.Start(userInitiated: true);
            var restarted = await WaitFor(() => fake2.RunState == ToolRunState.Running, 10000);
            Check("Démarrer après un arrêt", restarted && !fake2.Crashed && fake2.StartCount == 3,
                "lancement n°" + fake2.StartCount + ", plus de pastille d'erreur (" + _tools.AggregateState() + ")");

            // --- l'outil ferme lui-meme son tube
            before = KkaoNotes().Count;
            fake2.SendCommand("closepipe");
            var closedByTool = await WaitFor(() => fake2.RunState == ToolRunState.Stopped, 5000);
            Check("tube fermé par l'outil", closedByTool && fake2.ExitCodes.LastOrDefault() == 0 && KkaoNotes().Count == before && LogHas("[fake2] l'outil a ferme le tube"),
                "déconnexion vue, puis fin du processus (code " + fake2.ExitCodes.LastOrDefault() + "), aucune bulle");

            // --- KKAO ferme le tube (KKAO quitte ou plante) : l'outil doit quitter de lui-meme
            fake2.Start(userInitiated: true);
            await WaitFor(() => fake2.RunState == ToolRunState.Running, 10000);
            before = KkaoNotes().Count;
            var exitsBefore = fake2.ExitCodes.Count;
            watch.Restart();
            fake2.ClosePipe();
            var pipeClosed = await WaitFor(() => fake2.RunState == ToolRunState.Stopped, 10000);
            Check("tube fermé par KKAO → l'outil quitte seul",
                pipeClosed && fake2.ExitCodes.Count == exitsBefore + 1 && fake2.ExitCodes.Last() == 0 && KkaoNotes().Count == before && watch.ElapsedMilliseconds < 10000,
                "code " + fake2.ExitCodes.LastOrDefault() + " en " + watch.ElapsedMilliseconds + " ms, aucune bulle");

            // --- retry : 3 relances au maximum
            before = KkaoNotes().Count;
            fake3.Start(userInitiated: true);
            var gaveUp = await WaitFor(() => fake3.RunState == ToolRunState.Stopped && fake3.StartCount == 4, 20000);
            var limitNote = KkaoNotes().Skip(before).FirstOrDefault(n => n.Tool == fake3);
            Check("retry : 3 relances de suite au maximum",
                gaveUp && limitNote != null && limitNote.Title == "Faux outil 3 n'a pas pu démarrer" && fake3.ExitCodes.Count == 4 && fake3.ExitCodes.All(c => c == 75),
                fake3.StartCount + " lancements (1 + 3 relances), codes " + string.Join(", ", fake3.ExitCodes)
                + (limitNote == null ? ", aucune notification" : ", bulle « " + limitNote.Title + " » / « " + limitNote.Text + " »"));

            // --- Demarrer avec KKAO (settings.json, ici en memoire)
            var defaultFake3 = _tools.EffectiveAutostart(fake3);
            _tools.SetAutostart(fake3, true);
            var overriddenFake3 = _tools.EffectiveAutostart(fake3);
            _tools.SetAutostart(fake1, false);
            var overriddenFake1 = _tools.EffectiveAutostart(fake1);
            Check("« Démarrer avec KKAO » remplace autostart de tools.json",
                !defaultFake3 && overriddenFake3 && !overriddenFake1,
                "fake3 : false → true, fake1 : true → false (réglages en mémoire, settings.json non touché)");

            // --- Redemarrer
            var startsBefore = fake1.StartCount;
            exitsBefore = fake1.ExitCodes.Count;
            watch.Restart();
            fake1.Restart();
            var restartOk = await WaitFor(() => fake1.StartCount == startsBefore + 1 && fake1.RunState == ToolRunState.Running, 10000);
            Check("Redémarrer (quit puis relance)",
                restartOk && fake1.ExitCodes.Count == exitsBefore + 1 && fake1.ExitCodes.Last() == 0 && LogHasLine("[fake1] quit"),
                "quit reçu par l'outil, code " + fake1.ExitCodes.LastOrDefault() + ", relancé en " + watch.ElapsedMilliseconds + " ms");

            // --- Quitter KKAO
            fake2.Start(userInitiated: true);
            await WaitFor(() => fake2.RunState == ToolRunState.Running, 10000);
            var exits1 = fake1.ExitCodes.Count;
            var exits2 = fake2.ExitCodes.Count;
            before = KkaoNotes().Count;
            var done = false;
            watch.Restart();
            _tools.QuitAll(() => done = true);
            var quitOk = await WaitFor(() => done, 12000);
            var elapsed = watch.ElapsedMilliseconds;
            Check("Quitter KKAO : quit → code 0 en moins de 10 s",
                quitOk && elapsed < 10000 && !fake1.IsAlive && !fake2.IsAlive
                && fake1.ExitCodes.Count == exits1 + 1 && fake1.ExitCodes.Last() == 0
                && fake2.ExitCodes.Count == exits2 + 1 && fake2.ExitCodes.Last() == 0 && KkaoNotes().Count == before,
                "2 outils fermés en " + elapsed + " ms, codes " + fake1.ExitCodes.LastOrDefault() + " et " + fake2.ExitCodes.LastOrDefault() + ", aucun tué");

            // --- v2 : protocole 2, menu rapide, paquets, installation, reglages, interface
            await RunV2Async(fake1.Config);
        }

        void CheckMenuRendering(ToolHost fake1, ToolHost fake2, ToolHost fake3, ToolHost launcher)
        {
            var problems = new List<string>();
            using (var menu = new ContextMenuStrip())
            {
                new MenuBuilder(_tools, null).Build(menu);
                var items = menu.Items.Cast<ToolStripItem>().ToList();
                var openOk = items.Count > 5 && items[0] is ToolStripMenuItem first && first.Text == "Ouvrir " + Host && first.Font.Bold && items[1] is ToolStripSeparator;
                if (!openOk) problems.Add("« Ouvrir " + Branding.Name + " » en tête, en gras, puis un séparateur");
                var pinnedOk = items.Count > 5
                    && items.Skip(2).Take(2).All(i => i is ToolStripMenuItem m && m.Text == "Nouvelle note…" && m.Font.Bold && m.ShortcutKeyDisplayString == "Ctrl+Alt+N")
                    && items[4] is ToolStripSeparator;
                if (!pinnedOk) problems.Add("actions rapides par défaut (2 « pin » attendus, en gras, avec raccourci, puis un séparateur)");

                var tool1 = items.OfType<ToolStripMenuItem>().FirstOrDefault(i => i.Text == "Faux outil 1");
                if (tool1 == null) problems.Add("sous-menu « Faux outil 1 » absent");
                else
                {
                    var d = tool1.DropDownItems.Cast<ToolStripItem>().ToList();
                    var texts = d.Select(i => i is ToolStripSeparator ? "-" : i.Text).ToList();
                    var expected = new[] { "Nouvelle note…", "Afficher les post-it", "-", "Zone 1920×1080", "Zone 1280×720", "-", "Reprendre une procédure", "Réglages…", "-", "Démarrer avec " + Host, "Redémarrer", "Arrêter" };
                    if (!texts.SequenceEqual(expected)) problems.Add("contenu de « Faux outil 1 » : " + string.Join(" | ", texts));
                    else
                    {
                        if (!((ToolStripMenuItem)d[1]).Checked) problems.Add("case à cocher non cochée");
                        if (((ToolStripMenuItem)d[3]).Checked || d[3].Image != null) problems.Add("radio non choisi affiché coché");
                        if (!((ToolStripMenuItem)d[4]).Checked || d[4].Image == null) problems.Add("radio choisi sans puce");
                        var resume = (ToolStripMenuItem)d[6];
                        var resumeTexts = resume.DropDownItems.Cast<ToolStripItem>().Select(i => i.Text).ToList();
                        if (!resumeTexts.SequenceEqual(new[] { "Procédure A && B", "Procédure désactivée", "Archives" })) problems.Add("sous-menu : " + string.Join(" | ", resumeTexts));
                        else
                        {
                            if (resume.DropDownItems[1].Enabled) problems.Add("élément désactivé actif");
                            var archives = (ToolStripMenuItem)resume.DropDownItems[2];
                            if (archives.DropDownItems.Count != 1 || archives.DropDownItems[0].Text != "Vieille procédure") problems.Add("sous-sous-menu");
                        }
                        if (!((ToolStripMenuItem)d[9]).Checked) problems.Add("« Démarrer avec KKAO » non coché");
                    }
                }
                var tool3 = items.OfType<ToolStripMenuItem>().FirstOrDefault(i => i.Text == "Faux outil 3 — arrêté");
                if (tool3 == null) problems.Add("« Faux outil 3 — arrêté » absent");
                else
                {
                    var texts3 = tool3.DropDownItems.Cast<ToolStripItem>().Select(i => i.Text).ToList();
                    if (!texts3.SequenceEqual(new[] { "Démarrer avec " + Host, "Démarrer" }) || ((ToolStripMenuItem)tool3.DropDownItems[0]).Checked)
                        problems.Add("contenu de « Faux outil 3 » : " + string.Join(" | ", texts3));
                }
                var launcherItem = items.OfType<ToolStripMenuItem>().FirstOrDefault(i => i.Text == "Lanceur simple");
                if (launcherItem == null || launcherItem.DropDownItems.Count != 0) problems.Add("lanceur simple (élément sans sous-menu)");
                var tail = items.Skip(items.Count - 2).Select(i => i.Text).ToList();
                if (!tail.SequenceEqual(new[] { "Réglages…", "Quitter " + Host }))
                    problems.Add("entrées de KKAO : " + string.Join(" | ", tail));
                if (!(items[items.Count - 3] is ToolStripSeparator)) problems.Add("séparateur avant les entrées de KKAO");

                Check("menu de l'icône construit (sans l'afficher)", problems.Count == 0,
                    problems.Count == 0
                        ? items.Count + " éléments au premier niveau : Ouvrir, 2 actions rapides, " + _tools.Tools.Count + " outils, Réglages, Quitter ; & doublé, radio, case, désactivé, sous-menus imbriqués"
                        : string.Join(" ; ", problems));
            }
        }

        #endregion

        #region Verifications sans processus

        void CheckPriorityTable()
        {
            var order = new[] { StatusState.Idle, StatusState.Active, StatusState.Warn, StatusState.Pause, StatusState.Error, StatusState.Rec };
            var ok = true;
            for (var i = 0; i < order.Length; i++)
                for (var j = i; j < order.Length; j++)
                    ok &= StatusStates.Max(order[i], order[j]) == order[j] && StatusStates.Max(order[j], order[i]) == order[j];
            foreach (var name in new[] { "idle", "active", "warn", "pause", "error", "rec" })
                ok &= StatusStates.TryParse(name, out var parsed) && parsed.ToString().ToLowerInvariant() == name;
            ok &= !StatusStates.TryParse("bidon", out _);
            Check("priorité des états (table)", ok, "rec > error > pause > warn > active > idle");
        }

        void CheckQuoting()
        {
            var samples = new[] { "simple", "avec espace", "", @"C:\Program Files\x\", "guillemet\"dedans", @"fin\\", @"\\.\pipe\kkao-x-1", "é ✓ & %PATH%", "a\\\"b", "--kkao" };
            var joined = CommandLine.Join(samples);
            var back = NativeMethods.SplitArguments(joined);
            Check("arguments : guillemets Windows", back.SequenceEqual(samples), "relu par CommandLineToArgvW : " + back.Length + "/" + samples.Length + " arguments identiques");
        }

        void CheckPaths()
        {
            var baseDir = @"C:\base\tools";
            var ok = PathResolver.ResolveArg(@".\x.js", baseDir) == @"C:\base\tools\x.js"
                && PathResolver.ResolveArg(@"..\y", baseDir) == @"C:\base\y"
                && PathResolver.ResolveArg("../z", baseDir) == @"C:\base\z"
                && PathResolver.ResolveArg("--flag", baseDir) == "--flag"
                && PathResolver.ResolveArg("plain", baseDir) == "plain"
                && PathResolver.ResolveExe(@"..\a\b.exe", baseDir) == @"C:\base\a\b.exe"
                && PathResolver.ResolvePath(@"..\Procedurier", @"C:\projects\4-OUTILS\KKAO") == @"C:\projects\4-OUTILS\Procedurier";
            var details = "relatifs au dossier de tools.json (.\\ ..\\ ./ ../), autres arguments inchangés";

            // Le vrai tools.json est lu (jamais lance) : ses chemins doivent tomber sur les dossiers voisins
            var exeDir = Path.GetDirectoryName(Application.ExecutablePath);
            var realPath = ToolsFile.Locate(null, exeDir);
            if (realPath != null)
            {
                var real = ToolsFile.Load(realPath);
                var root = Directory.GetParent(real.Dir)?.FullName ?? "";
                var proc = real.Tools.FirstOrDefault(t => t.Id == "instruktion");
                var paper = real.Tools.FirstOrDefault(t => t.Id == "kalepin");
                var realOk = proc != null && paper != null
                    && proc.Exe == Path.Combine(root, @"Procedurier\node_modules\electron\dist\electron.exe")
                    && proc.Args.Count == 1 && proc.Args[0] == Path.Combine(root, "Procedurier")
                    && proc.Cwd == Path.Combine(root, "Procedurier") && !proc.Autostart && proc.Protocol && proc.Aliases.Contains("procedurier") && !paper.Autostart
                    && paper.Exe == Path.Combine(root, @"Kalepin\dist\Kalepin.exe") && paper.Args.Count == 0
                    && paper.Cwd == Path.Combine(root, @"Kalepin\dist") && paper.Protocol && paper.Aliases.Contains("papernotes");
                ok &= realOk;
                details += " ; vrai tools.json (" + realPath + ", non lancé) : " + (realOk
                    ? "InstruKtion → " + proc.Exe + " ; Kalepin (ancien id papernotes) → " + paper.Exe + " ; aucune ne démarre avec KKAO par défaut (seule la fenêtre s'ouvre)"
                    : "chemins inattendus");
            }
            Check("chemins relatifs à tools.json", ok, details);
        }

        void CheckMenuParser()
        {
            var message = Json.ParseObject("{\"type\":\"menu\",\"items\":[{\"type\":\"separator\"},{\"id\":\"a\",\"label\":\"A\"},{\"type\":\"separator\"},{\"type\":\"separator\"},"
                + "{\"label\":\"Vide\",\"items\":[]},{\"type\":\"separator\"},{\"id\":\"\",\"label\":\"\"},{\"type\":\"futur\",\"label\":\"x\"},{\"label\":\"Info seule\"}]}");
            var nodes = MenuNode.ParseList(Json.Get(message, "items"));
            var ok = nodes.Count == 5 && nodes[0].Id == "a" && nodes[1].IsSeparator && nodes[2].IsSubmenu && nodes[2].Items.Count == 0
                && nodes[3].IsSeparator && nodes[4].Label == "Info seule" && !nodes[4].IsClickable;
            Check("menu : séparateurs en trop, éléments vides et types inconnus", ok,
                "séparateurs de tête, doublés et de fin retirés ; élément sans id ni libellé ignoré ; type inconnu ignoré ; libellé sans id non cliquable");
        }

        void CheckIcons()
        {
            var problems = new List<string>();
            foreach (StatusState state in Enum.GetValues(typeof(StatusState)))
            {
                foreach (var size in new[] { 16, 20, 24, 32 })
                {
                    using (var bitmap = IconFactory.Render(state, size))
                    {
                        if (bitmap.GetPixel(0, 0).A != 0) problems.Add(state + "/" + size + " : coin non transparent");
                        var center = bitmap.GetPixel(size / 2, size / 3);
                        if (center.A != 255) problems.Add(state + "/" + size + " : fond non opaque");
                        var dot = IconFactory.DotColor(state);
                        var spot = bitmap.GetPixel((int)(24.6f * size / 32f), (int)(24.6f * size / 32f));
                        if (dot.HasValue && !Close(spot, dot.Value)) problems.Add(state + "/" + size + " : pastille " + spot);
                        if (!dot.HasValue && (Close(spot, IconFactory.DotColor(StatusState.Rec).Value) || Close(spot, IconFactory.DotColor(StatusState.Active).Value)))
                            problems.Add(state + "/" + size + " : pastille inattendue");
                    }
                }
                IconFactory.Get(state, 16);
            }
            Check("icône dessinée (6 états, 16 à 32 px)", problems.Count == 0, problems.Count == 0 ? "carré violet, K blanc, pastille de couleur sauf au repos" : string.Join(" ; ", problems));
        }

        void CheckFit()
        {
            var longText = "KKAO\n" + new string('x', 80);
            var fitted = ToolManager.Fit(longText, 63);
            Check("infobulle limitée à 63 caractères", fitted.Length == 63 && fitted.EndsWith("…", StringComparison.Ordinal), "85 → " + fitted.Length + " caractères");
        }

        static bool Close(Color a, Color b) => Math.Abs(a.R - b.R) < 40 && Math.Abs(a.G - b.G) < 40 && Math.Abs(a.B - b.B) < 40 && a.A > 200;

        #endregion

        #region Outils du test

        static bool IsFake(ToolConfig tool) => tool.Id.StartsWith("fake", StringComparison.OrdinalIgnoreCase);

        ToolHost Tool(string id) => _tools.Tools.FirstOrDefault(t => t.Id == id);

        List<ToolNotification> KkaoNotes() => _notes.Where(n => n.FromKkao).ToList();

        static string ArgAfter(List<string> args, string name)
        {
            var index = args.IndexOf(name);
            return index >= 0 && index + 1 < args.Count && !args[index + 1].StartsWith("--", StringComparison.Ordinal) ? args[index + 1] : null;
        }

        static string LocateSelfTestTools(string exeDir)
        {
            foreach (var dir in new[] { exeDir, Directory.GetParent(exeDir)?.FullName })
            {
                if (dir == null) continue;
                var candidate = Path.Combine(dir, "tests", "selftest-tools.json");
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        void DeleteMarker()
        {
            if (_marker == null) return;
            try { File.Delete(_marker); }
            catch { }
        }

        void OnLog(string line)
        {
            lock (_logGate)
            {
                if (_log.Count < 50000) _log.Add(line);
            }
        }

        bool LogHas(string fragment)
        {
            lock (_logGate) return _log.Any(l => l.Contains(fragment));
        }

        // Ligne exacte : "[fake1] quit" (ecrit par l'outil) et non "[fake1] quit envoye" (ecrit par KKAO)
        bool LogHasLine(string line)
        {
            lock (_logGate) return _log.Any(l => l == line);
        }

        int LogCount(string fragment)
        {
            lock (_logGate) return _log.Count(l => l.Contains(fragment));
        }

        static async Task<bool> WaitFor(Func<bool> condition, int timeoutMs)
        {
            var watch = Stopwatch.StartNew();
            while (true)
            {
                bool ok;
                try { ok = condition(); }
                catch (Exception) { ok = false; }
                if (ok) return true;
                if (watch.ElapsedMilliseconds >= timeoutMs) return false;
                await Task.Delay(20);
            }
        }

        void Check(string name, bool ok, string details)
        {
            _steps.Add(new Dictionary<string, object>
            {
                { "name", name },
                { "ok", ok },
                { "atMs", _clock.ElapsedMilliseconds },
                { "details", details },
            });
            Log.Write((ok ? "[OK] " : "[ECHEC] ") + name + " : " + details);
        }

        #endregion

        #region Fin

        void Finish()
        {
            if (_finished) return;
            _finished = true;
            try
            {
                Cleanup();
            }
            catch (Exception ex)
            {
                _fatal = (_fatal == null ? "" : _fatal + " | ") + "nettoyage : " + ex.Message;
            }
            WriteReport();
        }

        // Aucun faux outil ne doit survivre au test : tube ferme, puis (test seulement) fin forcee apres 3 s
        void Cleanup()
        {
            if (_tools != null)
            {
                var alive = _tools.Tools.Where(t => t.IsAlive).ToList();
                foreach (var tool in alive) tool.ClosePipe();
                var killed = alive.Where(t => t.KillForTest(3000)).Select(t => t.Id).ToList();
                if (alive.Count > 0 || killed.Count > 0)
                    Check("aucun faux outil laissé en marche", killed.Count == 0,
                        alive.Count + " encore ouvert(s) à la fin" + (killed.Count > 0 ? ", tué(s) : " + string.Join(", ", killed) : ", fermés par le tube"));
            }
            DeleteMarker();
        }

        void WriteReport()
        {
            var passed = _steps.Count(s => (bool)s["ok"]);
            var failed = _steps.Count - passed;
            var summary = passed + "/" + _steps.Count + " vérifications OK"
                + (failed > 0 ? ", " + failed + " en échec : " + string.Join(", ", _steps.Where(s => !(bool)s["ok"]).Select(s => (string)s["name"])) : "")
                + (_fatal != null ? " ; erreur : " + _fatal.Split('\n')[0] : "");
            var report = new Dictionary<string, object>
            {
                { "kkao", Program.Version },
                { "date", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") },
                { "ok", AllOk },
                { "summary", summary },
                { "durationMs", _clock.ElapsedMilliseconds },
                { "tools", _config?.FilePath },
                { "node", _tools?.Tools.FirstOrDefault()?.Config.Exe },
                { "log", Log.FilePath },
                { "fatal", _fatal },
                { "steps", _steps },
            };
            Log.Write("Test automatique termine : " + summary);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_reportPath));
                File.WriteAllText(_reportPath, Json.Pretty(Json.Serialize(report)) + "\n", new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                Log.Error("Ecriture du rapport " + _reportPath, ex);
            }
        }

        #endregion
    }
}
