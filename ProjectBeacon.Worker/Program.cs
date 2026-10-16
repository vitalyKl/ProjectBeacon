using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProjectBeacon.Infrastructure;
using ProjectBeacon.Infrastructure.Data;
using ProjectBeacon.Worker.Services;

var builder = Host.CreateApplicationBuilder(args);

var connectionString = PostgresConnection.Resolve(builder.Configuration);
builder.Services.AddInfrastructure(connectionString);
builder.Services.AddHostedService<ExpiredRecordsCleanupService>();
builder.Services.AddHostedService<StaleCommandRecoveryService>();

var host = builder.Build();

// Fail-closed role guard: the Worker runs as the application role; refuse to start in
// Production when that role has SUPERUSER/BYPASSRLS or cannot be verified.
if (PostgresConnection.IsPostgresBacked(host.Services))
    await PostgresConnection.EnforceAppRoleAsync(
        connectionString,
        builder.Environment.IsProduction(),
        host.Services.GetRequiredService<ILogger<Program>>());

host.Run();
