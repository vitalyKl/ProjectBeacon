using Microsoft.JSInterop;

namespace ProjectBeacon.Web.Theme;
/// <summary>
/// Circuit theme state. Reads the beacon-theme cookie. Dark is the default. System follows the browser.
/// </summary>
public sealed class BeaconThemeState
{
    private readonly IJSRuntime _js;
    private readonly IHttpContextAccessor _http;

    public BeaconThemeState(IJSRuntime js, IHttpContextAccessor http)
    {
        _js = js;
        _http = http;
        var raw = http.HttpContext?.Request.Cookies[ThemePreferenceResolver.CookieName];
        Preference = ThemePreferenceResolver.Parse(raw);
        IsDark = ThemePreferenceResolver.Resolve(Preference, systemIsDark: true);
    }

    public ThemePreference Preference { get; private set; }

    public bool IsDark { get; private set; }

    public event Action? Changed;

    public void ApplySystem(bool systemIsDark)
    {
        if (Preference != ThemePreference.System || IsDark == systemIsDark)
            return;
        IsDark = systemIsDark;
        Changed?.Invoke();
    }

    public async Task AdoptAsync(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return;
        var parsed = ThemePreferenceResolver.Parse(raw);
        if (parsed == Preference)
            return;
        Preference = parsed;
        IsDark = parsed == ThemePreference.System
            ? await ReadSystemAsync()
            : parsed == ThemePreference.Dark;
        Changed?.Invoke();
    }

    public async Task SetPreferenceAsync(ThemePreference preference)
    {
        Preference = preference;
        IsDark = preference switch
        {
            ThemePreference.Light => false,
            ThemePreference.System => await ReadSystemAsync(),
            _ => true
        };
        await PersistAsync();
        Changed?.Invoke();
    }

    public Task ToggleExplicitAsync() =>
        SetPreferenceAsync(IsDark ? ThemePreference.Light : ThemePreference.Dark);

    private async Task<bool> ReadSystemAsync()
    {
        try
        {
            return await _js.InvokeAsync<bool>("beaconTheme.systemDark");
        }
        catch (JSException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
        catch (JSDisconnectedException)
        {
            return true;
        }
    }

    private async Task PersistAsync()
    {
        try
        {
            await _js.InvokeVoidAsync("beaconTheme.persist", ThemePreferenceResolver.Format(Preference));
        }
        catch (JSException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (JSDisconnectedException)
        {
        }
    }
}
