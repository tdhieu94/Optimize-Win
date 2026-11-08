using System.Runtime.Versioning;
using Microsoft.Win32;
using OW.Contracts;
using OW.Core.Execution;
using OW.Core.Safety;
using OW.Core.Startup;

namespace OW.Service;

/// <summary>
/// Turns startup programs on or off with Windows' own switch, through <see cref="SafeExecutor"/> (journal + undo).
/// The UI only names an entry it saw; the service checks that this entry really exists for the caller and
/// refuses Windows' own entries.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class StartupService(SafeExecutor executor, ILogger<StartupService> logger)
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public StartupChangeResult SetEnabled(string? userSid, StartupChangeRequest request)
    {
        var target = new StartupApprovalTarget(
            request.MachineWide ? StartupScope.Machine : StartupScope.User,
            request.MachineWide ? null : userSid,
            request.Group ?? string.Empty,
            request.ValueName ?? string.Empty);
        if (target.Validate() is { } invalid)
        {
            return new StartupChangeResult(Guid.Empty, false, invalid);
        }

        var command = FindCommand(target);
        if (command is null)
        {
            return new StartupChangeResult(Guid.Empty, false, "Không tìm thấy chương trình này trong danh sách khởi động nữa. Hãy bấm \"Đọc lại danh sách\".");
        }

        var exe = StartupRules.ExtractExecutable(command, Environment.ExpandEnvironmentVariables);
        var advice = StartupRules.Advise(request.ValueName!, exe, null, exe is not null && File.Exists(exe), Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        if (!request.Enable && advice == StartupAdvice.KeepWindows)
        {
            return new StartupChangeResult(Guid.Empty, false, "Đây là thành phần của Windows hoặc phần mềm bảo vệ máy, OW không tắt.");
        }

        var name = target.Group == "StartupFolder" ? Path.GetFileNameWithoutExtension(target.ValueName) : target.ValueName;
        var title = (request.Enable ? "Bật khởi động: " : "Tắt khởi động: ") + name;
        var result = executor.Execute(new ActionPlan(title, ActionOrigin.UserSelected, [new SetStartupApprovalAction(target, request.Enable)]));
        logger.LogInformation("Startup {Action} {Target}: {Done} done, {Skipped} skipped, {Failed} failed", request.Enable ? "enable" : "disable", target.Encode(), result.Done, result.Skipped, result.Failed);

        if (result.Done == 1)
        {
            return new StartupChangeResult(result.BatchId, true, request.Enable
                ? $"Đã bật lại \"{name}\". Lần đăng nhập sau nó sẽ tự mở."
                : $"Đã tắt \"{name}\". Lần đăng nhập sau nó sẽ không tự mở nữa. Muốn bật lại thì bấm \"Bật\" hoặc \"Hoàn tác\" ở trang Lịch sử.");
        }

        return new StartupChangeResult(Guid.Empty, false, result.Skipped == 1
            ? $"\"{name}\" đã ở trạng thái này rồi."
            : $"Không đổi được \"{name}\". Hãy thử lại.");
    }

    /// <summary>The command this switch belongs to, read from the same place Windows reads it; null if there is none.</summary>
    private static string? FindCommand(StartupApprovalTarget target)
    {
        try
        {
            if (target.Group == "StartupFolder")
            {
                var folder = target.Scope == StartupScope.Machine
                    ? Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup)
                    : JunkService.ProfileDirectory(target.UserSid) is { } profile
                        ? Path.Combine(profile, "AppData", "Roaming", "Microsoft", "Windows", "Start Menu", "Programs", "Startup")
                        : null;
                var file = folder is null ? null : Path.Combine(folder, target.ValueName);
                return file is not null && Path.GetDirectoryName(file) == folder && File.Exists(file) ? file : null;
            }

            RegistryKey root;
            if (target.Scope == StartupScope.Machine)
            {
                root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, target.Group == "Run32" ? RegistryView.Registry32 : RegistryView.Registry64);
            }
            else
            {
                if (target.Group != "Run")
                {
                    return null;
                }

                using var users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64);
                root = users.OpenSubKey(target.UserSid!) ?? throw new IOException("Hive not loaded.");
            }

            using (root)
            using (var run = root.OpenSubKey(RunKey))
            {
                return run?.GetValue(target.ValueName) as string;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            return null;
        }
    }
}
