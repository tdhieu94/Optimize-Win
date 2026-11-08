using System.Runtime.Versioning;
using Microsoft.Win32;
using OW.Core.Startup;

namespace OW.Service;

/// <summary>
/// The real StartupApproved switches: HKEY_USERS\{sid}\... for the user's own programs, HKLM\... for every user's.
/// Only the fixed StartupApproved key is ever opened; the target's group and value name are validated first.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RegistryStartupApprovalStore : IStartupApprovalStore
{
    public byte[]? Get(StartupApprovalTarget target)
    {
        using var key = Open(target, writable: false);
        return key?.GetValue(target.ValueName) as byte[];
    }

    public void Set(StartupApprovalTarget target, byte[]? value)
    {
        using var key = Open(target, writable: true) ?? throw new IOException("Không mở được công tắc khởi động của Windows.");
        if (value is null)
        {
            key.DeleteValue(target.ValueName, throwOnMissingValue: false);
        }
        else
        {
            key.SetValue(target.ValueName, value, RegistryValueKind.Binary);
        }
    }

    private static RegistryKey? Open(StartupApprovalTarget target, bool writable)
    {
        if (target.Validate() is { } problem)
        {
            throw new UnauthorizedAccessException(problem);
        }

        var path = StartupApprovalTarget.ApprovedKey + target.Group;
        if (target.Scope == StartupScope.Machine)
        {
            using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            return writable ? hklm.CreateSubKey(path, writable: true) : hklm.OpenSubKey(path);
        }

        using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64);
        using var hive = users.OpenSubKey(target.UserSid!, writable) ?? throw new IOException("Tài khoản Windows này chưa đăng nhập nên chưa đổi được.");
        return writable ? hive.CreateSubKey(path, writable: true) : hive.OpenSubKey(path);
    }
}
