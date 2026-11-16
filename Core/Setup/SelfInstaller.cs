using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using KKAO.Apps;

namespace KKAO.Setup
{
    // Ou KKAO s'installe : %LOCALAPPDATA%\Programs\KKAO, menu Demarrer et Bureau de l'utilisateur (le test utilise des dossiers temporaires)
    internal sealed class InstallLayout
    {
        public string InstallRoot { get; set; }
        public string StartMenuDir { get; set; }
        public string DesktopDir { get; set; }

        public static InstallLayout ForCurrentUser() => new InstallLayout
        {
            InstallRoot = Paths.InstallRoot,
            StartMenuDir = Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            DesktopDir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };
    }

    internal sealed class InstallOptions
    {
        public bool StartWithWindows { get; set; } = true;
        public bool DesktopShortcut { get; set; } = true;
    }

    // Installation de KKAO pour l'utilisateur courant, sans droits admin : copie, raccourcis, demarrage avec Windows,
    // entree dans « Applications installees » (desinstallation par Windows), puis desinstallation propre.
    internal sealed class SelfInstaller
    {
        public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + Paths.TechName;
        public const string MarkerFile = "install.json";

        readonly InstallLayout _layout;
        readonly IRegistryAccess _registry;

        public SelfInstaller(InstallLayout layout, IRegistryAccess registry)
        {
            _layout = layout;
            _registry = registry;
        }

        public static SelfInstaller ForCurrentUser() => new SelfInstaller(InstallLayout.ForCurrentUser(), CurrentUserRegistry.Instance);

        public InstallLayout Layout => _layout;

        // Toujours KKAO.exe une fois installe, meme lance depuis « KKAO (1).exe » (fichier telecharge deux fois)
        public string ExeName => Paths.TechName + ".exe";

        public string InstalledExe => Path.Combine(_layout.InstallRoot, ExeName);

        public bool IsInstalled => File.Exists(InstalledExe);

