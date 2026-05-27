using Newtonsoft.Json;

namespace KHStrategyLab.Services;

public sealed class JsonFileStore
{
    private readonly object _lock = new();

    public List<T> LoadList<T>(string fileName)
    {
        var path = AppFolders.StorageFile(fileName);
        if (!File.Exists(path)) return [];

        lock (_lock)
        {
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return [];
            return JsonConvert.DeserializeObject<List<T>>(json) ?? [];
        }
    }

    public void SaveList<T>(string fileName, IEnumerable<T> items)
    {
        var path = AppFolders.StorageFile(fileName);
        var json = JsonConvert.SerializeObject(items, Formatting.Indented);

        lock (_lock)
        {
            File.WriteAllText(path, json);
        }
    }

    public T? LoadObject<T>(string fileName) where T : class
    {
        var path = AppFolders.StorageFile(fileName);
        if (!File.Exists(path)) return null;

        lock (_lock)
        {
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return null;
            return JsonConvert.DeserializeObject<T>(json);
        }
    }

    public void SaveObject<T>(string fileName, T value)
    {
        var path = AppFolders.StorageFile(fileName);
        var json = JsonConvert.SerializeObject(value, Formatting.Indented);

        lock (_lock)
        {
            File.WriteAllText(path, json);
        }
    }
}
