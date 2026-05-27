namespace KHStrategyLab.Services;

public static class AppFolders
{
    public static string Root => AppContext.BaseDirectory;
    public static string Storage => Path.Combine(Root, "Storage");
    public static string Logs => Path.Combine(Root, "Logs");
    public static string Data => Path.Combine(Root, "Data");

    public static void EnsureAll()
    {
        Directory.CreateDirectory(Storage);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Data);
    }

    public static string StorageFile(string fileName)
    {
        EnsureAll();
        return Path.Combine(Storage, fileName);
    }

    public static string LogFile(string prefix)
    {
        EnsureAll();
        var name = $"{DateTime.Now:yyyyMMdd}_{prefix}.log";
        return Path.Combine(Logs, name);
    }
}
