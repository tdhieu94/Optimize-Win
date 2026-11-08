namespace OW.Core.Safety;

public static class FileSafety
{
    /// <summary>
    /// True if the file itself or any folder above it is a symlink or junction.
    /// Following such a link could lead outside the folder the path appears to be in
    /// (for example a "Temp\x" junction that really points into C:\Windows), so OW refuses those paths.
    /// </summary>
    public static bool HasReparsePointOnPath(FileSystemInfo item)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.Attributes.HasFlag(FileAttributes.ReparsePoint) || item.LinkTarget is not null)
        {
            return true;
        }

        var dir = item is FileInfo f ? f.Directory : ((DirectoryInfo)item).Parent;
        while (dir is not null && dir.Parent is not null)
        {
            if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint) || dir.LinkTarget is not null)
            {
                return true;
            }

            dir = dir.Parent;
        }

        return false;
    }
}
