using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;

namespace NT.App;

/// <summary>
/// Attached property to smoothly animate WPF ProgressBar values over time using QuadraticEase.
/// </summary>
public static class SmoothProgressBar
{
    public static readonly DependencyProperty SmoothValueProperty =
        DependencyProperty.RegisterAttached(
            "SmoothValue",
            typeof(double),
            typeof(SmoothProgressBar),
            new PropertyMetadata(0.0, OnSmoothValueChanged));

    public static double GetSmoothValue(ProgressBar bar) => (double)bar.GetValue(SmoothValueProperty);
    public static void SetSmoothValue(ProgressBar bar, double value) => bar.SetValue(SmoothValueProperty, value);

    private static void OnSmoothValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ProgressBar bar) return;
        double target = (double)e.NewValue;
        if (!double.IsFinite(target)) target = 0;
        target = Math.Clamp(target, bar.Minimum, bar.Maximum);

        if (bar.IsIndeterminate) return;

        double current = bar.Value;
        if (Math.Abs(current - target) < 0.1 || !SystemParameters.ClientAreaAnimation)
        {
            bar.BeginAnimation(RangeBase.ValueProperty, null);
            bar.Value = target;
            return;
        }

        var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(250))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        bar.BeginAnimation(RangeBase.ValueProperty, anim);
    }
}
