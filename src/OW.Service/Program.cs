using OW.Core.Execution;
using OW.Core.Journal;
using OW.Core.Safety;
using OW.Service;

if (args.Contains("--self-test"))
{
    // Used by CI: read-only checks that must work on a real Windows machine.
    var disks = DiskInspector.GetDisks();
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(disks, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    return disks.Count > 0 ? 0 : 1;
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
builder.Services.AddSingleton<OwServiceImpl>();
builder.Services.AddSingleton<ClientVerifier>();
builder.Services.AddHostedService<MaintenanceWorker>();
builder.Services.AddHostedService<PipeServer>();

builder.Build().Run();
return 0;
