using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KKAO.Apps;
using KKAO.Setup;

namespace KKAO.Ui
{
    // Ce que la fenetre de KKAO peut faire : installer / desinstaller des applis, les demarrer, regler, menu rapide, missions…
    // Tout est appele sur le fil de l'interface ; les travaux longs (zip) partent sur un fil de fond.
    internal sealed class KkaoController
    {
        readonly SynchronizationContext _ui;
        readonly Dictionary<string, InstallState> _installs = new Dictionary<string, InstallState>(StringComparer.OrdinalIgnoreCase);
        System.Windows.Forms.Timer _throttle;

        public KkaoController(ToolManager tools, KkaoSettings settings, AppCatalog catalog, AppInstaller installer, SelfInstaller self,
            bool devMode, string toolsFilePath, SynchronizationContext ui, RemoteCatalogService remote = null)
        {
            Tools = tools;
            Settings = settings;
            LocalCatalog = catalog ?? AppCatalog.Empty();
            Remote = remote;
            Catalog = AppCatalog.Merge(LocalCatalog, remote?.Catalog);
            Installer = installer;
            Self = self;
            DevMode = devMode;
            ToolsFilePath = toolsFilePath;
            _ui = ui;
            Quick = new QuickMenuService(tools, settings);
            tools.Changed += _ => RaiseChangedSoon();
            tools.ToolsChanged += RaiseChanged;
            // Copie installee (ou developpement) : la mission « Installer KKAO » est deja accomplie
            if ((Paths.IsInstalledCopy || devMode) && !settings.QuestDone(Quests.InstallKkao)) settings.CompleteQuest(Quests.InstallKkao);
        }

        public ToolManager Tools { get; }
        public KkaoSettings Settings { get; }
        // Applis embarquees (dossier partage) + applis en ligne (release GitHub), recalcule a chaque lecture du catalogue en ligne
        public AppCatalog Catalog { get; private set; }
        public AppCatalog LocalCatalog { get; }
        public RemoteCatalogService Remote { get; }
        public AppInstaller Installer { get; }
        public SelfInstaller Self { get; }
        public QuickMenuService Quick { get; }
        public bool DevMode { get; }
        public string ToolsFilePath { get; }

        public bool IsInstalledCopy => Paths.IsInstalledCopy;
        public bool CanInstallKkao => !DevMode && !IsInstalledCopy;
        public bool CanUninstallKkao => IsInstalledCopy || (!DevMode && Self.IsInstalled);

        public event Action Changed;
        // Mission accomplie : id, titre du succes
        public event Action<string, string> Achievement;
        // Message pour l'utilisateur : titre, texte, erreur ?
        public event Action<string, string, bool> Message;

        public Action<string> OpenWindowRequest { get; set; }
        public Action QuitRequest { get; set; }
        public Action<string, string> RelaunchRequest { get; set; }

        sealed class InstallState
        {
            public bool Running;
            public double Fraction;
            public string Phase;
            public string Error;
        }

        #region Photo pour l'interface

        public List<AppInfo> Snapshot()
        {
            var list = new List<AppInfo>();
            foreach (var tool in Tools.Tools) list.Add(InfoFor(tool, Catalog.Find(tool.Id) ?? ReplacementFor(tool)));
            foreach (var app in Catalog.Apps.Where(a => Tools.Find(a.Id) == null && !a.Aliases.Any(IsInstalledTool))) list.Add(InfoFor(app));
            return list;
        }

        // Appli installee sous un ancien id (renommee depuis) : l'appli du catalogue qui la remplace
        CatalogApp ReplacementFor(ToolHost tool) => tool.Config.Source == ToolSource.Installed ? Catalog.FindByAlias(tool.Id) : null;

        bool IsInstalledTool(string id) => Tools.Find(id)?.Config.Source == ToolSource.Installed;

