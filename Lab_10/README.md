# Lab 10 — TCP обмен сообщениями между узлами кластера

## Описание реализации

Решение реализует клиент-серверную архитектуру на базе сокетов TCP (`TcpListener`, `System.Net.Sockets.TcpClient`, `NetworkStream`) без использования высокоуровневых сетевых библиотек.

### MessageProtocol

- Класс `NetworkMessage` содержит поля: `MessageId`, `MessageType`, `Payload`, `Timestamp`, `SenderId`.
- Сериализация выполняется через `BinaryWriter`/`BinaryReader` с префиксом длины (4 байта).
- Метод `ReadMessageAsync` обеспечивает корректное чтение при частичных данных из сетевого потока.
- Метод `Validate` проверяет обязательные поля и ограничивает размер сообщения (10 МБ).

### TcpServer

- Принимает подключения через `TcpListener`.
- Каждый клиент обрабатывается в отдельной задаче.
- Подключения ограничены через `SemaphoreSlim`.
- Поддерживаются события `ClientConnected`, `ClientDisconnected`, `MessageReceived`.
- Реализованы `BroadcastMessage` и `SendMessageToClient`.
- Состояние клиентов хранится в `Dictionary<string, TcpClientWrapper>`.

### TcpClient

- Подключение с таймаутом через `CancellationTokenSource`.
- Асинхронная отправка и приём сообщений.
- Обработка разрыва соединения и опциональное автопереподключение.
- События: `Connected`, `Disconnected`, `MessageReceived`.

### NetworkBenchmark

- `BenchmarkSingleClient` — тест одного клиента.
- `BenchmarkMultipleClients` — параллельная работа нескольких клиентов.
- `BenchmarkThroughput` — измерение пропускной способности за заданное время.
- `CompareLatency` — измерение минимальной, средней и максимальной задержки.

### Program

- Запускает сервер на `127.0.0.1:8888`.
- Создаёт 8 клиентов и выполняет обмен сообщениями через сервер.
- Запускает бенчмарки и выводит сводную статистику.

## Запуск

```
dotnet run --project Lab_10.csproj
```

## Структура файлов

- `Program.cs` — точка входа и демонстрация работы системы
- `TcpServer.cs` — сервер и обёртка клиентского подключения
- `TcpClient.cs` — TCP-клиент
- `MessageProtocol.cs` — протокол сериализации сообщений
- `NetworkBenchmark.cs` — тесты производительности
