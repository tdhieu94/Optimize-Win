namespace OW.Core.Startup;

/// <summary>Where Windows found a program that starts at sign-in.</summary>
public enum StartupLocation
{
    /// <summary>HKCU\...\Run: only for this user.</summary>
    UserRegistry,

    /// <summary>HKLM\...\Run (64 or 32 bit): for every user.</summary>
    MachineRegistry,

    /// <summary>Shortcut in this user's Startup folder.</summary>
    UserStartupFolder,

    /// <summary>Shortcut in the all-users Startup folder.</summary>
    CommonStartupFolder,
}

public enum StartupAdvice
{
    /// <summary>No opinion: the user decides.</summary>
    None,

    /// <summary>Part of Windows or its security; should stay on.</summary>
    KeepWindows,

    /// <summary>Hardware/driver helper (sound, graphics, touchpad...); safer to keep on.</summary>
    KeepHardware,

    /// <summary>The program it points to no longer exists, so it does nothing.</summary>
    Broken,
}

public sealed record StartupEntry(
    string Name,
    string Command,
    string? ExecutablePath,
    StartupLocation Location,
    bool IsEnabled,
    string? Publisher,
    bool ExecutableExists,
    StartupAdvice Advice);
