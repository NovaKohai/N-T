using System.Diagnostics;
using System.IO;

namespace NDM.App;

public static class CompletionActionInfo
{
    public static readonly IReadOnlyList<CompletionActionKind> All = Enum.GetValues<CompletionActionKind>();
    public static string Label(CompletionActionKind kind) => kind switch
    {
        CompletionActionKind.None => "Do nothing",
        CompletionActionKind.PlaySound => "Play sound",
        CompletionActionKind.OpenFolder => "Open downloads folder",
        CompletionActionKind.ExitApp => "Exit N&T",
        CompletionActionKind.Lock => "Lock computer",
        CompletionActionKind.Sleep => "Sleep",
        CompletionActionKind.Hibernate => "Hibernate",
        CompletionActionKind.LogOff => "Log off",
        CompletionActionKind.Restart => "Restart",
        CompletionActionKind.Shutdown => "Turn off computer",
        _ => kind.ToString()
    };
    public static string Description(CompletionActionKind kind) => kind switch
    {
        CompletionActionKind.None => "Nothing happens when the queue finishes.",
        CompletionActionKind.PlaySound => "Plays a notification sound once the last active download finishes.",
        CompletionActionKind.OpenFolder => "Opens the download folder in File Explorer.",
        CompletionActionKind.ExitApp => "Closes N&T completely (not just to the tray).",
        CompletionActionKind.Lock => "Locks this Windows session. Downloads are already finished.",
        CompletionActionKind.Sleep => "Puts the computer to sleep after a 30 second warning you can cancel.",
        CompletionActionKind.Hibernate => "Hibernates the computer after a 30 second warning you can cancel. Requires hibernation to be enabled in Windows.",
        CompletionActionKind.LogOff => "Signs you out of Windows after a 30 second warning you can cancel.",
        CompletionActionKind.Restart => "Restarts the computer after a 30 second warning you can cancel.",
        CompletionActionKind.Shutdown => "Turns off the computer after a 30 second warning you can cancel.",
        _ => ""
    };
    /// <summary>Actions that end or suspend the user's session and therefore get the cancellable countdown.</summary>
    public static bool IsPower(CompletionActionKind kind) => kind is CompletionActionKind.Sleep or CompletionActionKind.Hibernate or CompletionActionKind.LogOff or CompletionActionKind.Restart or CompletionActionKind.Shutdown;
    public static bool UsesForce(CompletionActionKind kind) => kind is CompletionActionKind.Restart or CompletionActionKind.Shutdown;
    /// <summary>Verb phrase for "N&T will ... in N seconds".</summary>
    public static string Verb(CompletionActionKind kind) => kind switch
    {
        CompletionActionKind.Sleep => "put the computer to sleep",
        CompletionActionKind.Hibernate => "hibernate the computer",
        CompletionActionKind.LogOff => "log you off",
        CompletionActionKind.Restart => "restart the computer",
        CompletionActionKind.Shutdown => "turn off the computer",
        _ => Label(kind).ToLowerInvariant()
    };
}

public sealed record SystemCommand(string FileName, string Arguments);

/// <summary>The exact command lines used for system actions. Fixed strings, system32 absolute paths, no user input.</summary>
public static class PowerCommands
{
    private static string System32(string file) => Path.Combine(Environment.SystemDirectory, file);
    public static SystemCommand? For(CompletionActionKind kind, bool force) => kind switch
    {
        CompletionActionKind.Lock => new(System32("rundll32.exe"), "user32.dll,LockWorkStation"),
        CompletionActionKind.Sleep => new(System32("rundll32.exe"), "powrprof.dll,SetSuspendState 0,1,0"),
        CompletionActionKind.Hibernate => new(System32("shutdown.exe"), "/h"),
        CompletionActionKind.LogOff => new(System32("shutdown.exe"), "/l"),
        CompletionActionKind.Restart => new(System32("shutdown.exe"), force ? "/r /f /t 0" : "/r /t 0"),
        CompletionActionKind.Shutdown => new(System32("shutdown.exe"), force ? "/s /f /t 0" : "/s /t 0"),
        _ => null
    };
}

