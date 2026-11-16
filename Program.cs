using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using KKAO.Apps;
using KKAO.Setup;

namespace KKAO
{
    // KKAO (« KMS Kaisse À Outils ») : une seule icone pour toutes les applis Koesio, chacune dans son processus.
    //   KKAO.exe                                  lanceur (icone pres de l'horloge + fenetre)
    //   KKAO.exe --autostart                      ouverture de session : discret, sans fenetre
    //   KKAO.exe --tools <fichier>                autre tools.json (developpement)
    //   KKAO.exe --uninstall [--quiet]            desinstallation (Parametres Windows > Applications)
    //   KKAO.exe --bundle <dossier>               prepare le dossier a partager aux collegues (+ zip)
    //   KKAO.exe --release [<paquet>]             prepare la release GitHub (out\release\) a partir du paquet
    //   KKAO.exe --verify-online <rapport.txt> [--catalog <url>]   telecharge et installe a blanc ce que la release publie
    //   KKAO.exe --update                         nouvelle version telechargee par KKAO : se met en place sans question
    //   KKAO.exe --screenshots <dossier>          rendus d'ecran de l'interface (donnees de demonstration)
    //   KKAO.exe --export-icon <fichier.ico>      icone de l'exe
    //   KKAO.exe --selftest <rapport.json> [--tools <fichier>]   test automatique sans interface
    internal static class Program
    {
        const string MutexName = "Local\\KKAO.SingleInstance";
        const string ShowSignalName = "Local\\KKAO.Show";
        const string QuitSignalName = "Local\\KKAO.Quit";
        // KKAO 1.0 : la nouvelle instance ouvrait le menu de l'ancienne
        const string LegacyShowMenuSignalName = "Local\\KKAO.ShowMenu";

        // Relance demandee en fin d'execution (copie installee apres l'installation)
        public static Tuple<string, string> PendingRelaunch;

        public static string Version
        {
            get
            {
                var assembly = typeof(Program).Assembly;
                var info = (AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(assembly, typeof(AssemblyInformationalVersionAttribute));
                var version = info?.InformationalVersion;
                if (!string.IsNullOrEmpty(version))
                {
                    var plus = version.IndexOf('+');
                    return plus > 0 ? version.Substring(0, plus) : version;
                }
                return assembly.GetName().Version.ToString(3);
            }
        }

        [STAThread]
        static int Main(string[] args)
        {
            var toolsArg = ArgValue(args, "--tools");
            if (HasArg(args, "--selftest"))
                return SelfTest.Run(ArgValue(args, "--selftest"), toolsArg);
            if (HasArg(args, "--export-icon"))
            {
                IconFactory.WriteIco(ArgValue(args, "--export-icon") ?? "kkao.ico", size => IconFactory.Render(StatusState.Idle, size), new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 });
                return 0;
            }
            if (HasArg(args, "--bundle"))
                return Bundler.Run(ArgValue(args, "--bundle"), ArgValue(args, "--recipes"));
            if (HasArg(args, "--release"))
                return Bundler.Release(ArgValue(args, "--release"));
            if (HasArg(args, "--verify-online"))
                return OnlineCheck.Run(ArgValue(args, "--verify-online"), ArgValue(args, "--catalog"));
            if (HasArg(args, "--screenshots"))
                return Ui.Screenshots.Run(ArgValue(args, "--screenshots"));

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (HasArg(args, "--uninstall-finish"))
                return Uninstaller.Finish(ArgValue(args, "--uninstall-finish"), HasArg(args, "--quiet"));
            if (HasArg(args, "--uninstall"))
                return Uninstaller.Run(HasArg(args, "--quiet"));

            var exeDir = Paths.ExeDir;
            var toolsPath = ToolsFile.Locate(toolsArg, exeDir);
            var devMode = toolsPath != null && File.Exists(toolsPath);

            // Lance depuis l'interieur du zip : Windows n'a extrait que l'exe, les applis embarquees manquent
            if (!devMode && Paths.LooksLikeInsideZip(exeDir) && !File.Exists(Path.Combine(exeDir, AppCatalog.FileName)))
            {
                MessageBox.Show("Extrayez d'abord le dossier " + Branding.Name + " :\n\nclic droit sur le fichier zip > « Extraire tout… », puis double-clic sur "
                    + Path.GetFileName(Paths.ExePath) + " dans le dossier extrait.", Branding.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }

            // Dossier extrait alors que KKAO est deja installe : on ouvre la copie installee, ou on la met a jour
            if (!devMode && !Paths.IsInstalledCopy)
            {
                var self = SelfInstaller.ForCurrentUser();
                if (self.IsInstalled && HandOffToInstalled(self, HasArg(args, "--update"))) return 0;
            }

            using (var mutex = new Mutex(true, MutexName, out var firstInstance))
            {
                if (!firstInstance)
                {
                    // Deja lance : l'instance en place ouvre sa fenetre (on lui cede le premier plan)
                    NativeMethods.AllowSetForegroundWindow(NativeMethods.AsfwAny);
                    if (!Signal(ShowSignalName)) Signal(LegacyShowMenuSignalName);
                    return 0;
                }

                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) => Log.Error("Erreur non geree", e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) =>
                {
                    Log.Write("Exception fatale : " + e.ExceptionObject);
                    Log.Flush();
                };
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                Log.Write("Demarrage de " + Branding.Name + " " + Version + " (" + Application.ExecutablePath + (devMode ? ", développement" : Paths.IsInstalledCopy ? ", installé" : ", dossier extrait") + ")");
                // Copie installee : la version telechargee pour la mise a jour a fini son travail
                if (Paths.IsInstalledCopy) AppInstaller.TryDeleteDirectory(Path.Combine(Paths.InstallRoot, "update"));

                using (var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSignalName))
                using (var quit = new EventWaitHandle(false, EventResetMode.AutoReset, QuitSignalName))
                using (var app = new TrayApp(toolsArg, show, quit, HasArg(args, StartupManager.AutostartArg), HasArg(args, "--after-update")))
                {
                    Application.Run(app);
                }
                Log.Write(Branding.Name + " ferme");
                Log.Flush();
                try { mutex.ReleaseMutex(); }
                catch (ApplicationException) { }
            }

