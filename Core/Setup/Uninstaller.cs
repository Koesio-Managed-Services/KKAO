using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;
using KKAO.Apps;

namespace KKAO.Setup
{
    // Desinstallation de KKAO (Parametres Windows > Applications, ou Reglages > Desinstaller) :
    // KKAO et ses applis sont fermes, raccourcis et demarrage retires, dossier d'installation supprime.
    // Les donnees de l'utilisateur (reglages %APPDATA%\KKAO, documents et reglages des applis) sont gardees.
    internal static class Uninstaller
    {
        public static int Run(bool quiet)
        {
            var self = SelfInstaller.ForCurrentUser();
            var root = self.Layout.InstallRoot;
            if (!quiet)
            {
                var answer = MessageBox.Show(
                    "Désinstaller " + Branding.Name + " de ce PC ?\n\n"
                    + Branding.Name + " et les applis qu'il a installées seront fermés puis supprimés.\n"
                    + "Vos données sont conservées : procédures, notes, post-it et réglages.",
                    Branding.Name + " — désinstallation", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                if (answer != DialogResult.Yes) return 0;
            }
            Log.Write("Désinstallation de " + Branding.Name + " demandée (" + root + ")");

            if (!Program.CloseRunningInstance(20000))
            {
                if (!quiet)
                    MessageBox.Show(Branding.Name + " ne s'est pas fermé. Quittez-le (clic droit sur son icône > Quitter), puis recommencez.",
                        Branding.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return 1;
            }
            // Les applis quittent quand KKAO ferme leur tube : on leur laisse le temps
            WaitForProcessesUnder(root, 15000);

            self.RemoveIntegration();
            Log.Write("Raccourcis, démarrage avec Windows et entrée de désinstallation retirés");
            Log.Flush();

            if (Paths.IsInstalledCopy)
            {
                // On ne peut pas supprimer le dossier de l'exe en cours : une copie temporaire finit le travail
                try
                {
                    var tempDir = Path.Combine(Path.GetTempPath(), Paths.TechName + "-desinstallation-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                    Directory.CreateDirectory(tempDir);
                    var tempExe = Path.Combine(tempDir, Path.GetFileName(Paths.ExePath));
                    File.Copy(Paths.ExePath, tempExe, true);
                    var config = Paths.ExePath + ".config";
                    if (File.Exists(config)) File.Copy(config, tempExe + ".config", true);
                    var args = "--uninstall-finish " + Process.GetCurrentProcess().Id + (quiet ? " --quiet" : "");
                    using (Process.Start(new ProcessStartInfo(tempExe, args) { UseShellExecute = false, WorkingDirectory = tempDir })) { }
                    return 0;
                }
                catch (Exception ex)
                {
                    Log.Error("Désinstallation", ex);
                    if (!quiet) MessageBox.Show("Désinstallation incomplète : " + ex.Message + "\n\nSupprimez le dossier " + root + " à la main.", Branding.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return 1;
                }
            }
            return Delete(root, quiet);
        }

        // Copie temporaire : attend la fin du processus d'origine, puis supprime le dossier d'installation (toujours le meme, jamais un chemin recu)
        public static int Finish(string parentPid, bool quiet)
        {
            if (int.TryParse(parentPid, out var pid))
            {
                try
                {
                    using (var parent = Process.GetProcessById(pid)) parent.WaitForExit(30000);
                }
                catch (ArgumentException)
                {
                    // deja termine
                }
            }
            return Delete(Paths.InstallRoot, quiet);
        }

        static int Delete(string root, bool quiet)
        {
            var watch = Stopwatch.StartNew();
            while (Directory.Exists(root) && watch.ElapsedMilliseconds < 30000)
            {
                AppInstaller.TryDeleteDirectory(root);
                if (Directory.Exists(root)) Thread.Sleep(1000);
            }
            var ok = !Directory.Exists(root);
            Log.Write(ok ? Branding.Name + " désinstallé" : "Dossier encore présent : " + root);
            Log.Flush();
            if (!quiet)
                MessageBox.Show(ok
                        ? Branding.Name + " a été désinstallé.\n\nVos données sont conservées (réglages dans %APPDATA%\\" + Paths.TechName + ", documents des applis)."
                        : Branding.Name + " est désinstallé, mais certains fichiers sont encore utilisés.\nSupprimez le dossier " + root + " après avoir redémarré le PC.",
                    Branding.Name, MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            return ok ? 0 : 1;
        }

        static void WaitForProcessesUnder(string root, int timeoutMs)
        {
            var prefix = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            var self = Process.GetCurrentProcess().Id;
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                var running = Process.GetProcesses().Where(p =>
                {
                    try { return p.Id != self && p.MainModule?.FileName?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true; }
                    catch (Exception) { return false; }
                    finally { p.Dispose(); }
                }).Any();
                if (!running) return;
                Thread.Sleep(500);
            }
        }
    }
}
