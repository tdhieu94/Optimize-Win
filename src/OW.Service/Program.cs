using OW.Core.Execution;
using OW.Core.Journal;
using OW.Core.Safety;
using OW.Service;

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
