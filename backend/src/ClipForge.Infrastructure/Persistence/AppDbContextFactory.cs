using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ClipForge.Infrastructure.Persistence;

/// <summary>Design-time factory used by `dotnet ef` (migrations). Reads ConnectionStrings__Default or uses the local default.</summary>
public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public const string DefaultLocalConnectionString =
        "Host=localhost;Port=5432;Database=clipforge;Username=clipforge;Password=clipforge";

    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Default") ?? DefaultLocalConnectionString;
        var options = new DbContextOptionsBuilder<AppDbContext>();
        Configure(options, connectionString);
        return new AppDbContext(options.Options);
    }

    public static void Configure(DbContextOptionsBuilder options, string connectionString)
    {
        options.UseNpgsql(connectionString, npgsql =>
            {
                npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.GetName().Name);

            })
            .UseSnakeCaseNamingConvention();
    }
}
