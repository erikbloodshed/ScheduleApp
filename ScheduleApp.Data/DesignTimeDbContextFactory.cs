using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ScheduleApp.Data;

/// <summary>
/// Lets "dotnet ef migrations add ..." / "dotnet ef database update" run against
/// this project directly. The connection string here is only used by the EF Core
/// CLI tools -- the running app reads its own from ScheduleApp.Wpf/appsettings.json.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<ScheduleDbContext>
{
    public ScheduleDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("SCHEDULEAPP_CONNECTION")
            ?? @"Server=.\SQLEXPRESS;Database=ScheduleAppDb;Trusted_Connection=True;TrustServerCertificate=True;";

        var optionsBuilder = new DbContextOptionsBuilder<ScheduleDbContext>();
        optionsBuilder.UseSqlServer(connectionString);

        return new ScheduleDbContext(optionsBuilder.Options);
    }
}