        AppInfo InfoFor(ToolHost tool, CatalogApp cat)
        {
            var renamed = cat != null && !string.Equals(cat.Id, tool.Id, StringComparison.OrdinalIgnoreCase);
            var info = new AppInfo
            {
                Id = tool.Id,
                Name = renamed ? cat.Name : tool.Name,
                Summary = Prefer(tool.Config.Summary, cat?.Summary),
                Description = Prefer(tool.Config.Description, cat?.Description),
                Publisher = Prefer(tool.Config.Publisher, cat?.Publisher),
                Version = Prefer(tool.Config.Version, tool.ToolVersion),
                AvailableVersion = cat?.Version,
                IconPath = File.Exists(tool.Config.IconPath ?? "") ? tool.Config.IconPath : cat?.IconPath,
                IconBytes = tool.HelloIcon,
                InCatalog = cat != null,
                HasPackage = cat?.CanInstall ?? false,
                Download = cat?.NeedsDownload ?? false,
                DownloadSize = cat?.NeedsDownload == true ? cat.Size : 0,
                Installed = tool.Config.Source == ToolSource.Installed,
                Dev = tool.Config.Source == ToolSource.ToolsFile,
                Protocol = tool.Config.Protocol,
                Run = MapRun(tool),
                Crashed = tool.Crashed && !tool.IsAlive,
                CrashSummary = tool.CrashSummary,
                Status = tool.Status,
                StatusTooltip = tool.StatusTooltip,
                Autostart = Tools.EffectiveAutostart(tool),
                IconMode = Settings.ToolIconMode(tool.Id),
                HasActionsCatalog = tool.HasActionsCatalog || Settings.CachedActions(tool.Id).Count > 0,
                HasSettingsSchema = tool.HasSettings,
                Settings = tool.Settings.ToList(),
                SettingsRevision = tool.SettingsRevision,
                Location = tool.Config.AppDir ?? (tool.Config.Exe != null ? Path.GetDirectoryName(tool.Config.Exe) : null),
                Size = cat?.InstalledSize ?? 0,
            };
            if (cat != null) info.Highlights.AddRange(cat.Highlights);
            info.UpdateAvailable = info.Installed && cat != null && cat.CanInstall && (renamed || VersionUtil.Compare(cat.Version, tool.Config.Version) > 0);
            var actions = Quick.AvailableActions(tool);
            foreach (var action in actions)
                info.Actions.Add(new AppActionInfo
                {
                    Id = action.Id,
                    Label = action.Label,
                    Shortcut = action.Shortcut,
                    ShortcutError = action.ShortcutError,
                    Description = action.Description,
                    Enabled = action.Enabled || !tool.IsReady,
                    IsDefault = action.Default,
                    InQuickMenu = Quick.Contains(tool.Id, action.Id),
                });
            info.HasOwnSettingsAction = actions.Any(a => a.Id == "settings") || MenuNode.Flatten(tool.Menu).Any(n => n.IsClickable && n.Id == "settings");
            ApplyInstallState(info);
            return info;
        }

        AppInfo InfoFor(CatalogApp app)
        {
            var info = new AppInfo
            {
                Id = app.Id,
                Name = app.Name,
                Summary = app.Summary,
                Description = app.Description,
                Publisher = app.Publisher,
                Version = app.Version,
                AvailableVersion = app.Version,
                IconPath = app.IconPath,
                InCatalog = true,
                HasPackage = app.CanInstall,
                Download = app.NeedsDownload,
                DownloadSize = app.NeedsDownload ? app.Size : 0,
                Run = AppRunState.NotInstalled,
                Size = app.InstalledSize,
            };
            info.Highlights.AddRange(app.Highlights);
            ApplyInstallState(info);
            return info;
        }

        void ApplyInstallState(AppInfo info)
        {
            if (!_installs.TryGetValue(info.Id, out var state)) return;
            info.Installing = state.Running;
            info.InstallFraction = state.Fraction;
            info.InstallPhase = state.Phase;
            info.InstallError = state.Error;
        }

        static AppRunState MapRun(ToolHost tool)
        {
            if (!tool.Config.Protocol) return AppRunState.Stopped;
            switch (tool.RunState)
            {
                case ToolRunState.Starting: return AppRunState.Starting;
                case ToolRunState.Running: return tool.IsReady ? AppRunState.Running : AppRunState.Starting;
                case ToolRunState.Stopping: return AppRunState.Stopping;
                case ToolRunState.Waiting: return AppRunState.Waiting;
                default: return AppRunState.Stopped;
            }
        }

