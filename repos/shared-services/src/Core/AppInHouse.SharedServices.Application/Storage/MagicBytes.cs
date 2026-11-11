namespace AppInHouse.SharedServices.Application.Storage;

/// <summary>
/// Tells what a file really is from its first bytes, whatever name or Content-Type the client gave it. Only the types a project may keep are
/// known; anything else (an executable, a script, an archive, an SVG that can carry script) is unknown and refused.
/// </summary>
public static class MagicBytes
{
    /// <summary>How many bytes <see cref="Detect"/> needs to see.</summary>
    public const int HeaderLength = 16;

    // Spelled as bytes: a "..."u8 literal would be written as UTF-8, where 0x89 becomes two bytes.
    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static string? Detect(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF) return "image/jpeg";
        if (header.StartsWith(PngSignature)) return "image/png";
        if (header.StartsWith("GIF87a"u8) || header.StartsWith("GIF89a"u8)) return "image/gif";
        if (header.StartsWith("%PDF-"u8)) return "application/pdf";
        if (header.StartsWith("OggS"u8)) return "audio/ogg";

        if (header.Length >= 12 && header[..4].SequenceEqual("RIFF"u8))
        {
            var kind = header.Slice(8, 4);
            if (kind.SequenceEqual("WEBP"u8)) return "image/webp";
            if (kind.SequenceEqual("WAVE"u8)) return "audio/wav";
        }

        // MP3: an ID3 tag, or the 11-bit frame sync with a real layer (layer bits 00 are reserved).
        if (header.StartsWith("ID3"u8)) return "audio/mpeg";
        if (header.Length >= 2 && header[0] == 0xFF && (header[1] & 0xE0) == 0xE0 && (header[1] & 0x06) != 0) return "audio/mpeg";

        return null;
    }

    /// <summary>
    /// Whether the type the client claimed fits what the bytes are. No claim (or the generic octet-stream) fits anything; the usual
    /// spellings of a type fit it ("image/jpg", "audio/x-wav"); anything else (a PNG claimed for a JPEG, an .exe claimed as an image) does not.
    /// </summary>
    public static bool DeclaredFits(string? declared, string detected)
    {
        if (string.IsNullOrWhiteSpace(declared)) return true;

        var type = declared.Split(';')[0].Trim().ToLowerInvariant();
        if (type is "" or "application/octet-stream" or "binary/octet-stream") return true;

        var canonical = type switch
        {
            "image/jpg" or "image/pjpeg" => "image/jpeg",
            "image/x-png" => "image/png",
            "audio/mp3" or "audio/x-mpeg" or "audio/x-mp3" => "audio/mpeg",
            "audio/x-wav" or "audio/wave" or "audio/vnd.wave" => "audio/wav",
            "application/ogg" => "audio/ogg",
            "application/x-pdf" => "application/pdf",
            _ => type,
        };
        return canonical == detected;
    }
}
