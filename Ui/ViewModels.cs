using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using System.Windows.Media;
using KKAO.Apps;

namespace KKAO.Ui
{
    // ===== Une appli (catalogue, installee ou de developpement) =====
    internal sealed class AppViewModel : ObservableObject
    {
        readonly MainViewModel _main;
        bool _applying;

        public AppViewModel(MainViewModel main, string id)
        {
            _main = main;
            Id = id;
            InstallCommand = new RelayCommand(() => _main.Controller?.InstallApp(Id, startAfter: true), () => CanInstall || HasUpdate);
            UninstallCommand = new RelayCommand(() => _main.Controller?.UninstallApp(Id), () => CanUninstall);
            StartCommand = new RelayCommand(() => _main.Controller?.Start(Id));
            StopCommand = new RelayCommand(() => _main.Controller?.Stop(Id));
            RestartCommand = new RelayCommand(() => _main.Controller?.Restart(Id));
            PrimaryCommand = new RelayCommand(RunPrimary, () => !IsBusy);
            OpenSettingsCommand = new RelayCommand(() => _main.Controller?.OpenOwnSettings(Id));
            OpenFolderCommand = new RelayCommand(() => _main.Controller?.OpenFolder(Location));
            ShowDetailCommand = new RelayCommand(() => _main.ShowApp(this));
        }

        public string Id { get; }

        string _name, _summary, _description, _publisher, _versionText, _stateText, _stateKind, _statusTooltip, _installPhase, _installError, _location, _sizeText, _sourceText, _settingsHint, _iconMode;
        public string Name { get => _name; private set => Set(ref _name, value); }
        public string Summary { get => _summary; private set => Set(ref _summary, value); }
        public string Description { get => _description; private set => Set(ref _description, value); }
        public string Publisher { get => _publisher; private set => Set(ref _publisher, value); }
        public string VersionText { get => _versionText; private set => Set(ref _versionText, value); }
        public string StateText { get => _stateText; private set => Set(ref _stateText, value); }
        // running, busy, error, stopped, available
        public string StateKind { get => _stateKind; private set => Set(ref _stateKind, value); }
        public string StatusTooltip { get => _statusTooltip; private set => Set(ref _statusTooltip, value); }
        public string InstallPhase { get => _installPhase; private set => Set(ref _installPhase, value); }
        public string InstallError { get => _installError; private set => Set(ref _installError, value); }
        public string Location { get => _location; private set => Set(ref _location, value); }
        public string SizeText { get => _sizeText; private set => Set(ref _sizeText, value); }
        public string SourceText { get => _sourceText; private set => Set(ref _sourceText, value); }
        public string SettingsHint { get => _settingsHint; private set => Set(ref _settingsHint, value); }

        ImageSource _icon;
        public ImageSource Icon { get => _icon; private set => Set(ref _icon, value); }

        List<string> _highlights = new List<string>();
        public List<string> Highlights { get => _highlights; private set => Set(ref _highlights, value); }

        bool _isInstalled, _isDev, _isRunning, _isBusy, _isInstalling, _canInstall, _hasUpdate, _canUninstall, _isStopped, _hasNativeSettings, _canOpenOwnSettings, _protocol, _hasActions;
        public bool IsInstalled { get => _isInstalled; private set => Set(ref _isInstalled, value); }
        public bool IsDev { get => _isDev; private set => Set(ref _isDev, value); }
        public bool IsPresent => IsInstalled || IsDev;
        public bool IsRunning { get => _isRunning; private set => Set(ref _isRunning, value); }
        public bool IsStopped { get => _isStopped; private set => Set(ref _isStopped, value); }
        public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
        public bool IsInstalling { get => _isInstalling; private set => Set(ref _isInstalling, value); }
        public bool CanInstall { get => _canInstall; private set => Set(ref _canInstall, value); }
        bool _isDownload;
        // L'installation passe par un telechargement (catalogue en ligne)
        public bool IsDownload { get => _isDownload; private set => Set(ref _isDownload, value); }
        public bool HasUpdate { get => _hasUpdate; private set => Set(ref _hasUpdate, value); }
        public bool CanUninstall { get => _canUninstall; private set => Set(ref _canUninstall, value); }
        public bool HasNativeSettings { get => _hasNativeSettings; private set => Set(ref _hasNativeSettings, value); }
        public bool CanOpenOwnSettings { get => _canOpenOwnSettings; private set => Set(ref _canOpenOwnSettings, value); }
        public bool Protocol { get => _protocol; private set => Set(ref _protocol, value); }
        public bool HasActions { get => _hasActions; private set => Set(ref _hasActions, value); }

