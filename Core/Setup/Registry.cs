using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace KKAO.Setup
{
    // Acces au registre de l'utilisateur (HKCU). Le test automatique utilise une version en memoire : rien n'est ecrit.
    internal interface IRegistryAccess
    {
        string GetString(string subKey, string name);
        void SetString(string subKey, string name, string value);
        void SetDword(string subKey, string name, int value);
        void DeleteValue(string subKey, string name);
        void DeleteKey(string subKey);
        bool KeyExists(string subKey);
    }

    internal sealed class CurrentUserRegistry : IRegistryAccess
    {
        public static readonly CurrentUserRegistry Instance = new CurrentUserRegistry();

        public string GetString(string subKey, string name)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(subKey)) return key?.GetValue(name) as string;
        }

        public void SetString(string subKey, string name, string value)
        {
            // CreateSubKey ouvre la cle si elle existe (rien n'est efface)
            using (var key = Registry.CurrentUser.CreateSubKey(subKey)) key.SetValue(name, value, RegistryValueKind.String);
        }

        public void SetDword(string subKey, string name, int value)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(subKey)) key.SetValue(name, value, RegistryValueKind.DWord);
        }

        public void DeleteValue(string subKey, string name)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(subKey, true)) key?.DeleteValue(name, false);
        }

        public void DeleteKey(string subKey) => Registry.CurrentUser.DeleteSubKeyTree(subKey, false);

        public bool KeyExists(string subKey)
        {
            using (var key = Registry.CurrentUser.OpenSubKey(subKey)) return key != null;
        }
    }

    internal sealed class MemoryRegistry : IRegistryAccess
    {
        public readonly Dictionary<string, Dictionary<string, object>> Keys = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);

        Dictionary<string, object> Key(string subKey, bool create)
        {
            if (Keys.TryGetValue(subKey, out var key)) return key;
            if (!create) return null;
            key = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            Keys[subKey] = key;
            return key;
        }

        public string GetString(string subKey, string name) => Key(subKey, false) is Dictionary<string, object> key && key.TryGetValue(name, out var v) ? v as string : null;

        public void SetString(string subKey, string name, string value) => Key(subKey, true)[name] = value;

        public void SetDword(string subKey, string name, int value) => Key(subKey, true)[name] = value;

        public void DeleteValue(string subKey, string name) => Key(subKey, false)?.Remove(name);

        public void DeleteKey(string subKey) => Keys.Remove(subKey);

        public bool KeyExists(string subKey) => Keys.ContainsKey(subKey);
    }
}