        public string InstalledVersion
        {
            get
            {
                try
                {
                    var info = FileVersionInfo.GetVersionInfo(InstalledExe);
                    return VersionUtil.Short(info.ProductVersion ?? info.FileVersion);
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        public string StartMenuShortcut => Path.Combine(_layout.StartMenuDir, Branding.Name + ".lnk");

        public string DesktopShortcut => Path.Combine(_layout.DesktopDir, Branding.Name + ".lnk");

        // Fichiers de KKAO a copier (chemin relatif source -> chemin relatif installe) : l'exe (renomme KKAO.exe), sa config,
        // et, seulement depuis un dossier partage complet (catalog.json present), le catalogue et les paquets des applis.
        // Lance depuis Telechargements : l'exe seul (les applis viennent du catalogue en ligne).
        public static List<KeyValuePair<string, string>> FilesToCopy(string sourceDir, string sourceExeName, string installedExeName)
        {
            var files = new List<KeyValuePair<string, string>>();
            void Add(string from, string to)
            {
                if (File.Exists(Path.Combine(sourceDir, from))) files.Add(new KeyValuePair<string, string>(from, to));
            }
            Add(sourceExeName, installedExeName);
            Add(sourceExeName + ".config", installedExeName + ".config");
            if (!File.Exists(Path.Combine(sourceDir, AppCatalog.FileName))) return files;
            Add(AppCatalog.FileName, AppCatalog.FileName);
            Add("LISEZ-MOI.txt", "LISEZ-MOI.txt");
            var packages = Path.Combine(sourceDir, AppCatalog.PackagesDir);
            if (Directory.Exists(packages))
                foreach (var file in Directory.EnumerateFiles(packages, "*", SearchOption.AllDirectories))
                {
                    var relative = file.Substring(sourceDir.TrimEnd('\\').Length + 1);
                    files.Add(new KeyValuePair<string, string>(relative, relative));
                }
            return files;
        }

        // sourceExeName : nom de l'exe dans sourceDir (par defaut celui qui tourne)
        public void Install(string sourceDir, InstallOptions options, Action<InstallProgress> progress, string sourceExeName = null)
        {
            options = options ?? new InstallOptions();
            sourceExeName = sourceExeName ?? Path.GetFileName(Paths.ExePath);
            var root = _layout.InstallRoot;
            Directory.CreateDirectory(root);
            var files = FilesToCopy(sourceDir, sourceExeName, ExeName);
            if (!files.Any(f => f.Value == ExeName)) throw new InstallException(sourceExeName + " introuvable dans " + sourceDir);
            long total = Math.Max(1, files.Sum(f => new FileInfo(Path.Combine(sourceDir, f.Key)).Length));
            long done = 0;
            foreach (var pair in files)
            {
                var from = Path.Combine(sourceDir, pair.Key);
                var to = Path.Combine(root, pair.Value);
                if (string.Equals(Path.GetFullPath(from), Path.GetFullPath(to), StringComparison.OrdinalIgnoreCase))
                    continue;
                Directory.CreateDirectory(Path.GetDirectoryName(to));
                CopyWithRetry(from, to);
                // Fichier telecharge (navigateur, zip extrait) : l'avertissement SmartScreen ne doit pas revenir a chaque ouverture de session
                Unblock(to);
                done += new FileInfo(from).Length;
                progress?.Invoke(new InstallProgress("Copie de " + Branding.Name + "…", 0.9 * done / total));
            }
            // Paquets (et catalogue) d'une version precedente que la nouvelle n'apporte plus
            RemoveStalePackages(root, files.Select(f => f.Value).ToList());
            if (!files.Any(f => f.Value == AppCatalog.FileName)) TryDelete(Path.Combine(root, AppCatalog.FileName));

            File.WriteAllText(Path.Combine(root, MarkerFile), Json.Pretty(Json.Serialize(new Dictionary<string, object>
            {
                { "version", Program.Version },
                { "installed", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss") },
                { "source", sourceDir },
            })) + "\n", new UTF8Encoding(false));

            progress?.Invoke(new InstallProgress("Raccourcis…", 0.95));
            var exe = InstalledExe;
            CreateShortcut(StartMenuShortcut, exe, null, Branding.Name + " — " + Branding.Tagline);
            if (options.DesktopShortcut) CreateShortcut(DesktopShortcut, exe, null, Branding.Name + " — " + Branding.Tagline);
            else TryDelete(DesktopShortcut);
            StartupManager.SetEnabledFor(options.StartWithWindows, exe, _registry);
            WriteUninstallEntry(exe);
            progress?.Invoke(new InstallProgress("Installé", 1));
            Log.Write(Branding.Name + " installé dans " + root + " (" + files.Count + " fichiers, démarrage avec Windows : "
                + (options.StartWithWindows ? "oui" : "non") + ", raccourci Bureau : " + (options.DesktopShortcut ? "oui" : "non") + ")");
        }

        public void WriteUninstallEntry(string exe)
        {
            var key = UninstallKey;
            _registry.SetString(key, "DisplayName", Branding.Name + " — " + Branding.LongName);
            _registry.SetString(key, "DisplayVersion", Program.Version);
            _registry.SetString(key, "Publisher", Branding.PublisherLong);
            _registry.SetString(key, "DisplayIcon", exe + ",0");
            _registry.SetString(key, "InstallLocation", _layout.InstallRoot);
            _registry.SetString(key, "UninstallString", "\"" + exe + "\" --uninstall");
            _registry.SetString(key, "QuietUninstallString", "\"" + exe + "\" --uninstall --quiet");
            _registry.SetString(key, "InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            _registry.SetDword(key, "NoModify", 1);
            _registry.SetDword(key, "NoRepair", 1);
            _registry.SetDword(key, "EstimatedSize", (int)Math.Min(int.MaxValue, AppInstaller.DirectorySize(_layout.InstallRoot) / 1024));
        }

        // Raccourcis, demarrage avec Windows, entree de desinstallation (les fichiers sont supprimes a part)
        public void RemoveIntegration()
        {
            TryDelete(StartMenuShortcut);
            TryDelete(DesktopShortcut);
            try { _registry.DeleteValue(StartupManager.RunKey, StartupManager.ValueName); }
            catch (Exception ex) { Log.Write("Valeur Run : " + ex.Message); }
            try { _registry.DeleteKey(UninstallKey); }
            catch (Exception ex) { Log.Write("Entree de desinstallation : " + ex.Message); }
        }

        static void RemoveStalePackages(string root, List<string> copied)
        {
            var packages = Path.Combine(root, AppCatalog.PackagesDir);
            if (!Directory.Exists(packages)) return;
            var keep = new HashSet<string>(copied.Select(f => Path.Combine(root, f)), StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(packages, "*", SearchOption.AllDirectories).ToList())
                if (!keep.Contains(file)) TryDelete(file);
        }

        static void CopyWithRetry(string from, string to)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    if (File.Exists(to)) File.SetAttributes(to, FileAttributes.Normal);
                    File.Copy(from, to, true);
                    return;
                }
                catch (IOException) when (attempt < 10)
                {
                    // Ancienne instance encore en train de se fermer
                    System.Threading.Thread.Sleep(500);
                }
            }
        }

        public static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                Log.Write("Suppression de " + path + " : " + ex.Message);
            }
        }

        // Retire la marque « telecharge depuis Internet » (flux Zone.Identifier) d'un fichier que KKAO vient d'installer
        public static void Unblock(string path)
        {
            try { NativeMethods.DeleteFile(path + ":Zone.Identifier"); }
            catch (Exception) { }
        }

        public static bool HasMarkOfTheWeb(string path)
        {
            try
            {
                var handle = NativeMethods.CreateFile(path + ":Zone.Identifier", 0x80000000, 1, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (handle == new IntPtr(-1)) return false;
                NativeMethods.CloseHandle(handle);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        #region Raccourcis (.lnk) par l'interface Shell IShellLinkW

        public static void CreateShortcut(string lnkPath, string target, string arguments, string description)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(lnkPath));
            var link = (IShellLinkW)new ShellLinkCoClass();
            try
            {
                link.SetPath(target);
                link.SetWorkingDirectory(Path.GetDirectoryName(target));
                if (!string.IsNullOrEmpty(arguments)) link.SetArguments(arguments);
                if (!string.IsNullOrEmpty(description)) link.SetDescription(description.Length > 250 ? description.Substring(0, 250) : description);
                link.SetIconLocation(target, 0);
                ((IPersistFile)link).Save(lnkPath, true);
            }
            finally
            {
                Marshal.FinalReleaseComObject(link);
            }
        }

        public static string ReadShortcutTarget(string lnkPath)
        {
            var link = (IShellLinkW)new ShellLinkCoClass();
            try
            {
                ((IPersistFile)link).Load(lnkPath, 0);
                var sb = new StringBuilder(1024);
                link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
                return sb.ToString();
            }
            finally
            {
                Marshal.FinalReleaseComObject(link);
            }
        }

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        class ShellLinkCoClass
        {
        }

        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
        interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
            void Resolve(IntPtr hwnd, uint fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        #endregion
    }
}
