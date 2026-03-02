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
        var events = new ManualResetEvent[CHUNK_COUNT];
        var exceptions = new Exception[CHUNK_COUNT];

        for (int i = 0; i < CHUNK_COUNT; i++)
        {
            events[i] = new ManualResetEvent(false);
            int startIdx = i * chunkSize;
            int endIdx = (i == CHUNK_COUNT - 1) ? data.Length : startIdx + chunkSize;
            int chunkIndex = i; // Захватываем локальную копию

            ThreadPool.QueueUserWorkItem(_ =>
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

        public decimal[] EndProcessData(IAsyncResult result)
        {
            if (!IsCompleted)
                AsyncWaitHandle.WaitOne();
            _event.Dispose();
            return _result;
        }
    }
}
