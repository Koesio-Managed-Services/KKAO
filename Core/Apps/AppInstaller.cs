using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace KKAO.Apps
{
    internal sealed class InstallProgress
    {
        public InstallProgress(string phase, double fraction)
        {
            Phase = phase;
            Fraction = Math.Max(0, Math.Min(1, fraction));
        }

        public string Phase { get; }
        public double Fraction { get; }
    }

    // Erreur expliquee a l'utilisateur (message affiche tel quel)
    internal sealed class InstallException : Exception
    {
        public InstallException(string message) : base(message) { }
    }

    // Installe les applis dans apps\<id>\ (par utilisateur, sans droits admin) a partir de leur zip.
    // Extraction dans un dossier temporaire, puis echange : une installation interrompue ne laisse jamais une appli a moitie copiee.
    internal sealed class AppInstaller
    {
        const string TempPrefix = ".";

        public AppInstaller(string appsRoot)
        {
            AppsRoot = appsRoot;
        }

        public string AppsRoot { get; }

        public string AppDir(string id) => Path.Combine(AppsRoot, id);

        public bool IsInstalled(string id) => File.Exists(Path.Combine(AppDir(id), AppManifest.FileName));

        // Applis installees : un dossier par appli contenant kkao-app.json (dossiers ".…" = temporaires, ignores)
        public List<ToolConfig> ScanInstalled(List<string> problems)
        {
            var list = new List<ToolConfig>();
            if (!Directory.Exists(AppsRoot)) return list;
            string[] dirs;
            try
            {
                dirs = Directory.GetDirectories(AppsRoot);
            }
            catch (Exception ex)
            {
                problems?.Add("dossier des applis illisible : " + ex.Message);
                return list;
            }
            foreach (var dir in dirs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var name = Path.GetFileName(dir);
                if (name.StartsWith(TempPrefix, StringComparison.Ordinal)) continue;
                var manifestPath = Path.Combine(dir, AppManifest.FileName);
                if (!File.Exists(manifestPath)) continue;
                try
                {
                    var manifest = AppManifest.Parse(File.ReadAllText(manifestPath, Encoding.UTF8), out var problem);
                    if (manifest == null)
                    {
                        problems?.Add(name + " : " + problem);
                        continue;
                    }
                    if (!string.Equals(manifest.Id, name, StringComparison.OrdinalIgnoreCase))
                    {
                        problems?.Add(name + " : le paquet annonce l'id « " + manifest.Id + " »");
                        continue;
                    }
                    list.Add(manifest.ToToolConfig(dir));
                }
                catch (Exception ex)
                {
                    problems?.Add(name + " : " + ex.Message);
                }
            }
            return list;
        }

        public AppManifest ReadManifest(string id)
        {
            var path = Path.Combine(AppDir(id), AppManifest.FileName);
            if (!File.Exists(path)) return null;
            try
            {
                return AppManifest.Parse(File.ReadAllText(path, Encoding.UTF8), out _);
            }
            catch (Exception)
            {
                return null;
            }
        }

        // Restes d'une installation interrompue ou d'une ancienne version encore verrouillee au moment de l'echange
        public void CleanupLeftovers()
        {
            if (!Directory.Exists(AppsRoot)) return;
            try
            {
                foreach (var dir in Directory.GetDirectories(AppsRoot, TempPrefix + "*"))
                    TryDeleteDirectory(dir);
            }
            catch (Exception ex)
            {
                Log.Write("Nettoyage du dossier des applis : " + ex.Message);
            }
        }

        // Appel sur un fil de fond. L'appli doit etre arretee (sinon ses fichiers sont verrouilles).
        public AppManifest Install(string packagePath, string expectedId, string expectedSha256, Action<InstallProgress> progress, CancellationToken token)
        {
            progress?.Invoke(new InstallProgress("Vérification du paquet…", 0));
            if (string.IsNullOrEmpty(packagePath) || !File.Exists(packagePath))
                throw new InstallException("Le paquet de l'appli est introuvable (" + (packagePath ?? "?") + "). Récupérez à nouveau le dossier " + Branding.Name + " complet.");

            if (!string.IsNullOrEmpty(expectedSha256))
            {
                var actual = Sha256Of(packagePath, f => progress?.Invoke(new InstallProgress("Vérification du paquet…", f * 0.15)), token);
                if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InstallException("Le paquet est abîmé (empreinte différente du catalogue). Récupérez à nouveau le dossier " + Branding.Name + " complet.");
            }

            Directory.CreateDirectory(AppsRoot);
            var temp = Path.Combine(AppsRoot, TempPrefix + expectedId + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                AppManifest manifest;
                using (var zip = ZipFile.OpenRead(packagePath))
                {
                    var manifestEntry = zip.GetEntry(AppManifest.FileName);
                    if (manifestEntry == null) throw new InstallException("Paquet invalide : " + AppManifest.FileName + " absent.");
                    string problem;
                    using (var reader = new StreamReader(manifestEntry.Open(), Encoding.UTF8))
                        manifest = AppManifest.Parse(reader.ReadToEnd(), out problem);
                    if (manifest == null) throw new InstallException("Paquet invalide : " + problem + ".");
                    if (!string.Equals(manifest.Id, expectedId, StringComparison.OrdinalIgnoreCase))
                        throw new InstallException("Le paquet contient l'appli « " + manifest.Id + " » au lieu de « " + expectedId + " ».");

                    var tempRoot = Path.GetFullPath(temp).TrimEnd('\\') + "\\";
                    long total = Math.Max(1, zip.Entries.Sum(e => e.Length));
                    long done = 0;
                    var lastReport = 0.0;
                    foreach (var entry in zip.Entries)
                    {
                        token.ThrowIfCancellationRequested();
                        var name = entry.FullName.Replace('/', '\\');
                        var target = Path.GetFullPath(Path.Combine(temp, name));
                        // Zip slip : aucun fichier hors du dossier de l'appli
                        if (!target.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
                            throw new InstallException("Paquet refusé : chemin interdit (" + entry.FullName + ").");
                        if (name.EndsWith("\\", StringComparison.Ordinal))
                        {
                            Directory.CreateDirectory(target);
                            continue;
                        }
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        entry.ExtractToFile(target, true);
                        done += entry.Length;
                        var fraction = 0.15 + 0.8 * done / total;
                        if (fraction - lastReport >= 0.01)
                        {
                            lastReport = fraction;
                            progress?.Invoke(new InstallProgress("Installation…", fraction));
                        }
                    }
                }

                progress?.Invoke(new InstallProgress("Finalisation…", 0.97));
                var final = AppDir(expectedId);
                string old = null;
                if (Directory.Exists(final))
                {
                    old = Path.Combine(AppsRoot, TempPrefix + expectedId + "-ancien-" + Guid.NewGuid().ToString("N").Substring(0, 8));
                    try
                    {
                        Directory.Move(final, old);
                    }
                    catch (IOException)
                    {
                        throw new InstallException("Des fichiers de l'appli sont encore utilisés. Fermez-la, puis réessayez.");
                    }
                    catch (UnauthorizedAccessException)
                    {
                        throw new InstallException("Des fichiers de l'appli sont encore utilisés. Fermez-la, puis réessayez.");
                    }
                }
                Directory.Move(temp, final);
                temp = null;
                if (old != null) TryDeleteDirectory(old);
                progress?.Invoke(new InstallProgress("Installée", 1));
                return manifest;
            }
            finally
            {
                if (temp != null) TryDeleteDirectory(temp);
            }
        }

        // L'appli doit etre arretee. Ses donnees utilisateur (%APPDATA%, Documents) ne sont pas touchees.
        public void Uninstall(string id)
        {
            var dir = AppDir(id);
            if (!Directory.Exists(dir)) return;
            var old = Path.Combine(AppsRoot, TempPrefix + id + "-supprime-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                Directory.Move(dir, old);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new InstallException("Des fichiers de l'appli sont encore utilisés. Fermez-la, puis réessayez.");
            }
            TryDeleteDirectory(old);
        }

        public static long DirectorySize(string dir)
        {
            try
            {
                return new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            }
            catch (Exception)
            {
                return 0;
            }
        }

        public static string Sha256Of(string path, Action<double> progress, CancellationToken token)
        {
            using (var sha = SHA256.Create())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            {
                var buffer = new byte[1 << 20];
                long read = 0;
                var length = Math.Max(1, stream.Length);
                int count;
                while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    token.ThrowIfCancellationRequested();
                    sha.TransformBlock(buffer, 0, count, null, 0);
                    read += count;
                    progress?.Invoke((double)read / length);
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return string.Concat(sha.Hash.Select(b => b.ToString("x2")));
            }
        }

        public static void TryDeleteDirectory(string dir)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (!Directory.Exists(dir)) return;
                    foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        try { File.SetAttributes(file, FileAttributes.Normal); }
                        catch { }
                    }
                    Directory.Delete(dir, true);
                    return;
                }
                catch (Exception)
                {
                    Thread.Sleep(150);
                }
            }
            Log.Write("Dossier non supprimé (encore utilisé ?) : " + dir + " — nouvel essai au prochain démarrage");
        }
    }
}
