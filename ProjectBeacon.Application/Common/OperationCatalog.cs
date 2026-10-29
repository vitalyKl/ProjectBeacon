namespace ProjectBeacon.Application.Common;

using System.Reflection;
using Domain.Enums;
using ProjectBeacon.Application.Devices;

/// <summary>Surfaces through which an operation may be invoked. Visibility is not authorization.</summary>
[Flags]
public enum OperationSurface
{
    None = 0,
    Internal = 1,
    Api = 2,
    Web = 4,
    Mcp = 8,
    Workstation = 16,
    Cli = 32
}

public enum OperationExecutionTarget
{
    Application,
    Workstation,
    ModelProvider,
    CodeIndex
}

public enum OperationContextScope
{
    None,
    User,
    Org,
    Project,
    Task,
    Device,
    ProjectRuntime,
    Workspace
}

/// <summary>Describes safe retry behavior; it does not replace the durable command delivery policy.</summary>
public enum OperationRetrySemantics
{
    NotDeclared,
    ReadOnly,
    Idempotent,
    GuardedMutation,
    AtMostOnce,
    Reconciliation
}

/// <summary>
/// Explicit metadata for a public/stable operation ID. Unannotated legacy requests remain
/// discoverable but are marked internal and use a diagnostic legacy ID.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class OperationDefinitionAttribute(string id, string summary) : Attribute
{
    public string Id { get; } = string.IsNullOrWhiteSpace(id)
        ? throw new ArgumentException("Operation ID is required.", nameof(id))
        : id.Trim();

    public string Summary { get; } = string.IsNullOrWhiteSpace(summary)
        ? throw new ArgumentException("Operation summary is required.", nameof(summary))
        : summary.Trim();

    public int Version { get; init; } = 1;
    public OperationSurface Surfaces { get; init; } = OperationSurface.Internal;
    public OperationExecutionTarget Target { get; init; } = OperationExecutionTarget.Application;
    public OperationContextScope Scope { get; init; } = OperationContextScope.None;
    public OperationRetrySemantics RetrySemantics { get; init; } = OperationRetrySemantics.NotDeclared;
    public string SecurityPolicy { get; init; } = "Enforced by the trusted Application/host boundary.";
}

/// <summary>
/// Runtime descriptor connecting a stable operation identity to typed contracts and execution metadata.
/// The descriptor advertises a capability; it never grants permission to execute it.
/// </summary>
public sealed record OperationDescriptor(
    string Id,
    int Version,
    string Summary,
    Type RequestType,
    Type ResponseType,
    OperationSurface Surfaces,
    OperationExecutionTarget Target,
    OperationContextScope Scope,
    OperationRetrySemantics RetrySemantics,
    string SecurityPolicy,
    WorkstationCommandKind? WorkstationCommand = null,
    string? PayloadContract = null);

