using System;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace KKAO.Ui
{
    // Themes sombre / clair (ou celui de Windows) : dictionnaires de couleurs echanges a chaud
    internal static class ThemeManager
    {
        static ResourceDictionary _colors;

        public static string Current { get; private set; } = "dark";

        // Animations coupees pour les rendus d'ecran (etat final dessine tout de suite)
        public static bool Motion { get; set; } = true;

        public static event Action Changed;

        public static void EnsureApplication()
        {
            if (Application.Current != null) return;
            new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var resources = Application.Current.Resources;
            resources.MergedDictionaries.Add(Load("Base"));
            _colors = Load("Dark");
            resources.MergedDictionaries.Add(_colors);
            resources.MergedDictionaries.Add(Load("Controls"));
        }

        // "system", "dark" ou "light"
        public static void Apply(string choice)
        {
            EnsureApplication();
            var effective = choice == "light" || choice == "dark" ? choice : (SystemUsesLightTheme() ? "light" : "dark");
            if (_colors != null && effective == Current && Application.Current.Resources.MergedDictionaries.Contains(_colors)) return;
            var merged = Application.Current.Resources.MergedDictionaries;
            var fresh = Load(effective == "light" ? "Light" : "Dark");
            var index = _colors == null ? -1 : merged.IndexOf(_colors);
            if (index >= 0) merged[index] = fresh;
            else merged.Insert(Math.Min(1, merged.Count), fresh);
            _colors = fresh;
            Current = effective;
            foreach (Window window in Application.Current.Windows) ApplyTitleBar(window);
            Changed?.Invoke();
        }

        public static bool IsDark => Current == "dark";

        public static bool SystemUsesLightTheme()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return key?.GetValue("AppsUseLightTheme") is int value && value != 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public static Color ColorOf(string key)
        {
            if (Application.Current?.TryFindResource(key) is SolidColorBrush brush) return brush.Color;
            return Colors.Gray;
        }

        // Barre de titre native assortie (sombre sur Windows 10/11, couleur de fond sur Windows 11)
        public static void ApplyTitleBar(Window window)
        {
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;
                var dark = IsDark ? 1 : 0;
                if (NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DwmUseImmersiveDarkMode, ref dark, 4) != 0)
                    NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DwmUseImmersiveDarkModeOld, ref dark, 4);
                var bg = ColorOf("BgNav");
                var caption = bg.R | (bg.G << 8) | (bg.B << 16);
                NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DwmCaptionColor, ref caption, 4);
                var fg = ColorOf("Text");
                var text = fg.R | (fg.G << 8) | (fg.B << 16);
                NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DwmTextColor, ref text, 4);
            }
            catch (Exception)
            {
                // Windows trop ancien : barre de titre par defaut
            }
        }

        static ResourceDictionary Load(string name) =>
            new ResourceDictionary { Source = new Uri("pack://application:,,,/KKAO;component/Ui/Theme/" + name + ".xaml", UriKind.Absolute) };
    }
}
