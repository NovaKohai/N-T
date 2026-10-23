using NT.Core;
namespace NT.App;

public static class OptionsHelper
{
    // The engine owns an immutable startup snapshot; preferences never disturb running jobs.
    public static DownloadOptions CreateDownloadOptions(AppSettings settings) => new()
    {
        MaxSegments = settings.MaxSegments,
        MaxConcurrentDownloads = settings.MaxConcurrentDownloads,
        RetryCount = settings.RetryCount,
        RequestTimeout = TimeSpan.FromSeconds(settings.RequestTimeoutSeconds),
        EnableDynamicSegmentation = settings.EnableDynamicSegmentation,
        GlobalSpeedLimit = settings.GlobalSpeedLimitBytesPerSecond,
        Proxy = settings.ProxyForDownloads ? settings.BuildProxy() : null
    };
}
