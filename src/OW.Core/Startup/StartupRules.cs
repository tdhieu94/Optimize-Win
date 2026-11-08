namespace OW.Core.Startup;

/// <summary>Platform-independent rules behind the startup list, kept separate so they can be unit tested anywhere.</summary>
public static class StartupRules
{
    private static readonly string[] HardwarePublishers =
    [
        "Realtek", "Intel", "NVIDIA", "Advanced Micro Devices", "AMD", "Synaptics", "ELAN", "Logitech",
        "Dolby", "Waves", "Conexant", "Qualcomm", "MediaTek", "Broadcom", "Alps", "Cirrus Logic",
    ];

    private static readonly string[] WindowsNames = ["SecurityHealth", "Windows Defender", "WindowsDefender"];

    /// <summary>
    /// Reads a StartupApproved value (what Task Manager writes when you disable a startup app).
    /// No value means enabled. Otherwise the lowest bit of the first byte set means disabled.
    /// </summary>
    public static bool IsEnabled(byte[]? startupApproved) =>
        startupApproved is not { Length: > 0 } || (startupApproved[0] & 1) == 0;

    /// <summary>
    /// Pulls the program path out of a Run command such as <c>"C:\Program Files\App\app.exe" --min</c>
    /// or <c>C:\Program Files\App\app.exe /background</c>. Returns null when it cannot tell.
    /// </summary>
    public static string? ExtractExecutable(string? command, Func<string, string>? expand = null)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return null;
        }

        var c = command.Trim();
        if (expand is not null)
        {
            c = expand(c);
        }

        if (c.StartsWith('"'))
        {
            var end = c.IndexOf('"', 1);
            return end > 1 ? c[1..end] : null;
        }

        // Unquoted: take up to the first known program extension so paths with spaces still work.
        foreach (var ext in new[] { ".exe", ".cmd", ".bat", ".com", ".lnk" })
        {
            var i = c.IndexOf(ext, StringComparison.OrdinalIgnoreCase);
            if (i > 0 && (i + ext.Length == c.Length || c[i + ext.Length] == ' ' || c[i + ext.Length] == ','))
            {
                return c[..(i + ext.Length)];
            }
        }

        var space = c.IndexOf(' ');
        return space > 0 ? c[..space] : c;
    }

    /// <summary>"C:\..." style path; checked by hand so the rule behaves the same in tests on Linux.</summary>
    private static bool IsFullWindowsPath(string p) =>
        p.Length >= 3 && char.IsAsciiLetter(p[0]) && p[1] == ':' && p[2] == '\\';

    public static StartupAdvice Advise(string name, string? executablePath, string? publisher, bool executableExists, string windowsDirectory)
    {
        if (executablePath is not null && !executableExists && IsFullWindowsPath(executablePath))
        {
            return StartupAdvice.Broken;
        }

        if (WindowsNames.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase))
            || (executablePath is not null && windowsDirectory.Length > 0
                && executablePath.StartsWith(windowsDirectory.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
        {
            return StartupAdvice.KeepWindows;
        }

        if (publisher is not null && HardwarePublishers.Any(p => publisher.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            return StartupAdvice.KeepHardware;
        }

        return StartupAdvice.None;
    }
}
