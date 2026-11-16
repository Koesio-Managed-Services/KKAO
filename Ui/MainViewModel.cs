using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows.Input;
using System.Windows.Media;

namespace KKAO.Ui
{
    // Etat de toute la fenetre KKAO. Controller == null : donnees de demonstration (rendus d'ecran).
    internal sealed class MainViewModel : ObservableObject
    {
        readonly Dictionary<string, AppViewModel> _apps = new Dictionary<string, AppViewModel>(StringComparer.OrdinalIgnoreCase);
        bool _applying;

        public MainViewModel(KkaoController controller)
        {
            Controller = controller;
            var (display, first, initials) = UserNames();
            UserDisplayName = display;
            UserFirstName = first;
            UserInitials = initials;
            NavigateCommand = new RelayCommand(p => Navigate(p as string));
            BackToAppsCommand = new RelayCommand(() => Navigate("apps"));
            FilterCommand = new RelayCommand(p => { AppsFilter = p as string ?? "all"; });
            InstallKkaoCommand = new RelayCommand(() => StartOnboarding("install"), () => CanInstallKkao);
            UninstallKkaoCommand = new RelayCommand(() => Controller?.UninstallKkao(), () => CanUninstallKkao);
            OpenLogCommand = new RelayCommand(() => Controller?.OpenLog());
            OpenAppsFolderCommand = new RelayCommand(() => Controller?.OpenAppsFolder());
            OpenDataFolderCommand = new RelayCommand(() => Controller?.OpenDataFolder());
            ReplayGuideCommand = new RelayCommand(() => StartOnboarding(null));
            StartTourCommand = new RelayCommand(() => TourRequested?.Invoke());
            ResetQuestsCommand = new RelayCommand(() => { Controller?.ResetQuests(); Refresh(); });
            ResetQuickCommand = new RelayCommand(() => { Controller?.ResetQuickMenu(); Refresh(); });
            AddSeparatorCommand = new RelayCommand(AddSeparator);
            InstallAllCommand = new RelayCommand(() => { foreach (var app in Apps.Where(a => a.CanInstall).ToList()) Controller?.InstallApp(app.Id, startAfter: false); });
            CheckUpdatesCommand = new RelayCommand(() => Controller?.CheckForUpdates(userInitiated: true), () => CanCheckUpdates);
            UpdateKkaoCommand = new RelayCommand(() => Controller?.UpdateKkao(), () => HasKkaoUpdate);
            if (controller != null)
            {
                controller.Changed += Refresh;
                controller.Achievement += (id, title) =>
                {
                    ToastRequested?.Invoke("Succès débloqué !", title, "\uE735", "achievement");
                    if (Quests.DoneCount(controller.Settings) == Quests.All.Count) ConfettiRequested?.Invoke();
                };
                controller.Message += (title, text, error) => ToastRequested?.Invoke(title, text, error ? "\uE7BA" : "\uE73E", error ? "error" : "info");
            }
        }

        public KkaoController Controller { get; }

        // Donnees de demonstration (rendus d'ecran)
        public List<AppInfo> DemoApps { get; set; } = new List<AppInfo>();
        public List<QuickInfo> DemoQuick { get; set; } = new List<QuickInfo>();
        public HashSet<string> DemoQuestsDone { get; set; } = new HashSet<string>();
        public List<QuickCatalogGroup> DemoCatalog { get; set; } = new List<QuickCatalogGroup>();

        public event Action<string, string, string, string> ToastRequested;
        public event Action TourRequested;
        public event Action ConfettiRequested;
        public event Action PageChanged;

        #region Identite

        public string BrandName => Branding.Name;
        public string BrandLong => Branding.LongName;
        public string Tagline => Branding.Tagline;
        public string VersionText => Branding.Name + " " + Program.Version;
        public string PublisherText => Branding.PublisherLong;
        public ImageSource LogoImage => UiImages.KkaoLogo(128);
        public ImageSource LogoLarge => UiImages.KkaoLogo(256);
        public ImageSource KoesioLogo => UiImages.KoesioLogo();
        public string UserDisplayName { get; }
        public string UserFirstName { get; }
        public string UserInitials { get; }

        public string Greeting => (DateTime.Now.Hour >= 18 || DateTime.Now.Hour < 5 ? "Bonsoir" : "Bonjour") + (string.IsNullOrEmpty(UserFirstName) ? "" : " " + UserFirstName);

