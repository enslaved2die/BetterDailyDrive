// Prepares the app's bundled cover art for upload via Spotify's "Upload Custom Playlist Cover Image"
// endpoint, which requires a base64-encoded JPEG no larger than 256 KB.
using System;
using System.IO;
using System.Linq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;

public static class CoverImage
{
    private const int MaxBase64Chars = 256 * 1024;

    public static string? GetJpegBase64()
    {
        try
        {
            var assembly = typeof(CoverImage).Assembly;
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("cover.png", StringComparison.OrdinalIgnoreCase));
            if (resourceName == null)
            {
                Console.WriteLine("Cover image resource not found in assembly.");
                return null;
            }

            using var resourceStream = assembly.GetManifestResourceStream(resourceName);
            if (resourceStream == null) return null;

            using var image = Image.Load(resourceStream);

            // Step down JPEG quality until the base64 payload fits Spotify's 256 KB limit.
            for (var quality = 85; quality >= 35; quality -= 10)
            {
                using var ms = new MemoryStream();
                image.Save(ms, new JpegEncoder { Quality = quality });
                var base64 = Convert.ToBase64String(ms.ToArray());
                if (base64.Length <= MaxBase64Chars) return base64;
            }

            Console.WriteLine("Could not encode cover image under Spotify's 256 KB limit.");
            return null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Could not prepare playlist cover image: {ex.Message}");
            return null;
        }
    }
}
