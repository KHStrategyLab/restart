namespace KHStrategyLab.Services;

public sealed class AppLogger
{
    private readonly object _lock = new();

    public void Info(string message)
    {
        Write("INFO", message);
    }

    public void Warn(string message)
    {
        Write("WARN", message);
    }

    public void Error(Exception ex, string message)
    {
        Write("ERROR", $"{message}\r\n{ex}");
    }

    public void Signal(string message)
    {
        Write("SIGNAL", message, "signals");
    }

    public void Order(string message)
    {
        Write("ORDER", message, "orders");
    }

    private void Write(string level, string message, string prefix = "app")
    {
        AppFolders.EnsureAll();
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        var file = AppFolders.LogFile(prefix);

        lock (_lock)
        {
            File.AppendAllText(file, line + Environment.NewLine);
        }
    }
}
