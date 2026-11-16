using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using KKAO.Apps;
using KKAO.Setup;
using KKAO.Ui;

namespace KKAO
{
    // KKAO lui-meme : l'icone pres de l'horloge, son menu, ses bulles, la fenetre et les icones des applis
    internal sealed class TrayApp : ApplicationContext
    {
        readonly SynchronizationContext _ui;
        readonly ToolManager _tools;
        readonly KkaoController _controller;
        readonly MainViewModel _vm;
        readonly MenuBuilder _builder;
        readonly ContextMenuStrip _menu;
        readonly NotifyIcon _icon;
        readonly AppTrayIcons _appIcons;
        readonly List<RegisteredWaitHandle> _waits = new List<RegisteredWaitHandle>();
        readonly int _iconSize = Math.Max(16, SystemInformation.SmallIconSize.Width);
        MainWindow _window;
        StatusState? _shownState;
        string _shownText;
        Action _balloonAction;

        readonly System.Windows.Forms.Timer _updateTimer;

        public TrayApp(string toolsArg, EventWaitHandle showSignal, EventWaitHandle quitSignal, bool autostart, bool afterUpdate = false)
        {
            _ui = SynchronizationContext.Current;
            var exeDir = Paths.ExeDir;

            // tools.json : applis de developpement (prioritaires) ; absent chez les collegues
            ToolsFile config = null;
            string problem = null;
            var toolsPath = ToolsFile.Locate(toolsArg, exeDir);
            if (toolsPath != null && File.Exists(toolsPath))
            {
                config = ToolsFile.Load(toolsPath);
                Log.Write("tools.json : " + toolsPath + " (" + config.Tools.Count + " outil(s))");
                foreach (var issue in config.Problems) Log.Write("tools.json : " + issue);
                if (config.Problems.Count > 0) problem = "tools.json : " + config.Problems[0];
            }
            else
            {
                toolsPath = null;
            }
            var devMode = config != null;

            var settings = KkaoSettings.Load(KkaoSettings.DefaultPath);

            // Applis installees par KKAO (%LOCALAPPDATA%\Programs\KKAO\apps\<id>)
            var installer = new AppInstaller(Paths.AppsRoot);
            installer.CleanupLeftovers();
            var problems = new List<string>();
            var installed = installer.ScanInstalled(problems);
            foreach (var issue in problems) Log.Write("Applis installées : " + issue);
            var all = new List<ToolConfig>();
            if (config != null) all.AddRange(config.Tools);
            foreach (var tool in installed)
            {
                if (all.Any(t => string.Equals(t.Id, tool.Id, StringComparison.OrdinalIgnoreCase)))
                    Log.Write("[" + tool.Id + "] installée, mais tools.json l'emporte (développement)");
                else all.Add(tool);
            }

            // Catalogue : applis livrees avec KKAO (dossier a partager, ou copie installee)
            var catalog = AppCatalog.LoadFrom(exeDir);
            if (catalog.FilePath != null) Log.Write("Catalogue : " + catalog.FilePath + " (" + catalog.Apps.Count + " appli(s))");
            foreach (var issue in catalog.Problems) Log.Write("Catalogue : " + issue);

            // Catalogue en ligne (releases GitHub) : copie du dernier passage tout de suite, nouvelle lecture 3 s apres le demarrage
            var remote = new RemoteCatalogService(Distribution.CatalogUrl, Path.Combine(Paths.InstallRoot, "cache"));
            remote.LoadCache();
            Log.Write(remote.Enabled
                ? "Catalogue en ligne : " + remote.Url + (remote.LastSuccess != null ? " (copie du " + remote.LastSuccess.Value.ToString("dd/MM HH:mm") + ", " + remote.Catalog.Apps.Count + " appli(s))" : "")
                : "Catalogue en ligne désactivé (KKAO_CATALOG_URL=off)");

            _tools = new ToolManager(all, settings, _ui);
            _tools.Changed += tool => OnToolsChanged();
            _tools.ToolsChanged += OnToolsChanged;
            _tools.Notification += ShowNotification;

            _controller = new KkaoController(_tools, settings, catalog, installer, SelfInstaller.ForCurrentUser(), devMode, toolsPath, _ui, remote)
            {
                OpenWindowRequest = ShowWindow,
                QuitRequest = Quit,
                RelaunchRequest = Relaunch,
            };
            _controller.Message += (title, text, error) =>
            {
                // Fenetre fermee : la nouvelle passe par une bulle
                if (_window == null || !_window.IsVisible) ShowBalloon(title, text, error ? ToolTipIcon.Error : ToolTipIcon.Info, () => ShowWindow(null));
            };
            _controller.Changed += OnToolsChanged;

            ThemeManager.EnsureApplication();
            System.Windows.Application.Current.DispatcherUnhandledException += (s, e) =>
            {
                Log.Error("Erreur de l'interface", e.Exception);
                e.Handled = true;
            };
            ThemeManager.Apply(settings.Theme);
            _vm = new MainViewModel(_controller);
            _vm.Refresh();

            _builder = new MenuBuilder(_tools, _controller.Quick)
            {
                ProblemText = config == null || config.Tools.Count == 0 ? problem : null,
                OpenWindow = () => ShowWindow(null),
                OpenSettings = () => ShowWindow("settings"),
                Quit = Quit,
                RunQuick = (tool, action) => _controller.RunAction(tool.Id, action, "quick"),
                ImageOf = tool => AppImages.Small(tool, 16, _controller.Catalog.Find(tool.Id)?.IconPath),
            };
            _menu = new ContextMenuStrip();
            _menu.Opening += (s, e) =>
            {
                _builder.ShowToolSubmenus = settings.ShowToolSubmenus;
                _builder.Build(_menu);
                MenuRenderer.Apply(_menu, ThemeManager.IsDark);
                e.Cancel = false;
            };
            _menu.Opened += MenuRenderer.RoundCorners;
            _builder.Build(_menu);

            _icon = new NotifyIcon
            {
                ContextMenuStrip = _menu,
                Icon = IconFactory.Get(StatusState.Idle, _iconSize),
                Text = Branding.Name,
                Visible = true,
            };
            // Clic gauche : la fenetre ; clic droit : le menu
            _icon.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left) ShowWindow(null);
            };
            _icon.BalloonTipClicked += (s, e) =>
            {
                var action = _balloonAction;
                _balloonAction = null;
                action?.Invoke();
            };