        public string HeroText => InstalledCount == 0
            ? "Bienvenue dans votre Kaisse À Outils. Installez vos premières applis Koesio en un clic : elles vivront toutes sous une seule icône, près de l'horloge."
            : "Toutes vos applis Koesio réunies sous une seule icône. Un clic gauche sur le « K » près de l'horloge pour revenir ici, un clic droit pour vos actions rapides.";

        static (string display, string first, string initials) UserNames()
        {
            var display = NativeMethods.UserDisplayName();
            var tokens = (display ?? "").Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            string first;
            string last = null;
            if (tokens.Count > 0)
            {
                // « MARTIN Camille » ou « Camille MARTIN » : le prenom n'est pas en majuscules
                first = tokens.FirstOrDefault(t => t.Any(char.IsLower)) ?? tokens[0];
                last = tokens.FirstOrDefault(t => t != first);
            }
            else
            {
                var user = Environment.UserName ?? "";
                var parts = user.Split('.', '_', '-');
                first = parts[0];
                if (parts.Length > 1) last = parts[1];
                display = null;
            }
            first = Capitalize(first);
            last = Capitalize(last);
            var initials = ((first?.Length > 0 ? first.Substring(0, 1) : "") + (last?.Length > 0 ? last.Substring(0, 1) : "")).ToUpperInvariant();
            return (display ?? (first + (last != null ? " " + last : "")).Trim(), first, initials.Length == 0 ? "?" : initials);
        }

        static string Capitalize(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var lower = text.ToLower(CultureInfo.CurrentCulture);
            // Prenoms composes : Jean-Marc
            return string.Join("-", lower.Split('-').Select(p => p.Length == 0 ? p : char.ToUpper(p[0], CultureInfo.CurrentCulture) + p.Substring(1)));
        }

        #endregion

        #region Navigation

        string _currentPage = "home";
        public string CurrentPage
        {
            get => _currentPage;
            set
            {
                if (!Set(ref _currentPage, value)) return;
                PageChanged?.Invoke();
            }
        }

        AppViewModel _selectedApp;
        public AppViewModel SelectedApp { get => _selectedApp; private set => Set(ref _selectedApp, value); }

        public ICommand NavigateCommand { get; }
        public ICommand BackToAppsCommand { get; }

        public void Navigate(string page)
        {
            if (string.IsNullOrEmpty(page)) return;
            if (page.StartsWith("app:", StringComparison.Ordinal))
            {
                if (_apps.TryGetValue(page.Substring(4), out var app)) ShowApp(app);
                return;
            }
            CurrentPage = page;
        }

        public void ShowApp(AppViewModel app)
        {
            SelectedApp = app;
            if (CurrentPage == "app") PageChanged?.Invoke();
            else CurrentPage = "app";
        }

        #endregion

        #region Applis

        public ObservableCollection<AppViewModel> Apps { get; } = new ObservableCollection<AppViewModel>();
        public ObservableCollection<AppViewModel> InstalledApps { get; } = new ObservableCollection<AppViewModel>();
        public ObservableCollection<AppViewModel> FilteredApps { get; } = new ObservableCollection<AppViewModel>();

        string _appsFilter = "all";
        public string AppsFilter
        {
            get => _appsFilter;
            set
            {
                if (Set(ref _appsFilter, value)) RefreshFilteredApps();
            }
        }

        public ICommand FilterCommand { get; }
        public ICommand InstallAllCommand { get; }

        int _installedCount, _availableCount, _runningCount;
        public int InstalledCount { get => _installedCount; private set => Set(ref _installedCount, value); }
        public int AvailableCount { get => _availableCount; private set => Set(ref _availableCount, value); }
        public int RunningCount { get => _runningCount; private set => Set(ref _runningCount, value); }
        public int AllCount => Apps.Count;
        public bool NoInstalledApps => InstalledCount == 0;
        public bool CanInstallSome => Apps.Any(a => a.CanInstall);
        public string InstalledCountText => InstalledCount == 0 ? "Aucune appli installée" : InstalledCount == 1 ? "1 appli installée" : InstalledCount + " applis installées";
        public string RunningCountText => RunningCount == 0 ? "aucune en marche" : RunningCount == 1 ? "1 en marche" : RunningCount + " en marche";
        public string QuickCountText => QuickChips.Count == 0 ? "aucune action rapide" : QuickChips.Count == 1 ? "1 action rapide" : QuickChips.Count + " actions rapides";
        public string AppsBadge => AvailableCount > 0 ? AvailableCount.ToString(CultureInfo.InvariantCulture) : null;

