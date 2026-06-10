using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Lab_13;

public sealed class MasterCompressionService
{
    private readonly CompressionAlgorithmRegistry algorithms;
    private readonly CompressionJobStore jobStore;
    private readonly IWorkerSelectionStrategy workerSelection;
    private readonly WorkerRegistry workerRegistry;
    private readonly TcpCompressionClient tcpClient;
    private readonly CompressionPipeline pipeline;
    private readonly IWebHostEnvironment environment;
    private readonly ILogger<MasterCompressionService> logger;

    public MasterCompressionService(
        CompressionAlgorithmRegistry algorithms,
        CompressionJobStore jobStore,
        IWorkerSelectionStrategy workerSelection,
        WorkerRegistry workerRegistry,
        TcpCompressionClient tcpClient,
        CompressionPipeline pipeline,
        IWebHostEnvironment environment,
        ILogger<MasterCompressionService> logger
    )
    {
        this.algorithms = algorithms;
        this.jobStore = jobStore;
        this.workerSelection = workerSelection;
        this.workerRegistry = workerRegistry;
        this.tcpClient = tcpClient;
        this.pipeline = pipeline;
        this.environment = environment;
        this.logger = logger;
    }

    public async Task<CompressionJob> StartCompressionAsync(
        string fileName,
        Stream input,
        CompressionAlgorithmType algorithm,
        DistributionMode mode,
        CancellationToken cancellationToken
    )
    {
        var frontStopwatch = Stopwatch.StartNew();
        await using var buffer = new MemoryStream();

        // Входной файл читается async/await, чтобы I/O не блокировал поток обработки запросов.
        await input.CopyToAsync(buffer, cancellationToken);
        var data = buffer.ToArray();
        frontStopwatch.Stop();

        if (data.Length == 0)
        {
            throw new InvalidDataException("Пустой файл нельзя сжать.");
        }

        var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var job = new CompressionJob
        {
            FileName = Path.GetFileName(fileName),
            Algorithm = algorithm,
            Mode = mode,
            OriginalBytes = data.LongLength,
            FrontExchangeMilliseconds = frontStopwatch.ElapsedMilliseconds,
            Status = "Поставлена в очередь",
        };

        job.AddLog($"Файл загружен на мастер за {frontStopwatch.ElapsedMilliseconds} мс.");
        jobStore.Add(job, linkedCancellation);

        _ = Task.Run(
            () => ExecuteJobAsync(job, data, linkedCancellation.Token),
            CancellationToken.None
        );

        return job;
    }

    public void CancelJob(string jobId)
    {
        jobStore.Cancel(jobId);
    }

    private async Task ExecuteJobAsync(
        CompressionJob job,
        byte[] data,
        CancellationToken cancellationToken
    )
    {
        var totalStopwatch = Stopwatch.StartNew();

        try
        {
            job.Status = "Последовательный бенчмарк";
            job.SequentialElapsedMilliseconds = await MeasureSequentialAsync(
                data,
                job.Algorithm,
                cancellationToken
            );
            job.AddLog($"Последовательное сжатие заняло {job.SequentialElapsedMilliseconds} мс.");

            job.Status = "Распределение по воркерам";
            var workItems = CreateWorkItems(job, data);
            job.TotalParts = workItems.Count;

            if (workItems.Count == 0)
            {
                throw new InvalidOperationException("Нет доступных воркеров для обработки.");
            }

            InitializeProgress(job, workItems);
            job.AddLog($"Создано {workItems.Count} TCP-заданий для режима {job.Mode}.");

            var results = await pipeline.RunAsync(
                workItems,
                (item, token) => ProcessWithRetryAsync(job, item, token),
                cancellationToken
            );

            var failed = results.Where(result => !result.Success).ToArray();
            if (failed.Length > 0)
            {
                throw new InvalidOperationException(
                    string.Join("; ", failed.Select(item => item.ErrorMessage))
                );
            }

            job.Status = "Агрегация результатов";
            var outputBytes = BuildOutput(job, results);
            var outputPath = await SaveOutputAsync(job, outputBytes, cancellationToken);

            job.OutputPath = outputPath;
            job.DownloadUrl = $"/compressed/{Path.GetFileName(outputPath)}";
            job.CompressedBytes = outputBytes.LongLength;
            job.WorkerExchangeMilliseconds = results
                .AsParallel()
                .Sum(result => result.NetworkElapsedMilliseconds);
            job.TotalElapsedMilliseconds = totalStopwatch.ElapsedMilliseconds;
            job.Status = "Завершена";
            job.AddLog($"Результат сохранен: {outputPath}.");
        }
        catch (OperationCanceledException)
        {
            job.Status = "Отменена";
            job.AddLog("Задача отменена пользователем.");
        }
        catch (Exception ex)
        {
            job.Status = "Ошибка";
            job.AddLog($"Ошибка: {ex.Message}");
            logger.LogError(ex, "Ошибка выполнения задачи {JobId}.", job.Id);
        }
        finally
        {
            totalStopwatch.Stop();
            job.TotalElapsedMilliseconds = totalStopwatch.ElapsedMilliseconds;
            jobStore.Complete(job.Id);
        }
    }

