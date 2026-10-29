using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace NT.App;

public static class WindowTransitions
{
    public static void ApplyEntrance(Window window)
    {
        if (!SystemParameters.ClientAreaAnimation) return;
        if (window is MiniWidgetWindow or SplashScreenWindow) return;
        if (window.Content is not UIElement content) return;

        TranslateTransform translate;
        if (content.RenderTransform is TranslateTransform tt)
        {
            translate = tt;
        }
        else if (content.RenderTransform is TransformGroup tg && tg.Children.OfType<TranslateTransform>().FirstOrDefault() is { } existingTt)
        {
            translate = existingTt;
        }
        else
        {
            translate = new TranslateTransform(0, 6);
            var group = new TransformGroup();
            if (content.RenderTransform != null && content.RenderTransform != Transform.Identity)
                group.Children.Add(content.RenderTransform);
            group.Children.Add(translate);
            content.RenderTransform = group;
        }

        content.Opacity = 0.0;
        translate.Y = 6.0;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var opacityAnim = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease };
        var translateAnim = new DoubleAnimation(6.0, 0.0, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease };

        content.BeginAnimation(UIElement.OpacityProperty, opacityAnim);
        translate.BeginAnimation(TranslateTransform.YProperty, translateAnim);
    }
}