        public ImageSource IconFor(string appId, string name) =>
            appId != null && _apps.TryGetValue(appId, out var app) && app.Icon != null ? app.Icon : UiImages.LetterTile(name ?? appId ?? "?");

        void SyncApps(List<AppInfo> infos)
        {
            var ids = infos.Select(i => i.Id).ToList();
            foreach (var info in infos)
            {
                if (!_apps.TryGetValue(info.Id, out var vm))
                {
                    vm = new AppViewModel(this, info.Id);
                    _apps[info.Id] = vm;
                }
                vm.Apply(info);
            }
            foreach (var gone in _apps.Keys.Where(k => !ids.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList()) _apps.Remove(gone);
            Replace(Apps, ids.Select(id => _apps[id]).ToList());
            Replace(InstalledApps, Apps.Where(a => a.IsPresent).ToList());
            InstalledCount = InstalledApps.Count;
            AvailableCount = Apps.Count(a => !a.IsPresent);
            RunningCount = Apps.Count(a => a.IsRunning);
            if (SelectedApp != null && !_apps.ContainsKey(SelectedApp.Id))
            {
                SelectedApp = null;
                if (CurrentPage == "app") CurrentPage = "apps";
            }
            RefreshFilteredApps();
            Raise(nameof(AllCount));
            Raise(nameof(NoInstalledApps));
            Raise(nameof(CanInstallSome));
            Raise(nameof(InstalledCountText));
            Raise(nameof(RunningCountText));
            Raise(nameof(AppsBadge));
            Raise(nameof(HeroText));
        }

        void RefreshFilteredApps()
        {
            IEnumerable<AppViewModel> list = Apps;
            if (AppsFilter == "installed") list = Apps.Where(a => a.IsPresent);
            else if (AppsFilter == "available") list = Apps.Where(a => !a.IsPresent);
            Replace(FilteredApps, list.ToList());
        }

        static void Replace<T>(ObservableCollection<T> target, List<T> items)
        {
            if (target.SequenceEqual(items)) return;
            target.Clear();
            foreach (var item in items) target.Add(item);
        }

        #endregion

        #region Menu rapide

        public ObservableCollection<QuickEntryViewModel> QuickEntries { get; } = new ObservableCollection<QuickEntryViewModel>();
        public ObservableCollection<QuickEntryViewModel> QuickChips { get; } = new ObservableCollection<QuickEntryViewModel>();
        List<QuickCatalogGroup> _quickCatalog = new List<QuickCatalogGroup>();
        public List<QuickCatalogGroup> QuickCatalog { get => _quickCatalog; private set => Set(ref _quickCatalog, value); }
        public bool HasQuickEntries => QuickChips.Count > 0;
        public ICommand AddSeparatorCommand { get; }
        public ICommand ResetQuickCommand { get; }

        string _quickSignature;

        void SyncQuick(List<QuickInfo> infos)
        {
            // Rien de change : on ne reconstruit pas (un libelle en cours d'edition garde le focus)
            var signature = string.Join("|", infos.Select(i => i.IsSeparator ? "-" : i.AppId + "/" + i.Entry.ActionId + "/" + i.Label + "/" + i.Shortcut + "/" + i.Enabled + "/" + i.Unavailable));
            if (signature == _quickSignature && QuickEntries.Count == infos.Count)
            {
                QuickCatalog = BuildQuickCatalog();
                return;
            }
            _quickSignature = signature;
            QuickEntries.Clear();
            QuickChips.Clear();
            for (var i = 0; i < infos.Count; i++)
            {
                var vm = new QuickEntryViewModel(this, infos[i], i);
                QuickEntries.Add(vm);
                if (!vm.IsSeparator && !vm.Unavailable) QuickChips.Add(vm);
            }
            Raise(nameof(HasQuickEntries));
            Raise(nameof(QuickCountText));
            QuickCatalog = BuildQuickCatalog();
        }

        List<QuickCatalogGroup> BuildQuickCatalog()
        {
            if (Controller == null) return DemoCatalog;
            var groups = new List<QuickCatalogGroup>();
            foreach (var tool in Controller.Tools.Tools.Where(t => t.Config.Protocol))
            {
                var group = new QuickCatalogGroup { AppName = tool.Name, AppIcon = IconFor(tool.Id, tool.Name) };
                foreach (var action in Controller.Quick.AvailableActions(tool))
                {
                    var toolId = tool.Id;
                    var actionId = action.Id;
                    group.Items.Add(new QuickCatalogItem
                    {
                        Label = action.Label,
                        Shortcut = action.Shortcut,
                        AlreadyIn = Controller.Quick.Contains(tool.Id, action.Id),
                        AddCommand = new RelayCommand(() => Controller.ToggleQuick(toolId, actionId, true)),
                    });
                }
                if (group.Items.Count == 0)
                    group.Hint = tool.IsReady ? tool.Name + " ne propose pas encore d'actions." : "Démarrez " + tool.Name + " une première fois pour découvrir ses actions.";
                groups.Add(group);
            }
            return groups;
        }

        List<QuickEntry> QuickModel() => QuickEntries.Select(e => e.Info.Entry.Clone()).ToList();

        void SaveQuick(List<QuickEntry> model)
        {
            if (Controller != null)
            {
                Controller.SaveQuickMenu(model);
                return;
            }
            // Demonstration
            var byEntry = DemoQuick.ToDictionary(q => q.Entry);
            DemoQuick = model.Select(e => new QuickInfo { Entry = e, Label = e.Label ?? e.KnownLabel, DefaultLabel = e.KnownLabel, AppId = e.ToolId, AppName = e.ToolId }).ToList();
            Refresh();
        }

        public void MoveQuick(int index, int delta)
        {
            var model = QuickModel();
            var target = index + delta;
            if (index < 0 || index >= model.Count || target < 0 || target >= model.Count) return;
            var item = model[index];
            model.RemoveAt(index);
            model.Insert(target, item);
            SaveQuick(model);
        }

        public void RemoveQuick(int index)
        {
            var model = QuickModel();
            if (index < 0 || index >= model.Count) return;
            model.RemoveAt(index);
            SaveQuick(model);
        }

        public void RenameQuick(int index, string label)
        {
            var model = QuickModel();
            if (index < 0 || index >= model.Count) return;
            model[index].Label = label;
            SaveQuick(model);
        }

        void AddSeparator()
        {
            var model = QuickModel();
            model.Add(QuickEntry.Separator());
            SaveQuick(model);
        }

        #endregion

        #region Premiers pas

        public ObservableCollection<QuestViewModel> QuestList { get; } = new ObservableCollection<QuestViewModel>();
        int _questsDone;
        public int QuestsDone { get => _questsDone; private set => Set(ref _questsDone, value); }
        public int QuestsTotal => Quests.All.Count;
        public double QuestProgress => QuestsTotal == 0 ? 0 : Math.Round(100.0 * QuestsDone / QuestsTotal);
        public bool AllQuestsDone => QuestsDone >= QuestsTotal;
        public string QuestsText => QuestsDone + " / " + QuestsTotal;
        public QuestViewModel NextQuest => QuestList.FirstOrDefault(q => !q.Done);
        public string QuestLevel =>
            QuestsDone >= QuestsTotal ? "Maître de la Kaisse"
            : QuestsDone >= 4 ? "Expert"
            : QuestsDone >= 2 ? "Habitué"
            : "Apprenti";
        public ICommand ResetQuestsCommand { get; }

        void SyncQuests()
        {
            if (QuestList.Count == 0)
                foreach (var quest in Quests.All)
                {
                    var target = quest.Target;
                    QuestList.Add(new QuestViewModel(quest, new RelayCommand(() => Navigate(target))));
                }
            foreach (var quest in QuestList)
                quest.Done = Controller != null ? Controller.Settings.QuestDone(quest.Id) : DemoQuestsDone.Contains(quest.Id);
            QuestsDone = QuestList.Count(q => q.Done);
            Raise(nameof(QuestProgress));
            Raise(nameof(AllQuestsDone));
            Raise(nameof(QuestsText));
            Raise(nameof(NextQuest));
            Raise(nameof(QuestLevel));
        }

        #endregion

        #region Reglages de KKAO

        string _theme = "system";
        public string Theme
        {
            get => _theme;
            set
            {
                if (!Set(ref _theme, value) || _applying) return;
                if (Controller != null) Controller.SetTheme(value);
                else ThemeManager.Apply(value);
            }
        }

        string _iconMode = "launcher";
        public string IconMode
        {
            get => _iconMode;
            set
            {
                if (Set(ref _iconMode, value) && !_applying) Controller?.SetIconMode(value);
            }
        }

        bool _startWithWindows, _openAtStartup, _showToolSubmenus = true;
        public bool StartWithWindows
        {
            get => _startWithWindows;
            set
            {
                if (Set(ref _startWithWindows, value) && !_applying && Controller != null) Controller.StartWithWindows = value;
            }
        }

        public bool OpenAtStartup
        {
            get => _openAtStartup;
            set
            {
                if (Set(ref _openAtStartup, value) && !_applying && Controller != null) Controller.Settings.OpenAtStartup = value;
            }
        }

        public bool ShowToolSubmenus
        {
            get => _showToolSubmenus;
            set
            {
                if (Set(ref _showToolSubmenus, value) && !_applying && Controller != null) Controller.Settings.ShowToolSubmenus = value;
            }
        }

        bool _demoInstalledCopy;
        public bool IsDevMode => Controller?.DevMode ?? false;
        public bool IsInstalledCopy => Controller?.IsInstalledCopy ?? _demoInstalledCopy;
        public bool CanInstallKkao => Controller?.CanInstallKkao ?? !_demoInstalledCopy;
        public bool CanUninstallKkao => Controller?.CanUninstallKkao ?? _demoInstalledCopy;
        public string InstallPlace => Controller?.Self.Layout.InstallRoot ?? @"C:\Users\…\AppData\Local\Programs\" + Paths.TechName;
        public string ToolsFilePath => Controller?.ToolsFilePath;
        public string InstallStatusText => IsDevMode
            ? "Mode développement : les applis viennent de tools.json (" + ToolsFilePath + ")."
            : IsInstalledCopy ? Branding.Name + " est installé pour vous dans " + InstallPlace + "."
            : Branding.Name + " n'est pas encore installé sur ce PC : il fonctionne depuis le dossier où vous l'avez extrait.";

        // ----- Mises a jour (catalogue en ligne : releases GitHub)
        public bool RemoteEnabled => Controller?.RemoteEnabled ?? true;
        public bool CheckingUpdates => Controller?.CheckingUpdates ?? false;
        public bool CanCheckUpdates => RemoteEnabled && !CheckingUpdates;
        public bool KkaoUpdating => Controller?.KkaoUpdating ?? false;
        public bool HasKkaoUpdate => Controller?.KkaoUpdate != null && !KkaoUpdating;
        public double KkaoUpdateProgress => Math.Round((Controller?.KkaoUpdateFraction ?? 0) * 100);
        public string KkaoUpdatePhase => Controller?.KkaoUpdatePhase;
        public string KkaoUpdateButtonText => "Mettre à jour vers " + Controller?.KkaoUpdate?.Version;
        // Applis : visibles dans le guide meme avant la reponse du catalogue en ligne
        public bool OffersApps => Apps.Any(a => !a.IsDev) || (Controller != null && Controller.RemoteEnabled && !Controller.DevMode);

        public string UpdatesText
        {
            get
            {
                var c = Controller;
                if (c == null) return "Tout est à jour. Dernière vérification : aujourd'hui à 09:30.";
                if (!c.RemoteEnabled) return "Recherche en ligne désactivée sur ce PC.";
                if (c.CheckingUpdates) return "Recherche des nouveautés…";
                var parts = new List<string>();
                var kkao = c.KkaoUpdate;
                if (kkao != null) parts.Add(Branding.Name + " " + kkao.Version + " est disponible (vous avez la " + Program.Version + ").");
                var apps = c.AppUpdateCount;
                if (apps > 0) parts.Add(apps == 1 ? "Une appli a une mise à jour : voir « Applications »." : apps + " applis ont une mise à jour : voir « Applications ».");
                if (kkao == null && apps == 0 && c.LastUpdateCheck != null && c.UpdateCheckError == null) parts.Add("Tout est à jour.");
                if (c.UpdateCheckError != null) parts.Add("Dernière recherche impossible : " + c.UpdateCheckError + ".");
                else if (c.LastUpdateCheck != null) parts.Add("Dernière vérification : " + When(c.LastUpdateCheck.Value) + ".");
                else parts.Add("Pas encore vérifié.");
                if (c.DevMode) parts.Add("Mode développement : " + Branding.Name + " lui-même ne se met pas à jour.");
                else if (!c.IsInstalledCopy) parts.Add("Installez " + Branding.Name + " pour recevoir ses propres mises à jour.");
                if (c.KkaoUpdateError != null && kkao != null) parts.Add(c.KkaoUpdateError);
                return string.Join(" ", parts);
            }
        }

        static string When(DateTime time) =>
            (time.Date == DateTime.Today ? "aujourd'hui" : time.Date == DateTime.Today.AddDays(-1) ? "hier" : "le " + time.ToString("dd/MM", CultureInfo.CurrentCulture))
            + " à " + time.ToString("HH:mm", CultureInfo.CurrentCulture);

        public ICommand CheckUpdatesCommand { get; }
        public ICommand UpdateKkaoCommand { get; }

        public ICommand InstallKkaoCommand { get; }
        public ICommand UninstallKkaoCommand { get; }
        public ICommand OpenLogCommand { get; }
        public ICommand OpenAppsFolderCommand { get; }
        public ICommand OpenDataFolderCommand { get; }
        public ICommand ReplayGuideCommand { get; }
        public ICommand StartTourCommand { get; }

        public void SetDemoInstalled(bool installed) => _demoInstalledCopy = installed;

        #endregion

        #region Guide de demarrage

        OnboardingViewModel _onboarding;
        public OnboardingViewModel Onboarding { get => _onboarding; private set => Set(ref _onboarding, value); }
        public bool IsOnboarding => Onboarding != null;

        public void StartOnboarding(string step)
        {
            Onboarding = new OnboardingViewModel(this, step);
            Raise(nameof(IsOnboarding));
        }

        public void EndOnboarding(bool startTour)
        {
            Onboarding = null;
            Raise(nameof(IsOnboarding));
            if (Controller != null)
            {
                Controller.Settings.OnboardingDone = true;
                Controller.Settings.OnboardingStep = null;
                // Applis installees pendant le guide : celles qui demarrent avec KKAO sont lancees maintenant
                foreach (var tool in Controller.Tools.Tools.Where(t => !t.IsAlive && t.Config.Source == ToolSource.Installed && Controller.Tools.EffectiveAutostart(t)))
                    tool.Start(userInitiated: true);
            }
            CurrentPage = "home";
            if (startTour) TourRequested?.Invoke();
        }

        public void TourFinished()
        {
            if (Controller != null) Controller.Settings.TourDone = true;
        }

        #endregion

        public void Refresh()
        {
            _applying = true;
            try
            {
                SyncApps(Controller?.Snapshot() ?? DemoApps);
                SyncQuick(Controller?.QuickSnapshot() ?? DemoQuick);
                SyncQuests();
                if (Controller != null)
                {
                    Theme = Controller.Settings.Theme;
                    IconMode = Controller.Settings.IconMode;
                    StartWithWindows = Controller.StartWithWindows;
                    OpenAtStartup = Controller.Settings.OpenAtStartup;
                    ShowToolSubmenus = Controller.Settings.ShowToolSubmenus;
                }
                Raise(nameof(IsInstalledCopy));
                Raise(nameof(CanInstallKkao));
                Raise(nameof(CanUninstallKkao));
                Raise(nameof(InstallStatusText));
                foreach (var name in new[] { nameof(RemoteEnabled), nameof(CheckingUpdates), nameof(CanCheckUpdates), nameof(KkaoUpdating), nameof(HasKkaoUpdate),
                    nameof(KkaoUpdateProgress), nameof(KkaoUpdatePhase), nameof(KkaoUpdateButtonText), nameof(UpdatesText), nameof(OffersApps) })
                    Raise(name);
                Raise(nameof(Greeting));
                Onboarding?.Refresh();
            }
            finally
            {
                _applying = false;
            }
        }

        // Theme de demonstration sans controleur
        public void SetDemoTheme(string theme)
        {
            _applying = true;
            Theme = theme;
            _applying = false;
        }
    }

