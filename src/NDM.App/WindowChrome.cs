using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
namespace NDM.App;
internal static class WindowChrome
{
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);
    public static void Apply(Window window, bool dark)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        int theme = dark ? 1 : 0, round = 2;
        _ = DwmSetWindowAttribute(handle, 20, ref theme, sizeof(int));
        _ = DwmSetWindowAttribute(handle, 33, ref round, sizeof(int));
    }
}
