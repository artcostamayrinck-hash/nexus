using QRCoder;

namespace Nexus.Security.Pairing;

public interface IQrCodeService
{
    byte[] GeneratePng(string payload, int pixelsPerModule = 12);
}

public sealed class QrCodeGeneratorService : IQrCodeService
{
    public byte[] GeneratePng(string payload, int pixelsPerModule = 12)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payload);

        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        using var pngQr = new PngByteQRCode(data);
        return pngQr.GetGraphic(Math.Clamp(pixelsPerModule, 4, 32));
    }
}