    // ===== Guide de demarrage (premier lancement, ou « Rejouer le guide ») =====
    internal sealed class OnboardingViewModel : ObservableObject
    {
        readonly MainViewModel _main;
        readonly List<string> _steps = new List<string>();
        int _index;

        public OnboardingViewModel(MainViewModel main, string startAt)
        {
            _main = main;
            _steps.Add("welcome");
            if (main.CanInstallKkao) _steps.Add("install");
            _steps.Add("theme");
            if (main.OffersApps) _steps.Add("apps");
            _steps.Add("tray");
            _steps.Add("quick");
            _steps.Add("done");
            var start = startAt == null ? -1 : _steps.IndexOf(startAt);
            _index = Math.Max(0, start);
            foreach (var app in main.Apps.Where(a => !a.IsDev)) Choices.Add(new OnboardingChoice(app));
            NextCommand = new RelayCommand(Next, () => CanGoNext);
            BackCommand = new RelayCommand(Back, () => _index > 0 && !Busy);
            SkipCommand = new RelayCommand(() => _main.EndOnboarding(startTour: false));
            InstallKkaoCommand = new RelayCommand(InstallKkao, () => !Busy && !KkaoInstalled);
            LaterCommand = new RelayCommand(() => GoTo(_index + 1));
            InstallAppsCommand = new RelayCommand(InstallApps, () => !Busy && Choices.Any(c => c.Selected && c.App.CanInstall));
            ThemeCommand = new RelayCommand(p => _main.Theme = p as string ?? "system");
            RetryCommand = new RelayCommand(() => _main.Controller?.CheckForUpdates(userInitiated: true));
        }

