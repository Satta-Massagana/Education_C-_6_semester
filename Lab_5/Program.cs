// Два блока отчёта
// Блок 1 — сравнение трёх типов коллекций в ОДИНАКОВЫХ условиях
// Блок 2 — сравнение двух способов сделать потокобезопасный словарь
using System.Diagnostics;
using Lab5.ConcurrentCollections;

const int testOperations = 10000;
const int testWorkers = 10;
const int randomSeed = 42; // Фиксированный seed: при каждом запуске одни и те же тестовые данные.

Random random = new(randomSeed);

// Отдельные экземпляры каталога: словарь тестируется напрямую, очередь — через свой каталог
ConcurrentLibraryCatalog dictionaryCatalog = new();
ConcurrentLibraryCatalog queueWorkloadCatalog = new();
TaskQueueManager queueManager = new(boundedCapacity: testOperations);
ConcurrentCache cache = new();
CollectionBenchmark benchmark = new();

// Предзагрузка: измеряем работу с уже заполненной коллекцией, а не стоимость первичного наполнения
SeedCatalog(dictionaryCatalog, testOperations, random);
SeedCatalog(queueWorkloadCatalog, testOperations, random);
SeedCache(cache, testOperations, random);

// Блок 1: три теста с одинаковым числом циклов, потоков и схемой «запись + чтение»
LoadTestResult dictionaryResult = RunDictionaryLoadTest(
    dictionaryCatalog,
    testOperations,
    testWorkers
);
LoadTestResult queueResult = await RunQueueLoadTest(
    queueManager,
    queueWorkloadCatalog,
    testOperations,
    testWorkers
);
LoadTestResult cacheResult = RunCacheLoadTest(cache, testOperations, testWorkers);

// Целостность: после конкурентного доступа данные не потеряны и нет сбоев операций
bool dictionaryIntegrity =
    dictionaryCatalog.GetBookCount() == testOperations && dictionaryResult.FailedOperations == 0;
bool queueIntegrity =
    queueWorkloadCatalog.GetBookCount() == testOperations
    && queueResult.SuccessfulOperations == testOperations * 2
    && queueResult.FailedOperations == 0;
bool cacheIntegrity =
    cache.GetCacheSize() >= testOperations
    && cacheResult.FailedOperations == 0
    && cacheResult.SuccessfulOperations == testOperations * 2;

Console.WriteLine("=== Результаты тестирования потокобезопасных коллекций ===");
Console.WriteLine(
    $"Единые параметры: {ReportFormat.Integer(testOperations)} циклов (запись + чтение), {ReportFormat.Integer(testWorkers)} потоков/обработчиков."
);
Console.WriteLine(
    "Цикл: ConcurrentDictionary — TryUpdate + TryGet; BlockingCollection — постановка задачи + тот же TryUpdate/TryGet; ConcurrentCache — AddToCache + TryGetFromCache."
);
Console.WriteLine();

PrintLoadTestSection("ConcurrentDictionary", dictionaryResult, dictionaryIntegrity, testWorkers);
PrintLoadTestSection("BlockingCollection", queueResult, queueIntegrity, testWorkers);
PrintLoadTestSection("ConcurrentCache", cacheResult, cacheIntegrity, testWorkers);

Console.WriteLine();

// Блок 2: только ConcurrentDictionary vs Dictionary+lock (одна задача — разные реализации словаря)
benchmark.CompareAllCollections();

static void SeedCatalog(ConcurrentLibraryCatalog catalog, int count, Random random)
{
    for (int i = 0; i < count; i++)
    {
        catalog.AddBook($"Book_{i:D4}", $"Author_{random.Next(1, 101):D3}");
    }
}

static void SeedCache(ConcurrentCache cache, int count, Random random)
{
    for (int i = 0; i < count; i++)
    {
        cache.AddToCache($"CacheKey_{i:D4}", new { Number = i, Score = random.Next(0, 10_000) });
    }
}

// ConcurrentDictionary
// любой поток берёт/кладёт, никто не ждёт для чтения
static LoadTestResult RunDictionaryLoadTest(
    ConcurrentLibraryCatalog catalog,
    int operationCount,
    int workerCount
)
{
    int success = 0;
    int failed = 0;
    Stopwatch sw = Stopwatch.StartNew();

    Parallel.ForEach(
        Enumerable.Range(0, operationCount),
        new ParallelOptions { MaxDegreeOfParallelism = workerCount },
        i => RunCatalogWriteReadCycle(catalog, i, ref success, ref failed)
    );

    sw.Stop();
    return new LoadTestResult(sw.ElapsedMilliseconds, success, failed);
}