        static string Prefer(string a, string b) => string.IsNullOrWhiteSpace(a) ? b : a;

        // Editeur du menu rapide : les entrees telles quelles (separateurs compris), chacune resolue
        public List<QuickInfo> QuickSnapshot()
        {
            var list = new List<QuickInfo>();
            foreach (var entry in Quick.Entries)
            {
                if (entry.IsSeparator)
                {
                    list.Add(new QuickInfo { Entry = entry });
                    continue;
                }
                var r = Quick.Resolve(entry);
                list.Add(new QuickInfo
                {
                    Entry = entry,
                    Label = r.Label,
                    DefaultLabel = r.Action?.Label ?? entry.KnownLabel ?? entry.ActionId,
                    AppId = entry.ToolId,
                    AppName = r.Tool?.Name ?? entry.ToolId,
                    Shortcut = r.Shortcut,
                    Enabled = r.Enabled,
                    Unavailable = r.Unavailable,
                    Bold = r.Bold,
                });
            }
            return list;
        }

        #endregion

        #region Applis : installer, desinstaller, demarrer

        // id : celui de l'appli du catalogue, ou l'ancien id d'une appli installee avant son renommage (elle est alors remplacee)
        // Paquets telecharges, le temps de l'installation (dossier « . » : ignore par l'inventaire, nettoye au demarrage)
        string DownloadsDir => Path.Combine(Installer.AppsRoot, ".telechargements");

        public void InstallApp(string id, bool startAfter)
        {
            var app = Catalog.Find(id) ?? Catalog.FindByAlias(id);
            if (app == null || !app.CanInstall)
            {
                Message?.Invoke("Installation impossible", "Cette appli n'est pas disponible pour le moment : ni dans le dossier " + Branding.Name
                    + ", ni dans le catalogue en ligne.", true);
                return;
            }
            if (_installs.TryGetValue(id, out var current) && current.Running) return;
            var state = new InstallState { Running = true, Phase = "Préparation…" };
            _installs[id] = state;
            RaiseChanged();

            if (app.HasPackage)
            {
                StopThenInstall(app, app.PackagePath, id, state, startAfter, false, 0);
                return;
            }
            // En ligne : telechargement pendant que l'ancienne version continue de tourner, puis installation
            var file = Path.Combine(DownloadsDir, app.Id + "-" + (app.Version ?? "0") + ".zip");
            state.Phase = "Téléchargement…";
            Log.Write("[" + id + "] téléchargement de " + app.Name + " " + app.Version + " depuis " + app.Url);
            Task.Run(() => Downloader.DownloadFile(app.Url, file, app.Size, (done, total) => Post(() =>
                {
                    state.Fraction = total > 0 ? DownloadShare * done / total : 0;
                    state.Phase = "Téléchargement… " + Bundler.FormatSize(done) + (total > 0 ? " sur " + Bundler.FormatSize(total) : "");
                    RaiseChangedSoon();
                }), CancellationToken.None))
                .ContinueWith(task => Post(() =>
                {
                    if (task.IsFaulted)
                    {
                        var reason = Downloader.Describe(task.Exception?.GetBaseException());
                        Log.Write("[" + id + "] téléchargement impossible : " + reason);
                        Downloader.TryDelete(file);
                        Fail(id, state, "Téléchargement impossible : " + reason + ".");
                        return;
                    }
                    StopThenInstall(app, file, id, state, startAfter, true, DownloadShare);
                }));
        }

        // Part de la barre de progression prise par le telechargement (le reste : verification et extraction)
        const double DownloadShare = 0.7;

