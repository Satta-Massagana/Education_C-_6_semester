using System.IO.Compression;

namespace Lab_13;

public interface ICompressionAlgorithm
{
    CompressionAlgorithmType Type { get; }
    string FileExtension { get; }
    Task<byte[]> CompressAsync(
        byte[] input,
        string originalFileName,
        CancellationToken cancellationToken,
        Func<long, ValueTask>? progressCallback = null
    );
}

public sealed class GZipCompressionAlgorithm : ICompressionAlgorithm
{
    public CompressionAlgorithmType Type => CompressionAlgorithmType.GZip;
    public string FileExtension => ".gz";

    public async Task<byte[]> CompressAsync(
        byte[] input,
        string originalFileName,
        CancellationToken cancellationToken,
        Func<long, ValueTask>? progressCallback = null
    )
    {
        await using var output = new MemoryStream();
        await using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            // Копируем кусками, чтобы воркер мог отправлять мастеру реальный прогресс сжатия.
            await CompressionStreamHelpers.CopyWithProgressAsync(
                input,
                gzip,
                cancellationToken,
                progressCallback
            );
        }

        return output.ToArray();
    }
}

public sealed class ZipCompressionAlgorithm : ICompressionAlgorithm
{
    public CompressionAlgorithmType Type => CompressionAlgorithmType.Zip;
    public string FileExtension => ".zip";

    public async Task<byte[]> CompressAsync(
        byte[] input,
        string originalFileName,
        CancellationToken cancellationToken,
        Func<long, ValueTask>? progressCallback = null
    )
    {
        await using var output = new MemoryStream();

        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entryName = SanitizeEntryName(originalFileName);
            var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);

            await using var entryStream = entry.Open();
            // ZIP хранит имя файла внутри архива, поэтому после распаковки расширение сохраняется автоматически.
            await CompressionStreamHelpers.CopyWithProgressAsync(
                input,
                entryStream,
                cancellationToken,
                progressCallback
            );
        }

        return output.ToArray();
    }

    private static string SanitizeEntryName(string originalFileName)
    {
        var fileName = Path.GetFileName(originalFileName);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return "compressed.bin";
        }

        var invalid = Path.GetInvalidFileNameChars();
        return new string(
            fileName.Select(character => invalid.Contains(character) ? '_' : character).ToArray()
        );
    }
}

public static class CompressionStreamHelpers
{
    public static async Task CopyWithProgressAsync(
        byte[] input,
        Stream output,
        CancellationToken cancellationToken,
        Func<long, ValueTask>? progressCallback
    )
    {
        const int chunkSize = 1024 * 1024;
        await using var inputStream = new MemoryStream(input);
        var buffer = new byte[chunkSize];
        long processed = 0;
        long lastReported = 0;

        while (true)
        {
            var read = await inputStream.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            processed += read;

            if (
                progressCallback is not null
                && (processed - lastReported >= chunkSize || processed == input.LongLength)
            )
            {
                lastReported = processed;
                await progressCallback(processed);
            }
        }
    }
}

public sealed class CompressionAlgorithmRegistry
{
    private readonly IReadOnlyDictionary<
        CompressionAlgorithmType,
        ICompressionAlgorithm
    > algorithms;

    public CompressionAlgorithmRegistry()
    {
        var registered = new ICompressionAlgorithm[]
        {
            new GZipCompressionAlgorithm(),
            new ZipCompressionAlgorithm(),
        };

        algorithms = registered.ToDictionary(algorithm => algorithm.Type);
    }

    public ICompressionAlgorithm Get(CompressionAlgorithmType type)
    {
        if (algorithms.TryGetValue(type, out var algorithm))
        {
            return algorithm;
        }

        throw new NotSupportedException($"Алгоритм {type} не поддерживается.");
    }
}
