using System.Collections.Concurrent;

namespace NzbWebDAV.WebDav.Base;

public class BaseStoreEmptyFileManager
{
    private static readonly ConcurrentDictionary<string, BaseStoreEmptyFileManager> emptyFileManagers = new();
    private readonly ConcurrentDictionary<string, BaseStoreEmptyFile> _emptyFiles = new();

    public static BaseStoreEmptyFileManager GetEmptyFileManager(string uuid)
    {
        lock (emptyFileManagers)
        {
            if (emptyFileManagers.TryGetValue(uuid, out var result))
                return result;
            result = new BaseStoreEmptyFileManager();
            emptyFileManagers[uuid] = result;
            return result;
        }
    }

    public ICollection<BaseStoreEmptyFile> GetAllEmptyFiles()
    {
        return _emptyFiles.Values;
    }

    public BaseStoreEmptyFile? Get(string name)
    {
        return _emptyFiles.TryGetValue(name, out var result) ? result : null;
    }

    public void Add(BaseStoreEmptyFile file)
    {
        _ = _emptyFiles.TryAdd(file.Name, file);
    }

    public bool Remove(string name)
    {
        return _emptyFiles.TryRemove(name, out _);
    }
}
