using System;
using System.Threading;
using System.Threading.Tasks;

public class TaskProcessor
{
    private const int CHUNK_COUNT = 8;

    public decimal[] ProcessDataWithThreadPool(decimal[] data)
    {
        int chunkSize = data.Length / CHUNK_COUNT;
        var results = new decimal[data.Length];
        var events = new ManualResetEvent[CHUNK_COUNT]; // синхронизация 8 потоков
        var exceptions = new Exception[CHUNK_COUNT]; // ловит ошибки от 8 потоков

        for (int i = 0; i < CHUNK_COUNT; i++) // делим куски между потоками
        {
            events[i] = new ManualResetEvent(false);
            int startIdx = i * chunkSize;
            int endIdx = (i == CHUNK_COUNT - 1) ? data.Length : startIdx + chunkSize;
            int chunkIndex = i;

            ThreadPool.QueueUserWorkItem(_ => // запускаем 8 задач
            {
                try
                {
                    ProcessChunk(data, results, startIdx, endIdx);
                }
                catch (Exception ex)
                {
                    exceptions[chunkIndex] = ex;
                }
                finally
                {
                    events[chunkIndex].Set();
                }
            });
        }

        WaitHandle.WaitAll(events);

        foreach (var ex in exceptions)
        {
            if (ex != null)
                throw ex;
        }

        return results;
    }

    // обертка ThreadPool в TAP
    public Task<decimal[]> ProcessDataAsync(decimal[] data)
    {
        var tcs = new TaskCompletionSource<decimal[]>();
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var result = ProcessDataWithThreadPool(data);
                tcs.SetResult(result);
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        return tcs.Task;
    }

    // обертка ThreadPool в APM
    public decimal[] ProcessDataWithAPM(decimal[] data)
    {
        var apmState = new ApmState(data);
        var ar = apmState.BeginProcessData(null, null);
        ar.AsyncWaitHandle.WaitOne();
        return apmState.EndProcessData(ar);
    }

    private void ProcessChunk(decimal[] source, decimal[] result, int start, int end)
    {
        for (int i = start; i < end; i++)
        {
            result[i] = source[i] * 2m + 1m;
        }
    }

    // Обработка по APM
    private class ApmState : IAsyncResult
    {
        private readonly decimal[] _data;
        private readonly ManualResetEvent _event;
        private decimal[] _result;
        private AsyncCallback _callback;
        private bool _completed;

        public ApmState(decimal[] data)
        {
            _data = data;
            _event = new ManualResetEvent(false);
        }

        public object AsyncState => this;
        public WaitHandle AsyncWaitHandle => _event;
        public bool CompletedSynchronously => false;
        public bool IsCompleted => _completed;
        public AsyncCallback Callback
        {
            get => _callback;
            set => _callback = value;
        }

        public IAsyncResult BeginProcessData(AsyncCallback callback, object state)
        {
            Callback = callback;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var processor = new TaskProcessor();
                    _result = processor.ProcessDataWithThreadPool(_data);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"APM Error: {ex.Message}");
                }
                finally
                {
                    _completed = true;
                    _event.Set();
                    _callback?.Invoke(this);
                }
            });
            return this;
        }

        // дожидается завершения + возвращает результат обработки для APM
        public decimal[] EndProcessData(IAsyncResult result)
        {
            if (!IsCompleted)
                AsyncWaitHandle.WaitOne();
            _event.Dispose();
            return _result;
        }
    }
}