/// <summary>
/// Code-first operation catalog. CQRS requests are discovered from their contracts and can opt into
/// stable IDs/surfaces through <see cref="OperationDefinitionAttribute"/>. Workstation operations
/// are added from the explicit workstation command catalog.
/// </summary>
public static class OperationCatalog
{
    private static readonly IReadOnlyList<OperationDescriptor> Operations = Build();
    private static readonly IReadOnlyDictionary<string, OperationDescriptor> ById =
        Operations.ToDictionary(operation => operation.Id, StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<Type, OperationDescriptor> ByRequestType =
        Operations.Where(operation => operation.Target == OperationExecutionTarget.Application)
            .GroupBy(operation => operation.RequestType)
            .ToDictionary(group => group.Key, group => group.First());

    public static IReadOnlyCollection<OperationDescriptor> All => Operations.ToArray();

    public static OperationDescriptor Get(string id) =>
        ById.TryGetValue(id, out var descriptor)
            ? descriptor
            : throw new KeyNotFoundException($"Operation '{id}' is not registered.");

    public static bool TryGet(string id, out OperationDescriptor? descriptor) =>
        ById.TryGetValue(id, out descriptor);

    public static bool TryGetByRequest(Type requestType, out OperationDescriptor? descriptor) =>
        ByRequestType.TryGetValue(requestType, out descriptor);

    public static IReadOnlyList<OperationDescriptor> ForSurface(OperationSurface surface) =>
        Operations.Where(operation => (operation.Surfaces & surface) == surface).ToArray();

    private static IReadOnlyList<OperationDescriptor> Build()
    {
        var results = new List<OperationDescriptor>();
        var assembly = typeof(OperationCatalog).Assembly;
        foreach (var type in assembly.GetTypes().Where(type =>
                     type.IsClass && !type.IsAbstract && !type.ContainsGenericParameters))
        {
            var contracts = type.GetInterfaces()
                .Where(contract => contract.IsGenericType)
                .Where(contract =>
                    contract.GetGenericTypeDefinition() == typeof(ICommand<>)
                    || contract.GetGenericTypeDefinition() == typeof(IQuery<>))
                .ToArray();
            if (contracts.Length == 0)
                continue;

            var contract = contracts[0];
            var response = contract.GetGenericArguments()[0];
            var definition = type.GetCustomAttribute<OperationDefinitionAttribute>();
            var id = definition?.Id ?? LegacyId(type);
            var summary = definition?.Summary ?? $"Internal legacy operation for {type.Name}.";
            results.Add(new OperationDescriptor(
                id,
                definition?.Version ?? 1,
                summary,
                type,
                response,
                definition?.Surfaces ?? OperationSurface.Internal,
                definition?.Target ?? OperationExecutionTarget.Application,
                definition?.Scope ?? OperationContextScope.None,
                definition?.RetrySemantics ?? OperationRetrySemantics.NotDeclared,
                definition?.SecurityPolicy ?? "Enforced by the trusted Application/host boundary."));
        }

        foreach (var command in WorkstationCommandCatalog.All)
        {
            results.Add(new OperationDescriptor(
                command.OperationId,
                command.Version,
                command.Summary,
                typeof(string), // persisted payload JSON
                typeof(string), // result JSON
                OperationSurface.Workstation | OperationSurface.Internal,
                OperationExecutionTarget.Workstation,
                command.Scope == WorkstationCommandScope.ProjectRuntime
                    ? OperationContextScope.ProjectRuntime
                    : OperationContextScope.Device,
                command.RetrySemantics,
                command.SecurityPolicy,
                command.Kind,
                command.PayloadContract));
        }

        var invalid = results.Where(operation =>
            string.IsNullOrWhiteSpace(operation.Id)
            || operation.Version <= 0
            || string.IsNullOrWhiteSpace(operation.Summary)
            || operation.RequestType is null
            || operation.ResponseType is null
            || operation.Surfaces == OperationSurface.None).ToArray();
        if (invalid.Length > 0)
            throw new InvalidOperationException("Operation catalog contains incomplete descriptors.");

        var duplicates = results.GroupBy(operation => operation.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1).ToArray();
        if (duplicates.Length > 0)
            throw new InvalidOperationException(
                $"Duplicate operation IDs: {string.Join(", ", duplicates.Select(group => group.Key))}.");

        return results.OrderBy(operation => operation.Id, StringComparer.Ordinal).ToArray();
    }

    private static string LegacyId(Type type)
    {
        var root = typeof(OperationCatalog).Namespace!;
        var ns = type.Namespace ?? root;
        var scope = ns.StartsWith(root, StringComparison.Ordinal)
            ? ns[root.Length..].Trim('.').Replace('.', '-').ToLowerInvariant()
            : "external";
        var name = type.Name;
        if (name.EndsWith("Command", StringComparison.Ordinal))
            name = name[..^"Command".Length];
        else if (name.EndsWith("Query", StringComparison.Ordinal))
            name = name[..^"Query".Length];
        var slug = string.Concat(name.Select((character, index) =>
            char.IsUpper(character) && index > 0 ? "-" + char.ToLowerInvariant(character) : char.ToLowerInvariant(character).ToString()));
        return $"legacy.{scope}.{slug}";
    }
}
