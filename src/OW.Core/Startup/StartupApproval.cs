namespace OW.Core.Startup;

/// <summary>Whose startup switch: the signed-in user's (HKEY_USERS\{sid}) or the machine's (HKLM).</summary>
public enum StartupScope
{
    User,
    Machine,
}

/// <summary>
/// One on/off switch in Windows' own "StartupApproved" list, the same switch Task Manager flips.
/// Turning a program off this way removes nothing: the Run entry or shortcut stays where it is.
/// </summary>
/// <param name="Group">"Run", "Run32" or "StartupFolder" (the subkey under ...\Explorer\StartupApproved).</param>
/// <param name="ValueName">The Run value name, or the shortcut's file name for the Startup folder.</param>
public sealed record StartupApprovalTarget(StartupScope Scope, string? UserSid, string Group, string ValueName)
{
    public const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

    public static readonly IReadOnlySet<string> Groups = new HashSet<string>(StringComparer.Ordinal) { "Run", "Run32", "StartupFolder" };

    /// <summary>Text kept in the journal so undo knows exactly which switch to put back.</summary>
    public string Encode() => $"startup|{Scope}|{UserSid ?? "-"}|{Group}|{ValueName}";

    public static StartupApprovalTarget? Decode(string text)
    {
        var parts = text.Split('|', 5);
        if (parts.Length != 5 || parts[0] != "startup" || !Enum.TryParse<StartupScope>(parts[1], out var scope))
        {
            return null;
        }

        var target = new StartupApprovalTarget(scope, parts[2] == "-" ? null : parts[2], parts[3], parts[4]);
        return target.Validate() is null ? target : null;
    }

    /// <summary>Null when the target is well formed; otherwise why it is refused.</summary>
    public string? Validate()
    {
        if (!Groups.Contains(Group))
        {
            return "Chỉ được đổi công tắc khởi động của Windows.";
        }

        if (string.IsNullOrWhiteSpace(ValueName) || ValueName.Length > 260 || ValueName.IndexOfAny(['\\', '\0', '|']) >= 0)
        {
            return "Tên chương trình khởi động không hợp lệ.";
        }

        if (Scope == StartupScope.User && (UserSid is null || !UserSid.StartsWith("S-1-5-21-", StringComparison.Ordinal) || UserSid.Any(c => !(char.IsAsciiDigit(c) || c is '-' or 'S'))))
        {
            return "Không xác định được tài khoản Windows.";
        }

        if (Scope == StartupScope.Machine && UserSid is not null)
        {
            return "Công tắc của máy không gắn với tài khoản.";
        }

        return null;
    }
}

/// <summary>Reads and writes the StartupApproved values. The service uses the registry; tests use memory.</summary>
public interface IStartupApprovalStore
{
    /// <summary>The current value, or null when Windows has none (which means "on").</summary>
    byte[]? Get(StartupApprovalTarget target);

    /// <summary>Writes the value, or removes it when <paramref name="value"/> is null.</summary>
    void Set(StartupApprovalTarget target, byte[]? value);
}

public static class StartupApprovalValues
{
    /// <summary>
    /// The 12-byte value Task Manager writes: 02 = on, 03 = off, followed by the time it was turned off
    /// (FILETIME, zero when on).
    /// </summary>
    public static byte[] For(bool enabled, DateTime nowUtc)
    {
        var value = new byte[12];
        value[0] = enabled ? (byte)0x02 : (byte)0x03;
        if (!enabled)
        {
            BitConverter.GetBytes(nowUtc.ToFileTimeUtc()).CopyTo(value, 4);
        }

        return value;
    }

    public static string ToText(byte[]? value) => value is null ? "-" : Convert.ToHexString(value);

    public static byte[]? FromText(string? text) => string.IsNullOrEmpty(text) || text == "-" ? null : Convert.FromHexString(text);
}