/// <summary>Seam for everything that touches the real session. Tests substitute a recording fake.</summary>
public interface IPowerActions { void Execute(CompletionActionKind kind, bool force); }

public sealed class SystemPowerActions : IPowerActions
{
    public void Execute(CompletionActionKind kind, bool force)
    {
        var command = PowerCommands.For(kind, force) ?? throw new ArgumentOutOfRangeException(nameof(kind));
        Process.Start(new ProcessStartInfo(command.FileName, command.Arguments) { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
    }
}

/// <summary>Validation and launch description for the optional "run program" step. Never goes through cmd.exe /c concatenation.</summary>
public static class RunProgramValidator
{
    private static readonly string[] DirectExtensions = { ".exe", ".com" };
    private static readonly string[] BatchExtensions = { ".bat", ".cmd" };
    private const string BatchMetacharacters = "&|<>^%\"!";
    /// <summary>Returns a user-facing problem, or null when the program and arguments are acceptable.</summary>
    public static string? Validate(string? path, string? arguments)
    {
        path = path?.Trim() ?? ""; arguments ??= "";
        if (path.Length == 0) return "Choose a program to run when downloads finish.";
        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || path.Contains('"')) return "The program path contains invalid characters.";
        if (!Path.IsPathFullyQualified(path)) return "Enter the full path of the program, for example C:\\Tools\\backup.exe.";
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (!DirectExtensions.Contains(ext) && !BatchExtensions.Contains(ext) && ext != ".lnk") return "The program must be an .exe, .com, .bat, .cmd or .lnk file.";
        if (!File.Exists(path)) return "The program file was not found.";
        if (arguments.Length > 2000) return "Arguments are too long (2000 characters maximum).";
        if (arguments.Any(c => char.IsControl(c))) return "Arguments cannot contain line breaks or control characters.";
        if (BatchExtensions.Contains(ext) && arguments.IndexOfAny(BatchMetacharacters.ToCharArray()) >= 0)
            return "Arguments for .bat/.cmd files cannot contain & | < > ^ % ! or quotes, because the command interpreter would act on them.";
        return null;
    }
    public static ProcessStartInfo BuildStartInfo(string path, string? arguments)
    {
        if (Validate(path, arguments) is { } problem) throw new FormatException(problem);
        path = Path.GetFullPath(path.Trim());
        string ext = Path.GetExtension(path).ToLowerInvariant();
        // .exe/.com/.bat/.cmd are started directly by CreateProcess; only a shortcut needs the shell to resolve it.
        return new ProcessStartInfo(path) { Arguments = arguments ?? "", UseShellExecute = ext == ".lnk", WorkingDirectory = Path.GetDirectoryName(path)! };
    }
}

public enum CountdownOutcome { Elapsed, DoItNow, Cancelled }
public interface ICountdownPrompt { CountdownOutcome Show(CompletionActionKind kind, int seconds); }

/// <summary>Owns the session-scoped choice (with optional remembering) and executes it when the queue finishes.</summary>
public sealed class CompletionActionRunner
{
    private readonly AppSettings settings;
    private readonly IPowerActions power;
    private readonly Action save;
    private readonly Action playSound, openFolder, exitApp;
    private readonly Action<ProcessStartInfo> launch;
    public ICountdownPrompt Countdown { get; set; }
    public int CountdownSeconds { get; set; } = 30;
    public string? LastError { get; private set; }
    public event Action? SessionActionChanged;
    public CompletionActionRunner(AppSettings settings, IPowerActions power, ICountdownPrompt countdown, Action save, Action playSound, Action openFolder, Action exitApp, Action<ProcessStartInfo>? launch = null)
    {
        this.settings = settings; this.power = power; Countdown = countdown; this.save = save;
        this.playSound = playSound; this.openFolder = openFolder; this.exitApp = exitApp;
        this.launch = launch ?? (psi => Process.Start(psi)?.Dispose());
    }
    public CompletionActionKind Current => settings.SessionCompletionAction;
    /// <summary>Applies a choice for this session; persists it too only when "Remember" is on.</summary>
    public void Choose(CompletionActionKind kind)
    {
        settings.SessionCompletionAction = kind;
        if (settings.CompletionRemember)
        {
            var previous = settings.CompletionAction;
            settings.CompletionAction = kind;
            try { save(); LastError = null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { settings.CompletionAction = previous; LastError = "The choice applies to this session but could not be saved."; }
        }
        SessionActionChanged?.Invoke();
    }
    /// <summary>Called once per queue-finished edge. Returns true when the configured action was carried out.</summary>
    public bool Run()
    {
        var kind = Current;
        return Perform(kind, settings.CompletionForceClose, resetSessionOnCancel: true, runProgram: true);
    }
    /// <summary>Carries out one download's own completion action with the same countdown/cancel safety. Force only reaches Shut down / Restart.</summary>
    public bool RunFor(CompletionActionKind kind, bool force) => Perform(kind, force, resetSessionOnCancel: false, runProgram: false);
    private bool Perform(CompletionActionKind kind, bool force, bool resetSessionOnCancel, bool runProgram)
    {
        LastError = null;
        if (CompletionActionInfo.IsPower(kind))
        {
            var outcome = Countdown.Show(kind, CountdownSeconds);
            if (outcome == CountdownOutcome.Cancelled)
            {
                if (resetSessionOnCancel) { settings.SessionCompletionAction = CompletionActionKind.None; SessionActionChanged?.Invoke(); } // Cancel resets this session only; a remembered preference is untouched.
                return false;
            }
        }
        if (runProgram) RunProgram();
        try
        {
            switch (kind)
            {
                case CompletionActionKind.None: return false;
                case CompletionActionKind.PlaySound: playSound(); break;
                case CompletionActionKind.OpenFolder: openFolder(); break;
                case CompletionActionKind.ExitApp: exitApp(); break;
                default: power.Execute(kind, force && CompletionActionInfo.UsesForce(kind)); break;
            }
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        { LastError = $"\"{CompletionActionInfo.Label(kind)}\" could not be carried out."; return false; }
    }
    private void RunProgram()
    {
        if (!settings.RunProgramEnabled) return;
        try { launch(RunProgramValidator.BuildStartInfo(settings.RunProgramPath, settings.RunProgramArguments)); }
        catch (Exception ex) when (ex is FormatException or System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        { LastError = "The program to run after downloads could not be started."; }
    }
}

/// <summary>Detects the busy-to-idle edge of a queue snapshot stream.</summary>
public readonly record struct QueueJob(string Id, string Status, DateTimeOffset? ScheduledAt);

public sealed class QueueFinishTrigger
{
    private readonly HashSet<string> completed = new();
    private bool hadActive;
    private int completedThisRun;
    public bool IgnoreScheduled { get; set; }
    /// <param name="history">Jobs already in the queue at start; they are history, never completions.</param>
    public QueueFinishTrigger(IEnumerable<QueueJob> history)
    {
        foreach (var job in history) if (job.Status == "Completed") completed.Add(job.Id);
    }
    /// <summary>Records an item that was already complete when first seen (restored torrents): history, not a completion.</summary>
    public void MarkHistory(QueueJob job) { if (job.Status == "Completed") completed.Add(job.Id); }
    private static bool IsActive(string status) => status is "Downloading" or "Connecting" or "Queued";
    /// <summary>True exactly once per batch: when active jobs drop to zero and something completed in this run.</summary>
    public bool Observe(IEnumerable<QueueJob> snapshot, DateTimeOffset now)
    {
        int active = 0; bool futureScheduled = false;
        foreach (var job in snapshot)
        {
            if (job.Status == "Completed" && completed.Add(job.Id)) { completedThisRun++; hadActive = true; }
            if (IsActive(job.Status)) active++;
            else if (job.Status == "Scheduled" && job.ScheduledAt is { } at && at > now) futureScheduled = true;
        }
        if (active > 0) { hadActive = true; return false; }
        if (!hadActive) return false;
        if (futureScheduled && !IgnoreScheduled) return false; // Hold the armed state until the scheduled job has run.
        bool fire = completedThisRun > 0;
        hadActive = false; completedThisRun = 0;
        return fire;
    }
}