        public ICommand RetryCommand { get; }

        // Catalogue en ligne pas encore recu (ou injoignable) : bouton « Reessayer »
        public bool ShowRetry => Choices.Count == 0 && _main.Controller?.RemoteEnabled == true && _main.Controller?.CheckingUpdates != true;

        public ObservableCollection<OnboardingChoice> Choices { get; } = new ObservableCollection<OnboardingChoice>();

        public string Step => _steps[_index];
        public int StepNumber => _index + 1;
        public int StepCount => _steps.Count;
        public List<OnboardingDot> Dots => _steps.Select((s, i) => new OnboardingDot { Active = i == _index, Done = i < _index }).ToList();
        public bool IsWelcome => Step == "welcome";
        public bool IsInstall => Step == "install";
        public bool IsTheme => Step == "theme";
        public bool IsApps => Step == "apps";
        public bool IsTray => Step == "tray";
        public bool IsQuick => Step == "quick";
        public bool IsDone => Step == "done";
        public bool ShowBack => _index > 0 && !IsDone;
        public bool ShowSkip => !IsDone;

        public string NextText => IsWelcome ? "C'est parti !" : IsDone ? "Découvrir " + Branding.Name : "Suivant";

        bool _busy;
        public bool Busy { get => _busy; private set { Set(ref _busy, value); Raise(nameof(CanGoNext)); } }
        public bool CanGoNext => !Busy && !(IsInstall && !KkaoInstalled && !_installSkippable);

