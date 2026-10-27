using System.Windows;
using System.Windows.Input;

namespace NT.App;

public partial class MiniWidgetWindow : Window
{
    private readonly Action restoreMainWindow;
    private readonly Func<string, Task> handleDrop;
    private readonly Action<double, double> onPositionChanged;

    public MiniWidgetWindow(
        Action restoreMainWindow,
        Func<string, Task> handleDrop,
        Action<double, double> onPositionChanged,
        double? initialLeft = null,
        double? initialTop = null)
    {
        this.restoreMainWindow = restoreMainWindow;
        this.handleDrop = handleDrop;
        this.onPositionChanged = onPositionChanged;

        InitializeComponent();

        if (initialLeft.HasValue && initialTop.HasValue && initialLeft.Value >= 0 && initialTop.Value >= 0)
        {
            Left = initialLeft.Value;
            Top = initialTop.Value;
        }
        else
        {
            Left = SystemParameters.WorkArea.Right - Width - 20;
            Top = SystemParameters.WorkArea.Top + 60;
        }

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try { DragMove(); } catch { }
                onPositionChanged(Left, Top);
            }
        };

        MouseDoubleClick += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left)
                restoreMainWindow();
        };

        AllowDrop = true;
        Drop += async (_, e) =>
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                {
                    foreach (var f in files) await handleDrop(f);
                }
            }
            else if (e.Data.GetDataPresent(DataFormats.Text))
            {
                if (e.Data.GetData(DataFormats.Text) is string text && !string.IsNullOrWhiteSpace(text))
                {
                    await handleDrop(text.Trim());
                }
            }
        };
    }

    public string CurrentSpeedText => SpeedText.Text;
    public string CurrentActiveCountText => ActiveCountText.Text;

    public void UpdateTelemetry(double speedBytesPerSec, int activeCount)
    {
        ActiveCountText.Text = activeCount == 1 ? "1 active" : $"{activeCount} active";
        SpeedText.Text = speedBytesPerSec > 0 ? $"↓ {DownloadRow.FormatBytes(speedBytesPerSec)}/s" : "↓ 0.0 B/s";
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Hide();
    }
}
