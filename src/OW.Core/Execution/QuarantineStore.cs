namespace OW.Core.Execution;

/// <summary>
/// Holds files that OW "deleted" so they can be put back. Files live under
/// <c>{root}\{batchId}\{operationId}.owq</c> until the retention period ends.
/// </summary>
public sealed class QuarantineStore
{
    public QuarantineStore(string rootDirectory)
    {
        RootDirectory = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(RootDirectory);
    }

    public string RootDirectory { get; }

    /// <summary>Where a file for this operation will be kept. Computed before the move so the journal can record it first.</summary>
    public string GetBackupPath(Guid batchId, Guid operationId) =>
        Path.Combine(RootDirectory, batchId.ToString("N"), operationId.ToString("N") + ".owq");

    /// <summary>Moves <paramref name="sourcePath"/> into quarantine and checks that nothing was lost on the way.</summary>
    public void MoveIn(string sourcePath, string backupPath, long expectedSize)
    {
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
        var root = RootDirectory.EndsWith(Path.DirectorySeparatorChar) ? RootDirectory : RootDirectory + Path.DirectorySeparatorChar;
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return full.StartsWith(root, cmp);
    }

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
