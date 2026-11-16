using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KKAO.Ui
{
    // Proprietes attachees des styles : couleur au survol, glyphe, coins
    public static class Btn
    {
        public static readonly DependencyProperty HoverProperty = DependencyProperty.RegisterAttached("Hover", typeof(Brush), typeof(Btn), new PropertyMetadata(null));
        public static Brush GetHover(DependencyObject d) => (Brush)d.GetValue(HoverProperty);
        public static void SetHover(DependencyObject d, Brush value) => d.SetValue(HoverProperty, value);

        public static readonly DependencyProperty GlyphProperty = DependencyProperty.RegisterAttached("Glyph", typeof(string), typeof(Btn), new PropertyMetadata(null));
        public static string GetGlyph(DependencyObject d) => (string)d.GetValue(GlyphProperty);
        public static void SetGlyph(DependencyObject d, string value) => d.SetValue(GlyphProperty, value);

        public static readonly DependencyProperty RadiusProperty = DependencyProperty.RegisterAttached("Radius", typeof(CornerRadius), typeof(Btn), new PropertyMetadata(new CornerRadius(10)));
        public static CornerRadius GetRadius(DependencyObject d) => (CornerRadius)d.GetValue(RadiusProperty);
        public static void SetRadius(DependencyObject d, CornerRadius value) => d.SetValue(RadiusProperty, value);

        // Texte secondaire (badge d'un element de navigation, aide d'une tuile)
        public static readonly DependencyProperty BadgeProperty = DependencyProperty.RegisterAttached("Badge", typeof(string), typeof(Btn), new PropertyMetadata(null));
        public static string GetBadge(DependencyObject d) => (string)d.GetValue(BadgeProperty);
        public static void SetBadge(DependencyObject d, string value) => d.SetValue(BadgeProperty, value);
    }

    internal abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            Raise(name);
            return true;
        }

        protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        protected void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    internal sealed class RelayCommand : ICommand
    {
        readonly Action<object> _run;
        readonly Func<object, bool> _can;

        public RelayCommand(Action<object> run, Func<object, bool> can = null)
        {
            _run = run;
            _can = can;
        }

        public RelayCommand(Action run, Func<bool> can = null) : this(_ => run(), can == null ? (Func<object, bool>)null : _ => can()) { }

        public event EventHandler CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }

        public bool CanExecute(object parameter) => _can == null || _can(parameter);

        public void Execute(object parameter)
        {
            try
            {
                _run(parameter);
            }
            catch (Exception ex)
            {
                Log.Error("Action de l'interface", ex);
            }
        }
    }

    // Visible si vrai (ConverterParameter=invert : l'inverse)
    public sealed class BoolToVisibility : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var on = value is bool b && b;
            if (parameter as string == "invert") on = !on;
            return on ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    // Visible si non nul et non vide (ConverterParameter=invert : l'inverse)
    public sealed class HasValueToVisibility : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var has = value != null && !(value is string s && s.Length == 0) && !(value is int i && i == 0);
            if (parameter as string == "invert") has = !has;
            return has ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }

    // Boutons radio lies a une propriete texte : coche si valeur == parametre
    public sealed class EqualsToBool : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            string.Equals(value as string, parameter as string, StringComparison.OrdinalIgnoreCase);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            value is bool b && b ? parameter : Binding.DoNothing;
    }

    public sealed class InverseBool : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => !(value is bool b && b);
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => !(value is bool b && b);
    }

    // Images de l'interface : fichiers, octets PNG, dessins System.Drawing (jamais de fichier verrouille)
    internal static class UiImages
    {
        static readonly Dictionary<string, ImageSource> Cache = new Dictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        public static ImageSource FromFile(string path, int decodeWidth = 128)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            var key = "file:" + path + ":" + decodeWidth + ":" + File.GetLastWriteTimeUtc(path).Ticks;
            if (Cache.TryGetValue(key, out var cached)) return cached;
            try
            {
                ImageSource image;
                if (path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
                {
                    var decoder = new IconBitmapDecoder(new Uri(path), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    BitmapFrame best = null;
                    foreach (var frame in decoder.Frames)
                        if (best == null || frame.PixelWidth > best.PixelWidth) best = frame;
                    image = best;
                }
                else
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                    bitmap.DecodePixelWidth = decodeWidth;
                    bitmap.UriSource = new Uri(path);
                    bitmap.EndInit();
                    image = bitmap;
                }
                image?.Freeze();
                Cache[key] = image;
                return image;
            }
            catch (Exception ex)
            {
                Log.Write("Image illisible " + path + " : " + ex.Message);
                return null;
            }
        }

        public static ImageSource FromBytes(byte[] png, string key)
        {
            if (png == null || png.Length == 0) return null;
            key = "bytes:" + key + ":" + png.Length;
            if (Cache.TryGetValue(key, out var cached)) return cached;
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = new MemoryStream(png);
                bitmap.EndInit();
                bitmap.Freeze();
                Cache[key] = bitmap;
                return bitmap;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static ImageSource FromDrawing(System.Drawing.Bitmap drawing, string key)
        {
            if (key != null && Cache.TryGetValue("draw:" + key, out var cached)) return cached;
            using (drawing)
            using (var memory = new MemoryStream())
            {
                drawing.Save(memory, System.Drawing.Imaging.ImageFormat.Png);
                var image = FromBytes(memory.ToArray(), "drawing-" + (key ?? Guid.NewGuid().ToString()));
                if (key != null && image != null) Cache["draw:" + key] = image;
                return image;
            }
        }

        public static ImageSource KkaoLogo(int size = 256) => FromDrawing(IconFactory.Render(StatusState.Idle, size), "kkao-logo-" + size);

        public static ImageSource LetterTile(string name) => FromDrawing(IconFactory.LetterTile(name, 128), "tile-" + name);

        public static ImageSource KoesioLogo()
        {
            const string key = "koesio-logo";
            if (Cache.TryGetValue(key, out var cached)) return cached;
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri("pack://application:,,,/assets/koesio-logo.jpg");
                bitmap.DecodePixelWidth = 600;
                bitmap.EndInit();
                bitmap.Freeze();
                Cache[key] = bitmap;
                return bitmap;
            }
            catch (Exception ex)
            {
                Log.Write("Logo Koesio : " + ex.Message);
                return null;
            }
        }
    }
}