        void StopThenInstall(CatalogApp app, string package, string id, InstallState state, bool startAfter, bool deleteAfter, double offset)
        {
            var existing = Tools.Find(id);
            var wasRunning = existing?.IsAlive == true;
            void Proceed()
            {
                Log.Write("[" + id + "] installation de " + app.Name + " " + app.Version + " depuis " + package);
                Task.Run(() =>
                    {
                        try
                        {
                            return Installer.Install(package, app.Id, app.Sha256, p => Post(() =>
                            {
                                state.Fraction = offset + (1 - offset) * p.Fraction;
                                state.Phase = p.Phase;
                                RaiseChangedSoon();
                            }), CancellationToken.None);
                        }
                        finally
                        {
                            if (deleteAfter) Downloader.TryDelete(package);
                        }
                    })
                    .ContinueWith(task => Post(() => OnInstalled(app, state, task, startAfter || wasRunning, id)));
            }
            if (existing != null && existing.IsAlive)
            {
                state.Phase = "Fermeture de " + existing.Name + "…";
                RaiseChanged();
                Tools.StopAndWait(existing, 12000, stopped =>
                {
                    if (stopped) Proceed();
                    else
                    {
                        if (deleteAfter) Downloader.TryDelete(package);
                        Fail(id, state, app.Name + " ne s'est pas fermée : fermez-la, puis réessayez.");
                    }
                });
            }
            else Proceed();
        }

        void OnInstalled(CatalogApp app, InstallState state, Task<AppManifest> task, bool start, string requestedId)
        {
            state.Running = false;
            if (task.IsFaulted)
            {
                var ex = task.Exception?.GetBaseException();
                Log.Error("[" + app.Id + "] installation", ex);
                Fail(requestedId, state, ex is InstallException ? ex.Message : "Installation impossible : " + ex?.Message);
                return;
            }
            _installs.Remove(requestedId);
            var config = task.Result.ToToolConfig(Installer.AppDir(app.Id));
            var tool = Tools.AddOrReplace(config);
            Log.Write("[" + app.Id + "] installee : " + config.Exe);
            // Appli renommee : l'ancienne installation disparait, ses reglages passent au nouvel id
            if (!string.Equals(requestedId, app.Id, StringComparison.OrdinalIgnoreCase))
            {
                var old = Tools.Find(requestedId);
                if (old != null) Tools.Remove(old);
                Settings.MigrateToolId(requestedId, app.Id);
                Quick.ForgetTool(requestedId);
                Task.Run(() => Installer.Uninstall(requestedId)).ContinueWith(t =>
                {
                    if (t.IsFaulted) Log.Write("[" + requestedId + "] ancienne installation non supprimée : " + t.Exception?.GetBaseException().Message);
                    else Log.Write("[" + requestedId + "] ancienne installation supprimée (remplacée par " + app.Id + ")");
                });
            }
            CompleteQuest(Quests.InstallApp);
            Message?.Invoke(tool.Name + " est installée", string.IsNullOrEmpty(app.Summary) ? "Elle apparaît dans vos applis." : app.Summary, false);
            if (start) tool.Start(userInitiated: true);
            RaiseChanged();
        }

        void Fail(string id, InstallState state, string message)
        {
            state.Running = false;
            state.Error = message;
            Message?.Invoke("Installation impossible", message, true);
            RaiseChanged();
        }

        public bool IsInstalling(string id) => _installs.TryGetValue(id, out var s) && s.Running;

        public bool AnyInstalling => _installs.Values.Any(s => s.Running);

        public void UninstallApp(string id)
        {
            var tool = Tools.Find(id);
            if (tool == null || tool.Config.Source != ToolSource.Installed) return;
            var state = new InstallState { Running = true, Phase = "Désinstallation…" };
            _installs[id] = state;
            RaiseChanged();
            Tools.StopAndWait(tool, 12000, stopped =>
            {
                if (!stopped)
                {
                    Fail(id, state, tool.Name + " ne s'est pas fermée : fermez-la, puis réessayez.");
                    return;
                }
                Task.Run(() => Installer.Uninstall(id)).ContinueWith(task => Post(() =>
                {
                    state.Running = false;
                    if (task.IsFaulted)
                    {
                        var ex = task.Exception?.GetBaseException();
                        Fail(id, state, ex is InstallException ? ex.Message : "Désinstallation impossible : " + ex?.Message);
                        return;
                    }
                    _installs.Remove(id);
                    Tools.Remove(tool);
                    Quick.ForgetTool(id);
                    Settings.ForgetTool(id);
                    Log.Write("[" + id + "] desinstallee");
                    Message?.Invoke(tool.Name + " est désinstallée", "Ses données (documents, réglages) sont conservées.", false);
                    RaiseChanged();
                }));
            });
        }