        double _installProgress;
        public double InstallProgress { get => _installProgress; private set => Set(ref _installProgress, value); }

        string _primaryText, _primaryGlyph;
        public string PrimaryText { get => _primaryText; private set => Set(ref _primaryText, value); }
        public string PrimaryGlyph { get => _primaryGlyph; private set => Set(ref _primaryGlyph, value); }

        bool _autostart;
        public bool Autostart
        {
            get => _autostart;
            set
            {
                if (Set(ref _autostart, value) && !_applying) _main.Controller?.SetAutostart(Id, value);
            }
        }

        public string IconMode
        {
            get => _iconMode;
            set
            {
                if (Set(ref _iconMode, value) && !_applying && value != null) _main.Controller?.SetToolIconMode(Id, value);
            }
        }

        public ObservableCollection<AppActionViewModel> Actions { get; } = new ObservableCollection<AppActionViewModel>();
        public ObservableCollection<SettingsSectionViewModel> SettingsSections { get; } = new ObservableCollection<SettingsSectionViewModel>();

        public ICommand InstallCommand { get; }
        public ICommand UninstallCommand { get; }
        public ICommand StartCommand { get; }
        public ICommand StopCommand { get; }
        public ICommand RestartCommand { get; }
        public ICommand PrimaryCommand { get; }
        public ICommand OpenSettingsCommand { get; }
        public ICommand OpenFolderCommand { get; }
        public ICommand ShowDetailCommand { get; }

        int _settingsRevision = -1;

        // L'appli publie l'action reservee « open » (sa fenetre principale)
        bool _hasOpen;
        public bool HasOpen { get => _hasOpen; private set => Set(ref _hasOpen, value); }

        void RunPrimary()
        {
            var c = _main.Controller;
            if (c == null) return;
            if (CanInstall || HasUpdate) c.InstallApp(Id, startAfter: true);
            else if (HasOpen) c.OpenApp(Id);
            else if (!IsRunning) c.Start(Id);
            else _main.ShowApp(this);
        }

