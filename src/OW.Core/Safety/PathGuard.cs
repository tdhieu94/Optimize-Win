namespace OW.Core.Safety;

/// <summary>Who decided that an action should touch a path.</summary>
public enum ActionOrigin
{
    /// <summary>A cleanup rule chose the path automatically (e.g. temp files).</summary>
    Rule,

    /// <summary>The user picked this exact path in the UI (e.g. a duplicate file).</summary>
    UserSelected,
}

/// <summary>How paths are written on the platform being guarded.</summary>
public enum PathStyle
{
    Windows,
    Unix,
}

/// <summary>Result of asking the <see cref="PathGuard"/> whether a path may be touched.</summary>
public readonly record struct PathCheck(bool Allowed, string? Reason)
{
    public static PathCheck Ok { get; } = new(true, null);

    public static PathCheck Deny(string reason) => new(false, reason);
}

/// <summary>
/// Last line of defence before any write: decides whether a path may be modified at all.
/// It is purely string based so the same rules can be unit tested on any OS.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>System roots (Windows, Program Files, ...) can never be touched, whoever asks.</item>
/// <item>User data roots (Documents, Pictures, ...) can only be touched when the user picked the path.</item>
/// <item>Relative paths, "..", wildcards, network paths and drive roots are always refused.</item>
/// </list>
/// </remarks>
public sealed class PathGuard
{
    private static readonly string[] PersonalFolderNames =
        ["Documents", "Desktop", "Pictures", "Videos", "Music", "Downloads", "OneDrive", "Favorites", "Contacts", "Saved Games"];

    private readonly PathStyle _style;
    private readonly char _sep;
    private readonly StringComparison _cmp;
    private readonly string[] _systemRoots;
    private readonly string[] _userDataRoots;

    public PathGuard(PathStyle style, IEnumerable<string> systemRoots, IEnumerable<string> userDataRoots)
    {
        _style = style;
        _sep = style == PathStyle.Windows ? '\\' : '/';
        _cmp = style == PathStyle.Windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        _systemRoots = NormalizeRoots(systemRoots);
        _userDataRoots = NormalizeRoots(userDataRoots);
    }

