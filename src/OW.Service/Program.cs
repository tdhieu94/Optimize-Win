using OW.Core.Execution;
using OW.Core.Journal;
using OW.Core.Safety;
using OW.Service;

if (args is ["--scan-timing", var timingPath])
{
    // Used by CI: how fast a read-only space scan runs on a real Windows folder.
    var progress = new OW.Core.Space.SpaceScanProgress();
    using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(8));
    var task = Task.Run(() => OW.Core.Space.SpaceAnalyzer.Scan(timingPath, null, cts.Token, progress));
    while (!task.Wait(TimeSpan.FromSeconds(10)))
    {
        Console.WriteLine($"  ... {progress.Files} files, {progress.Folders} folders, at {progress.CurrentFolder}");
    }

    var timed = task.Result;
    var reportJson = System.Text.Json.JsonSerializer.Serialize(timed);
    Console.WriteLine($"Scanned {timingPath}: {timed.Root.FileCount} files, {timed.FolderCount} folders, {timed.Root.SizeBytes / 1073741824.0:0.0} GB, {timed.InaccessibleFolders} not readable, {timed.SkippedLinks} links skipped, {timed.Duration.TotalSeconds:0.0}s, {reportJson.Length / 1024} KB of JSON");
    return 0;
}

if (args.Contains("--self-test"))
{
    // Used by CI: read-only checks that must work on a real Windows machine.
    var disks = DiskInspector.GetDisks();
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(disks, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

    // Read-only space scan of the install folder, sent through JSON the same way the UI receives it.
    var installDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
    var scan = OW.Core.Space.SpaceAnalyzer.Scan(installDir);
    var json = System.Text.Json.JsonSerializer.Serialize(scan);
    Console.WriteLine($"Space scan of {installDir}: {scan.Root.SizeBytes / 1048576.0:0.0} MB, {scan.Root.FileCount} files, {scan.FolderCount} folders, {scan.Duration.TotalSeconds:0.00}s, {json.Length} bytes of JSON");
    foreach (var item in scan.Root.Children.Take(5))
    {
        Console.WriteLine($"  {item.SizeBytes / 1048576.0,8:0.0} MB  {item.Name}");
    }

    // Read-only junk scan for the account running the test (nothing is moved).
    if (OperatingSystem.IsWindows())
    {
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
        var junkScan = new JunkService(null!, TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<JunkService>.Instance).Scan(sid);
        Console.WriteLine($"Junk scan for {sid}: {junkScan.Note}");
        foreach (var c in junkScan.Categories)
        {
            Console.WriteLine($"  {c.Id}: {c.FileCount} files, {c.Bytes / 1048576.0:0.0} MB");
        }

        if (junkScan.Categories.Count == 0)
        {
            return 1;
        }
    }

    return disks.Count > 0 && scan.Root.FileCount > 0 ? 0 : 1;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(o => o.ServiceName = ServiceConstants.ServiceName);

var paths = ServicePaths.CreateDefault();
builder.Services.AddSingleton(paths);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(_ => new SqliteJournal(paths.JournalFile));
builder.Services.AddSingleton(_ => new QuarantineStore(paths.QuarantineDirectory));
builder.Services.AddSingleton(_ => PathGuard.CreateForCurrentMachine());
builder.Services.AddSingleton<SafeExecutor>();
builder.Services.AddSingleton<SpaceService>();
builder.Services.AddSingleton<JunkService>();
builder.Services.AddSingleton<UpdateService>();
builder.Services.AddSingleton<OwServiceImpl>();
builder.Services.AddSingleton<ClientVerifier>();
builder.Services.AddHostedService<MaintenanceWorker>();
builder.Services.AddHostedService<PipeServer>();

builder.Build().Run();
return 0;
