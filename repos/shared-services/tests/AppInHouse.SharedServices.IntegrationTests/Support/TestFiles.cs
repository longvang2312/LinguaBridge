using SkiaSharp;

namespace AppInHouse.SharedServices.IntegrationTests.Support;

/// <summary>Files for upload tests: real images (made with SkiaSharp) and files that only look like something.</summary>
public static class TestFiles
{
    public static byte[] Image(SKEncodedImageFormat format, int width, int height)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.OrangeRed);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }

    public static byte[] Png(int width = 640, int height = 480) => Image(SKEncodedImageFormat.Png, width, height);
    public static byte[] Jpeg(int width = 640, int height = 480) => Image(SKEncodedImageFormat.Jpeg, width, height);

    /// <summary>A PDF header followed by filler: enough to be recognised as a PDF.</summary>
    public static byte[] Pdf(int total = 2048)
    {
        var bytes = new byte[total];
        "%PDF-1.7\n"u8.CopyTo(bytes);
        for (var i = 9; i < total; i++) bytes[i] = (byte)(i % 200 + 32);
        return bytes;
    }

    /// <summary>A Windows executable (starts with "MZ") that a client may call image/png.</summary>
    public static byte[] Executable() => [.. "MZ\u0090\0\u0003\0\0\0"u8, .. new byte[600]];

    public static byte[] Script() => "#!/bin/sh\necho owned\n"u8.ToArray();
}
