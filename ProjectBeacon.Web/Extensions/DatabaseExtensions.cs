namespace ProjectBeacon.Web.Extensions;

using Microsoft.EntityFrameworkCore;
using ProjectBeacon.Infrastructure.Data;

public static class DatabaseExtensions
{
    public static WebApplication MigrateDatabase(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<BeaconDbContext>();
        context.Database.Migrate();
        return app;
    }
}