        public void Start(string id) => Tools.Find(id)?.Start(userInitiated: true);

        public void Stop(string id) => Tools.Find(id)?.Stop();

        public void Restart(string id) => Tools.Find(id)?.Restart();

        public void RunAction(string toolId, string actionId, string source)
        {
            var tool = Tools.Find(toolId);
            if (tool == null) return;
            if (tool.RunAction(actionId, source) && source == "quick") CompleteQuest(Quests.QuickAction);
        }

        public void SetSetting(string toolId, string settingId, object value)
        {
            var tool = Tools.Find(toolId);
            if (tool != null && tool.SendSetSetting(settingId, value)) CompleteQuest(Quests.AppSettings);
        }

        public void OpenOwnSettings(string toolId)
        {
            RunAction(toolId, "settings", "settings");
            CompleteQuest(Quests.AppSettings);
        }

        public void SetAutostart(string id, bool value)
        {
            var tool = Tools.Find(id);
            if (tool != null) Tools.SetAutostart(tool, value);
        }

        public void SetToolIconMode(string id, string mode)
        {
            Settings.SetToolIconMode(id, mode);
            RaiseChanged();
        }

        public void SetIconMode(string mode)
        {
            Settings.IconMode = mode;
            RaiseChanged();
        }

        public void OpenFolder(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
            try { Process.Start("explorer.exe", "\"" + path + "\""); }
            catch (Exception ex) { Log.Error("Ouverture de " + path, ex); }
        }

        #endregion

        #region Reglages de KKAO

        public void SetTheme(string theme)
        {
            Settings.Theme = theme;
            ThemeManager.Apply(theme);
            CompleteQuest(Quests.ChooseTheme);
            RaiseChanged();
        }

        public bool StartWithWindows
        {
            get => StartupManager.IsEnabled;
            set
            {
                StartupManager.SetEnabled(value);
                RaiseChanged();
            }
        }

        public void SaveQuickMenu(IEnumerable<QuickEntry> entries)
        {
            Quick.Save(entries);
            CompleteQuest(Quests.CustomizeMenu);
            RaiseChanged();
        }

        public void ResetQuickMenu()
        {
            Quick.Reset();
            RaiseChanged();
        }

        public void ToggleQuick(string toolId, string actionId, bool include)
        {
            var tool = Tools.Find(toolId);
            var action = tool == null ? null : Quick.AvailableActions(tool).FirstOrDefault(a => a.Id == actionId);
            if (action == null) return;
            Quick.Toggle(tool, action, include);
            CompleteQuest(Quests.CustomizeMenu);
            RaiseChanged();
        }

        public void CompleteQuest(string id)
        {
            if (!Settings.CompleteQuest(id)) return;
            Log.Write("Mission accomplie : " + id);
            Achievement?.Invoke(id, Quests.AchievementTitle(id));
            RaiseChanged();
        }

        public void ResetQuests()
        {
            Settings.ResetQuests();
            if (Paths.IsInstalledCopy || DevMode) Settings.CompleteQuest(Quests.InstallKkao);
            RaiseChanged();
        }

        // Installation de KKAO pour l'utilisateur ; done(null) si tout va bien, sinon le message d'erreur
        public void InstallKkao(InstallOptions options, Action<InstallProgress> progress, Action<string> done)
        {
            Task.Run(() => Self.Install(Paths.ExeDir, options, p => Post(() => progress?.Invoke(p))))
                .ContinueWith(task => Post(() =>
                {
                    if (task.IsFaulted)
                    {
                        var ex = task.Exception?.GetBaseException();
                        Log.Error("Installation de " + Branding.Name, ex);
                        done?.Invoke(ex is InstallException ? ex.Message : "Installation impossible : " + ex?.Message);
                        return;
                    }
                    Settings.CompleteQuest(Quests.InstallKkao);
                    done?.Invoke(null);
                }));
        }