// BlockingCollection
// Главный поток ставит задачи в очередь, обработчики забирают их через GetConsumingEnumerable.
static async Task<LoadTestResult> RunQueueLoadTest(
    TaskQueueManager queueManager,
    ConcurrentLibraryCatalog catalog,
    int operationCount,
    int workerCount
)
{
    int success = 0;
    int failed = 0;
    Stopwatch sw = Stopwatch.StartNew();

    // Запускаем workerCount обработчиков до постановки задач
    Task<int> processingTask = queueManager.ProcessTasks(workerCount);

    for (int i = 0; i < operationCount; i++)
    {
        int index = i;
        // «Запись» для очереди — успешная постановка задачи (Add в BlockingCollection).
        bool added = queueManager.AddTask(
            $"Op_{index:D4}",
            // «Чтение/обработка» — выполнение того же цикла, что и у ConcurrentDictionary.
            () => RunCatalogWriteReadCycle(catalog, index, ref success, ref failed)
        );

        if (!added)
        {
            AccountResult(false, ref success, ref failed);
            AccountResult(false, ref success, ref failed);
        }
    }

    // Сигнал: новых задач больше не будет, обработчики могут завершиться после опустошения очереди.
    queueManager.CompleteAdding();
    int processed = await processingTask;

    // Если часть задач не выполнена — фиксируем две неудачи на каждый невыполненный цикл (запись+чтение).
    for (int i = 0; i < operationCount - processed; i++)
    {
        AccountResult(false, ref success, ref failed);
        AccountResult(false, ref success, ref failed);
    }

    sw.Stop();
    return new LoadTestResult(sw.ElapsedMilliseconds, success, failed);
}

// ConcurrentCache: снова Parallel.For, как у словаря — те же 10 «обработчиков» (потоков).
static LoadTestResult RunCacheLoadTest(ConcurrentCache cache, int operationCount, int workerCount)
{
    int success = 0;
    int failed = 0;
    Stopwatch sw = Stopwatch.StartNew();

    Parallel.ForEach(
        Enumerable.Range(0, operationCount),
        new ParallelOptions { MaxDegreeOfParallelism = workerCount },
        i => RunCacheWriteReadCycle(cache, i, ref success, ref failed)
    );

    sw.Stop();
    return new LoadTestResult(sw.ElapsedMilliseconds, success, failed);
}

// Один цикл для каталога: запись (TryUpdate) + чтение (TryGet) = до 2 успешных операций.
static void RunCatalogWriteReadCycle(
    ConcurrentLibraryCatalog catalog,
    int index,
    ref int success,
    ref int failed
)
{
    string key = $"Book_{index:D4}";
    if (!catalog.TryGetBook(key, out Book book))
    {
        AccountResult(false, ref success, ref failed);
        AccountResult(false, ref success, ref failed);
        return;
    }

    Book updated = new() { Title = key, Author = $"A_{index % 100:D3}" };
    // TryUpdate меняет значение только если ключ не изменился другим потоком (атомарно).
    bool writeOk = catalog.Books.TryUpdate(key, updated, book);
    AccountResult(writeOk, ref success, ref failed);

    bool readOk = catalog.TryGetBook(key, out _);
    AccountResult(readOk, ref success, ref failed);
}

// Один цикл для кэша: добавление в ConcurrentBag через AddToCache + чтение с проверкой срока жизни.
static void RunCacheWriteReadCycle(
    ConcurrentCache cache,
    int index,
    ref int success,
    ref int failed
)
{
    string key = $"CacheKey_{index:D4}";
    cache.AddToCache(key, index);
    AccountResult(true, ref success, ref failed);

    bool readOk = cache.TryGetFromCache(key, out _);
    AccountResult(readOk, ref success, ref failed);
}

// Interlocked — безопасный инкремент счётчиков из нескольких потоков без lock.
static void AccountResult(bool isSuccessful, ref int success, ref int failed)
{
    if (isSuccessful)
    {
        Interlocked.Increment(ref success);
    }
    else
    {
        Interlocked.Increment(ref failed);
    }
}

static void PrintLoadTestSection(
    string title,
    LoadTestResult result,
    bool integrity,
    int workerCount
)
{
    double opsPerSec =
        result.ElapsedMilliseconds == 0
            ? result.SuccessfulOperations
            : result.SuccessfulOperations / (result.ElapsedMilliseconds / 1000.0);

    Console.WriteLine($"{title}:");
    Console.WriteLine($"  Количество операций: {ReportFormat.Integer(testOperations)}");
    // У всех трёх коллекций 10 параллельных исполнителей (потоков или worker-ов очереди).
    Console.WriteLine($"  Количество обработчиков: {ReportFormat.Integer(workerCount)}");
    Console.WriteLine($"  Время выполнения: {ReportFormat.Integer(result.ElapsedMilliseconds)} мс");
    Console.WriteLine($"  Успешные операции: {ReportFormat.Integer(result.SuccessfulOperations)}");
    Console.WriteLine(
        $"  Производительность: {ReportFormat.Number(opsPerSec)} {ReportFormat.OperationsPerSecond}"
    );
    Console.WriteLine($"  Целостность данных: {ReportFormat.Integrity(integrity)}");
    Console.WriteLine();
}

readonly record struct LoadTestResult(
    long ElapsedMilliseconds,
    int SuccessfulOperations,
    int FailedOperations
);