        // Installation de KKAO
        bool _startWithWindows = true, _desktopShortcut = true, _kkaoInstalled, _installSkippable = true;
        public bool StartWithWindows { get => _startWithWindows; set => Set(ref _startWithWindows, value); }
        public bool DesktopShortcut { get => _desktopShortcut; set => Set(ref _desktopShortcut, value); }
        public bool KkaoInstalled { get => _kkaoInstalled; private set { Set(ref _kkaoInstalled, value); Raise(nameof(CanGoNext)); } }
        double _installProgress;
        public double InstallProgress { get => _installProgress; private set => Set(ref _installProgress, value); }
        string _installText, _installError;
        public string InstallText { get => _installText; private set => Set(ref _installText, value); }
        public string InstallError { get => _installError; private set => Set(ref _installError, value); }
        public string InstallPlace => _main.InstallPlace;

        public ICommand NextCommand { get; }
        public ICommand BackCommand { get; }
        public ICommand SkipCommand { get; }
        public ICommand InstallKkaoCommand { get; }
        public ICommand LaterCommand { get; }
        public ICommand InstallAppsCommand { get; }
        public ICommand ThemeCommand { get; }

        public string AppsHint
        {
            get
            {
                var c = _main.Controller;
                if (Choices.Count == 0)
                {
                    if (c?.CheckingUpdates == true) return "Recherche des applis disponibles…";
                    if (c?.UpdateCheckError != null) return "Liste des applis indisponible : " + c.UpdateCheckError + ". Réessayez, ou installez-les plus tard depuis « Applications ».";
                    return c?.RemoteEnabled == true ? "La liste des applis arrive…" : "Aucune appli n'est livrée avec ce " + Branding.Name + ".";
                }
                if (Choices.All(x => x.App.IsInstalled)) return "Tout est installé. Vous pourrez en ajouter d'autres depuis « Applications ».";
                return Choices.Any(x => x.App.IsDownload)
                    ? "Cochez celles qui vous intéressent : elles se téléchargent et s'installent pour vous seul. Rien n'est installé sans votre accord."
                    : "Cochez celles qui vous intéressent : elles s'installent pour vous seul, en quelques secondes. Rien n'est installé sans votre accord.";
            }
        }

