using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace NT.App;

public record MiniWidgetOptions(
    Action RestoreMainWindow,
    Func<string, Task> HandleDrop,
    Action<double, double> OnPositionChanged,
    double? InitialLeft = null,
    double? InitialTop = null,
    Action? OnClose = null,
    Action? OnAddUrl = null,
    Action? OnPauseAll = null,
    Action? OnResumeAll = null,
    Action? OnToggleShutdown = null,
    Func<bool>? IsShutdownArmed = null);

public partial class MiniWidgetWindow : Window
{
    private readonly MiniWidgetOptions _options;
    private bool _isClosing;

    public MiniWidgetWindow(MiniWidgetOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        InitializeComponent();

        ContextMenuOpening += (_, _) => ContextMenu = BuildContextMenu();
        ContextMenu = BuildContextMenu();

        // Screen placement and safety bounds
        double vLeft = SystemParameters.VirtualScreenLeft;
        double vTop = SystemParameters.VirtualScreenTop;
        double vWidth = SystemParameters.VirtualScreenWidth;
        double vHeight = SystemParameters.VirtualScreenHeight;

        if (_options.InitialLeft.HasValue && _options.InitialTop.HasValue &&
            _options.InitialLeft.Value >= vLeft - 40 &&
            _options.InitialLeft.Value < vLeft + vWidth - 40 &&
            _options.InitialTop.Value >= vTop &&
            _options.InitialTop.Value < vTop + vHeight - 40)
        {
            Left = _options.InitialLeft.Value;
            Top = _options.InitialTop.Value;
        }
        else
        {
            Left = Math.Max(vLeft + 20, SystemParameters.WorkArea.Right - Width - 20);
            Top = Math.Max(vTop + 20, SystemParameters.WorkArea.Top + 60);
        }

        Loaded += (_, _) =>
        {
            if (SystemParameters.ClientAreaAnimation)
            {
                Opacity = 0;
                var anim = new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(150))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                BeginAnimation(OpacityProperty, anim);
            }
        };

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject dep && FindVisualParent<Button>(dep) != null)
                return;

            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try { DragMove(); } catch (InvalidOperationException) { }
                ClampPosition();
                _options.OnPositionChanged(Left, Top);
            }
        };

        MouseDoubleClick += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Left)
                _options.RestoreMainWindow();
        };

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                AnimateClose();
            }
        };

        AllowDrop = true;
        DragEnter += (_, e) =>
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Text))
            {
                DropOverlay.Visibility = Visibility.Visible;
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            }
        };

        DragOver += (_, e) =>
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Text))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
            }
        };

        DragLeave += (_, _) =>
        {
            DropOverlay.Visibility = Visibility.Collapsed;
        };

        Drop += async (_, e) =>
        {
            DropOverlay.Visibility = Visibility.Collapsed;
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                {
                    foreach (var f in files) await _options.HandleDrop(f);
                }
            }
            else if (e.Data.GetDataPresent(DataFormats.Text))
            {
                if (e.Data.GetData(DataFormats.Text) is string text && !string.IsNullOrWhiteSpace(text))
                {
                    await _options.HandleDrop(text.Trim());
                }
            }
        };
    }

    public MiniWidgetWindow(
        Action restoreMainWindow,
        Func<string, Task> handleDrop,
        Action<double, double> onPositionChanged,
        double? initialLeft = null,
        double? initialTop = null)
        : this(new MiniWidgetOptions(restoreMainWindow, handleDrop, onPositionChanged, initialLeft, initialTop))
    {
    }

    private void ClampPosition()
    {
        double vLeft = SystemParameters.VirtualScreenLeft;
        double vTop = SystemParameters.VirtualScreenTop;
        double vWidth = SystemParameters.VirtualScreenWidth;
        double vHeight = SystemParameters.VirtualScreenHeight;

        double minLeft = vLeft - Width + 48;
        double maxLeft = vLeft + vWidth - 48;
        double minTop = vTop;
        double maxTop = vTop + vHeight - 48;

        Left = Math.Clamp(Left, minLeft, maxLeft);
        Top = Math.Clamp(Top, minTop, maxTop);
    }

    private ContextMenu BuildContextMenu()
    {
        var menu = new ContextMenu();

        var openItem = new MenuItem { Header = "Open N&T" };
        openItem.Click += (_, _) => _options.RestoreMainWindow();
        menu.Items.Add(openItem);

        if (_options.OnAddUrl != null)
        {
            var addItem = new MenuItem { Header = "Add download…" };
            addItem.Click += (_, _) => _options.OnAddUrl();
            menu.Items.Add(addItem);
        }

        menu.Items.Add(new Separator());

        if (_options.OnPauseAll != null)
        {
            var pauseItem = new MenuItem { Header = "Pause all" };
            pauseItem.Click += (_, _) => _options.OnPauseAll();
            menu.Items.Add(pauseItem);
        }

        if (_options.OnResumeAll != null)
        {
            var resumeItem = new MenuItem { Header = "Resume all" };
            resumeItem.Click += (_, _) => _options.OnResumeAll();
            menu.Items.Add(resumeItem);
        }

        menu.Items.Add(new Separator());

        if (_options.OnToggleShutdown != null)
        {
            var shutdownItem = new MenuItem
            {
                Header = "Turn off computer when done",
                IsCheckable = true,
                IsChecked = _options.IsShutdownArmed?.Invoke() ?? false
            };
            shutdownItem.Click += (_, _) => _options.OnToggleShutdown();
            menu.Items.Add(shutdownItem);
            menu.Items.Add(new Separator());
        }

        var hideItem = new MenuItem { Header = "Hide widget" };
        hideItem.Click += (_, _) => AnimateClose();
        menu.Items.Add(hideItem);

        return menu;
    }

    public string CurrentSpeedText => SpeedText.Text;
    public string CurrentActiveCountText => ActiveCountText.Text;

    public void UpdateTelemetry(double speedBytesPerSec, int activeCount, double progress = 0, double uploadSpeedBytesPerSec = 0)
    {
        ActiveCountText.Text = activeCount == 1 ? "1 active" : $"{activeCount} active";
        string downText = speedBytesPerSec > 0 ? $"↓ {DownloadRow.FormatBytes(speedBytesPerSec)}/s" : "↓ 0.0 B/s";
        SpeedText.Text = uploadSpeedBytesPerSec > 0
            ? $"{downText}  ↑ {DownloadRow.FormatBytes(uploadSpeedBytesPerSec)}/s"
            : downText;

        UpdateProgressBar(progress, activeCount);
    }

    private void UpdateProgressBar(double progress, int activeCount)
    {
        if (activeCount > 0 && progress > 0)
        {
            OverallProgressBar.Visibility = Visibility.Visible;
            PercentText.Visibility = Visibility.Visible;
            PercentText.Text = $"{Math.Round(progress)}%";

            double target = Math.Clamp(progress, 0, 100);
            if (SystemParameters.ClientAreaAnimation)
            {
                var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(250))
                {
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };
                OverallProgressBar.BeginAnimation(RangeBase.ValueProperty, anim);
            }
            else
            {
                OverallProgressBar.BeginAnimation(RangeBase.ValueProperty, null);
                OverallProgressBar.Value = target;
            }
        }
        else
        {
            OverallProgressBar.BeginAnimation(RangeBase.ValueProperty, null);
            OverallProgressBar.Value = 0;
            OverallProgressBar.Visibility = Visibility.Collapsed;
            PercentText.Visibility = Visibility.Collapsed;
        }
    }

    public void AnimateClose()
    {
        if (_isClosing) return;
        _isClosing = true;

        if (SystemParameters.ClientAreaAnimation)
        {
            var anim = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(120))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            anim.Completed += (_, _) =>
            {
                Hide();
                _options.OnClose?.Invoke();
            };
            BeginAnimation(OpacityProperty, anim);
        }
        else
        {
            Hide();
            _options.OnClose?.Invoke();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        AnimateClose();
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child != null && child is not T)
        {
            child = VisualTreeHelper.GetParent(child);
        }
        return child as T;
    }
}
