namespace ProjectBeacon.Web.Shared;

/// <summary>Logs unexpected errors outside a Blazor UI circuit and creates support correlation IDs.</summary>
public interface IUiDiagnosticService
{
    string LogException(Exception exception, string operation, object? context = null);
}