        void Next()
        {
            if (IsDone)
            {
                _main.EndOnboarding(startTour: true);
                return;
            }
            GoTo(_index + 1);
        }

        void Back() => GoTo(_index - 1);

        void GoTo(int index)
        {
            if (index < 0 || index >= _steps.Count) return;
            _index = index;
            RaiseAll();
            if (IsDone) _main.Controller?.CompleteQuest(Quests.InstallKkao);
        }

        public void JumpTo(string step)
        {
            var i = _steps.IndexOf(step);
            if (i >= 0) GoTo(i);
        }

        void InstallKkao()
        {
            var controller = _main.Controller;
            if (controller == null)
            {
                KkaoInstalled = true;
                return;
            }
            Busy = true;
            InstallError = null;
            InstallText = "Installation…";
            controller.InstallKkao(new Setup.InstallOptions { StartWithWindows = StartWithWindows, DesktopShortcut = DesktopShortcut },
                p =>
                {
                    InstallProgress = Math.Round(p.Fraction * 100);
                    InstallText = p.Phase;
                },
                error =>
                {
                    if (error != null)
                    {
                        Busy = false;
                        InstallError = error;
                        InstallText = null;
                        return;
                    }
                    InstallProgress = 100;
                    InstallText = Branding.Name + " est installé ! Il redémarre depuis son nouveau dossier…";
                    KkaoInstalled = true;
                    // Le guide reprend a l'etape suivante dans la copie installee
                    var next = _index + 1 < _steps.Count ? _steps[_index + 1] : "done";
                    var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
                    timer.Tick += (s, e) =>
                    {
                        timer.Stop();
                        controller.RelaunchInstalled(next);
                    };
                    timer.Start();
                });
        }