        // Apres l'installation : KKAO se relance depuis son dossier definitif (le guide reprend a l'etape suivante)
        public void RelaunchInstalled(string resumeStep)
        {
            Settings.OnboardingStep = resumeStep;
            RelaunchRequest?.Invoke(Self.InstalledExe, "--after-install");
        }

        public void UninstallKkao()
        {
            var exe = File.Exists(Self.InstalledExe) ? Self.InstalledExe : Paths.ExePath;
            try
            {
                using (Process.Start(new ProcessStartInfo(exe, "--uninstall") { UseShellExecute = false })) { }
            }
            catch (Exception ex)
            {
                Log.Error("Desinstallation", ex);
                Message?.Invoke("Désinstallation impossible", ex.Message, true);
            }
        }

        public void OpenLog()
        {
            if (!File.Exists(Log.FilePath)) Log.Write("Journal de " + Branding.Name);
            Log.Flush();
            try
            {
                using (Process.Start(new ProcessStartInfo(Log.FilePath) { UseShellExecute = true })) { }
            }
            catch (Exception)
            {
                try { using (Process.Start("notepad.exe", "\"" + Log.FilePath + "\"")) { } }
                catch (Exception ex) { Log.Error("Ouverture du journal", ex); }
            }
        }

        public void OpenAppsFolder()
        {
            Directory.CreateDirectory(Installer.AppsRoot);
            OpenFolder(Installer.AppsRoot);
        }

        public void OpenDataFolder() => OpenFolder(Paths.DataDir);

        #endregion

        #region Catalogue en ligne et mises a jour

        bool _checking;
        public bool RemoteEnabled => Remote?.Enabled == true;
        public bool CheckingUpdates => _checking;
        public DateTime? LastUpdateCheck => Remote?.LastSuccess;
        public string UpdateCheckError => Remote?.LastError;

        // Nouvelle version de KKAO proposee a la copie installee (pas en developpement, pas depuis le dossier de telechargement)
        public KkaoRelease KkaoUpdate =>
            Remote?.Kkao != null && !DevMode && IsInstalledCopy && VersionUtil.Compare(Remote.Kkao.Version, Program.Version) > 0 ? Remote.Kkao : null;

        public int AppUpdateCount => Snapshot().Count(a => a.UpdateAvailable);

        // userInitiated : bouton « Rechercher les mises a jour » (resultat annonce meme s'il n'y a rien)
        public void CheckForUpdates(bool userInitiated)
        {
            if (!RemoteEnabled || _checking) return;
            _checking = true;
            RaiseChanged();
            Task.Run(() => Remote.Fetch()).ContinueWith(task => Post(() =>
            {
                _checking = false;
                if (task.IsFaulted)
                {
                    var reason = Downloader.Describe(task.Exception?.GetBaseException());
                    Remote.SetError(reason);
                    Log.Write("Catalogue en ligne : " + reason);
                    if (userInitiated) Message?.Invoke("Recherche impossible", reason + ".", true);
                    RaiseChanged();
                    return;
                }
                Remote.Apply(task.Result);
                Catalog = AppCatalog.Merge(LocalCatalog, Remote.Catalog);
                var updates = Snapshot().Where(a => a.UpdateAvailable).ToList();
                var kkao = KkaoUpdate;
                Log.Write("Catalogue en ligne : " + string.Join(", ", Remote.Catalog.Apps.Select(a => a.Name + " " + a.Version))
                    + (Remote.Kkao != null ? " ; " + Branding.Name + " " + Remote.Kkao.Version : "")
                    + (updates.Count > 0 ? " ; à mettre à jour : " + string.Join(", ", updates.Select(a => a.Name)) : ""));
                if (kkao != null && (userInitiated || Settings.MarkUpdateNotified("kkao", kkao.Version)))
                    Message?.Invoke(Branding.Name + " " + kkao.Version + " est disponible", "Réglages > Mises à jour : un clic suffit, vos réglages et vos applis sont conservés.", false);
                foreach (var app in updates.Where(a => userInitiated || Settings.MarkUpdateNotified(a.Id, a.AvailableVersion)))
                    Message?.Invoke(app.Name + " " + app.AvailableVersion + " est disponible", "Applications > " + app.Name + " > Mettre à jour.", false);
                if (userInitiated && kkao == null && updates.Count == 0)
                    Message?.Invoke("Tout est à jour", Branding.Name + " " + Program.Version + " et vos applis sont dans leur dernière version.", false);
                RaiseChanged();
            }));
        }