    private async Task<long> MeasureSequentialAsync(
        byte[] data,
        CompressionAlgorithmType algorithm,
        CancellationToken cancellationToken
    )
    {
        var stopwatch = Stopwatch.StartNew();
        _ = await algorithms.Get(algorithm).CompressAsync(data, cancellationToken);
        stopwatch.Stop();
        return stopwatch.ElapsedMilliseconds;
    }

    private IReadOnlyList<CompressionWorkItem> CreateWorkItems(CompressionJob job, byte[] data)
    {
        var workers = workerSelection.SelectWorkers(4);

        return job.Mode switch
        {
            DistributionMode.SingleWorker => CreateSingleWorkerItem(job, data, workers),
            DistributionMode.SplitAcrossWorkers => CreateSplitItems(job, data, workers),
            DistributionMode.FullCopyToAllWorkers => CreateFullCopyItems(job, data, workers),
            _ => throw new NotSupportedException($"Режим {job.Mode} не поддерживается."),
        };
    }

    private static IReadOnlyList<CompressionWorkItem> CreateSingleWorkerItem(
        CompressionJob job,
        byte[] data,
        IReadOnlyList<WorkerDescriptor> workers
    )
    {
        var worker = workers.FirstOrDefault();
        return worker is null
            ? Array.Empty<CompressionWorkItem>()
            : new[]
            {
                new CompressionWorkItem(
                    job.Id,
                    job.FileName,
                    job.Algorithm,
                    job.Mode,
                    worker,
                    0,
                    1,
                    data
                ),
            };
    }

    private static IReadOnlyList<CompressionWorkItem> CreateFullCopyItems(
        CompressionJob job,
        byte[] data,
        IReadOnlyList<WorkerDescriptor> workers
    )
    {
        var selected = workers.Take(4).ToArray();

        return selected
            .Select(
                (worker, index) =>
                    new CompressionWorkItem(
                        job.Id,
                        job.FileName,
                        job.Algorithm,
                        job.Mode,
                        worker,
                        index,
                        selected.Length,
                        data
                    )
            )
            .ToArray();
    }

    private static IReadOnlyList<CompressionWorkItem> CreateSplitItems(
        CompressionJob job,
        byte[] data,
        IReadOnlyList<WorkerDescriptor> workers
    )
    {
        var selected = workers.Take(Math.Min(4, Math.Max(1, data.Length))).ToArray();
        if (selected.Length == 0)
        {
            return Array.Empty<CompressionWorkItem>();
        }

        var partCount = selected.Length;
        var baseSize = data.Length / partCount;
        var remainder = data.Length % partCount;
        var offset = 0;
        var items = new List<CompressionWorkItem>(partCount);

        for (var index = 0; index < partCount; index++)
        {
            var partSize = baseSize + (index < remainder ? 1 : 0);
            var part = new byte[partSize];
            Buffer.BlockCopy(data, offset, part, 0, partSize);
            offset += partSize;

            items.Add(
                new CompressionWorkItem(
                    job.Id,
                    job.FileName,
                    job.Algorithm,
                    job.Mode,
                    selected[index],
                    index,
                    partCount,
                    part
                )
            );
        }

        return items;
    }

