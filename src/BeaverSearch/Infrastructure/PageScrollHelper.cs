using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace BeaverSearch.Infrastructure;

/// <summary>
/// One consistent smooth wheel-scroll behavior for all BeaverSearch pages.
/// It intentionally scrolls the outer page even when the pointer is over
/// DataGrid/ListBox — matching the approved dashboard mockups.
/// </summary>
public static class PageScrollHelper
{
    public static readonly DependencyProperty AnimatedVerticalOffsetProperty =
        DependencyProperty.RegisterAttached(
            "AnimatedVerticalOffset",
            typeof(double),
            typeof(PageScrollHelper),
            new PropertyMetadata(0d, OnAnimatedVerticalOffsetChanged));

    public static double GetAnimatedVerticalOffset(DependencyObject obj)
        => (double)obj.GetValue(AnimatedVerticalOffsetProperty);

    public static void SetAnimatedVerticalOffset(DependencyObject obj, double value)
        => obj.SetValue(AnimatedVerticalOffsetProperty, value);

    private static void OnAnimatedVerticalOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ScrollViewer viewer && e.NewValue is double offset)
            viewer.ScrollToVerticalOffset(offset);
    }

    public static void Handle(ScrollViewer scrollViewer, MouseWheelEventArgs e)
    {
        if (scrollViewer.ScrollableHeight <= 0) return;

        var step = Math.Max(74.0, Math.Abs(e.Delta) * 0.62);
        var target = e.Delta > 0
            ? scrollViewer.VerticalOffset - step
            : scrollViewer.VerticalOffset + step;
        target = Math.Clamp(target, 0, scrollViewer.ScrollableHeight);

        // Keep the attached value synchronized with the real position before
        // starting a new animation. This also makes rapid wheel input feel natural.
        scrollViewer.BeginAnimation(AnimatedVerticalOffsetProperty, null);
        SetAnimatedVerticalOffset(scrollViewer, scrollViewer.VerticalOffset);

        var animation = new DoubleAnimation
        {
            From = scrollViewer.VerticalOffset,
            To = target,
            Duration = TimeSpan.FromMilliseconds(210),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        scrollViewer.BeginAnimation(AnimatedVerticalOffsetProperty, animation, HandoffBehavior.SnapshotAndReplace);
        e.Handled = true;
    }
}
