namespace OW.Service;

internal static class ServiceConstants
{
    public const string ServiceName = "OptimizeWinService";

    /// <summary>File name of the UI program that is allowed to talk to the service.</summary>
    public const string AppExeName = "OptimizeWin.exe";

    /// <summary>How long "deleted" files stay in quarantine before they are removed for good.</summary>
    public static readonly TimeSpan QuarantineRetention = TimeSpan.FromDays(7);
}
