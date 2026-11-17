using System.Collections.Generic;

namespace KKAO.Ui
{
    internal enum AppRunState
    {
        NotInstalled,
        Stopped,
        Starting,
        Running,
        Stopping,
        Waiting,
    }

    // Photo d'une appli pour l'interface (catalogue + installation + etat), sans aucun objet WPF
    internal sealed class AppInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Summary { get; set; }
        public string Description { get; set; }
        public string Publisher { get; set; }
        public string Version { get; set; }
        public string AvailableVersion { get; set; }
        public List<string> Highlights { get; set; } = new List<string>();
        public string IconPath { get; set; }
        public byte[] IconBytes { get; set; }
        public bool InCatalog { get; set; }
        // Installable : paquet embarque ou telechargeable
        public bool HasPackage { get; set; }
        // Installation = telechargement depuis le catalogue en ligne (taille du paquet)
        public bool Download { get; set; }
        public long DownloadSize { get; set; }
        public bool Installed { get; set; }
        public bool Dev { get; set; }
        public bool Protocol { get; set; } = true;
        public AppRunState Run { get; set; }
        public bool Crashed { get; set; }
        public string CrashSummary { get; set; }
        public StatusState Status { get; set; }
        public string StatusTooltip { get; set; }
        public bool Installing { get; set; }
        public double InstallFraction { get; set; }
        public string InstallPhase { get; set; }
        public string InstallError { get; set; }
        public bool UpdateAvailable { get; set; }
        public bool Autostart { get; set; }
        public string IconMode { get; set; } = "inherit";
        public List<AppActionInfo> Actions { get; set; } = new List<AppActionInfo>();
        public bool HasActionsCatalog { get; set; }
        public bool HasSettingsSchema { get; set; }
        public List<SettingsSection> Settings { get; set; } = new List<SettingsSection>();
        public int SettingsRevision { get; set; }
        public bool HasOwnSettingsAction { get; set; }
        // Action reservee « open » : la fenetre principale de l'appli (bouton « Ouvrir »)
        public bool HasOpenAction { get; set; }
        public string Location { get; set; }
        public long Size { get; set; }
    }

    internal sealed class AppActionInfo
    {
        public string Id { get; set; }
        public string Label { get; set; }
        public string Shortcut { get; set; }
        public string ShortcutError { get; set; }
        public string Description { get; set; }
        public bool Enabled { get; set; } = true;
        public bool IsDefault { get; set; }
        public bool InQuickMenu { get; set; }
    }

    internal sealed class QuickInfo
    {
        public QuickEntry Entry { get; set; }
        public bool IsSeparator => Entry?.IsSeparator ?? false;
        public string Label { get; set; }
        public string DefaultLabel { get; set; }
        public string AppId { get; set; }
        public string AppName { get; set; }
        public string Shortcut { get; set; }
        public bool Enabled { get; set; } = true;
        public bool Unavailable { get; set; }
        public bool Bold { get; set; }
    }
}
