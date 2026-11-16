using System;
using System.IO;
using System.Windows.Forms;

namespace KKAO
{
    // Emplacements de KKAO. Noms techniques : ne changent jamais, meme si KKAO est renomme (PROTOCOLE.md).
    internal static class Paths
    {
        public const string TechName = "KKAO";

        public static string ExePath => Application.ExecutablePath;

        public static string ExeDir => Path.GetDirectoryName(ExePath);

        // KKAO installe pour l'utilisateur (sans droits admin), comme VS Code ou Teams
        public static string InstallRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", TechName);

        // Applis installees par KKAO : une par dossier (apps\<id>\)
        public static string AppsRoot => Path.Combine(InstallRoot, "apps");

        // Reglages et journal : %APPDATA%\KKAO
        public static string DataDir => Log.DataDir;

        // KKAO lance depuis son dossier d'installation (et non depuis un dossier extrait ou de developpement)
        public static bool IsInstalledCopy => IsSameDirectory(ExeDir, InstallRoot);

        public static bool IsSameDirectory(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            try
            {
                return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Exe ouvert directement dans un zip par l'Explorateur : Windows n'a extrait que lui dans un dossier temporaire
        public static bool LooksLikeInsideZip(string exeDir)
        {
            if (string.IsNullOrEmpty(exeDir)) return false;
            var temp = Path.GetTempPath().TrimEnd('\\');
            return exeDir.StartsWith(temp, StringComparison.OrdinalIgnoreCase)
                && (exeDir.IndexOf(".zip", StringComparison.OrdinalIgnoreCase) >= 0 || exeDir.IndexOf("Temp1_", StringComparison.OrdinalIgnoreCase) >= 0
                    || exeDir.IndexOf("\\Rar$", StringComparison.OrdinalIgnoreCase) >= 0 || exeDir.IndexOf("\\7z", StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }

    // Comparaison de versions "1.2.3" (suffixes -beta, +sha ignores)
    internal static class VersionUtil
    {
        public static Version Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new Version(0, 0);
            var clean = text.Trim().TrimStart('v', 'V');
            var cut = clean.IndexOfAny(new[] { '-', '+', ' ' });
            if (cut > 0) clean = clean.Substring(0, cut);
            var parts = clean.Split('.');
            var numbers = new int[4];
            for (var i = 0; i < parts.Length && i < 4; i++)
                int.TryParse(parts[i], out numbers[i]);
            return new Version(numbers[0], numbers[1], numbers[2], numbers[3]);
        }

        public static int Compare(string a, string b) => Parse(a).CompareTo(Parse(b));

        // "1.2.3.0" -> "1.2.3"
        public static string Short(string text)
        {
            var v = Parse(text);
            return v.Revision > 0 ? v.ToString(4) : v.ToString(3);
        }
    }
}
