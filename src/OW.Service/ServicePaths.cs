using System.Security.AccessControl;
using System.Security.Principal;

namespace OW.Service;

/// <summary>Where the service keeps its journal and quarantine: %ProgramData%\OptimizeWin.</summary>
public sealed class ServicePaths
{
    private ServicePaths(string dataDirectory)
    {
        DataDirectory = dataDirectory;
        JournalFile = Path.Combine(dataDirectory, "journal.db");
        QuarantineDirectory = Path.Combine(dataDirectory, "Quarantine");
        UpdatesDirectory = Path.Combine(dataDirectory, "Updates");
    }

    /// <summary>Downloaded updates and the backup of the previous version (locked down like the rest).</summary>
    public string UpdatesDirectory { get; }

    public string DataDirectory { get; }

    public string JournalFile { get; }

    public string QuarantineDirectory { get; }

    public static ServicePaths CreateDefault()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OptimizeWin");
        CreateLockedDown(dir);
        CreateLockedDown(Path.Combine(dir, "Quarantine"));
        CreateLockedDown(Path.Combine(dir, "Updates"));
        return new ServicePaths(dir);
    }

    private readonly HashSet<string> _driveQuarantines = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _driveGate = new();

    /// <summary>
    /// The quarantine folder on the drive of <paramref name="path"/> (for example E:\OptimizeWin.Quarantine),
    /// created hidden and locked down the first time. Null for drives that are not local fixed drives.
    /// </summary>
    public string? DriveQuarantineFor(string path)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(path));
            if (string.IsNullOrEmpty(root) || root.Length != 3 || root[1] != ':')
            {
                return null;
            }

            var drive = new DriveInfo(root);
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
            {
                return null;
            }

            var dir = Path.Combine(drive.RootDirectory.FullName, OW.Core.Execution.QuarantineStore.DriveFolderName);
            lock (_driveGate)
            {
                if (_driveQuarantines.Add(dir))
                {
                    try
                    {
                        CreateLockedDown(dir);
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException or InvalidOperationException or IOException)
                    {
                        // A drive without permissions (exFAT/FAT32): still keep files on that drive, just not locked down.
                        Directory.CreateDirectory(dir);
                    }

                    var info = new DirectoryInfo(dir);
                    info.Attributes |= FileAttributes.Hidden | FileAttributes.System;
                }
            }

            return dir;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Only SYSTEM and Administrators may read or change OW's data. Ordinary programs cannot
    /// tamper with the journal or read quarantined files.
    /// </summary>
    private static void CreateLockedDown(string path)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
        {
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(sid, null),
                FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        var info = new DirectoryInfo(path);
        if (info.Exists)
        {
            info.SetAccessControl(security);
        }
        else
        {
            info.Create(security);
        }
    }
}
