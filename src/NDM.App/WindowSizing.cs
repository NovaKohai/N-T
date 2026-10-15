using System.Windows;

namespace NDM.App;

/// <summary>
/// Keeps every window inside the usable screen area at any display scale: at 150% a 1920x1080 screen offers only about 1280x690 device-independent
/// units, so windows designed at 100% would otherwise run off the screen (clipped controls, unreachable buttons).
/// </summary>
public static class WindowSizing
{
    private static bool attached;
    public const double Margin = 24;

    /// <summary>Clamps size, minimum size and maximum size of <paramref name="window"/> to <paramref name="work"/> (device-independent units).</summary>
    public static void Fit(Window window, Rect work)
    {
        double maxW = Math.Max(360, work.Width - Margin), maxH = Math.Max(320, work.Height - Margin);
        window.MaxWidth = Math.Min(window.MaxWidth, maxW); window.MaxHeight = Math.Min(window.MaxHeight, maxH);
        if (window.MinWidth > maxW) window.MinWidth = maxW;
        if (window.MinHeight > maxH) window.MinHeight = maxH;
        if (!double.IsNaN(window.Width) && window.SizeToContent is not (SizeToContent.Width or SizeToContent.WidthAndHeight) && window.Width > maxW) window.Width = maxW;
        if (!double.IsNaN(window.Height) && window.SizeToContent is not (SizeToContent.Height or SizeToContent.WidthAndHeight) && window.Height > maxH) window.Height = maxH;
    }

    /// <summary>Registers the clamp for every window of the application.</summary>
    public static void Attach()
    {
        if (attached) return; attached = true;
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, args) =>
        {
            if (sender is Window window && ReferenceEquals(args.OriginalSource, window)) Fit(window, SystemParameters.WorkArea);
        }));
    }
}
