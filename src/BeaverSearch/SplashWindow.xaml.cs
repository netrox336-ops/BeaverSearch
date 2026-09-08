using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace BeaverSearch;

public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
        Loaded += SplashWindow_Loaded;
    }

    private void SplashWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        SplashRoot.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280)) { EasingFunction = ease });
        if (BrandGroup.RenderTransform is ScaleTransform scale)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.92, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.92, 1, TimeSpan.FromMilliseconds(420)) { EasingFunction = ease });
        }
    }

    public async Task PlaySequenceAsync()
    {
        await Dispatcher.Yield(DispatcherPriority.Loaded);
        var accent = new SolidColorBrush(Color.FromRgb(242, 181, 104));
        var steps = new[]
        {
            ("Подключение к yooma.su + CYBERSHOKE...", StepYooma, 0.24, "Community DOM"),
            ("Инициализация DOM SteamID parser...", StepResolver, 0.48, "SteamID DOM"),
            ("Подготовка Inventory Scanner...", StepInventory, 0.74, "Inventory"),
            ("Подготовка Price Engine...", StepPrices, 1.0, "Price Engine")
        };

        foreach (var (text, target, fraction, stage) in steps)
        {
            App.LogStartupStage("Splash stage: " + stage);
            StageText.Text = text;
            target.Foreground = accent;
            var width = 420 * fraction;
            ProgressFill.BeginAnimation(WidthProperty, new DoubleAnimation(ProgressFill.ActualWidth, width, TimeSpan.FromMilliseconds(190))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.HoldEnd
            });
            await Task.Delay(190);
        }

        StageText.Text = "Готово";
        await Task.Delay(70);
    }

    public async Task FadeOutAsync()
    {
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
            FillBehavior = FillBehavior.HoldEnd
        };
        SplashRoot.BeginAnimation(OpacityProperty, fade);
        await Task.Delay(180);
    }
}