    private static void InitializeProgress(
        CompressionJob job,
        IEnumerable<CompressionWorkItem> workItems
    )
    {
        foreach (var item in workItems)
        {
            var key = GetProgressKey(item.Worker.Id, item.PartIndex);
            job.WorkerProgress[key] = new WorkerProgress
            {
                WorkerId = item.Worker.Id,
                PartIndex = item.PartIndex,
                TotalParts = item.TotalParts,
                OriginalBytes = item.Payload.LongLength,
                ProgressPercent = 5,
                Status = "Назначена",
            };
        }
    }

    private async Task<WorkerCompressionResult> ProcessWithRetryAsync(
        CompressionJob job,
        CompressionWorkItem item,
        CancellationToken cancellationToken
    )
    {
        var attemptedWorkers = new HashSet<string>();
        var current = item;

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attemptedWorkers.Add(current.Worker.Id);
            var activeWorkerId = current.Worker.Id;
            var progressKey = GetProgressKey(current.Worker.Id, current.PartIndex);
            var progress = job.WorkerProgress.GetOrAdd(
                progressKey,
                _ => new WorkerProgress
                {
                    WorkerId = current.Worker.Id,
                    PartIndex = current.PartIndex,
                    TotalParts = current.TotalParts,
                    OriginalBytes = current.Payload.LongLength,
                }
            );

            workerRegistry.IncrementActive(activeWorkerId);
            progress.Status = attempt == 1 ? "Отправка по TCP" : "Повторная отправка";
            progress.ProgressPercent = 25;

            try
            {
                var result = await tcpClient.CompressAsync(current, cancellationToken);

                if (!result.Success)
                {
                    throw new InvalidOperationException(
                        result.ErrorMessage ?? "Воркер вернул ошибку без описания."
                    );
                }

                workerRegistry.MarkCompleted(current.Worker.Id);
                progress.Status = "Готово";
                progress.ProgressPercent = 100;
                progress.CompressedBytes = result.CompressedBytes;
                progress.WorkerElapsedMilliseconds = result.WorkerElapsedMilliseconds;
                progress.NetworkElapsedMilliseconds = result.NetworkElapsedMilliseconds;
                job.MarkPartCompleted();
                job.AddLog(
                    $"{current.Worker.Id} сжал часть {current.PartIndex + 1}/{current.TotalParts} за {result.WorkerElapsedMilliseconds} мс."
                );

                return result;
            }
            catch (OperationCanceledException)
            {
                progress.Status = "Отменено";
                throw;
            }
            catch (Exception ex)
            {
                workerRegistry.MarkFailed(current.Worker.Id);
                progress.Status = "Ошибка, переназначение";
                progress.ProgressPercent = 100;
                progress.ErrorMessage = ex.Message;
                job.AddLog(
                    $"{current.Worker.Id} не справился с частью {current.PartIndex + 1}: {ex.Message}"
                );

                var replacement = workerSelection
                    .SelectWorkers(1, attemptedWorkers)
                    .FirstOrDefault();
                if (replacement is null)
                {
                    return new WorkerCompressionResult(
                        false,
                        current.Worker.Id,
                        current.PartIndex,
                        current.TotalParts,
                        Array.Empty<byte>(),
                        current.Payload.LongLength,
                        0,
                        0,
                        0,
                        "Нет доступного воркера для переназначения."
                    );
                }

                current = current with { Worker = replacement };
                job.AddLog($"Часть {current.PartIndex + 1} переназначена на {replacement.Id}.");
            }
            finally
            {
                workerRegistry.DecrementActive(activeWorkerId);
            }
        }

