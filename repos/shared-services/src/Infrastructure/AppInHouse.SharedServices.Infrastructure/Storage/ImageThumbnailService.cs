using AppInHouse.SharedServices.Application.Storage;
using SkiaSharp;

namespace AppInHouse.SharedServices.Infrastructure.Storage;

/// <summary>
/// Makes WebP thumbnails with SkiaSharp (MIT licensed; the Linux native library is bundled by the NoDependencies package). The size is read
/// from the image header first (<c>SKCodec</c> does not decode the pixels) and an image beyond <see cref="ImageLimits"/> is refused before
/// any pixel memory is allocated; only the first frame is decoded; the metadata (EXIF, location) is not copied into the thumbnail.
/// </summary>
public sealed class ImageThumbnailService : IImageThumbnailService
{
    public Task<byte[]> CreateThumbnailAsync(ReadOnlyMemory<byte> image, string mimeType, CancellationToken cancellationToken = default) =>
        Task.Run(() => Create(image.ToArray()), cancellationToken);

    private static byte[] Create(byte[] image)
    {
        using var data = SKData.CreateCopy(image);
        using var codec = SKCodec.Create(data) ?? throw new ImageRejectedException("The file is not an image that can be read.");

        // The header says how big the image is without decoding it: refuse before the memory for its pixels is asked for.
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0) throw new ImageRejectedException("The file is not an image that can be read.");
        if (info.Width > ImageLimits.MaxDimension || info.Height > ImageLimits.MaxDimension || (long)info.Width * info.Height > ImageLimits.MaxPixels)
            throw new ImageRejectedException($"The image is {info.Width}x{info.Height} pixels; at most {ImageLimits.MaxDimension}x{ImageLimits.MaxDimension} is accepted.");

        // Decoding must reach the end of the image: a cut-off file is refused rather than turned into half a thumbnail.
        using var decoded = new SKBitmap(info);
        if (codec.GetPixels(decoded.Info, decoded.GetPixels()) != SKCodecResult.Success)
            throw new ImageRejectedException("The file is not an image that can be read (it may be damaged or cut short).");
        using var upright = Orient(decoded, codec.EncodedOrigin);

        var scale = Math.Min(1.0, (double)ImageLimits.ThumbnailSize / Math.Max(upright.Width, upright.Height));
        var target = new SKImageInfo(Math.Max(1, (int)Math.Round(upright.Width * scale)), Math.Max(1, (int)Math.Round(upright.Height * scale)));
        using var resized = upright.Resize(target, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)) ?? throw new ImageRejectedException("The image could not be resized.");
        using var picture = SKImage.FromBitmap(resized);
        using var encoded = picture.Encode(SKEncodedImageFormat.Webp, 80) ?? throw new ImageRejectedException("The thumbnail could not be encoded.");
        return encoded.ToArray();
    }

    /// <summary>Turns a photo the way its EXIF orientation says (the usual camera cases); the thumbnail itself carries no EXIF.</summary>
    private static SKBitmap Orient(SKBitmap bitmap, SKEncodedOrigin origin)
    {
        var degrees = origin switch
        {
            SKEncodedOrigin.BottomRight => 180,
            SKEncodedOrigin.RightTop => 90,
            SKEncodedOrigin.LeftBottom => 270,
            _ => 0,
        };
        if (degrees == 0) return bitmap.Copy();

        var turned = degrees == 180 ? new SKBitmap(bitmap.Width, bitmap.Height) : new SKBitmap(bitmap.Height, bitmap.Width);
        using var canvas = new SKCanvas(turned);
        canvas.Translate(turned.Width / 2f, turned.Height / 2f);
        canvas.RotateDegrees(degrees);
        canvas.Translate(-bitmap.Width / 2f, -bitmap.Height / 2f);
        using (var source = SKImage.FromBitmap(bitmap)) canvas.DrawImage(source, 0, 0, new SKSamplingOptions(SKFilterMode.Linear));
        return turned;
    }
}