            _appIcons = new AppTrayIcons(_tools, settings, _builder, tool => _controller.Catalog.Find(tool.Id)?.IconPath) { Dark = ThemeManager.IsDark };
            ThemeManager.Changed += () => _appIcons.Dark = ThemeManager.IsDark;

            // Nouvelle instance (raccourci, menu Demarrer) : la fenetre s'ouvre ; desinstallation / mise a jour : KKAO se ferme
            _waits.Add(Wait(showSignal, () => ShowWindow(null)));
            _waits.Add(Wait(quitSignal, Quit));

            RefreshIcon();
            if (problem != null) ShowBalloon(Branding.Name, problem, ToolTipIcon.Warning, OpenToolsFolder);
            else if (afterUpdate)
                ShowBalloon(Branding.Name + " " + Program.Version + " est installé", "Mise à jour terminée : vos réglages et vos applis sont conservés.", ToolTipIcon.Info, () => ShowWindow(null));
            _tools.StartAutostart();

            // Nouveautes en ligne : 3 s apres le demarrage, puis toutes les 6 h
            _updateTimer = new System.Windows.Forms.Timer { Interval = 3000 };
            _updateTimer.Tick += (s, e) =>
            {
                _updateTimer.Interval = 6 * 3600 * 1000;
                _controller.CheckForUpdates(userInitiated: false);
            };
            if (remote.Enabled) _updateTimer.Start();