        public void Apply(AppInfo info)
        {
            _applying = true;
            try
            {
                Name = info.Name;
                Summary = info.Summary;
                Description = info.Description;
                Publisher = string.IsNullOrEmpty(info.Publisher) ? (info.Dev ? "Développement" : Branding.Publisher) : info.Publisher;
                VersionText = string.IsNullOrEmpty(info.Version) ? "" : "v" + info.Version;
                Highlights = info.Highlights;
                // Grande image du paquet d'abord (nette en 84 px), sinon celle du hello (64 px), sinon une tuile
                Icon = UiImages.FromFile(info.IconPath) ?? UiImages.FromBytes(info.IconBytes, Id) ?? UiImages.LetterTile(info.Name);
                IsInstalled = info.Installed;
                IsDev = info.Dev;
                Raise(nameof(IsPresent));
                Protocol = info.Protocol;
                IsInstalling = info.Installing;
                InstallProgress = Math.Round(info.InstallFraction * 100);
                InstallPhase = info.InstallPhase;
                InstallError = info.InstallError;
                CanInstall = !info.Installed && !info.Dev && info.HasPackage && !info.Installing;
                IsDownload = info.Download;
                HasUpdate = info.UpdateAvailable && !info.Installing;
                CanUninstall = info.Installed && !info.Installing;
                IsRunning = info.Run == AppRunState.Running;
                IsStopped = info.Run == AppRunState.Stopped;
                IsBusy = info.Installing || info.Run == AppRunState.Starting || info.Run == AppRunState.Stopping || info.Run == AppRunState.Waiting;
                StatusTooltip = info.StatusTooltip;
                Autostart = info.Autostart;
                IconMode = info.IconMode;
                Location = info.Location;
                SizeText = info.Size > 0 ? Bundler.FormatSize(info.Size) : null;
                SourceText = info.Dev ? "Appli de développement (tools.json)" : info.Installed ? "Installée par " + Branding.Name
                    : info.Download ? "Téléchargée à l'installation" : "Livrée avec " + Branding.Name + ", pas encore installée";
                HasOpen = info.Protocol && info.HasOpenAction;

                // Etat affiche
                if (info.Installing)
                {
                    StateKind = "busy";
                    StateText = (info.InstallPhase ?? "Installation…") + " " + Math.Round(info.InstallFraction * 100) + " %";
                }
                else if (info.Run == AppRunState.NotInstalled)
                {
                    StateKind = "available";
                    StateText = !info.HasPackage ? "Bientôt disponible"
                        : info.Download && info.DownloadSize > 0 ? "Disponible · " + Bundler.FormatSize(info.DownloadSize) + " à télécharger" : "Disponible";
                }
                else if (info.Crashed)
                {
                    StateKind = "error";
                    StateText = "Arrêtée — " + (info.CrashSummary ?? "erreur");
                }
                else if (!info.Protocol)
                {
                    StateKind = "stopped";
                    StateText = "Prête";
                }
                else
                {
                    switch (info.Run)
                    {
                        case AppRunState.Running:
                            StateKind = info.Status == StatusState.Error ? "error" : "running";
                            StateText = info.Status == StatusState.Idle || info.Status == StatusState.Active ? "En marche" : "En marche — " + StatusStates.Label(info.Status);
                            break;
                        case AppRunState.Starting:
                        case AppRunState.Waiting:
                            StateKind = "busy";
                            StateText = "Démarrage…";
                            break;
                        case AppRunState.Stopping:
                            StateKind = "busy";
                            StateText = "Arrêt…";
                            break;
                        default:
                            StateKind = "stopped";
                            StateText = "Arrêtée";
                            break;
                    }
                }
                if (HasUpdate) StateText += " · mise à jour " + info.AvailableVersion;

                // Bouton principal de la carte
                if (CanInstall) { PrimaryText = "Installer"; PrimaryGlyph = ""; }
                else if (HasUpdate) { PrimaryText = "Mettre à jour"; PrimaryGlyph = ""; }
                else if (info.Installing) { PrimaryText = "Installation…"; PrimaryGlyph = null; }
                // Appli installee : « Ouvrir » = son interface principale (action reservee « open »), demarree a la demande
                else if (!info.Protocol || HasOpen || !IsRunning) { PrimaryText = "Ouvrir"; PrimaryGlyph = ""; }
                else { PrimaryText = "Détails"; PrimaryGlyph = null; }

                // Actions du catalogue
                SyncActions(info.Actions);
                HasActions = Actions.Count > 0;

                // Reglages exposes
                HasNativeSettings = info.HasSettingsSchema && info.Settings.Count > 0;
                CanOpenOwnSettings = info.HasOwnSettingsAction && IsRunning;
                if (info.SettingsRevision != _settingsRevision || !HasNativeSettings)
                {
                    _settingsRevision = info.SettingsRevision;
                    SyncSettings(info.Settings);
                }
                SettingsHint = HasNativeSettings ? null
                    : !info.Protocol ? Name + " se règle dans sa propre fenêtre."
                    : !IsRunning ? "Démarrez " + Name + " pour voir et modifier ses réglages ici."
                    : CanOpenOwnSettings ? Name + " ne publie pas encore ses réglages dans " + Branding.Name + " : ouvrez sa fenêtre de réglages."
                    : Name + " ne publie pas encore ses réglages dans " + Branding.Name + ".";
            }
            finally
            {
                _applying = false;
            }
        }

        void SyncActions(List<AppActionInfo> infos)
        {
            if (Actions.Count == infos.Count && Actions.Select(a => a.Id).SequenceEqual(infos.Select(i => i.Id)))
            {
                for (var i = 0; i < infos.Count; i++) Actions[i].Apply(infos[i]);
                return;
            }
            Actions.Clear();
            foreach (var info in infos)
            {
                var vm = new AppActionViewModel(_main, this, info.Id);
                vm.Apply(info);
                Actions.Add(vm);
            }
        }

        void SyncSettings(List<SettingsSection> sections)
        {
            // Champ en cours d'edition garde tel quel si la structure ne change pas
            var sameShape = SettingsSections.Count == sections.Count
                && SettingsSections.Zip(sections, (a, b) => a.Title == b.Title && a.Items.Select(i => i.Id).SequenceEqual(b.Items.Select(i => i.Id))).All(x => x);
            if (sameShape)
            {
                for (var s = 0; s < sections.Count; s++)
                    for (var i = 0; i < sections[s].Items.Count; i++)
                        SettingsSections[s].Items[i].Apply(sections[s].Items[i]);
                return;
            }
            SettingsSections.Clear();
            foreach (var section in sections)
            {
                var vm = new SettingsSectionViewModel { Title = section.Title };
                foreach (var item in section.Items)
                {
                    var itemVm = new SettingItemViewModel(_main, Id);
                    itemVm.Apply(item);
                    vm.Items.Add(itemVm);
                }
                SettingsSections.Add(vm);
            }
        }
    }

    internal sealed class AppActionViewModel : ObservableObject
    {
        readonly MainViewModel _main;
        readonly AppViewModel _app;
        bool _applying;

