using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;

namespace ITSeti.Server;

public sealed record PackageUpload(string Key, string Version, string Name, string Platform, string FileName, long Length, string Sha256);

/// <summary>Reads an admin multipart upload directly into its final package file without ASP.NET form buffering.</summary>
public static class PackageUploadReader
{
    private const int MaxFieldBytes = 1024;
    private const int MaxFields = 8;

    public static async Task<PackageUpload> ReadAsync(HttpRequest request, string destination, long maxFileBytes, CancellationToken cancellationToken)
    {
        if (request.ContentLength is > 0 && request.ContentLength > maxFileBytes + 16L * 1024 * 1024)
            throw new InvalidDataException("Multipart request exceeds the allowed size.");
        if (!MediaTypeHeaderValue.TryParse(request.ContentType, out var mediaType) ||
            !string.Equals(mediaType.MediaType.Value, "multipart/form-data", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Expected a multipart form upload.");

        var boundary = HeaderUtilities.RemoveQuotes(mediaType.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary) || boundary.Length > 128)
            throw new InvalidDataException("Multipart boundary is missing or invalid.");

        var reader = new MultipartReader(boundary, request.Body)
        {
            HeadersCountLimit = 16,
            HeadersLengthLimit = 16 * 1024
        };
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        string? fileName = null;
        long fileLength = 0;
        string? sha256 = null;

        try
        {
        MultipartSection? section;
        while ((section = await reader.ReadNextSectionAsync(cancellationToken)) is not null)
        {
            if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition) ||
                !string.Equals(disposition.DispositionType.Value, "form-data", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Multipart section has an invalid content disposition.");

            var name = HeaderUtilities.RemoveQuotes(disposition.Name).Value;
            if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("Multipart field name is missing.");

            var rawFileName = HeaderUtilities.RemoveQuotes(disposition.FileNameStar.HasValue ? disposition.FileNameStar : disposition.FileName).Value;
            if (rawFileName is not null)
            {
                if (!string.Equals(name, "file", StringComparison.Ordinal) || fileName is not null)
                    throw new InvalidDataException("Only one file field is supported.");
                fileName = Path.GetFileName(rawFileName.Replace('\\', '/'));
                if (string.IsNullOrWhiteSpace(fileName)) fileName = "package.bin";

                using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                var buffer = new byte[64 * 1024];
                int read;
                while ((read = await section.Body.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    fileLength += read;
                    if (fileLength > maxFileBytes) throw new InvalidDataException("Uploaded file exceeds its size limit.");
                    hasher.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                sha256 = Convert.ToHexString(hasher.GetHashAndReset());
                continue;
            }

            if (name is not ("key" or "version" or "name" or "platform") || fields.Count >= MaxFields || fields.ContainsKey(name))
                throw new InvalidDataException("Multipart form contains an unexpected or duplicate field.");
            fields.Add(name, await ReadFieldAsync(section.Body, cancellationToken));
        }

        if (fileName is null || fileLength == 0 || sha256 is null)
            throw new InvalidDataException("Multipart form must contain one non-empty file.");
        return new PackageUpload(
            fields.GetValueOrDefault("key", string.Empty),
            fields.GetValueOrDefault("version", string.Empty),
            fields.GetValueOrDefault("name", string.Empty),
            fields.GetValueOrDefault("platform", string.Empty),
            fileName, fileLength, sha256);
        }
        catch
        {
            try { File.Delete(destination); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    private static async Task<string> ReadFieldAsync(Stream body, CancellationToken cancellationToken)
    {
        using var content = new MemoryStream();
        var buffer = new byte[256];
        int read;
        while ((read = await body.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (content.Length + read > MaxFieldBytes) throw new InvalidDataException("Multipart field exceeds its size limit.");
            await content.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        return new UTF8Encoding(false, true).GetString(content.GetBuffer(), 0, checked((int)content.Length));
    }
}