            // Premier lancement : guide de demarrage. Demarrage avec Windows : discret (sauf reglage contraire).
            if (!settings.OnboardingDone)
            {
                ShowWindow(null);
                _vm.StartOnboarding(settings.OnboardingStep);
            }
            else if (!autostart || settings.OpenAtStartup)
            {
                ShowWindow(null);
            }
        }

        RegisteredWaitHandle Wait(EventWaitHandle signal, Action action) =>
            signal == null ? null : ThreadPool.RegisterWaitForSingleObject(signal, (state, timedOut) =>
            {
                try { _ui.Post(_ => action(), null); }
                catch (Exception) { /* KKAO en cours de fermeture */ }
            }, null, Timeout.Infinite, false);

        void OnToolsChanged()
        {
            RefreshIcon();
            _appIcons?.Sync();
        }

        void RefreshIcon()
        {
            if (_icon == null) return;
            var state = _tools.AggregateState();
            if (_shownState != state)
            {
                _icon.Icon = IconFactory.Get(state, _iconSize);
                _shownState = state;
            }
            // & disparait des infobulles de la zone de notification : esperluette pleine chasse
            var text = _tools.BuildTooltip().Replace("&", "＆");
            if (text != _shownText)
            {
                _icon.Text = text;
                _shownText = text;
            }
        }

        void ShowWindow(string page)
        {
            if (_tools.Quitting) return;
            try
            {
                if (_window == null) _window = new MainWindow(_vm, _controller.Settings);
                _window.ShowAndActivate(page);
            }
            catch (Exception ex)
            {
                Log.Error("Ouverture de la fenêtre", ex);
            }
        }

        void ShowNotification(ToolNotification notification)
        {
            var icon = notification.Kind == "error" ? ToolTipIcon.Error : notification.Kind == "warn" ? ToolTipIcon.Warning : ToolTipIcon.Info;
            // Appli avec son icone : la bulle part de son icone
            if (!notification.FromKkao && _appIcons.TryBalloon(notification.Tool, notification.Title, notification.Text, icon)) return;
            // Bulle de KKAO (outil arrete...) : un clic ouvre la page de l'appli
            var toolId = notification.Tool?.Id;
            ShowBalloon(notification.Title, notification.Text, icon, notification.FromKkao && toolId != null ? (Action)(() => ShowWindow("app:" + toolId)) : null);
        }

        void ShowBalloon(string title, string text, ToolTipIcon icon, Action onClick)
        {
            if (_icon == null || !_icon.Visible) return;
            try
            {
                _balloonAction = onClick;
                _icon.ShowBalloonTip(6000,
                    ToolManager.Fit(string.IsNullOrWhiteSpace(title) ? Branding.Name : title, 63),
                    ToolManager.Fit(string.IsNullOrWhiteSpace(text) ? " " : text, 255),
                    icon);
            }
            catch (Exception ex)
            {
                Log.Error("Notification", ex);
            }
        }

        void OpenToolsFolder()
        {
            var dir = Path.GetDirectoryName(_controller.ToolsFilePath ?? Paths.ExePath);
            _controller.OpenFolder(dir);
        }

        // Apres l'installation de KKAO : la copie installee prend le relais (le guide reprend la ou il en etait)
        void Relaunch(string exe, string args)
        {
            Program.PendingRelaunch = Tuple.Create(exe, args);
            Quit();
        }

        // Quitter KKAO : quit a toutes les applis, la boucle de messages continue jusqu'a 10 s, puis fin
        void Quit()
        {
            if (_tools.Quitting) return;
            _tools.QuitAll(() =>
            {
                _icon.Visible = false;
                _appIcons.Dispose();
                if (_window != null)
                {
                    _window.AllowClose = true;
                    _window.Close();
                }
                ExitThread();
            });
            RefreshIcon();
            _appIcons.Sync();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _updateTimer?.Dispose();
                foreach (var wait in _waits) wait?.Unregister(null);
                if (_icon != null)
                {
                    _icon.Visible = false;
                    _icon.Dispose();
                }
                _appIcons?.Dispose();
                _menu?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
