## Описание реализации

**TaskProcessor.cs** реализует три метода параллельной обработки массива из 10M элементов:
- `ProcessDataWithThreadPool`: Делит на 8 чанков, ThreadPool.QueueUserWorkItem, ManualResetEvent для синхронизации
- `ProcessDataAsync`: TaskCompletionSource + ThreadPool, TAP паттерн
- `ProcessDataWithAPM`: Custom IAsyncResult, Begin/EndProcessData с ThreadPool

**AsyncLogger.cs**:
- `LogAsync`: FileStream с FileOptions.Asynchronous
- `LogWithCallback`: APM с BeginWrite/EndWrite и callback

**Program.cs**: Генерация данных (seed=42), замеры Stopwatch, сравнение с последовательной обработкой, статистика ускорения

## Результаты
```

```
