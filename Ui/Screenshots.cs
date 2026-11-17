using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace KKAO.Ui
{
    // KKAO.exe --screenshots <dossier> : chaque page et chaque etape du guide, en sombre et en clair, dessinees hors ecran
    // avec des donnees de demonstration. Aucun outil lance, aucun reglage ecrit, aucune fenetre visible.
    internal static class Screenshots
    {
        public const int Width = 1180;
        public const int Height = 760;

        public static int Run(string dir)
        {
            dir = Path.GetFullPath(string.IsNullOrWhiteSpace(dir) ? "kkao-ecrans" : dir);
            Directory.CreateDirectory(dir);
            Log.FilePath = Path.Combine(dir, "ecrans.log");
            var count = 0;
            try
            {
                foreach (var file in Render(dir)) count++;
                Log.Write(count + " rendus dans " + dir);
                Log.Flush();
                return 0;
            }
            catch (Exception ex)
            {
                Log.Write("Rendu impossible : " + ex);
                Log.Flush();
                return 1;
            }
        }

        // dir == null : rendu en memoire seulement (test automatique)
        public static IEnumerable<string> Render(string dir)
        {
            ThemeManager.Motion = false;
            ThemeManager.EnsureApplication();
            var results = new List<string>();
            foreach (var theme in new[] { "dark", "light" })
            {
                ThemeManager.Apply(theme);

                // Applis installees : accueil, catalogue, fiche, actions rapides, missions, reglages, visite guidee
                var vm = Demo(installed: true);
                vm.SetDemoTheme(theme);
                vm.Refresh();
                var shell = new ShellView { DataContext = vm };
                var window = Host(shell);
                try
                {
                    foreach (var page in new[] { "home", "apps", "app", "quick", "quests", "settings" })
                    {
                        if (page == "app") vm.Navigate("app:instruktion");
                        else vm.Navigate(page);
                        results.Add(Save(shell, dir, theme + "-" + page));
                    }
                    // Bas des reglages (mises a jour, guide, depannage, a propos)
                    var scroller = Descendants<ScrollViewer>(shell).OrderByDescending(s => s.ExtentHeight).FirstOrDefault();
                    scroller?.ScrollToEnd();
                    results.Add(Save(shell, dir, theme + "-settings-bas"));
                    scroller?.ScrollToHome();
                    vm.Navigate("home");
                    shell.ShowToast("Succès débloqué !", Quests.AchievementTitle(Quests.InstallApp), "\uE735", "achievement");
                    results.Add(Save(shell, dir, theme + "-succes"));
                    shell.ShowCoachStep(0);
                    results.Add(Save(shell, dir, theme + "-visite"));
                }
                finally
                {
                    window.Close();
                }

                // Premier lancement : rien d'installe, guide de demarrage
                var fresh = Demo(installed: false);
                fresh.SetDemoTheme(theme);
                fresh.Refresh();
                var freshShell = new ShellView { DataContext = fresh };
                var freshWindow = Host(freshShell);
                try
                {
                    fresh.Navigate("apps");
                    results.Add(Save(freshShell, dir, theme + "-apps-neuf"));
                    fresh.Navigate("home");
                    results.Add(Save(freshShell, dir, theme + "-home-neuf"));
                    fresh.StartOnboarding(null);
                    foreach (var step in new[] { "welcome", "install", "theme", "apps", "tray", "quick", "done" })
                    {
                        fresh.Onboarding.JumpTo(step);
                        if (step == "install") fresh.Onboarding.SetDemoState(false, 64, "Copie de " + Branding.Name + "…");
                        results.Add(Save(freshShell, dir, theme + "-guide-" + step));
                    }
                }
                finally
                {
                    freshWindow.Close();
                }

                results.Add(RenderTrayMenu(dir, theme));
            }
            return results;
        }

        // Menu du clic droit sur l'icone (WinForms, theme de KKAO), dessine sans etre affiche
        static string RenderTrayMenu(string dir, string theme)
        {
            var root = ToolsRoot();
            var settings = KkaoSettings.InMemory();
            var tools = new List<ToolConfig>
            {
                new ToolConfig { Id = "instruktion", Name = "InstruKtion", ConfigName = "InstruKtion", Exe = "demo.exe", Protocol = true, Autostart = true, IconPath = root == null ? null : Path.Combine(root, @"Procedurier\assets\icon.png") },
                new ToolConfig { Id = "kalepin", Name = "Kalepin", ConfigName = "Kalepin", Exe = "demo.exe", Protocol = true, Autostart = true, IconPath = root == null ? null : Path.Combine(root, @"Kalepin\assets\icon.png") },
            };
            settings.SetCachedActions("kalepin", new[]
            {
                Action("quickNote", "Nouvelle note…", "Ctrl+Alt+F1", true),
                Action("newPostIt", "Nouveau post-it", null, false),
            });
            settings.SetCachedActions("instruktion", new[]
            {
                Action("newProcedure", "Nouvelle procédure", "Ctrl+Alt+F4", false),
                Action("pause", "Pause", "F5", false),
                Action("finish", "Terminer la procédure", "F8", false),
            });
            settings.SetQuickMenu(new[]
            {
                new QuickEntry { ToolId = "kalepin", ActionId = "quickNote", Label = "Quicknote" },
                new QuickEntry { ToolId = "instruktion", ActionId = "newProcedure" },
                new QuickEntry { ToolId = "kalepin", ActionId = "newPostIt", Label = "Post-it" },
                QuickEntry.Separator(),
                new QuickEntry { ToolId = "instruktion", ActionId = "pause" },
            });
            var manager = new ToolManager(tools, settings, System.Threading.SynchronizationContext.Current);
            var builder = new MenuBuilder(manager, new QuickMenuService(manager, settings)) { ImageOf = tool => AppImages.Small(tool, 16) };
            using (var menu = new System.Windows.Forms.ContextMenuStrip())
            {
                builder.Build(menu);
                MenuRenderer.Apply(menu, theme == "dark");
                var size = menu.GetPreferredSize(System.Drawing.Size.Empty);
                menu.Size = size;
                using (var bitmap = new System.Drawing.Bitmap(size.Width, size.Height))
                {
                    menu.DrawToBitmap(bitmap, new System.Drawing.Rectangle(System.Drawing.Point.Empty, size));
                    if (dir == null) return theme + "-menu";
                    var path = Path.Combine(dir, theme + "-menu.png");
                    bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                    return path;
                }
            }
        }

        static Dictionary<string, object> Action(string id, string label, string shortcut, bool isDefault)
        {
            var action = new Dictionary<string, object> { { "id", id }, { "label", label } };
            if (shortcut != null) action["shortcut"] = shortcut;
            if (isDefault) action["default"] = true;
            return action;
        }

        // Fenetre hors ecran (jamais visible, sans bouton dans la barre des taches) : la mise en page est celle d'une vraie fenetre
        static Window Host(FrameworkElement content)
        {
            var window = new Window
            {
                Content = content,
                Width = Width,
                Height = Height,
                Left = -32000,
                Top = -32000,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                ShowActivated = false,
                Background = Brushes.Transparent,
            };
            window.Show();
            Settle();
            return window;
        }

        static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
        {
            var count = VisualTreeHelper.GetChildrenCount(root);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T match) yield return match;
                foreach (var nested in Descendants<T>(child)) yield return nested;
            }
        }

        static void Settle()
        {
            for (var i = 0; i < 3; i++)
                Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
        }

        static string Save(FrameworkElement element, string dir, string name)
        {
            Settle();
            element.UpdateLayout();
            var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(element);
            if (dir == null) return name;
            var path = Path.Combine(dir, name + ".png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(path)) encoder.Save(stream);
            return path;
        }

        // Dossier des projets (C:\projects\4-OUTILS) : pour reprendre les vraies icones des applis dans les rendus
        static string ToolsRoot()
        {
            var dir = Paths.ExeDir;
            for (var i = 0; i < 6 && dir != null; i++)
            {
                if (File.Exists(Path.Combine(dir, "KKAO.csproj"))) return Path.GetDirectoryName(dir);
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }

        public static MainViewModel Demo(bool installed)
        {
            var root = ToolsRoot();
            string Icon(string relative) => root == null ? null : Path.Combine(root, relative);
            var vm = new MainViewModel(null);
            vm.SetDemoInstalled(installed);

            var instruktion = new AppInfo
            {
                Id = "instruktion",
                Name = "InstruKtion",
                Summary = "Vos procédures en un temps record",
                Description = "Captures annotées, vidéo, chapitres et export HTML ou IT Glue en un clic, sans quitter votre écran.",
                Publisher = "Koesio",
                Version = "0.2.0",
                AvailableVersion = "0.2.0",
                IconPath = Icon(@"Procedurier\assets\icon.png"),
                InCatalog = true,
                HasPackage = true,
                Installed = installed,
                Run = installed ? AppRunState.Running : AppRunState.NotInstalled,
                Status = StatusState.Active,
                StatusTooltip = installed ? "Procédure en cours : Départ d'un collaborateur — 12 étapes" : null,
                Autostart = true,
                Size = 318L << 20,
                Location = @"C:\Users\…\AppData\Local\Programs\KKAO\apps\instruktion",
                HasActionsCatalog = installed,
                HasOwnSettingsAction = true,
                HasOpenAction = installed,
                HasSettingsSchema = installed,
            };
            instruktion.Highlights.AddRange(new[] { "Capture en un raccourci", "Annotations et floutage", "Export IT Glue" });
            if (installed)
            {
                instruktion.Actions.AddRange(new[]
                {
                    new AppActionInfo { Id = "newProcedure", Label = "Nouvelle procédure", Shortcut = "Ctrl+Alt+F4", InQuickMenu = true },
                    new AppActionInfo { Id = "capture", Label = "Capturer la zone", Shortcut = "F12" },
                    new AppActionInfo { Id = "pause", Label = "Pause", Shortcut = "F5", InQuickMenu = true },
                    new AppActionInfo { Id = "finish", Label = "Terminer la procédure", Shortcut = "F8", InQuickMenu = true },
                    new AppActionInfo { Id = "copyItGlue", Label = "Copier pour IT Glue" },
                    new AppActionInfo { Id = "viewer", Label = "Ouvrir la procédure", IsDefault = true },
                });
                instruktion.Settings = DemoSettings();
                instruktion.SettingsRevision = 1;
            }

            var kalepin = new AppInfo
            {
                Id = "kalepin",
                Name = "Kalepin",
                Summary = "Vos idées notées en deux secondes",
                Description = "Une note rapide part dans votre Paper Dropbox, rangée sous la date du jour. Et des post-it sur le bureau pour ne rien oublier.",
                Publisher = "Koesio",
                Version = "2.1.0",
                AvailableVersion = "2.1.0",
                IconPath = Icon(@"Kalepin\assets\icon.png"),
                InCatalog = true,
                HasPackage = true,
                Installed = installed,
                Run = installed ? AppRunState.Running : AppRunState.NotInstalled,
                Status = StatusState.Warn,
                StatusTooltip = installed ? "Kalepin — 2 notes en attente d'envoi" : null,
                Autostart = true,
                Size = 1L << 20,
                HasActionsCatalog = installed,
                HasOwnSettingsAction = true,
                HasOpenAction = installed,
            };
            kalepin.Highlights.AddRange(new[] { "Note au raccourci", "Classée par jour", "Post-it sur le bureau" });
            if (installed)
                kalepin.Actions.AddRange(new[]
                {
                    new AppActionInfo { Id = "quickNote", Label = "Nouvelle note…", Shortcut = "Ctrl+Alt+F1", IsDefault = true, InQuickMenu = true },
                    new AppActionInfo { Id = "newPostIt", Label = "Nouveau post-it", InQuickMenu = true },
                    new AppActionInfo { Id = "showPostIts", Label = "Afficher les post-it", Shortcut = "Ctrl+Alt+M" },
                    new AppActionInfo { Id = "openPaper", Label = "Ouvrir le Paper" },
                });
            vm.DemoApps = new List<AppInfo> { instruktion, kalepin };

            if (installed)
            {
                vm.DemoQuick = new List<QuickInfo>
                {
                    Quick("kalepin", "Kalepin", "quickNote", "Quicknote", "Nouvelle note…", "Ctrl+Alt+F1"),
                    Quick("instruktion", "InstruKtion", "newProcedure", null, "Nouvelle procédure", "Ctrl+Alt+F4"),
                    Quick("kalepin", "Kalepin", "newPostIt", "Post-it", "Nouveau post-it", null),
                    new QuickInfo { Entry = QuickEntry.Separator() },
                    Quick("instruktion", "InstruKtion", "pause", null, "Pause", "F5"),
                    Quick("instruktion", "InstruKtion", "finish", null, "Terminer la procédure", "F8"),
                };
                vm.DemoQuestsDone = new HashSet<string> { Quests.InstallKkao, Quests.InstallApp, Quests.ChooseTheme, Quests.QuickAction };
                var group1 = new QuickCatalogGroup { AppName = "InstruKtion", AppIcon = UiImages.FromFile(instruktion.IconPath) ?? UiImages.LetterTile("InstruKtion") };
                foreach (var a in instruktion.Actions) group1.Items.Add(new QuickCatalogItem { Label = a.Label, Shortcut = a.Shortcut, AlreadyIn = a.InQuickMenu, AddCommand = new RelayCommand(() => { }) });
                var group2 = new QuickCatalogGroup { AppName = "Kalepin", AppIcon = UiImages.FromFile(kalepin.IconPath) ?? UiImages.LetterTile("Kalepin") };
                foreach (var a in kalepin.Actions) group2.Items.Add(new QuickCatalogItem { Label = a.Label, Shortcut = a.Shortcut, AlreadyIn = a.InQuickMenu, AddCommand = new RelayCommand(() => { }) });
                vm.DemoCatalog = new List<QuickCatalogGroup> { group1, group2 };
            }
            return vm;
        }

        static QuickInfo Quick(string appId, string appName, string actionId, string label, string defaultLabel, string shortcut) => new QuickInfo
        {
            Entry = new QuickEntry { ToolId = appId, ActionId = actionId, Label = label, KnownLabel = defaultLabel },
            Label = label ?? defaultLabel,
            DefaultLabel = defaultLabel,
            AppId = appId,
            AppName = appName,
            Shortcut = shortcut,
        };

        static List<SettingsSection> DemoSettings()
        {
            var json = "{\"sections\":["
                + "{\"title\":\"Raccourcis\",\"items\":["
                + "{\"id\":\"hotkey.newProcedure\",\"label\":\"Nouvelle procédure\",\"type\":\"hotkey\",\"value\":\"Ctrl+Alt+F4\"},"
                + "{\"id\":\"hotkey.capture\",\"label\":\"Capturer la zone\",\"type\":\"hotkey\",\"value\":\"F12\",\"description\":\"Actif seulement pendant une procédure\"},"
                + "{\"id\":\"hotkey.copyItGlue\",\"label\":\"Copier pour IT Glue\",\"type\":\"hotkey\",\"value\":\"\"}]},"
                + "{\"title\":\"Procédures\",\"items\":["
                + "{\"id\":\"proceduresDir\",\"label\":\"Dossier des procédures\",\"type\":\"folder\",\"value\":\"C:\\\\Users\\\\camille.martin\\\\Documents\\\\InstruKtion\"},"
                + "{\"id\":\"author\",\"label\":\"Auteur\",\"type\":\"text\",\"value\":\"Camille Martin\"},"
                + "{\"id\":\"stepNumbering\",\"label\":\"Numérotation des étapes\",\"type\":\"choice\",\"value\":\"global\",\"options\":[{\"value\":\"global\",\"label\":\"Continue\"},{\"value\":\"perChapter\",\"label\":\"Par chapitre\"}]}]},"
                + "{\"title\":\"Export\",\"items\":["
                + "{\"id\":\"export.format.html\",\"label\":\"HTML\",\"type\":\"toggle\",\"value\":true},"
                + "{\"id\":\"export.format.itglue\",\"label\":\"IT Glue\",\"type\":\"toggle\",\"value\":true},"
                + "{\"id\":\"export.format.md\",\"label\":\"Markdown\",\"type\":\"toggle\",\"value\":false}]},"
                + "{\"title\":\"Vidéo\",\"items\":["
                + "{\"id\":\"video.fps\",\"label\":\"Images par seconde\",\"type\":\"choice\",\"value\":\"30\",\"options\":[{\"value\":\"15\",\"label\":\"15\"},{\"value\":\"24\",\"label\":\"24\"},{\"value\":\"30\",\"label\":\"30\"},{\"value\":\"60\",\"label\":\"60\"}]},"
                + "{\"id\":\"video.maxMinutes\",\"label\":\"Durée maximale (minutes)\",\"type\":\"number\",\"value\":20,\"min\":1,\"max\":240},"
                + "{\"id\":\"openSettings\",\"label\":\"Tous les réglages…\",\"type\":\"button\",\"action\":\"settings\",\"description\":\"Zones, sons, apparence de l'export…\"}]}"
                + "]}";
            return SettingsSection.ParseList(Json.Get(Json.ParseObject(json), "sections"));
        }
    }
}
