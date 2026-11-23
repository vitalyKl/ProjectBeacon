namespace ProjectBeacon.Web.Shared;

using Microsoft.Extensions.Localization;
using ProjectBeacon.Web.Resources;

public static class UiErrorMessages
{
    public static string WithDiagnosticId(
        this IStringLocalizer<Web> localizer,
        string messageKey,
        string diagnosticId) =>
        $"{localizer[messageKey].Value}{Environment.NewLine}{localizer["DiagnosticId", diagnosticId].Value}";
}
