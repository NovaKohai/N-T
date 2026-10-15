using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using NDM.Core;

namespace NDM.App;

public partial class App : Application
{
    static App() => WindowSizing.Attach();
    private Mutex? _singleton;
    private bool _ownsMutex;
    private DownloadManager? _manager;
    private NativeBridge? _bridge;
    private ITorrentFeature? _torrentFeature;
    private TorrentController? _torrents;
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(int processId);
    // NDM_STATE_DIR relocates all state and gives that instance its own singleton mutex and pipe, so isolated runs never touch a live queue.
    private static readonly string? StateOverride = Environment.GetEnvironmentVariable("NDM_STATE_DIR") is { Length: > 0 } v ? Path.GetFullPath(v) : null;
    public const string VariantFolder = "NDM";
    public static string StateDirectory => StateOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), VariantFolder);
    private static string InstanceSuffix => StateOverride is null ? "" : "." + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(StateOverride.ToLowerInvariant())))[..12];
    public static string PipeName => NativeBridge.PipeName + InstanceSuffix;
    public AppSettings Settings { get; private set; } = new();
    /// <summary>Release checks and the update dialog (null until the main window exists).</summary>
    public UpdateController? Updates { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        var startup = StartupArguments.Parse(e.Args);
        _singleton = new Mutex(true, @"Local\NDM.App" + InstanceSuffix, out _ownsMutex);
        if (!_ownsMutex) { ForwardToRunningInstance(startup); Shutdown(); return; }
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown; // --tray never shows the window, so 'last window closed' cannot decide.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, args) =>
            {
                if (sender is Window window && ReferenceEquals(args.OriginalSource, window))
                    WindowChrome.Apply(window, Settings.Theme == "Dark");
            }));
        try
        {
            Directory.CreateDirectory(StateDirectory);
            Settings = AppSettings.Load(StateDirectory);
            ApplyTheme(Settings.Theme);
            Directory.CreateDirectory(Settings.DownloadDirectory);
            _manager = new DownloadManager(
                Settings.DownloadDirectory, 
                StateDirectory, 
                OptionsHelper.CreateDownloadOptions(Settings),
                fileName => CategoryHelper.GetDownloadPath(fileName, Settings));
            _torrents = new TorrentController(Path.Combine(StateDirectory, "torrent"), Settings);
            _torrentFeature = new TorrentFeature(_torrents, Settings);
            var window = new MainWindow(_manager, Settings, () => Settings.Save(StateDirectory), enableTray: true, power: null, torrents: _torrentFeature);
            window.JobCompletion = new JobCompletionStore(Path.Combine(StateDirectory, "job-completion.json"));
            window.JobCompletion.Prune(_manager.Jobs.Select(j => j.Id));
            MainWindow = window;
            window.Closed += (_, _) => Shutdown();
            SetUpUpdates(window);
            _bridge = new NativeBridge(HandleBrowserDownload, PipeName, HandleControl);
            _bridge.Start();
            if (startup.Tray) window.StartInTray(); else window.Show();
            // The engine (and its listening socket) only exists once the user has torrents.
            if (TorrentController.HasSavedTorrents(Path.Combine(StateDirectory, "torrent"))) _ = _torrents.EnsureAsync().ContinueWith(_ => { }, TaskScheduler.Default);
            foreach (var magnet in startup.Magnets) Dispatcher.InvokeAsync(() => window.AddMagnetAsync(magnet));
            foreach (var file in startup.TorrentFiles) Dispatcher.InvokeAsync(() => window.AddTorrentPathAsync(Path.GetFullPath(file)));
        }
        catch (Exception ex)
        {
            MessageBox.Show("NeoDM could not start. Check that your destination and state folders are writable.\n\n" + ex.Message, "NeoDM", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void SetUpUpdates(MainWindow window)
    {
        string version = AboutInfo.Version;
        var installer = new UpdateInstaller(UpdateInstaller.DefaultDirectory, version, () => Settings.BuildProxy());
        Updates = new UpdateController(Settings, () => Settings.Save(StateDirectory), version, () => new UpdateChecker(version, () => Settings.BuildProxy()), installer);
        UpdateWindow? open = null;
        Updates.ShowAvailable = release =>
        {
            if (open is { IsLoaded: true }) { WindowActivation.BringToFront(open); return; }
            open = new UpdateWindow(release, Updates) { Owner = window is { IsVisible: true, WindowState: not WindowState.Minimized } ? window : null };
            open.Show();
        };
        Updates.ShowMessage = text => MessageBox.Show(window is { IsVisible: true } ? window : null, text, "NeoDM updates", MessageBoxButton.OK, MessageBoxImage.Information);
        window.Updates = Updates;
        Updates.Start();
    }

    public void ApplyTheme(string theme)
    {
        Resources.MergedDictionaries[0] = new ResourceDictionary { Source = new Uri($"pack://application:,,,/NeoDM;component/Themes/{(theme == "Dark" ? "Dark" : "Light")}.xaml", UriKind.Absolute) };
        foreach (Window window in Windows) WindowChrome.Apply(window, theme == "Dark");
    }

    /// <summary>Second launch: hand the work (or just 'show yourself') to the running instance, then exit.</summary>
    private void ForwardToRunningInstance(StartupArguments args)
    {
        if (args.Tray && !args.HasWork) return; // A repeated autostart must not pop the window up.
        var messages = new List<(string Action, string? Value)>();
        messages.AddRange(args.Magnets.Select(m => (NativeBridge.ActionAddMagnet, (string?)m)));
        foreach (var file in args.TorrentFiles)
        {
            try { messages.Add((NativeBridge.ActionAddTorrent, Path.GetFullPath(file))); } catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        if (messages.Count == 0) messages.Add((NativeBridge.ActionShow, null));
        try { AllowSetForegroundWindow(-1); } catch (Exception) { }
        foreach (var (action, value) in messages)
        {
            var result = Task.Run(() => NativeBridge.SendControlAsync(PipeName, action, value, TimeSpan.FromSeconds(8))).GetAwaiter().GetResult();
            if (!result.Ok)
            {
                MessageBox.Show("NeoDM is already running but did not accept the request.\n\n" + (result.Error ?? ""), "NeoDM", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
    }

    /// <summary>Runs on the pipe thread: validates nothing heavy, queues the UI work and answers immediately.</summary>
    private string? HandleControl(NativeBridge.ControlRequest request)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (MainWindow is not MainWindow window) return;
            switch (request.Action)
            {
                case NativeBridge.ActionShow: window.BringToFront(); break;
                case NativeBridge.ActionAddMagnet when request.Value is { } magnet: _ = window.AddMagnetAsync(magnet); break;
                case NativeBridge.ActionAddTorrent when request.Value is { } path: _ = window.AddTorrentPathAsync(path); break;
            }
        }));
        return null;
    }

    private void HandleBrowserDownload(NativeBridge.BrowserDownloadRequest request)
    {
        Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                // The browser download is already gone (the extension cancels it before asking), so this dialog must never end up hidden:
                // it has its own taskbar button, is not owned by a minimised/tray-hidden main window and is brought above the browser when it opens.
                var dialog = new DownloadOptionsDialog(request.Url, request.FileName, request.Referer, request.Cookie, Settings.DownloadDirectory)
                {
                    // A --tray start never showed the main window, and WPF refuses an unshown owner; an owned dialog would also minimise with it.
                    Owner = MainWindow is { IsVisible: true, WindowState: not WindowState.Minimized } ? MainWindow : null,
                    ShowInTaskbar = true
                };
                dialog.ContentRendered += (_, _) => WindowActivation.BringToFront(dialog);

                if (dialog.ShowDialog() != true || dialog.Request is null)
                {
                    // The user pressed Cancel: discard on purpose (the extension does not restart the browser download for this reply).
                    request.Respond(new NativeBridge.BrowserDownloadResponse(false, null, "Download cancelled in NeoDM.", Cancelled: true));
                    return;
                }

                DownloadJob job;
                
                switch (dialog.UserAction)
                {
                    case DownloadOptionsDialog.DialogAction.StartNow:
                        job = await _manager!.AddScheduledAsync(dialog.Request, dialog.SpeedLimit.HasValue ? DateTimeOffset.UtcNow.AddYears(1) : null);
                        if (dialog.SpeedLimit.HasValue)
                        {
                            _manager.SetSpeedLimit(job.Id, dialog.SpeedLimit);
                            _manager.Resume(job.Id);
                        }
                        break;

                    case DownloadOptionsDialog.DialogAction.Queue:
                        job = await _manager!.AddScheduledAsync(dialog.Request, dialog.SpeedLimit.HasValue ? DateTimeOffset.UtcNow.AddYears(1) : null);
                        if (dialog.SpeedLimit.HasValue)
                        {
                            _manager.SetSpeedLimit(job.Id, dialog.SpeedLimit);
                        }
                        break;

                    case DownloadOptionsDialog.DialogAction.Schedule:
                        job = await _manager!.AddScheduledAsync(dialog.Request, dialog.SpeedLimit.HasValue ? DateTimeOffset.UtcNow.AddYears(1) : dialog.ScheduledAt);
                        if (dialog.SpeedLimit.HasValue)
                        {
                            _manager.SetSpeedLimit(job.Id, dialog.SpeedLimit);
                            _manager.Schedule(job.Id, dialog.ScheduledAt ?? DateTimeOffset.UtcNow);
                        }
                        else if (dialog.ScheduledAt.HasValue)
                        {
                            _manager.Schedule(job.Id, dialog.ScheduledAt.Value);
                        }
                        break;

                    default:
                        request.Respond(new NativeBridge.BrowserDownloadResponse(false, null, "Download cancelled in NeoDM.", Cancelled: true));
                        return;
                }

                // Only downloads that start right now open the window; queued-with-limit and scheduled ones stay quiet.
                bool startedNow = dialog.UserAction == DownloadOptionsDialog.DialogAction.StartNow || (dialog.UserAction == DownloadOptionsDialog.DialogAction.Queue && !dialog.SpeedLimit.HasValue);
                if (startedNow && MainWindow is MainWindow main) main.ShowCapturedProgress(job.Id);
                request.Respond(new NativeBridge.BrowserDownloadResponse(true, job.Id));
            }
            catch (Exception ex)
            {
                request.Respond(new NativeBridge.BrowserDownloadResponse(false, null, ex.Message));
            }
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _bridge?.Dispose();
        _torrentFeature?.Dispose(); // stops torrents and writes fast-resume data 
        _manager?.Dispose();
        if (_ownsMutex) _singleton?.ReleaseMutex();
        _singleton?.Dispose();
        base.OnExit(e);
    }
}
