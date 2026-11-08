using OW.Core.Execution;
using OW.Core.Journal;
using OW.Core.Safety;
using OW.Service;

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
builder.Services.AddSingleton<OwServiceImpl>();
builder.Services.AddSingleton<ClientVerifier>();
builder.Services.AddHostedService<MaintenanceWorker>();
builder.Services.AddHostedService<PipeServer>();

builder.Build().Run();
return 0;
