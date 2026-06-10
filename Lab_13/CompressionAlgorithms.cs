using System.IO.Compression;

namespace Lab_13;

public interface ICompressionAlgorithm
{
    CompressionAlgorithmType Type { get; }
    string FileExtension { get; }
    Task<byte[]> CompressAsync(byte[] input, CancellationToken cancellationToken);
}

public sealed class GZipCompressionAlgorithm : ICompressionAlgorithm
{
    public CompressionAlgorithmType Type => CompressionAlgorithmType.GZip;
    public string FileExtension => ".gz";

    public async Task<byte[]> CompressAsync(byte[] input, CancellationToken cancellationToken)
    {
        await using var output = new MemoryStream();
        await using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            // CopyToAsync освобождает поток выполнения на время операций записи в поток сжатия.
            await new MemoryStream(input).CopyToAsync(gzip, cancellationToken);
        }

        return output.ToArray();
    }
}

public sealed class DeflateCompressionAlgorithm : ICompressionAlgorithm
{
    public CompressionAlgorithmType Type => CompressionAlgorithmType.Deflate;
    public string FileExtension => ".deflate";

    public async Task<byte[]> CompressAsync(byte[] input, CancellationToken cancellationToken)
    {
        await using var output = new MemoryStream();
        await using (
            var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true)
        )
        {
            await new MemoryStream(input).CopyToAsync(deflate, cancellationToken);
        }

        return output.ToArray();
    }
}

public sealed class BrotliCompressionAlgorithm : ICompressionAlgorithm
{
    public CompressionAlgorithmType Type => CompressionAlgorithmType.Brotli;
    public string FileExtension => ".br";

    public async Task<byte[]> CompressAsync(byte[] input, CancellationToken cancellationToken)
    {
        await using var output = new MemoryStream();
        await using (
            var brotli = new BrotliStream(output, CompressionLevel.Optimal, leaveOpen: true)
        )
        {
            await new MemoryStream(input).CopyToAsync(brotli, cancellationToken);
        }

        return output.ToArray();
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
            new DeflateCompressionAlgorithm(),
            new BrotliCompressionAlgorithm(),
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
