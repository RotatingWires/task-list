using Microsoft.Extensions.Logging;
using System.Text;
using System.Threading.Channels;

sealed class SingleFileLoggerProvider : ILoggerProvider
{
    private readonly Channel<string> messages = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly string path;
    private readonly long maxBytes;
    private readonly Task writerTask;
    private bool disposed;

    public SingleFileLoggerProvider(string path, long maxBytes)
    {
        this.path = path;
        this.maxBytes = maxBytes;
        writerTask = WriteLoopAsync();
    }

    public ILogger CreateLogger(string categoryName) => new SingleFileLogger(categoryName, messages.Writer);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        messages.Writer.TryComplete();
        try { writerTask.GetAwaiter().GetResult(); }
        catch { }
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            await using var stream = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.Write,
                FileShare.ReadWrite,
                bufferSize: 4096,
                useAsync: true);

            stream.Seek(0, SeekOrigin.End);

            await foreach (var entry in messages.Reader.ReadAllAsync())
            {
                var bytes = Encoding.UTF8.GetBytes(entry);
                if (stream.Length + bytes.Length > maxBytes)
                {
                    await stream.FlushAsync();
                    stream.SetLength(0);
                    stream.Position = 0;
                }

                await stream.WriteAsync(bytes);
                await stream.FlushAsync();
            }
        }
        catch
        {
            // File logging must never take down TaskList. Console logging remains available.
        }
    }

    private sealed class SingleFileLogger(string categoryName, ChannelWriter<string> writer) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var message = formatter(state, exception);
            if (string.IsNullOrEmpty(message) && exception is null) return;

            var level = logLevel switch
            {
                LogLevel.Trace => "trce",
                LogLevel.Debug => "dbug",
                LogLevel.Information => "info",
                LogLevel.Warning => "warn",
                LogLevel.Error => "fail",
                LogLevel.Critical => "crit",
                _ => "none"
            };

            var builder = new StringBuilder();
            builder.Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
                .Append(' ')
                .Append(level)
                .Append(": ")
                .Append(categoryName)
                .Append('[')
                .Append(eventId.Id)
                .AppendLine("]");

            AppendIndented(builder, message);
            if (exception is not null) AppendIndented(builder, exception.ToString());

            writer.TryWrite(builder.ToString());
        }

        private static void AppendIndented(StringBuilder builder, string text)
        {
            using var reader = new StringReader(text);
            string? line;
            while ((line = reader.ReadLine()) is not null)
                builder.Append("      ").AppendLine(line);
        }
    }
}
