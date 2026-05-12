using System.Collections.Concurrent;
using System.Diagnostics;
using Lab5.ConcurrentCollections;

const int catalogBookCount = 1000;
const int queueTaskCount = 1000;
const int cacheItemCount = 500;
const int catalogWorkers = 50;
const int queueWorkers = 10;
const int cacheWorkers = 20;
const int randomSeed = 42;

Random random = new(randomSeed);

ConcurrentLibraryCatalog catalog = new();
TaskQueueManager queueManager = new(boundedCapacity: queueTaskCount);
ConcurrentCache cache = new();
CollectionBenchmark benchmark = new();

List<(string title, string author)> books = GenerateBooks(catalogBookCount, random);
List<(string key, object value)> cacheItems = GenerateCacheItems(cacheItemCount, random);

foreach ((string title, string author) in books)
{
    catalog.AddBook(title, author);
}

foreach ((string key, object value) in cacheItems)
{
    cache.AddToCache(key, value);
}

int catalogSuccess = 0;
int catalogFailed = 0;
Stopwatch catalogSw = Stopwatch.StartNew();

Parallel.ForEach(
    Enumerable.Range(0, catalogBookCount),
    new ParallelOptions { MaxDegreeOfParallelism = catalogWorkers },
    i =>
    {
        string oldTitle = $"Book_{i:D4}";
        string newTitle = $"{oldTitle}_Updated";
        string newAuthor = $"Updated_Author_{i % 50:D2}";

        bool updated = catalog.UpdateBook(oldTitle, newTitle, newAuthor);
        if (updated)
            Interlocked.Increment(ref catalogSuccess);
        else
            Interlocked.Increment(ref catalogFailed);

        bool found = catalog.TryGetBook(newTitle, out _);
        if (found)
            Interlocked.Increment(ref catalogSuccess);
        else
            Interlocked.Increment(ref catalogFailed);
    }
);

catalogSw.Stop();

int processedTasksCounter = 0;
Task<int> processingTask = queueManager.ProcessTasks(queueWorkers);
Stopwatch queueSw = Stopwatch.StartNew();

for (int i = 0; i < queueTaskCount; i++)
{
    int taskNumber = i;
    bool added = queueManager.AddTask(
        $"QueueTask_{taskNumber:D4}",
        () =>
        {
            Interlocked.Increment(ref processedTasksCounter);
        }
    );

    if (!added)
    {
        throw new InvalidOperationException($"Не удалось добавить задачу {taskNumber} в очередь.");
    }
}

queueManager.CompleteAdding();
int processedByWorkers = await processingTask;
queueSw.Stop();
int processedTasks = Math.Min(processedTasksCounter, processedByWorkers);

int cacheSuccess = 0;
Stopwatch cacheSw = Stopwatch.StartNew();

Parallel.ForEach(
    Enumerable.Range(0, cacheItemCount),
    new ParallelOptions { MaxDegreeOfParallelism = cacheWorkers },
    i =>
    {
        string key = $"CacheKey_{i:D4}";
        cache.AddToCache(key, $"Updated_{i}");
        bool exists = cache.TryGetFromCache(key, out _);
        if (exists)
        {
            Interlocked.Increment(ref cacheSuccess);
        }
    }
);

cacheSw.Stop();

(long concurrentMs, int concurrentSuccess, int concurrentFail) =
    benchmark.BenchmarkConcurrentDictionary(catalogBookCount);
(long syncMs, int syncSuccess) = benchmark.BenchmarkSynchronizedDictionary(catalogBookCount);

bool catalogIntegrity = catalog.GetBookCount() == catalogBookCount && catalogFailed == 0;
bool queueIntegrity = processedTasks == queueTaskCount;
bool cacheIntegrity = cache.GetCacheSize() >= cacheItemCount;
bool synchronizationProblems = !(catalogIntegrity && queueIntegrity && cacheIntegrity);

double catalogOpsPerSec =
    catalogSw.ElapsedMilliseconds == 0
        ? catalogSuccess
        : catalogSuccess / (catalogSw.ElapsedMilliseconds / 1000.0);
double queueOpsPerSec =
    queueSw.ElapsedMilliseconds == 0
        ? processedTasks
        : processedTasks / (queueSw.ElapsedMilliseconds / 1000.0);
double concurrentOpsPerSec =
    concurrentMs == 0 ? concurrentSuccess : concurrentSuccess / (concurrentMs / 1000.0);
double speedup = syncMs == 0 ? 0 : (double)syncMs / Math.Max(concurrentMs, 1);
double syncOverheadPercent = syncMs == 0 ? 0 : ((syncMs - concurrentMs) / (double)syncMs) * 100.0;

Console.WriteLine("=== Результаты тестирования потокобезопасных коллекций ===");
Console.WriteLine("ConcurrentDictionary:");
Console.WriteLine($"  Количество операций: {catalogBookCount}");
Console.WriteLine($"  Время выполнения: {catalogSw.ElapsedMilliseconds} мс");
Console.WriteLine($"  Успешные операции: {catalogSuccess}");
Console.WriteLine($"  Производительность: {catalogOpsPerSec:F2} операций/сек");
Console.WriteLine($"  Целостность данных: {(catalogIntegrity ? "Да" : "Нет")}");
Console.WriteLine();
Console.WriteLine("BlockingCollection:");
Console.WriteLine($"  Количество задач: {queueTaskCount}");
Console.WriteLine($"  Количество обработчиков: {queueWorkers}");
Console.WriteLine($"  Время выполнения: {queueSw.ElapsedMilliseconds} мс");
Console.WriteLine($"  Обработанные задачи: {processedTasks}");
Console.WriteLine($"  Производительность: {queueOpsPerSec:F2} задач/сек");
Console.WriteLine();
Console.WriteLine("ConcurrentCache:");
Console.WriteLine($"  Количество операций: {cacheItemCount}");
Console.WriteLine($"  Время выполнения: {cacheSw.ElapsedMilliseconds} мс");
Console.WriteLine($"  Успешные операции: {cacheSuccess}");
Console.WriteLine();
Console.WriteLine("Сравнение производительности:");
Console.WriteLine($"  ConcurrentDictionary vs Synchronized Dictionary: {speedup:F2}x");
Console.WriteLine($"  Накладные расходы синхронизации: {syncOverheadPercent:F2}%");
Console.WriteLine();
Console.WriteLine(
    synchronizationProblems
        ? "Обнаружены потенциальные проблемы синхронизации."
        : "Проблем синхронизации не обнаружено."
);
Console.WriteLine();

await benchmark.CompareAllCollections();

_ = concurrentFail;
_ = syncSuccess;

static List<(string title, string author)> GenerateBooks(int count, Random random)
{
    List<(string title, string author)> data = new(count);
    for (int i = 0; i < count; i++)
    {
        string title = $"Book_{i:D4}";
        string author = $"Author_{random.Next(1, 101):D3}";
        data.Add((title, author));
    }

    return data;
}

static List<(string key, object value)> GenerateCacheItems(int count, Random random)
{
    List<(string key, object value)> data = new(count);
    for (int i = 0; i < count; i++)
    {
        string key = $"CacheKey_{i:D4}";
        object value = new
        {
            Number = i,
            Score = random.Next(0, 10_000),
            CreatedAt = DateTime.UtcNow,
        };
        data.Add((key, value));
    }

    return data;
}
