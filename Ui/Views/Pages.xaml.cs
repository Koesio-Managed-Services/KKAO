using System.Windows.Controls;

namespace KKAO.Ui
{
    public partial class HomePage : UserControl
    {
        public HomePage() => InitializeComponent();
    }

    public partial class AppsPage : UserControl
    {
        public AppsPage() => InitializeComponent();
    }

    public partial class AppDetailPage : UserControl
    {
        public AppDetailPage() => InitializeComponent();

        internal void ScrollToTop() => Scroller?.ScrollToTop();
    }

    public partial class QuickPage : UserControl
    {
        public QuickPage() => InitializeComponent();
    }

    public partial class QuestsPage : UserControl
    {
        public QuestsPage() => InitializeComponent();
    }

    public partial class SettingsPage : UserControl
    {
        public SettingsPage() => InitializeComponent();
    }

    public partial class OnboardingView : UserControl
    {
        public OnboardingView() => InitializeComponent();
    }
}
