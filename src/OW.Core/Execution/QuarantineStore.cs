namespace OW.Core.Execution;

/// <summary>
/// Holds files that OW "deleted" so they can be put back. Files live under
/// <c>{root}\{batchId}\{operationId}.owq</c> until the retention period ends.
/// </summary>
/// <remarks>
/// A file is always kept on its own drive: moving it is then only a rename, so quarantining a big file on
/// D: never fills up C:. Files on the main root's drive use <see cref="RootDirectory"/>; files on other
/// drives use the folder that <c>sameDriveRoot</c> names for that drive. A move to another drive is refused.
/// </remarks>
public sealed class QuarantineStore
{
    private readonly Func<string, string?>? _sameDriveRoot;

    /// <param name="sameDriveRoot">For a file path on another drive than <paramref name="rootDirectory"/>, the quarantine folder to use on that drive (or null if none).</param>
    public QuarantineStore(string rootDirectory, Func<string, string?>? sameDriveRoot = null)
    {
        RootDirectory = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(RootDirectory);
        _sameDriveRoot = sameDriveRoot;
    }

    public string RootDirectory { get; }

    /// <summary>Name of the quarantine folder OW keeps at the top of each other drive (D:\OptimizeWin.Quarantine).</summary>
    public const string DriveFolderName = "OptimizeWin.Quarantine";

    private static StringComparison Cmp => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Where a file for this operation will be kept. Computed before the move so the journal can record it first.</summary>
    public string GetBackupPath(Guid batchId, Guid operationId) =>
        Path.Combine(RootDirectory, batchId.ToString("N"), operationId.ToString("N") + ".owq");

    /// <summary>Same, but on the drive of <paramref name="sourcePath"/>.</summary>
    public string GetBackupPath(Guid batchId, Guid operationId, string sourcePath) =>
        Path.Combine(RootFor(sourcePath), batchId.ToString("N"), operationId.ToString("N") + ".owq");

    /// <summary>The quarantine folder for a file: the main one if it is on the same drive, else that drive's own.</summary>
    public string RootFor(string sourcePath)
    {
        if (SameDrive(sourcePath, RootDirectory) || _sameDriveRoot is null)
        {
            return RootDirectory;
        }

        return _sameDriveRoot(sourcePath) is { } root ? Path.GetFullPath(root) : RootDirectory;
    }

    /// <summary>Moves <paramref name="sourcePath"/> into quarantine and checks that nothing was lost on the way.</summary>
    public void MoveIn(string sourcePath, string backupPath, long expectedSize)
    {
        if (!IsInsideRoot(backupPath))
        {
            throw new InvalidOperationException("Từ chối: chỗ cất file không nằm trong vùng cách ly.");
        }

        if (!SameDrive(sourcePath, backupPath))
        {
            throw new IOException("Vùng cách ly nằm trên ổ khác với file nên không dọn (để tránh làm đầy ổ). File vẫn ở chỗ cũ.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
        File.Move(sourcePath, backupPath, overwrite: false);

        var moved = new FileInfo(backupPath);
        if (!moved.Exists || moved.Length != expectedSize)
        {
            throw new IOException("Kích thước file trong vùng cách ly không khớp với file gốc.");
        }
    }

    /// <summary>
    /// Puts a quarantined file back. If something already exists at the original path,
    /// the file is restored next to it under a new name instead of overwriting anything.
    /// </summary>
    /// <returns>The path the file was actually restored to.</returns>
    public string Restore(string backupPath, string originalPath)
    {
        if (!File.Exists(backupPath))
        {
            throw new FileNotFoundException("File trong vùng cách ly không còn nữa.", backupPath);
        }

        var dir = Path.GetDirectoryName(originalPath)!;
        Directory.CreateDirectory(dir);

        var destination = originalPath;
        var name = Path.GetFileNameWithoutExtension(originalPath);
        var ext = Path.GetExtension(originalPath);
        for (var i = 1; File.Exists(destination) || Directory.Exists(destination); i++)
        {
            if (i > 999)
            {
                throw new IOException("Không tìm được tên trống để khôi phục file.");
            }

            destination = Path.Combine(dir, $"{name} (OW khôi phục {i}){ext}");
        }

        File.Move(backupPath, destination, overwrite: false);
        TryDeleteEmptyDirectory(Path.GetDirectoryName(backupPath)!);
        return destination;
    }

    /// <summary>Permanently removes a quarantined copy.</summary>
    public void Purge(string backupPath)
    {
        if (!IsInsideRoot(backupPath))
        {
            throw new InvalidOperationException("Từ chối xóa file nằm ngoài vùng cách ly.");
        }

        if (File.Exists(backupPath))
        {
            File.SetAttributes(backupPath, FileAttributes.Normal);
            File.Delete(backupPath);
        }

        TryDeleteEmptyDirectory(Path.GetDirectoryName(backupPath)!);
    }

    public bool IsInsideRoot(string path)
    {
        var full = Path.GetFullPath(path);
        if (IsUnder(full, RootDirectory))
        {
            return true;
        }

        return _sameDriveRoot?.Invoke(full) is { } other && !SameDrive(full, RootDirectory) && IsUnder(full, Path.GetFullPath(other));
    }

    private static bool IsUnder(string full, string root)
    {
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, Cmp);
    }

    private static bool SameDrive(string a, string b) =>
        string.Equals(Path.GetPathRoot(Path.GetFullPath(a)), Path.GetPathRoot(Path.GetFullPath(b)), Cmp);

    private void TryDeleteEmptyDirectory(string dir)
    {
        try
        {
            if (IsInsideRoot(dir) && Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
            {
                Directory.Delete(dir);
            }
        }
        catch (IOException)
        {
            // Best effort only; an empty folder left behind is harmless.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
