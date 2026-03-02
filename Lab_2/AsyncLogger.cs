using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

public static class AsyncLogger
{
    private static readonly string logFile = "app.log";

    public static Task LogAsync(string message)
    {
        return Task.Run(async () =>
        {
            string fullMessage = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}\r\n";
            var bytes = System.Text.Encoding.UTF8.GetBytes(fullMessage);

            using var fs = new FileStream(
                logFile,
                FileMode.Append,
                FileAccess.Write,
                FileShare.ReadWrite,
                4096,
                FileOptions.Asynchronous
            );
            await fs.WriteAsync(bytes, 0, bytes.Length);
        });
    }

    public static IAsyncResult LogWithCallback(string message, Action callback)
    {
        var state = new LogApmState(message, callback);
        state.BeginLog(null, null);
        return state;
    }

    private class LogApmState : IAsyncResult
    {
        private readonly string _message;
        private readonly Action _userCallback;
        private readonly ManualResetEvent _event;
        private AsyncCallback _internalCallback;
        private bool _completed;

        public LogApmState(string message, Action userCallback)
        {
            _message = message;
            _userCallback = userCallback;
            _event = new ManualResetEvent(false);
        }

        public object AsyncState => null;
        public WaitHandle AsyncWaitHandle => _event;
        public bool CompletedSynchronously => false;
        public bool IsCompleted => _completed;

        public AsyncCallback Callback
        {
            get => _internalCallback;
            set => _internalCallback = value;
        }

        public IAsyncResult BeginLog(AsyncCallback callback, object state)
        {
            Callback = callback;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string fullMessage = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {_message}\r\n";
                    var bytes = System.Text.Encoding.UTF8.GetBytes(fullMessage);

                    using var fs = new FileStream(
                        logFile,
                        FileMode.Append,
                        FileAccess.Write,
                        FileShare.ReadWrite,
                        4096,
                        FileOptions.Asynchronous
                    );
                    fs.BeginWrite(bytes, 0, bytes.Length, WriteCallback, fs);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Log error: {ex.Message}");
                }
            });
            return this;
        }

        private void WriteCallback(IAsyncResult ar)
        {
            try
            {
                ((FileStream)ar.AsyncState).EndWrite(ar);
            }
            finally
            {
                _completed = true;
                _event.Set();
                Callback?.Invoke(this);
                _userCallback?.Invoke();
            }
        }
    }
}
