using System.Security.Cryptography;

namespace ITSeti.Maintenance.Infrastructure;

public static class VerifiedPackageFile
{
    public static async Task SaveAsync(Stream input, string destination, long expectedSize, string expectedHash,
        CancellationToken cancellationToken = default)
    {
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".pending";
        try
        {
            using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long received = 0;
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[64 * 1024];
                int count;
                while ((count = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    received += count;
                    if (received > expectedSize) throw new InvalidDataException("Установщик больше объявленного размера.");
                    hasher.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                }
                await output.FlushAsync(cancellationToken);
            }
            if (received != expectedSize) throw new InvalidDataException("Загрузка установщика оборвалась.");
            if (!Convert.ToHexString(hasher.GetHashAndReset()).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException("SHA-256 установщика не совпадает с серверным каталогом.");
            File.Move(temporary, destination, true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
