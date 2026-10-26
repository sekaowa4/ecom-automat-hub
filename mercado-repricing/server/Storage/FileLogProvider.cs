namespace Repricing.Storage;
public sealed class FileLogProvider(string directory) : ILoggerProvider
{
    readonly object gate=new();
    readonly string logDirectory=directory;
    public ILogger CreateLogger(string categoryName)=>new FileLogger(this,categoryName);
    public void Dispose() { }
    sealed class FileLogger(FileLogProvider owner,string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)where TState:notnull=>null;
        public bool IsEnabled(LogLevel level)=>level>=LogLevel.Warning;
        public void Log<TState>(LogLevel level,EventId eventId,TState state,Exception? exception,Func<TState,Exception?,string> formatter)
        {
            if(!IsEnabled(level))return;
            try{lock(owner.gate){Directory.CreateDirectory(owner.logDirectory);File.AppendAllText(Path.Combine(owner.logDirectory,$"app-{DateTime.Today:yyyyMMdd}.log"),$"{DateTimeOffset.Now:O} [{level}] {category} {formatter(state,exception)}\n{exception}\n");}}catch{/* Logging must not stop a task. */}
        }
    }
}
