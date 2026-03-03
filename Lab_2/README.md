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
====================================
Размер данных: 10 000 000 элементов

Последовательная: 677 мс
ThreadPool: 138 мс
TAP: 180 мс
APM: 176 мс

Ускорение ThreadPool: 4,91x
Ускорение TAP: 3,76x
Ускорение APM: 3,85x
====================================
Размер данных: 100 000 000 элементов

Последовательная: 4731 мс
ThreadPool: 1314 мс
TAP: 1800 мс
APM: 1768 мс

Ускорение ThreadPool: 3,60x
Ускорение TAP: 2,63x
Ускорение APM: 2,68x
====================================
Размер данных: 500 000 000 элементов

Последовательная: 25560 мс
ThreadPool: 7364 мс
TAP: 27519 мс
APM: 20705 мс

Ускорение ThreadPool: 3,47x
Ускорение TAP: 0,93x
Ускорение APM: 1,23x
====================================
При 1 000 000 000 случился System.OutOfMemoryException (съел всю ОЗУ - 32 ГБ, и весь pagefile.sys на SSD - 7.7 ГБ), так как:
1 млрд элементов × 16 байт = 16 ГБ
ThreadPool:  data + results = 32 ГБ
TAP:         data + results = 32 ГБ  
APM:         data + results = 32 ГБ

Судя по логам, упало именно на TAP
2026-03-03 23:12:45 - Начало ThreadPool обработки
2026-03-03 23:15:00 - Начало TAP обработки
"конец файла app.log"
```
