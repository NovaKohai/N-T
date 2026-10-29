using System.Windows;
using System.Windows.Media.Animation;

namespace NT.App;

public partial class SplashScreenWindow : Window
{
    private bool _isClosing;

    public SplashScreenWindow()
    {
        InitializeComponent();
        VersionText.Text = "v" + AboutInfo.Version;

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
    }

    public void SetStatus(string text)
    {
        if (Dispatcher.CheckAccess())
        {
            StatusText.Text = text;
        }
        else
        {
            Dispatcher.BeginInvoke(new Action(() => StatusText.Text = text));
        }
    }

    public async Task FadeOutAndCloseAsync(int durationMs = 180)
    {
        if (_isClosing) return;
        _isClosing = true;

        if (SystemParameters.ClientAreaAnimation)
        {
            var tcs = new TaskCompletionSource<bool>();
            var anim = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(durationMs))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
            };
            anim.Completed += (_, _) =>
            {
                Close();
                tcs.TrySetResult(true);
            };
            BeginAnimation(OpacityProperty, anim);
            await tcs.Task;
        }
        else
        {
            Close();
        }
    }
}
