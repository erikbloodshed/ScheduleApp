using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting.WindowsServices;
using ScheduleApp.Core.Attendance;
using ScheduleApp.Core.Configuration;
using ScheduleApp.Data;
using ScheduleApp.Data.Attendance;
using ScheduleApp.PushListener.Services;
using Serilog;
using Serilog.Formatting.Compact;

// Same source name Event Viewer needs -- see this project's README, "Deployment" step 2
// (the event source has to exist before a non-admin service account can write to it).
const string EventLogSourceName = "ScheduleAppPushListener";

// Bootstrap logger: console-only, used only if something throws before the real, configured
// logger further down gets built (e.g. a malformed connection string, the port already in
// use). Deliberately minimal -- anything fancier here is one more thing that could itself
// fail before logging even works.
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // Optional, machine-wide override -- see SharedConfigFile's own doc comment. Added
    // after CreateBuilder's own default sources (appsettings.json, environment variables,
    // command-line args), so it wins over all of them if present; a machine that hasn't
    // set it up yet just keeps using ConnectionStrings:ScheduleDb from this project's own
    // appsettings.json, same as before this existed. Must run before anything below reads
    // builder.Configuration.
    builder.Configuration.AddJsonFile(SharedConfigFile.ResolvePath(), optional: true, reloadOnChange: false);

    // Lets this run as a proper Windows Service (sc.exe create / dotnet publish + register) so it
    // survives reboots and needs no one logged in -- the whole point of push is that it keeps
    // working whether or not anyone ever opens ScheduleApp.Desktop. When run as a service this
    // just changes how the host lifetime is managed; running "dotnet run" at a console still
    // works exactly the same as before for local testing.
    builder.Host.UseWindowsService();

    // Replaces the plain Microsoft.Extensions.Logging + Console.WriteLine setup this project
    // started with. Console.WriteLine (and plain console ILogger output) goes nowhere once this
    // runs as a service -- there's no console attached -- so every sink below writes somewhere
    // durable instead: a rotating JSON file always, plus Event Log when service-hosted.
    // MinimumLevel/Override come from appsettings.json's "Serilog" section (ReadFrom.Configuration
    // below), so Development vs Production verbosity still works the same way the old "Logging"
    // section did -- see appsettings.Development.json.
    builder.Host.UseSerilog((context, _, loggerConfig) =>
    {
        loggerConfig
            .ReadFrom.Configuration(context.Configuration)
            .Enrich.FromLogContext()
            .WriteTo.Console()
            // Rolls daily and by size, keeps a bounded number of files -- this is the same fix
            // RawUploadLogger got (see that class) applied to the structured log too, so neither
            // one grows forever on a service meant to run for months unattended.
            // CompactJsonFormatter (one JSON object per line, real properties -- not
            // JsonFormatter's more verbose structure) means every structured field already
            // passed to LogInformation calls below (SerialNumber, Received, Duplicate, etc.)
            // round-trips as an actual JSON property, grep/jq-able per device or event type,
            // not just baked into a rendered message string.
            .WriteTo.File(
                new CompactJsonFormatter(),
                path: Path.Combine(AppContext.BaseDirectory, "logs", "pushlistener-.json"),
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: 50 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: 31);

        // Same condition the old Microsoft.Extensions.Logging.EventLog setup used: only wired
        // up when actually running as a service, a no-op for local `dotnet run`. Event Log
        // stays in the picture specifically because Event Viewer is still the first place this
        // project's README points you to when a device never shows up -- only the sinks around
        // it changed. manageEventSource: false matches the existing deployment step (the source
        // must already exist via an admin-run New-EventLog) -- letting Serilog try to create it
        // itself would need admin rights this service account deliberately doesn't have.
        if (WindowsServiceHelpers.IsWindowsService())
        {
            loggerConfig.WriteTo.EventLog(EventLogSourceName, manageEventSource: false);
        }
    });

    // Some ZKTeco firmware defaults the "ADMS / Cloud Server" setting to port 8080, others to
    // plain 80 -- check the device's own Comm -> Cloud Server -> Server Port field. Configurable
    // here rather than hardcoded (unlike the old AdmsServer prototype), since this is meant to
    // actually run unattended in production. Falls back to 8080 if unset.
    var listenUrl = builder.Configuration["Push:ListenUrl"] ?? "http://0.0.0.0:8080";
    builder.WebHost.UseUrls(listenUrl);

    // Same connection string ScheduleApp.Desktop/appsettings.json points at -- this is what
    // makes a pushed punch show up in Desktop's Punch Records grid and Generate Reports without
    // either side needing to know the other exists. Set this once via the shared config file
    // (see SharedConfigFile's own doc comment and this project's README, "Deployment" step 4)
    // instead of copy-pasting it into both projects' appsettings.json files -- that's exactly
    // the hand-sync-it-yourself failure mode the shared file exists to remove.
    var connectionString = builder.Configuration.GetConnectionString("ScheduleDb")
        ?? throw new InvalidOperationException(
            "Missing 'ConnectionStrings:ScheduleDb' -- set it in appsettings.json, or in " +
            $"{SharedConfigFile.DefaultPath} (see SharedConfigFile).");

    builder.Services.AddDbContext<ScheduleDbContext>(options =>
        options.UseSqlServer(connectionString,
            sql => sql.EnableRetryOnFailure(
                maxRetryCount: 5,
                maxRetryDelay: TimeSpan.FromSeconds(10),
                errorNumbersToAdd: null)));

    // The same repository ZkTecoAttendanceLogReader (the pull path) and the .dat import path
    // both already use -- AddLogsAsync's dedup-by-batch logic is shared, not reimplemented here.
    builder.Services.AddScoped<IAttendanceLogRepository, SqlAttendanceLogRepository>();

    builder.Services.AddSingleton<DeviceRegistry>();
    builder.Services.AddSingleton<RawUploadLogger>();

    builder.Services.AddControllers();

    var app = builder.Build();
    var startupLogger = app.Services.GetRequiredService<ILogger<Program>>();

    // ---------------------------------------------------------------------------------------
    // This listener deliberately does NOT call db.Database.Migrate() the way
    // ScheduleApp.Desktop's App.xaml.cs does. ScheduleApp.Desktop owns the schema; this process
    // only ever reads/writes rows within tables Desktop already created. Two independent
    // processes racing to apply migrations against the same database is exactly the kind of
    // thing worth avoiding, so this just checks and warns instead of acting.
    //
    // Note this only ever needs SELECT/INSERT on AttendanceLogs -- unlike ScheduleApp.Desktop
    // (which creates/migrates the schema and needs db_ddladmin for that), whatever SQL Server
    // login the service account uses here only needs db_datareader + db_datawriter on
    // ScheduleAppDb. See this project's README for the exact grant.
    // ---------------------------------------------------------------------------------------
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<ScheduleDbContext>();
        try
        {
            // Same reasoning as ScheduleApp.Desktop's App.xaml.cs: GetPendingMigrationsAsync()
            // is one of the EF Core APIs that doesn't automatically use the execution strategy
            // configured above (dotnet/efcore#27450), so wrap it explicitly to actually get a
            // retry if SQL Server Express is still starting up when this runs.
            var strategy = db.Database.CreateExecutionStrategy();
            var pending = (await strategy.ExecuteAsync(() => db.Database.GetPendingMigrationsAsync())).ToList();
            if (pending.Count > 0)
            {
                startupLogger.LogWarning(
                    "{PendingCount} pending EF Core migration(s) on ScheduleAppDb. This listener " +
                    "does not apply migrations itself -- run ScheduleApp.Desktop (or `dotnet ef " +
                    "database update`) at least once first, since it owns the schema.",
                    pending.Count);
            }
            else
            {
                startupLogger.LogInformation("Connected to ScheduleAppDb -- schema is up to date.");
            }
        }
        catch (Exception ex)
        {
            startupLogger.LogWarning(ex,
                "Could not verify ScheduleAppDb's schema. Check the connection string, that the " +
                "service account has a SQL Server login (see README), and that SQL Server Express " +
                "is running and reachable from this machine.");
        }
    }

    app.MapControllers();

#pragma warning disable CA1873 // Avoid potentially expensive logging
    startupLogger.LogInformation(
        "ScheduleApp.PushListener listening on {ListenUrl} -- waiting for device pushes at /iclock/*.",
        listenUrl);
#pragma warning restore CA1873 // Avoid potentially expensive logging

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "ScheduleApp.PushListener terminated unexpectedly during startup.");
}
finally
{
    // The file sink batches writes -- flush before the process actually exits so a crash or a
    // service stop doesn't drop the last few log lines.
    Log.CloseAndFlush();
}