    /// <summary>Default guard for the machine the code runs on.</summary>
    public static PathGuard CreateForCurrentMachine()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new PathGuard(
                PathStyle.Unix,
                ["/bin", "/boot", "/dev", "/etc", "/lib", "/lib64", "/proc", "/sbin", "/sys", "/usr"],
                [Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)]);
        }

        var system = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "OptimizeWin"),
            @"C:\Boot",
            @"C:\Recovery",
            @"C:\System Volume Information",
            @"C:\$Recycle.Bin",
        };

        var user = new List<string>
        {
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        };

        // The service runs as SYSTEM, whose own "Documents" is not Hieu's. Protect the personal folders
        // of every profile on the machine, not just of the account running this code.
        var profiles = new List<string> { Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
        try
        {
            var usersRoot = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory)) is { } publicDir
                ? Path.GetDirectoryName(publicDir)
                : null;
            if (usersRoot is not null && Directory.Exists(usersRoot))
            {
                profiles.AddRange(Directory.GetDirectories(usersRoot));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        foreach (var profile in profiles.Where(p => !string.IsNullOrEmpty(p)))
        {
            user.AddRange(PersonalFolderNames.Select(name => Path.Combine(profile, name)));
        }

        return new PathGuard(PathStyle.Windows, system, user);
    }

    public PathCheck Check(string? path, ActionOrigin origin)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return PathCheck.Deny("Đường dẫn trống.");
        }

        if (path.IndexOfAny(['*', '?', '\0', '"', '<', '>', '|']) >= 0)
        {
            return PathCheck.Deny("Đường dẫn chứa ký tự không hợp lệ.");
        }

        // Checked on the raw path, before trailing dots are trimmed, so "C:\a\.." cannot slip through.
        var raw = _style == PathStyle.Windows ? path.Replace('/', '\\') : path;
        if (raw.Split(_sep).Any(s => s.Length > 0 && s.Trim().Trim('.').Length == 0 && s.Contains('.')))
        {
            return PathCheck.Deny("Đường dẫn không được chứa \".\" hoặc \"..\".");
        }

        var normalized = Normalize(path);

        if (_style == PathStyle.Windows)
        {
            if (normalized.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return PathCheck.Deny("Không thao tác trên ổ mạng hoặc đường dẫn đặc biệt.");
            }

            if (normalized.Length < 3 || !char.IsAsciiLetter(normalized[0]) || normalized[1] != ':' || normalized[2] != '\\')
            {
                return PathCheck.Deny("Đường dẫn phải là đường dẫn đầy đủ (ví dụ C:\\...).");
            }

            // A colon after the drive letter means an alternate data stream.
            if (normalized.IndexOf(':', 2) >= 0)
            {
                return PathCheck.Deny("Đường dẫn chứa ký tự không hợp lệ.");
            }

            // 8.3 short names (C:\PROGRA~1) could hide a protected folder behind another spelling.
            if (normalized.Split('\\').Any(IsShortNameSegment))
            {
                return PathCheck.Deny("Không dùng tên rút gọn kiểu 8.3 (ví dụ PROGRA~1); hãy dùng đường dẫn đầy đủ.");
            }
        }
        else if (!normalized.StartsWith('/'))
        {
            return PathCheck.Deny("Đường dẫn phải là đường dẫn đầy đủ.");
        }

        var segments = normalized.Split(_sep, StringSplitOptions.RemoveEmptyEntries);
        var minSegments = _style == PathStyle.Windows ? 2 : 1;
        if (segments.Length < minSegments)
        {
            return PathCheck.Deny("Không được thao tác trên cả ổ đĩa.");
        }

        foreach (var root in _systemRoots)
        {
            if (IsSameOrUnder(normalized, root))
            {
                return PathCheck.Deny($"Thư mục hệ thống được bảo vệ: {root}");
            }
        }

        if (origin == ActionOrigin.Rule)
        {
            foreach (var root in _userDataRoots)
            {
                if (IsSameOrUnder(normalized, root))
                {
                    return PathCheck.Deny($"Dữ liệu cá nhân chỉ được xử lý khi bạn tự chọn: {root}");
                }
            }
        }

        foreach (var root in _userDataRoots)
        {
            if (string.Equals(normalized, root, _cmp))
            {
                return PathCheck.Deny($"Không được thao tác trên cả thư mục cá nhân: {root}");
            }
        }

        return PathCheck.Ok;
    }

    internal string Normalize(string path)
    {
        var p = path.Trim();
        if (_style == PathStyle.Windows)
        {
            p = p.Replace('/', '\\');
            if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\\.\", StringComparison.Ordinal))
            {
                // Keep the prefix so Check() refuses device / long-path syntax explicitly.
                return p;
            }

            var unc = p.StartsWith(@"\\", StringComparison.Ordinal);
            while (p.Contains(@"\\", StringComparison.Ordinal))
            {
                p = p.Replace(@"\\", @"\", StringComparison.Ordinal);
            }

            if (unc)
            {
                p = @"\" + p;
            }

            // Windows silently drops trailing dots and spaces from every folder name,
            // so "C:\Windows.\x" and "C:\Windows \x" both mean "C:\Windows\x".
            var parts = p.Split('\\');
            for (var i = 0; i < parts.Length; i++)
            {
                if (parts[i].Length > 0 && parts[i].Trim().Trim('.').Length > 0)
                {
                    parts[i] = parts[i].TrimEnd('.', ' ');
                }
            }

            p = string.Join('\\', parts);
        }
        else
        {
            while (p.Contains("//", StringComparison.Ordinal))
            {
                p = p.Replace("//", "/", StringComparison.Ordinal);
            }
        }

        // Trailing separators and dots/spaces are ignored by Windows, so strip them to avoid bypasses.
        var trimChars = _style == PathStyle.Windows ? new[] { _sep, ' ', '.' } : new[] { _sep };
        while (p.Length > 1 && Array.IndexOf(trimChars, p[^1]) >= 0 && !(_style == PathStyle.Windows && p.Length == 3 && p[1] == ':'))
        {
            p = p[..^1];
        }

        if (_style == PathStyle.Windows && p.Length == 2 && p[1] == ':')
        {
            p += '\\';
        }

        return p;
    }

    private static bool IsShortNameSegment(string segment)
    {
        var tilde = segment.IndexOf('~');
        return tilde >= 0 && tilde + 1 < segment.Length && char.IsAsciiDigit(segment[tilde + 1]);
    }

    private string[] NormalizeRoots(IEnumerable<string> roots) =>
        roots.Where(r => !string.IsNullOrWhiteSpace(r))
             .Select(Normalize)
             .Distinct(StringComparer.FromComparison(_cmp))
             .ToArray();

    private bool IsSameOrUnder(string path, string root)
    {
        if (string.Equals(path, root, _cmp))
        {
            return true;
        }

        var prefix = root.EndsWith(_sep) ? root : root + _sep;
        return path.StartsWith(prefix, _cmp);
    }
}
