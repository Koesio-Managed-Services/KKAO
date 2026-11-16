using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace KKAO.Ui
{
    // Noms affiches utilisables dans le XAML ({x:Static ui:Brand.Name}) : renommer KKAO = Branding.cs seulement
    public static class Brand
    {
        public static string Name => Branding.Name;
        public static string LongName => Branding.LongName;
        public static string Tagline => Branding.Tagline;
        public static string Publisher => Branding.PublisherLong;
    }

    // Choix du gabarit d'un reglage expose selon son type (toggle, text, number, choice, hotkey, folder, info, button)
    public sealed class SettingTemplateSelector : DataTemplateSelector
    {
        public override DataTemplate SelectTemplate(object item, DependencyObject container)
        {
            var type = (item as SettingItemViewModel)?.Type ?? "info";
            var element = container as FrameworkElement;
            return element?.TryFindResource("Setting." + type) as DataTemplate ?? element?.TryFindResource("Setting.info") as DataTemplate;
        }
    }

    // Saisie d'un raccourci clavier : clic, puis la combinaison (Echap : annuler, Retour arriere : aucun raccourci)
    public sealed class HotkeyBox : Button
    {
        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register("Value", typeof(string), typeof(HotkeyBox),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((HotkeyBox)d).UpdateText()));

        bool _capturing;

        public HotkeyBox()
        {
            Style = Application.Current?.TryFindResource("Btn") as Style;
            MinWidth = 170;
            FontFamily = Application.Current?.TryFindResource("MonoFont") as FontFamily ?? FontFamily;
            FontWeight = FontWeights.Normal;
            Click += (s, e) => StartCapture();
            LostKeyboardFocus += (s, e) => StopCapture();
            PreviewKeyDown += OnKey;
            UpdateText();
        }

        public string Value
        {
            get => (string)GetValue(ValueProperty);
            set => SetValue(ValueProperty, value);
        }

        void StartCapture()
        {
            _capturing = true;
            Focus();
            Keyboard.Focus(this);
            UpdateText();
        }

        void StopCapture()
        {
            if (!_capturing) return;
            _capturing = false;
            UpdateText();
        }

        void UpdateText()
        {
            if (_capturing)
            {
                Content = "Appuyez sur la combinaison…";
                SetResourceReference(ForegroundProperty, "AccentFg");
                return;
            }
            Content = string.IsNullOrEmpty(Value) ? "Aucun raccourci" : Value;
            SetResourceReference(ForegroundProperty, string.IsNullOrEmpty(Value) ? "TextFaint" : "Text");
        }

        void OnKey(object sender, KeyEventArgs e)
        {
            if (!_capturing) return;
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape)
            {
                StopCapture();
                return;
            }
            if ((key == Key.Back || key == Key.Delete) && Keyboard.Modifiers == ModifierKeys.None)
            {
                _capturing = false;
                Value = "";
                UpdateText();
                return;
            }
            if (IsModifier(key)) return;
            var text = Format(Keyboard.Modifiers, key);
            if (text == null) return;
            _capturing = false;
            Value = text;
            UpdateText();
        }

        static bool IsModifier(Key key) =>
            key == Key.LeftCtrl || key == Key.RightCtrl || key == Key.LeftAlt || key == Key.RightAlt ||
            key == Key.LeftShift || key == Key.RightShift || key == Key.LWin || key == Key.RWin;

        // Format du protocole : Ctrl+Alt+F1 (modificateurs Ctrl, Alt, Maj, Win)
        public static string Format(ModifierKeys modifiers, Key key)
        {
            var isFunction = key >= Key.F1 && key <= Key.F24;
            // Un raccourci global contient Ctrl, Alt ou Win (ou une touche de fonction seule)
            if (!isFunction && (modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == 0) return null;
            var parts = new List<string>();
            if ((modifiers & ModifierKeys.Control) != 0) parts.Add("Ctrl");
            if ((modifiers & ModifierKeys.Alt) != 0) parts.Add("Alt");
            if ((modifiers & ModifierKeys.Shift) != 0) parts.Add("Maj");
            if ((modifiers & ModifierKeys.Windows) != 0) parts.Add("Win");
            string name;
            if (key >= Key.D0 && key <= Key.D9) name = ((int)(key - Key.D0)).ToString();
            else if (key >= Key.NumPad0 && key <= Key.NumPad9) name = "Num" + (int)(key - Key.NumPad0);
            else if (key >= Key.A && key <= Key.Z) name = key.ToString();
            else name = key.ToString();
            parts.Add(name);
            return string.Join("+", parts);
        }
    }

    // Apercu du menu de l'icone (page « Actions rapides »)
    public sealed class MenuPreviewRow
    {
        public string Label { get; set; }
        public string Shortcut { get; set; }
        public bool IsSeparator { get; set; }
        public bool Bold { get; set; }
        public ImageSource Icon { get; set; }
        public bool HasArrow { get; set; }
    }
}
