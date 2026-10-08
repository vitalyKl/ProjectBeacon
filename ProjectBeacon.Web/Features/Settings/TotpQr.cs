using QRCoder;

namespace ProjectBeacon.Web.Features.Settings;
/// <summary>
/// Renders an otpauth payload as a PNG data URL for the authenticator setup screen.
/// </summary>
public static class TotpQr
{
    public static string DataUrl(string payload)
    {
        using var generator = new QRCodeGenerator();
        var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.Q);
        var png = new PngByteQRCode(data);
        return "data:image/png;base64," + Convert.ToBase64String(png.GetGraphic(8));
    }
}
