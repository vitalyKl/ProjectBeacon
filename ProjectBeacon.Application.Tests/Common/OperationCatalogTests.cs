namespace ProjectBeacon.Application.Tests.Common;

using ProjectBeacon.Application.Common;
using ProjectBeacon.Application.Devices;
using ProjectBeacon.Domain.Enums;
using Xunit;

public sealed class OperationCatalogTests
{
    [Fact]
    public void Catalog_HasUniqueStableIdsAndValidVersions()
    {
        var operations = OperationCatalog.All;
        Assert.NotEmpty(operations);
        Assert.Equal(operations.Count, operations.Select(operation => operation.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(operations, operation =>
        {
            Assert.False(string.IsNullOrWhiteSpace(operation.Id));
            Assert.True(operation.Version > 0);
            Assert.NotEqual(OperationSurface.None, operation.Surfaces);
        });
    }

    [Fact]
    public void Catalog_ContainsEveryWorkstationCommand()
    {
        var operations = OperationCatalog.All
            .Where(operation => operation.WorkstationCommand.HasValue)
            .ToDictionary(operation => operation.WorkstationCommand!.Value);

        Assert.Equal(Enum.GetValues<WorkstationCommandKind>().Length, operations.Count);
        foreach (var command in WorkstationCommandCatalog.All)
        {
            Assert.True(operations.TryGetValue(command.Kind, out var operation));
            Assert.Equal(command.OperationId, operation!.Id);
            Assert.Equal(command.Version, operation.Version);
        }
    }

    [Fact]
    public void Catalog_RejectsUnknownOperationIds()
    {
        Assert.Throws<KeyNotFoundException>(() => OperationCatalog.Get("not.registered"));
    }
}