        return new WorkerCompressionResult(
            false,
            current.Worker.Id,
            current.PartIndex,
            current.TotalParts,
            Array.Empty<byte>(),
            current.Payload.LongLength,
            0,
            0,
            0,
            "Превышено число попыток переназначения."
        );
    }

    private byte[] BuildOutput(CompressionJob job, IReadOnlyList<WorkerCompressionResult> results)
    {
        if (job.Mode == DistributionMode.FullCopyToAllWorkers)
        {
            // Для режима сравнения берем самый быстрый полный результат, а остальные оставляем в статистике.
            var fastestPayload = results
                .OrderBy(result =>
                    result.WorkerElapsedMilliseconds + result.NetworkElapsedMilliseconds
                )
                .First()
                .CompressedPayload;

            return AddArchiveFileNameIfNeeded(job, fastestPayload);
        }

        if (job.Mode == DistributionMode.SingleWorker)
        {
            return AddArchiveFileNameIfNeeded(job, results.Single().CompressedPayload);
        }

        return BuildDistributedContainer(job, results);
    }

    private static byte[] AddArchiveFileNameIfNeeded(CompressionJob job, byte[] compressedPayload)
    {
        if (
            job.Algorithm != CompressionAlgorithmType.GZip
            || job.Mode == DistributionMode.SplitAcrossWorkers
        )
        {
            return compressedPayload;
        }

        return AddOriginalFileNameToGzip(compressedPayload, job.FileName);
    }

    private static byte[] AddOriginalFileNameToGzip(byte[] gzipBytes, string originalFileName)
    {
        var safeOriginalName = Path.GetFileName(originalFileName);
        if (string.IsNullOrWhiteSpace(safeOriginalName) || gzipBytes.Length < 10)
        {
            return gzipBytes;
        }

        var isGzip = gzipBytes[0] == 0x1f && gzipBytes[1] == 0x8b && gzipBytes[2] == 0x08;
        if (!isGzip)
        {
            return gzipBytes;
        }

        const byte fileNameFlag = 0x08;
        if ((gzipBytes[3] & fileNameFlag) != 0)
        {
            return gzipBytes;
        }

        var header = gzipBytes.AsSpan(0, 10).ToArray();
        header[3] = (byte)(header[3] | fileNameFlag);
        var fileNameBytes = Encoding.UTF8.GetBytes(safeOriginalName);

        using var output = new MemoryStream(gzipBytes.Length + fileNameBytes.Length + 1);

        // В GZip имя исходного файла хранится в optional-поле FNAME сразу после 10 байт заголовка.
        output.Write(header);
        output.Write(fileNameBytes);
        output.WriteByte(0);
        output.Write(gzipBytes, 10, gzipBytes.Length - 10);

        return output.ToArray();
    }

    private static byte[] BuildDistributedContainer(
        CompressionJob job,
        IReadOnlyList<WorkerCompressionResult> results
    )
    {
        var ordered = results.OrderBy(result => result.PartIndex).ToArray();
        var manifest = new
        {
            Format = "DCMP1",
            job.FileName,
            Algorithm = job.Algorithm.ToString(),
            job.OriginalBytes,
            Parts = ordered.Select(part => new
            {
                part.PartIndex,
                part.OriginalBytes,
                part.CompressedBytes,
                part.WorkerId,
            }),
        };

        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest);

        // Контейнер нужен, потому что каждая часть сжимается независимо и должна хранить метаданные.
        writer.Write("DCMP1");
        writer.Write(manifestBytes.Length);
        writer.Write(manifestBytes);

        foreach (var part in ordered)
        {
            writer.Write(part.CompressedPayload.Length);
            writer.Write(part.CompressedPayload);
        }

        writer.Flush();
        return output.ToArray();
    }

    private async Task<string> SaveOutputAsync(
        CompressionJob job,
        byte[] outputBytes,
        CancellationToken cancellationToken
    )
    {
        var directory = Path.Combine(environment.ContentRootPath, "CompressedFiles");
        Directory.CreateDirectory(directory);

        var extension =
            job.Mode == DistributionMode.SplitAcrossWorkers
                ? ".dcmp"
                : algorithms.Get(job.Algorithm).FileExtension;

        var safeOriginalName = SanitizeFileName(job.FileName);
        var originalNameWithoutExtension = Path.GetFileNameWithoutExtension(safeOriginalName);
        var originalExtension = Path.GetExtension(safeOriginalName);

        // Расширение исходного файла ставим перед .gz/.br/.deflate,
        // чтобы WinRAR после распаковки дал файл с правильным типом, например report_a1b2c3d4.pdf.
        var fileName = $"{originalNameWithoutExtension}_{job.Id}{originalExtension}{extension}";
        var path = Path.Combine(directory, fileName);

        await File.WriteAllBytesAsync(path, outputBytes, cancellationToken);
        return path;
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(
            fileName.Select(character => invalid.Contains(character) ? '_' : character).ToArray()
        );
        return string.IsNullOrWhiteSpace(safe) ? "compressed" : safe;
    }

    private static string GetProgressKey(string workerId, int partIndex)
    {
        return $"{workerId}:{partIndex}";
    }
}