            if (PendingRelaunch != null)
            {
                try
                {
                    Log.Write("Relance : " + PendingRelaunch.Item1 + " " + PendingRelaunch.Item2);
                    using (Process.Start(new ProcessStartInfo(PendingRelaunch.Item1, PendingRelaunch.Item2) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(PendingRelaunch.Item1) })) { }
                }
                catch (Exception ex)
                {
                    Log.Error("Relance de la copie installée", ex);
                    MessageBox.Show(Branding.Name + " est installé, mais n'a pas pu redémarrer : lancez-le depuis le menu Démarrer.", Branding.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                Log.Flush();
            }
            return 0;
        }

        // true : la copie installee a pris le relais (ce processus s'arrete).
        // silent : version telechargee par KKAO lui-meme (l'utilisateur a deja clique « Mettre a jour »)
        static bool HandOffToInstalled(SelfInstaller self, bool silent)
        {
            var installedVersion = self.InstalledVersion;
            var updated = false;
            if (VersionUtil.Compare(Version, installedVersion) > 0)
            {
                var answer = silent ? DialogResult.Yes : MessageBox.Show(
                    "Ce dossier contient " + Branding.Name + " " + Version + ", plus récent que celui installé sur ce PC (" + (installedVersion ?? "?") + ").\n\n"
                    + "Mettre à jour " + Branding.Name + " maintenant ? Vos réglages et vos applis sont conservés.",
                    Branding.Name, MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer == DialogResult.Yes)
                {
                    if (!CloseRunningInstance(20000))
                    {
                        MessageBox.Show(Branding.Name + " est encore ouvert : quittez-le (clic droit sur son icône > Quitter), puis relancez la mise à jour.",
                            Branding.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return true;
                    }
                    try
                    {
                        var options = new InstallOptions
                        {
                            StartWithWindows = StartupManager.IsEnabledFor(self.InstalledExe, CurrentUserRegistry.Instance),
                            DesktopShortcut = File.Exists(self.DesktopShortcut),
                        };
                        self.Install(Paths.ExeDir, options, null);
                        updated = true;
                        Log.Write(Branding.Name + " mis à jour : " + (installedVersion ?? "?") + " → " + Version + (silent ? " (téléchargé par " + Branding.Name + ")" : ""));
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Mise à jour de " + Branding.Name, ex);
                        MessageBox.Show("La mise à jour a échoué : " + ex.Message, Branding.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
                        if (!silent) return true;
                    }
                }
            }
            try
            {
                using (Process.Start(new ProcessStartInfo(self.InstalledExe, updated ? "--after-update" : "") { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(self.InstalledExe) })) { }
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Lancement de la copie installée", ex);
                return false;
            }
        }

        // Demande a l'instance en marche de se fermer et attend qu'elle l'ait fait
        public static bool CloseRunningInstance(int timeoutMs)
        {
            Signal(QuitSignalName);
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                try
                {
                    using (var mutex = new Mutex(false, MutexName, out _))
                    {
                        if (mutex.WaitOne(0))
                        {
                            mutex.ReleaseMutex();
                            return true;
                        }
                    }
                }
                catch (AbandonedMutexException)
                {
                    return true;
                }
                Thread.Sleep(250);
            }
            return false;
        }

        static bool Signal(string name)
        {
            try
            {
                using (var signal = EventWaitHandle.OpenExisting(name)) signal.Set();
                return true;
            }
            catch (Exception)
            {
                // instance absente, en cours de demarrage ou de fermeture
                return false;
            }
        }

        internal static bool HasArg(string[] args, string name) => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

        internal static string ArgValue(string[] args, string name)
        {
            for (var i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase) && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    return args[i + 1];
            return null;
        }
    }
}
