using QRCoder;

namespace Wisp.Android;

/// <summary>Renders the temporary-link URI as a QR code PNG.</summary>
internal static class QrCodeRenderer
{
    public static ImageSource Create(string value, int pixelsPerModule = 10)
    {
        using var generator = new QRCodeGenerator();
        // Low error-correction keeps the module count down: the temporary-link
        // URI is long, and the code must scan reliably at ~230 dp on a phone
        // screen. The screen itself has clean contrast, so L is adequate.
        using var data = generator.CreateQrCode(value, QRCodeGenerator.ECCLevel.L);
        var png = new PngByteQRCode(data).GetGraphic(Math.Clamp(pixelsPerModule, 4, 16));
        return ImageSource.FromStream(() => new MemoryStream(png));
    }
}
