using OW.Contracts;
using OW.Core.Space;

namespace OW.Service;

/// <summary>Runs read-only space scans for the UI, one at a time.</summary>
public sealed class SpaceService(ILogger<SpaceService> logger)
{
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    public async Task<SpaceReport> AnalyzeAsync(string driveLetter, CancellationToken cancellationToken)
    {
        var drive = ValidateDrive(driveLetter);

        await _oneAtATime.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            logger.LogInformation("Space scan of {Drive} started", drive.Name);
            var scan = await Task.Factory.StartNew(
                () => SpaceAnalyzer.Scan(drive.RootDirectory.FullName, null, cancellationToken),
                cancellationToken,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).ConfigureAwait(false);
            logger.LogInformation(
                "Space scan of {Drive} finished in {Seconds:0.0}s: {Folders} folders, {Denied} not readable",
                drive.Name,
                scan.Duration.TotalSeconds,
                scan.FolderCount,
                scan.InaccessibleFolders);

            return new SpaceReport(
                drive.Name[..1],
                drive.TotalSize,
                drive.TotalFreeSpace,
                ToItem(scan.Root),
                scan.LargestFiles.Select(ToItem).ToList(),
                scan.FolderCount,
                scan.InaccessibleFolders,
                scan.Duration.TotalSeconds);
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    /// <summary>Only a single letter naming a ready, local, fixed drive is accepted.</summary>
    internal static DriveInfo ValidateDrive(string? driveLetter)
    {
        if (driveLetter is not { Length: 1 } || !char.IsAsciiLetter(driveLetter[0]))
        {
            throw new ArgumentException("Chỉ nhận một chữ cái tên ổ, ví dụ C.", nameof(driveLetter));
        }

        var drive = new DriveInfo(char.ToUpperInvariant(driveLetter[0]) + @":\");
        if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
        {
            throw new ArgumentException($"Ổ {drive.Name} không phải ổ cứng trong máy hoặc chưa sẵn sàng.", nameof(driveLetter));
        }

        return drive;
    }

    private static SpaceItem ToItem(SpaceNode n) =>
        new(n.Name, n.Path, n.IsFolder, n.SizeBytes, n.FileCount, n.Children.Select(ToItem).ToList());
}
