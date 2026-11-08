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

if (args.Contains("--windows-analysis") || args.Contains("--component-cleanup-ci"))
{
    // Used by CI: read-only DISM analysis, then (only with --component-cleanup-ci, on the throw-away runner)
    // the real component cleanup through SafeExecutor with a temporary journal.
    var log = Microsoft.Extensions.Logging.Abstractions.NullLogger<WindowsCleanupService>.Instance;
    var tmp = Path.Combine(Path.GetTempPath(), "ow-ci-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tmp);
    using var ciJournal = new SqliteJournal(Path.Combine(tmp, "journal.db"));
    var dism = new DismRunner();
    var ciExecutor = new SafeExecutor(ciJournal, new QuarantineStore(Path.Combine(tmp, "q")), PathGuard.CreateForCurrentMachine(),
        componentCleaner: new DismComponentCleaner(dism, Microsoft.Extensions.Logging.Abstractions.NullLogger<DismComponentCleaner>.Instance));
    var windows = new WindowsCleanupService(ciExecutor, ciJournal, dism, log);
    var report = windows.Analyze();
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    if (report.ComponentStoreBytes is null)
    {
        Console.WriteLine("Component store size could not be read: " + report.ComponentStoreError);
        return 1;
    }

    if (args.Contains("--component-cleanup-ci"))
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var result = ciExecutor.Execute(new ActionPlan("CI component cleanup", ActionOrigin.UserSelected, [new RunComponentCleanupAction()]));
        var op = ciJournal.GetOperations(result.BatchId).Single();
        Console.WriteLine($"Component cleanup: {op.State} in {clock.Elapsed.TotalMinutes:0.0} min, freed {result.BytesFreed / 1048576.0:0} MB: {op.Message}");
        var undo = ciExecutor.UndoBatch(result.BatchId);
        Console.WriteLine("Undo says: " + string.Join(" ", undo.Messages));
        if (op.State != OW.Core.Journal.OperationState.Done || undo.Restored != 0)
        {
            return 1;
        }
    }

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

        // Duplicate search on a throw-away folder (read-only), including a program folder that must be skipped.
        var dupDir = Path.Combine(Path.GetTempPath(), "ow-selftest-dup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dupDir, "Game"));
        try
        {
            var bytes = new byte[2 * 1024 * 1024];
            Random.Shared.NextBytes(bytes);
            File.WriteAllBytes(Path.Combine(dupDir, "a.jpg"), bytes);
            File.WriteAllBytes(Path.Combine(dupDir, "b.jpg"), bytes);
            File.WriteAllBytes(Path.Combine(dupDir, "Game", "c.jpg"), bytes);
            File.WriteAllText(Path.Combine(dupDir, "Game", "game.exe"), "x");
            var dups = OW.Core.Duplicates.DuplicateFinder.Find([dupDir]);
            Console.WriteLine($"Duplicate scan of {dupDir}: {dups.Groups.Count} group(s), {dups.FilesSeen} files, {dups.SkippedProgramFolders} program folder(s) skipped");
            if (dups.Groups.Count != 1 || dups.Groups[0].Files.Count != 2 || dups.SkippedProgramFolders != 1)
            {
                return 1;
            }
        }
        finally
        {
            Directory.Delete(dupDir, recursive: true);
        }

        // The real startup switch store, on a made-up name that no program uses, then removed again.
        var store = new RegistryStartupApprovalStore();
        var probe = new OW.Core.Startup.StartupApprovalTarget(OW.Core.Startup.StartupScope.Machine, null, "Run", "OW-SelfTest-" + Guid.NewGuid().ToString("N"));
        var before = store.Get(probe);
        store.Set(probe, OW.Core.Startup.StartupApprovalValues.For(false, DateTime.UtcNow));
        var off = store.Get(probe);
        store.Set(probe, null);
        var after = store.Get(probe);
        Console.WriteLine($"Startup switch store: before={before is null}, off={off?[0]}, removed={after is null}");
        if (before is not null || off is not { Length: 12 } || off[0] != 3 || after is not null)
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
builder.Services.AddSingleton(_ => new QuarantineStore(paths.QuarantineDirectory, paths.DriveQuarantineFor));
builder.Services.AddSingleton(_ => PathGuard.CreateForCurrentMachine());
builder.Services.AddSingleton(sp => new SafeExecutor(
    sp.GetRequiredService<SqliteJournal>(),
    sp.GetRequiredService<QuarantineStore>(),
    sp.GetRequiredService<PathGuard>(),
    sp.GetRequiredService<TimeProvider>(),
    sp.GetRequiredService<OW.Core.Startup.IStartupApprovalStore>(),
    sp.GetRequiredService<OW.Core.WindowsCleanup.IComponentStoreCleaner>()));
builder.Services.AddSingleton<DismRunner>();
builder.Services.AddSingleton<OW.Core.WindowsCleanup.IComponentStoreCleaner, DismComponentCleaner>();
builder.Services.AddSingleton<WindowsCleanupService>();
builder.Services.AddSingleton<SpaceService>();
builder.Services.AddSingleton<JunkService>();
builder.Services.AddSingleton<DuplicateService>();
builder.Services.AddSingleton<StartupService>();
builder.Services.AddSingleton<OW.Core.Startup.IStartupApprovalStore, RegistryStartupApprovalStore>();
builder.Services.AddSingleton<UpdateService>();
builder.Services.AddSingleton<OwServiceImpl>();
builder.Services.AddSingleton<ClientVerifier>();
builder.Services.AddHostedService<MaintenanceWorker>();
builder.Services.AddHostedService<PipeServer>();

builder.Build().Run();
return 0;