        // Mise a jour de KKAO : telechargement de la nouvelle version (empreinte verifiee), puis relance par elle (--update)
        bool _kkaoUpdating;
        double _kkaoUpdateFraction;
        string _kkaoUpdatePhase, _kkaoUpdateError;
        public bool KkaoUpdating => _kkaoUpdating;
        public double KkaoUpdateFraction => _kkaoUpdateFraction;
        public string KkaoUpdatePhase => _kkaoUpdatePhase;
        public string KkaoUpdateError => _kkaoUpdateError;

        public string UpdateDir => Path.Combine(Self.Layout.InstallRoot, "update");

        public void UpdateKkao()
        {
            var release = KkaoUpdate;
            if (release == null || _kkaoUpdating) return;
            _kkaoUpdating = true;
            _kkaoUpdateError = null;
            _kkaoUpdateFraction = 0;
            _kkaoUpdatePhase = "Téléchargement…";
            RaiseChanged();
            var exe = Path.Combine(UpdateDir, release.Version, Self.ExeName);
            Log.Write("Mise à jour de " + Branding.Name + " vers " + release.Version + " depuis " + release.Url);
            Task.Run(() =>
            {
                Downloader.DownloadFile(release.Url, exe, release.Size, (done, total) => Post(() =>
                {
                    _kkaoUpdateFraction = total > 0 ? 0.95 * done / total : 0;
                    _kkaoUpdatePhase = "Téléchargement… " + Bundler.FormatSize(done) + (total > 0 ? " sur " + Bundler.FormatSize(total) : "");
                    RaiseChangedSoon();
                }), CancellationToken.None);
                var sha = AppInstaller.Sha256Of(exe, null, CancellationToken.None);
                if (!string.Equals(sha, release.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    Downloader.TryDelete(exe);
                    throw new InstallException("le fichier reçu ne correspond pas à celui publié (empreinte différente) : mise à jour annulée");
                }
            }).ContinueWith(task => Post(() =>
            {
                if (task.IsFaulted)
                {
                    _kkaoUpdating = false;
                    _kkaoUpdateError = "Mise à jour impossible : " + Downloader.Describe(task.Exception?.GetBaseException()) + ".";
                    Log.Write(_kkaoUpdateError);
                    Message?.Invoke("Mise à jour impossible", _kkaoUpdateError, true);
                    RaiseChanged();
                    return;
                }
                _kkaoUpdateFraction = 1;
                _kkaoUpdatePhase = Branding.Name + " redémarre…";
                RaiseChanged();
                Log.Write("Nouvelle version prête (empreinte vérifiée) : " + exe);
                RelaunchRequest?.Invoke(exe, "--update");
            }));
        }

        #endregion

        #region Evenements

        void Post(Action action)
        {
            try { _ui.Post(_ => action(), null); }
            catch (Exception) { }
        }

        public void RaiseChanged()
        {
            _throttle?.Stop();
            Changed?.Invoke();
        }

        // Rafales (progression, etats) : une seule mise a jour toutes les 120 ms
        void RaiseChangedSoon()
        {
            if (_throttle == null)
            {
                _throttle = new System.Windows.Forms.Timer { Interval = 120 };
                _throttle.Tick += (s, e) =>
                {
                    _throttle.Stop();
                    Changed?.Invoke();
                };
            }
            if (!_throttle.Enabled) _throttle.Start();
        }

        #endregion
    }
}
