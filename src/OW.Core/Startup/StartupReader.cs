using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace OW.Core.Startup;

/// <summary>
/// Lists programs that start at sign-in, the same places Task Manager's "Startup apps" looks at.
/// Read-only. Must run as the signed-in user (not the service) so "this user" entries are the right ones.
/// </summary>
[SupportedOSPlatform("windows")]
public static class StartupReader
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    public static IReadOnlyList<StartupEntry> Read()
    {
        var list = new List<StartupEntry>();
        var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        void Add(string name, string command, string? exe, StartupLocation location, bool enabled)
        {
            var exists = exe is not null && SafeExists(exe);
            var publisher = exists ? Publisher(exe!) : null;
            var advice = StartupRules.Advise(name, exe, publisher, exists, windowsDir);
            list.Add(new StartupEntry(name, command, exe, location, enabled, publisher, exists, advice));
        }

        ReadRun(RegistryHive.CurrentUser, RegistryView.Default, "Run", StartupLocation.UserRegistry, Add);
        ReadRun(RegistryHive.LocalMachine, RegistryView.Registry64, "Run", StartupLocation.MachineRegistry, Add);
        ReadRun(RegistryHive.LocalMachine, RegistryView.Registry32, "Run32", StartupLocation.MachineRegistry, Add);

        ReadFolder(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            RegistryHive.CurrentUser,
            StartupLocation.UserStartupFolder,
            Add);
        ReadFolder(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
            RegistryHive.LocalMachine,
            StartupLocation.CommonStartupFolder,
            Add);

        return list
            .OrderByDescending(e => e.IsEnabled)
            .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static void ReadRun(RegistryHive hive, RegistryView view, string approvedName, StartupLocation location, Action<string, string, string?, StartupLocation, bool> add)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var run = root.OpenSubKey(RunKey);
            if (run is null)
            {
                return;
            }

            using var approved = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64).OpenSubKey(ApprovedKey + approvedName);
            foreach (var name in run.GetValueNames())
            {
                if (string.IsNullOrWhiteSpace(name) || run.GetValue(name) is not string command || string.IsNullOrWhiteSpace(command))
                {
                    continue;
                }

                var exe = StartupRules.ExtractExecutable(command, Environment.ExpandEnvironmentVariables);
                add(name, command, exe, location, StartupRules.IsEnabled(approved?.GetValue(name) as byte[]));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // Not readable: show what we can.
        }
    }

    private static void ReadFolder(string folder, RegistryHive hive, StartupLocation location, Action<string, string, string?, StartupLocation, bool> add)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            return;
        }

        try
        {
            using var approved = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64).OpenSubKey(ApprovedKey + "StartupFolder");
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var fileName = Path.GetFileName(file);
                if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                add(Path.GetFileNameWithoutExtension(file), file, file, location, StartupRules.IsEnabled(approved?.GetValue(fileName) as byte[]));
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
        }
    }

    private static bool SafeExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static string? Publisher(string exe)
    {
        if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            var company = FileVersionInfo.GetVersionInfo(exe).CompanyName;
            return string.IsNullOrWhiteSpace(company) ? null : company.Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