        void InstallApps()
        {
            foreach (var choice in Choices.Where(c => c.Selected && c.App.CanInstall))
                _main.Controller?.InstallApp(choice.App.Id, startAfter: false);
        }

        public void Refresh()
        {
            Busy = _main.Controller?.AnyInstalling == true || (IsInstall && Busy && !KkaoInstalled);
            // Applis arrivees avec le catalogue en ligne pendant le guide
            foreach (var app in _main.Apps.Where(a => !a.IsDev && Choices.All(c => c.App != a)).ToList()) Choices.Add(new OnboardingChoice(app));
            Raise(nameof(AppsHint));
            Raise(nameof(ShowRetry));
            foreach (var choice in Choices) choice.Refresh();
        }

        public void SetDemoState(bool kkaoInstalled, double progress, string text)
        {
            KkaoInstalled = kkaoInstalled;
            InstallProgress = progress;
            InstallText = text;
        }
    }

    internal sealed class OnboardingDot
    {
        public bool Active { get; set; }
        public bool Done { get; set; }
    }

    internal sealed class OnboardingChoice : ObservableObject
    {
        public OnboardingChoice(AppViewModel app)
        {
            App = app;
            _selected = app.CanInstall;
        }

        public AppViewModel App { get; }

        bool _selected;
        public bool Selected { get => _selected; set => Set(ref _selected, value); }
        public bool Selectable => App.CanInstall;

        public void Refresh()
        {
            if (!App.CanInstall && !App.IsInstalling) _selected = App.IsInstalled;
            Raise(nameof(Selected));
            Raise(nameof(Selectable));
        }
    }
}
