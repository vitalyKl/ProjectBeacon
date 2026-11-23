namespace ProjectBeacon.Web.Shared;

using Microsoft.Extensions.Localization;

public static class UiErrorMessages
{
    public static string WithDiagnosticId(
        this IStringLocalizer localizer,
        string messageKey,
        string diagnosticId) =>
        $"{localizer[messageKey].Value}{Environment.NewLine}{localizer["DiagnosticId", diagnosticId].Value}";
}
