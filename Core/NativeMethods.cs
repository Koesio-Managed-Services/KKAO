using System;
using System.Runtime.InteropServices;

namespace KKAO
{
    internal static class NativeMethods
    {
        // ASFW_ANY : tout processus peut prendre le premier plan (la nouvelle instance le cede a l'ancienne)
        public const int AsfwAny = -1;

        // Autorise un outil a passer sa fenetre au premier plan (sinon Windows la fait seulement clignoter)
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool AllowSetForegroundWindow(int processId);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        // Flux Zone.Identifier (« fichier telecharge ») : chemin "fichier:Zone.Identifier"
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool DeleteFile(string path);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);

        // Barre de titre sombre (Windows 10 1809+) et couleur de legende (Windows 11)
        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        public const int DwmUseImmersiveDarkModeOld = 19;
        public const int DwmUseImmersiveDarkMode = 20;
        public const int DwmWindowCornerPreference = 33;
        public const int DwmCaptionColor = 35;
        public const int DwmTextColor = 36;
        public const int DwmCornerRound = 2;
        public const int DwmCornerRoundSmall = 3;

        // Nom complet de l'utilisateur (« Camille MARTIN »), pour l'accueil
        [DllImport("secur32.dll", CharSet = CharSet.Unicode)]
        static extern bool GetUserNameEx(int format, System.Text.StringBuilder name, ref uint size);

        public static string UserDisplayName()
        {
            try
            {
                var size = 256u;
                var sb = new System.Text.StringBuilder((int)size);
                // NameDisplay = 3
                if (GetUserNameEx(3, sb, ref size)) return sb.ToString();
            }
            catch (Exception)
            {
                // pas de domaine, pas de nom complet
            }
            return null;
        }

        [DllImport("shell32.dll", SetLastError = true)]
        static extern IntPtr CommandLineToArgvW([MarshalAs(UnmanagedType.LPWStr)] string commandLine, out int count);

        [DllImport("kernel32.dll")]
        static extern IntPtr LocalFree(IntPtr memory);

        // Decoupage d'une ligne de commande comme le fera le programme lance (verification du test automatique)
        public static string[] SplitArguments(string arguments)
        {
            // Le premier element suit des regles a part (nom du programme) : on en met un factice
            var pointer = CommandLineToArgvW("x.exe " + arguments, out var count);
            if (pointer == IntPtr.Zero) return new string[0];
            try
            {
                var result = new string[Math.Max(0, count - 1)];
                for (var i = 1; i < count; i++)
                    result[i - 1] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointer, i * IntPtr.Size));
                return result;
            }
            finally
            {
                LocalFree(pointer);
            }
        }
    }
}
