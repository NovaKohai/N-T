using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
namespace NT.App;
internal static class WindowChrome
{
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);
    /// <summary>Immersive dark mode follows the theme; branded themes also colour the caption/border/text (Windows 11 22000+; older systems ignore the attributes).</summary>
    public static void Apply(Window window, bool dark, System.Windows.Media.Color? caption = null, System.Windows.Media.Color? text = null, System.Windows.Media.Color? border = null)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        int theme = dark ? 1 : 0, round = 2;
        _ = DwmSetWindowAttribute(handle, 20, ref theme, sizeof(int));
        _ = DwmSetWindowAttribute(handle, 33, ref round, sizeof(int));
        int Colorref(System.Windows.Media.Color c) => c.R | (c.G << 8) | (c.B << 16);
        int none = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_DEFAULT
        int value = caption is { } cap ? Colorref(cap) : none; _ = DwmSetWindowAttribute(handle, 35, ref value, sizeof(int));
        value = text is { } txt ? Colorref(txt) : none; _ = DwmSetWindowAttribute(handle, 36, ref value, sizeof(int));
        value = border is { } brd ? Colorref(brd) : none; _ = DwmSetWindowAttribute(handle, 34, ref value, sizeof(int));
    }
}
