using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media.Imaging;

namespace KKAO.Ui
{
    // La fenetre de KKAO : fermee = cachee (KKAO reste pres de l'horloge), position et taille retenues
    internal sealed class MainWindow : Window
    {
        readonly MainViewModel _vm;
        readonly KkaoSettings _settings;

        public MainWindow(MainViewModel vm, KkaoSettings settings)
        {
            _vm = vm;
            _settings = settings;
            Title = Branding.Name + " — " + Branding.LongName;
            Width = 1180;
            Height = 760;
            MinWidth = 960;
            MinHeight = 620;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            SetResourceReference(BackgroundProperty, "Bg");
            Content = new ShellView { DataContext = vm };
            try
            {
                Icon = BitmapFrame.Create(new Uri("pack://application:,,,/assets/kkao.ico"));
            }
            catch (Exception)
            {
                // icone par defaut
            }
            LoadPlacement();
            SourceInitialized += (s, e) => ThemeManager.ApplyTitleBar(this);
            Closing += OnClosing;
            // Clavier des fenetres WPF ouvertes depuis la boucle de messages WinForms (Tab, raccourcis, saisie)
            System.Windows.Forms.Integration.ElementHost.EnableModelessKeyboardInterop(this);
        }

        public bool AllowClose { get; set; }

        public ShellView Shell => Content as ShellView;

        void LoadPlacement()
        {
            var bounds = _settings.WindowBounds;
            if (bounds == null || double.IsNaN(bounds[0]) || double.IsNaN(bounds[2])) return;
            var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
            var rect = new Rect(bounds[0], bounds[1], Math.Max(MinWidth, bounds[2]), Math.Max(MinHeight, bounds[3]));
            // Fenetre retrouvee sur un ecran encore branche, sinon centree
            if (!screen.IntersectsWith(new Rect(rect.Left + 40, rect.Top + 10, 200, 60))) return;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = rect.Left;
            Top = rect.Top;
            Width = rect.Width;
            Height = rect.Height;
            if (_settings.WindowMaximized) WindowState = WindowState.Maximized;
        }

        void OnClosing(object sender, CancelEventArgs e)
        {
            var r = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
            if (!r.IsEmpty) _settings.SetWindow(r.Left, r.Top, r.Width, r.Height, WindowState == WindowState.Maximized);
            if (AllowClose) return;
            // KKAO reste pres de l'horloge : la fenetre est seulement cachee
            e.Cancel = true;
            Hide();
        }

        public void ShowAndActivate(string page)
        {
            if (!string.IsNullOrEmpty(page)) _vm.Navigate(page);
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            // Premier plan meme si une autre fenetre l'a (clic sur l'icone, nouvelle instance)
            Topmost = true;
            Topmost = false;
            Focus();
        }
    }
}