        public AppActionViewModel(MainViewModel main, AppViewModel app, string id)
        {
            _main = main;
            _app = app;
            Id = id;
            RunCommand = new RelayCommand(() => _main.Controller?.RunAction(_app.Id, Id, "menu"), () => Enabled);
        }

        public string Id { get; }
        string _label, _shortcut, _shortcutError, _description;
        public string Label { get => _label; private set => Set(ref _label, value); }
        public string Shortcut { get => _shortcut; private set => Set(ref _shortcut, value); }
        public string ShortcutError { get => _shortcutError; private set => Set(ref _shortcutError, value); }
        public string Description { get => _description; private set => Set(ref _description, value); }
        bool _enabled;
        public bool Enabled { get => _enabled; private set => Set(ref _enabled, value); }

        bool _inQuick;
        public bool InQuickMenu
        {
            get => _inQuick;
            set
            {
                if (Set(ref _inQuick, value) && !_applying) _main.Controller?.ToggleQuick(_app.Id, Id, value);
            }
        }

        public ICommand RunCommand { get; }

        public void Apply(AppActionInfo info)
        {
            _applying = true;
            Label = info.Label;
            Shortcut = info.Shortcut;
            ShortcutError = info.ShortcutError;
            Description = info.Description;
            Enabled = info.Enabled;
            InQuickMenu = info.InQuickMenu;
            _applying = false;
        }
    }

    internal sealed class SettingsSectionViewModel
    {
        public string Title { get; set; }
        public ObservableCollection<SettingItemViewModel> Items { get; } = new ObservableCollection<SettingItemViewModel>();
    }

    // Un reglage expose par une appli (protocole v2, message "settings")
    internal sealed class SettingItemViewModel : ObservableObject
    {
        readonly MainViewModel _main;
        readonly string _toolId;
        bool _applying;
        System.Windows.Threading.DispatcherTimer _saving;

        public SettingItemViewModel(MainViewModel main, string toolId)
        {
            _main = main;
            _toolId = toolId;
            ButtonCommand = new RelayCommand(() => { if (Action != null) _main.Controller?.RunAction(_toolId, Action, "settings"); }, () => Enabled);
            BrowseCommand = new RelayCommand(Browse, () => Enabled);
            SelectOptionCommand = new RelayCommand(p => { if (p is string v) TextValue = v; });
        }

        public string Id { get; private set; }
        string _label, _description, _type, _error, _placeholder, _action, _textValue;
        public string Label { get => _label; private set => Set(ref _label, value); }
        public string Description { get => _description; private set => Set(ref _description, value); }
        public string Type { get => _type; private set => Set(ref _type, value); }
        public string Error { get => _error; private set => Set(ref _error, value); }
        public string Placeholder { get => _placeholder; private set => Set(ref _placeholder, value); }
        public string Action { get => _action; private set => Set(ref _action, value); }
        bool _enabled, _isSaving;
        public bool Enabled { get => _enabled; private set => Set(ref _enabled, value); }
        public bool IsSaving { get => _isSaving; private set => Set(ref _isSaving, value); }
        public List<SettingOptionViewModel> Options { get; private set; } = new List<SettingOptionViewModel>();

        bool _boolValue;
        public bool BoolValue
        {
            get => _boolValue;
            set
            {
                if (Set(ref _boolValue, value) && !_applying) Send(value);
            }
        }

