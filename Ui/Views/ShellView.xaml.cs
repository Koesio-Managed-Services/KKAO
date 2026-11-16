using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace KKAO.Ui
{
    public partial class ShellView : UserControl
    {
        readonly Dictionary<string, FrameworkElement> _pages = new Dictionary<string, FrameworkElement>();
        MainViewModel _vm;
        DispatcherTimer _toastTimer;
        List<CoachStep> _tour;
        int _tourIndex;

        public ShellView()
        {
            InitializeComponent();
            DataContextChanged += (s, e) => Attach(e.NewValue as MainViewModel);
            PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape && CoachLayer.Visibility == Visibility.Visible)
                {
                    EndTour();
                    e.Handled = true;
                }
            };
        }

        void Attach(MainViewModel vm)
        {
            if (_vm != null)
            {
                _vm.PageChanged -= OnPageChanged;
                _vm.PropertyChanged -= OnVmChanged;
                _vm.ToastRequested -= OnToast;
                _vm.TourRequested -= OnTour;
                _vm.ConfettiRequested -= Burst;
            }
            _vm = vm;
            if (vm == null) return;
            vm.PageChanged += OnPageChanged;
            vm.PropertyChanged += OnVmChanged;
            vm.ToastRequested += OnToast;
            vm.TourRequested += OnTour;
            vm.ConfettiRequested += Burst;
            OnPageChanged();
            UpdateOnboarding();
        }

        void OnVmChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainViewModel.Onboarding)) UpdateOnboarding();
        }

        void OnToast(string title, string text, string glyph, string kind) => ShowToast(title, text, glyph, kind);

        void OnTour() => Dispatcher.BeginInvoke(new Action(StartTour), DispatcherPriority.Loaded);

        #region Pages

        void OnPageChanged() => ShowPage(_vm?.CurrentPage ?? "home");

        internal void ShowPage(string page)
        {
            if (!_pages.TryGetValue(page, out var view))
            {
                switch (page)
                {
                    case "apps": view = new AppsPage(); break;
                    case "app": view = new AppDetailPage(); break;
                    case "quick": view = new QuickPage(); break;
                    case "quests": view = new QuestsPage(); break;
                    case "settings": view = new SettingsPage(); break;
                    default: view = new HomePage(); break;
                }
                _pages[page] = view;
            }
            if (page == "app" && view is AppDetailPage detail) detail.ScrollToTop();
            PageHost.Content = view;
            if (ThemeManager.Motion)
            {
                view.Opacity = 0;
                view.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
            }
        }

        void UpdateOnboarding()
        {
            var onboarding = _vm?.Onboarding;
            if (onboarding == null)
            {
                OnboardingHost.Content = null;
                return;
            }
            var view = OnboardingHost.Content as OnboardingView ?? new OnboardingView();
            view.DataContext = onboarding;
            OnboardingHost.Content = view;
        }

        #endregion

        #region Notification dans la fenetre

        internal void ShowToast(string title, string text, string glyph, string kind)
        {
            ToastTitle.Text = title;
            ToastText.Text = text ?? "";
            ToastText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
            ToastGlyph.Text = glyph ?? "";
            ToastBadge.Background = kind == "error" ? (Brush)FindResource("Coral") : (Brush)FindResource("HeroBrush");
            Toast.Visibility = Visibility.Visible;
            if (ThemeManager.Motion)
            {
                ToastShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(420, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                Toast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)));
            }
            if (_toastTimer == null)
            {
                _toastTimer = new DispatcherTimer();
                _toastTimer.Tick += (s, e) =>
                {
                    _toastTimer.Stop();
                    HideToast();
                };
            }
            _toastTimer.Stop();
            _toastTimer.Interval = TimeSpan.FromSeconds(kind == "error" ? 9 : 5);
            if (ThemeManager.Motion) _toastTimer.Start();
        }

        void HideToast()
        {
            if (!ThemeManager.Motion)
            {
                Toast.Visibility = Visibility.Collapsed;
                return;
            }
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220));
            fade.Completed += (s, e) => Toast.Visibility = Visibility.Collapsed;
            Toast.BeginAnimation(OpacityProperty, fade);
        }

        void CloseToast(object sender, RoutedEventArgs e)
        {
            _toastTimer?.Stop();
            HideToast();
        }

        #endregion

        #region Visite guidee

        sealed class CoachStep
        {
            public FrameworkElement Target;
            public string Title;
            public string Text;
            public string Marker;
        }

        internal void StartTour()
        {
            _tour = new List<CoachStep>
            {
                new CoachStep { Target = NavApps, Title = "Vos applis", Text = "Installez, démarrez et réglez vos applis Koesio ici. Un clic suffit.", Marker = "ici !" },
                new CoachStep { Target = NavQuick, Title = "Actions rapides", Text = "Composez le menu du clic droit sur l'icône " + Branding.Name + " : vos actions préférées, toujours à portée.", Marker = "pratique !" },
                new CoachStep { Target = NavQuests, Title = "Premiers pas", Text = "Six missions pour tout découvrir… et des succès à débloquer.", Marker = "à vous de jouer" },
                new CoachStep { Target = NavSettings, Title = "Réglages", Text = "Thème sombre ou clair, démarrage avec Windows, icônes : tout se règle ici.", Marker = null },
                new CoachStep { Target = null, Title = "Votre icône vous attend", Text = "Le « K » violet vit près de l'horloge. Clic gauche : cette fenêtre. Clic droit : vos actions rapides. Bonne route !", Marker = null },
            };
            _tourIndex = 0;
            ShowCoach();
        }

        internal void ShowCoachStep(int index)
        {
            if (_tour == null) StartTour();
            _tourIndex = Math.Max(0, Math.Min(index, _tour.Count - 1));
            ShowCoach();
        }

        void ShowCoach()
        {
            CoachLayer.Children.Clear();
            if (_tour == null || _tourIndex >= _tour.Count)
            {
                EndTour();
                return;
            }
            CoachLayer.Visibility = Visibility.Visible;
            UpdateLayout();
            var step = _tour[_tourIndex];
            var size = new Size(Math.Max(1, Root.ActualWidth), Math.Max(1, Root.ActualHeight));
            var full = new RectangleGeometry(new Rect(size));
            Rect hole = Rect.Empty;
            if (step.Target != null && step.Target.IsVisible)
            {
                hole = step.Target.TransformToAncestor(Root).TransformBounds(new Rect(step.Target.RenderSize));
                hole.Inflate(6, 6);
            }
            Geometry shade = hole.IsEmpty ? (Geometry)full : new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(hole, 12, 12));
            CoachLayer.Children.Add(new Path { Data = shade, Fill = (Brush)FindResource("Overlay") });
            if (!hole.IsEmpty)
            {
                var ring = new Rectangle { Width = hole.Width, Height = hole.Height, RadiusX = 12, RadiusY = 12, Stroke = (Brush)FindResource("Coral"), StrokeThickness = 2.5 };
                Canvas.SetLeft(ring, hole.Left);
                Canvas.SetTop(ring, hole.Top);
                CoachLayer.Children.Add(ring);
            }

            var bubble = BuildBubble(step);
            bubble.Measure(new Size(360, double.PositiveInfinity));
            double left, top;
            if (hole.IsEmpty)
            {
                left = (size.Width - bubble.DesiredSize.Width) / 2;
                top = (size.Height - bubble.DesiredSize.Height) / 2;
            }
            else
            {
                left = hole.Right + 70;
                top = Math.Max(16, Math.Min(hole.Top - 20, size.Height - bubble.DesiredSize.Height - 16));
                if (step.Marker != null)
                {
                    var marker = new TextBlock { Text = "← " + step.Marker, Style = (Style)FindResource("Marker"), RenderTransform = new RotateTransform(-6) };
                    Canvas.SetLeft(marker, hole.Right + 12);
                    Canvas.SetTop(marker, hole.Top + hole.Height / 2 - 34);
                    CoachLayer.Children.Add(marker);
                    top = Math.Max(top, hole.Top + 8);
                }
            }
            Canvas.SetLeft(bubble, Math.Max(16, left));
            Canvas.SetTop(bubble, Math.Max(16, top));
            CoachLayer.Children.Add(bubble);
            if (ThemeManager.Motion) bubble.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        }

        Border BuildBubble(CoachStep step)
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = (_tourIndex + 1) + " / " + _tour.Count, Style = (Style)FindResource("Caption"), Margin = new Thickness(0, 0, 0, 4) });
            panel.Children.Add(new TextBlock { Text = step.Title, Style = (Style)FindResource("H2"), Margin = new Thickness(0, 0, 0, 6) });
            panel.Children.Add(new TextBlock { Text = step.Text, Style = (Style)FindResource("Muted") });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
            var last = _tourIndex == _tour.Count - 1;
            if (!last)
            {
                var skip = new Button { Content = "Passer", Style = (Style)FindResource("BtnGhost"), Margin = new Thickness(0, 0, 8, 0) };
                skip.Click += (s, e) => EndTour();
                buttons.Children.Add(skip);
            }
            var next = new Button { Content = last ? "C'est parti !" : "Suivant", Style = (Style)FindResource("BtnPrimary") };
            next.Click += (s, e) =>
            {
                _tourIndex++;
                ShowCoach();
            };
            buttons.Children.Add(next);
            panel.Children.Add(buttons);
            return new Border
            {
                Style = (Style)FindResource("Card"),
                Width = 360,
                Padding = new Thickness(22, 18, 22, 18),
                Child = panel,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 30, ShadowDepth = 6, Opacity = 0.4 },
            };
        }

        void EndTour()
        {
            CoachLayer.Children.Clear();
            CoachLayer.Visibility = Visibility.Collapsed;
            _tour = null;
            _vm?.TourFinished();
        }

        #endregion

        #region Confettis

        internal void Burst()
        {
            if (!ThemeManager.Motion || ActualWidth < 10) return;
            var random = new Random();
            var colors = new[] { "#6E398E", "#E94B57", "#41C0F0", "#FAF9F5", "#F5B301", "#C9A3E6" }.Select(c => (Brush)new BrushConverter().ConvertFromString(c)).ToArray();
            var pieces = new List<Piece>();
            for (var i = 0; i < 160; i++)
            {
                var rect = new Rectangle { Width = random.Next(6, 11), Height = random.Next(9, 16), Fill = colors[random.Next(colors.Length)], RadiusX = 2, RadiusY = 2, RenderTransformOrigin = new Point(0.5, 0.5) };
                var piece = new Piece
                {
                    Shape = rect,
                    X = ActualWidth / 2 + random.Next(-120, 120),
                    Y = ActualHeight * 0.35,
                    Vx = (random.NextDouble() - 0.5) * 18,
                    Vy = -6 - random.NextDouble() * 12,
                    Spin = (random.NextDouble() - 0.5) * 20,
                    Rotation = random.Next(0, 360),
                };
                rect.RenderTransform = new RotateTransform(piece.Rotation);
                ConfettiLayer.Children.Add(rect);
                pieces.Add(piece);
            }
            var started = DateTime.UtcNow;
            EventHandler tick = null;
            tick = (s, e) =>
            {
                var elapsed = (DateTime.UtcNow - started).TotalSeconds;
                foreach (var p in pieces)
                {
                    p.Vy += 0.42;
                    p.Vx *= 0.99;
                    p.X += p.Vx;
                    p.Y += p.Vy;
                    p.Rotation += p.Spin;
                    Canvas.SetLeft(p.Shape, p.X);
                    Canvas.SetTop(p.Shape, p.Y);
                    ((RotateTransform)p.Shape.RenderTransform).Angle = p.Rotation;
                    if (elapsed > 2.2) p.Shape.Opacity = Math.Max(0, 1 - (elapsed - 2.2));
                }
                if (elapsed > 3.4)
                {
                    CompositionTarget.Rendering -= tick;
                    foreach (var p in pieces) ConfettiLayer.Children.Remove(p.Shape);
                }
            };
            CompositionTarget.Rendering += tick;
        }

        sealed class Piece
        {
            public Rectangle Shape;
            public double X, Y, Vx, Vy, Spin, Rotation;
        }

        #endregion
    }
}
