using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ProjectBeacon.Infrastructure;
using ProjectBeacon.Infrastructure.Data;
using ProjectBeacon.Worker.Services;

var builder = Host.CreateApplicationBuilder(args);

var connectionString = PostgresConnection.Resolve(builder.Configuration);
builder.Services.AddInfrastructure(connectionString);
builder.Services.AddHostedService<ExpiredRecordsCleanupService>();

var host = builder.Build();
host.Run();