        public string TextValue
        {
            get => _textValue;
            set
            {
                if (!Set(ref _textValue, value) || _applying) return;
                if (Type == "number")
                {
                    if (double.TryParse((value ?? "").Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) Send(number);
                    else Error = "Nombre attendu";
                }
                else Send(value ?? "");
                foreach (var option in Options) option.Refresh(value);
            }
        }

        public ICommand ButtonCommand { get; }
        public ICommand BrowseCommand { get; }
        public ICommand SelectOptionCommand { get; }

        void Send(object value)
        {
            Error = null;
            IsSaving = true;
            _main.Controller?.SetSetting(_toolId, Id, value);
            // Sans reponse de l'appli en 3 s : on arrete d'attendre
            if (_saving == null)
            {
                _saving = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                _saving.Tick += (s, e) =>
                {
                    _saving.Stop();
                    IsSaving = false;
                };
            }
            _saving.Stop();
            _saving.Start();
        }

        void Browse()
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog { Description = Label, SelectedPath = TextValue ?? "", ShowNewFolderButton = true })
            {
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) TextValue = dialog.SelectedPath;
            }
        }

        public void Apply(SettingItem item)
        {
            _applying = true;
            Id = item.Id;
            Label = item.Label;
            Description = item.Description;
            Type = item.Type;
            Error = item.Error;
            Placeholder = item.Placeholder;
            Action = item.Action;
            Enabled = item.Enabled;
            BoolValue = item.ValueBool;
            _textValue = item.Type == "number" && item.Value != null
                ? Convert.ToDouble(item.Value, CultureInfo.InvariantCulture).ToString(CultureInfo.CurrentCulture)
                : item.ValueText;
            Raise(nameof(TextValue));
            Options = item.Options.Select(o => new SettingOptionViewModel(this, o.Value, o.Label)).ToList();
            foreach (var option in Options) option.Refresh(item.ValueText);
            Raise(nameof(Options));
            IsSaving = false;
            _saving?.Stop();
            _applying = false;
        }
    }

    internal sealed class SettingOptionViewModel : ObservableObject
    {
        readonly SettingItemViewModel _owner;

        public SettingOptionViewModel(SettingItemViewModel owner, string value, string label)
        {
            _owner = owner;
            Value = value;
            Label = label;
        }

        public string Value { get; }
        public string Label { get; }
        public string Group => "setting-" + _owner.Id;

        bool _selected;
        public bool Selected
        {
            get => _selected;
            set
            {
                if (Set(ref _selected, value) && value && _owner.TextValue != Value) _owner.TextValue = Value;
            }
        }

        public void Refresh(string current)
        {
            _selected = current == Value;
            Raise(nameof(Selected));
        }
    }

    // ===== Menu rapide (page « Actions rapides ») =====
    internal sealed class QuickEntryViewModel : ObservableObject
    {
        readonly MainViewModel _main;

        public QuickEntryViewModel(MainViewModel main, QuickInfo info, int index)
        {
            _main = main;
            Info = info;
            Index = index;
            _label = info.Entry.Label;
            MoveUpCommand = new RelayCommand(() => _main.MoveQuick(Index, -1), () => Index > 0);
            MoveDownCommand = new RelayCommand(() => _main.MoveQuick(Index, +1), () => Index < _main.QuickEntries.Count - 1);
            RemoveCommand = new RelayCommand(() => _main.RemoveQuick(Index));
            RunCommand = new RelayCommand(() => { if (!IsSeparator) _main.Controller?.RunAction(Info.AppId, Info.Entry.ActionId, "quick"); }, () => Info.Enabled);
        }

        public QuickInfo Info { get; }
        public int Index { get; }
        public bool IsSeparator => Info.IsSeparator;
        public string DisplayLabel => Info.Label;
        public string DefaultLabel => Info.DefaultLabel;
        public string AppName => Info.AppName;
        public string Shortcut => Info.Shortcut;
        public bool Unavailable => Info.Unavailable;
        public bool Bold => Info.Bold;
        // Visible dans l'apercu, comme dans le vrai menu : ni action disparue, ni separateur en tete, en fin ou double
        public bool InPreview { get; set; } = true;
        public ImageSource AppIcon => _main.IconFor(Info.AppId, Info.AppName);

        string _label;
        // Libelle affiche dans l'editeur : le personnalise, sinon celui de l'appli (le vider revient a celui de l'appli)
        public string Label
        {
            get => _label ?? Info.DefaultLabel;
            set
            {
                var clean = string.IsNullOrWhiteSpace(value) || value.Trim() == Info.DefaultLabel ? null : value.Trim();
                if (_label == clean) return;
                _label = clean;
                Raise(nameof(Label));
                _main.RenameQuick(Index, clean);
            }
        }

        public ICommand MoveUpCommand { get; }
        public ICommand MoveDownCommand { get; }
        public ICommand RemoveCommand { get; }
        public ICommand RunCommand { get; }
    }

    internal sealed class QuickCatalogGroup
    {
        public string AppName { get; set; }
        public ImageSource AppIcon { get; set; }
        public string Hint { get; set; }
        public List<QuickCatalogItem> Items { get; } = new List<QuickCatalogItem>();
    }

    internal sealed class QuickCatalogItem
    {
        public string Label { get; set; }
        public string Shortcut { get; set; }
        public bool AlreadyIn { get; set; }
        public ICommand AddCommand { get; set; }
    }

    // ===== Premiers pas =====
    internal sealed class QuestViewModel : ObservableObject
    {
        public QuestViewModel(Quest quest, ICommand go)
        {
            Quest = quest;
            GoCommand = go;
        }

        public Quest Quest { get; }
        public string Id => Quest.Id;
        public string Title => Quest.Title;
        public string Description => Quest.Description;
        public string Glyph => Quest.Glyph;
        public string Achievement => Quests.AchievementTitle(Quest.Id);
        bool _done;
        public bool Done { get => _done; set => Set(ref _done, value); }
        public ICommand GoCommand { get; }
    }
}
