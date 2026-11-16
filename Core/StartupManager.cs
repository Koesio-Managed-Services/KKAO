using System;
using KKAO.Setup;

namespace KKAO
{
    // Lancement de KKAO a l'ouverture de session : valeur HKCU\...\Run "KKAO" (pas besoin de droits admin).
    // "--autostart" : KKAO demarre discretement (icone seule), sans ouvrir sa fenetre.
    internal static class StartupManager
    {
        public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        public const string ValueName = Paths.TechName;
        public const string AutostartArg = "--autostart";

        public static string CommandFor(string exePath) => "\"" + exePath + "\" " + AutostartArg;

        // Coche = ce KKAO-ci demarre avec Windows (une valeur qui pointe vers un autre chemin ne compte pas)
        public static bool IsEnabled => IsEnabledFor(Paths.ExePath, CurrentUserRegistry.Instance);

        public static void SetEnabled(bool enabled) => SetEnabledFor(enabled, Paths.ExePath, CurrentUserRegistry.Instance);

        public static bool IsEnabledFor(string exePath, IRegistryAccess registry)
        {
            try
            {
                var value = registry.GetString(RunKey, ValueName)?.Trim();
                return value != null && value.StartsWith("\"" + exePath + "\"", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Log.Error("Lecture de la cle Run", ex);
                return false;
            }
        }

        public static void SetEnabledFor(bool enabled, string exePath, IRegistryAccess registry)
        {
            try
            {
                // Une valeur qui pointe vers un autre KKAO (installe ailleurs) est aussi retiree : c'est le meme lanceur
                if (enabled) registry.SetString(RunKey, ValueName, CommandFor(exePath));
                else registry.DeleteValue(RunKey, ValueName);
                Log.Write("Lancement au demarrage de Windows : " + (enabled ? "active (" + CommandFor(exePath) + ")" : "desactive"));
            }
            catch (Exception ex)
            {
                Log.Error("Ecriture de la cle Run", ex);
            }
        }
    }
}
