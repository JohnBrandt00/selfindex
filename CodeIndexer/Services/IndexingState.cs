namespace CodeIndexer.Services;

public class IndexingState
{
    private readonly List<string> _log = [];
    private readonly object _lock = new();

    public event Action? OnChanged;

    public string WatchPath { get; private set; } = "";
    public bool IsWatching { get; private set; }
    public bool IsIndexing { get; private set; }
    public int TotalFiles { get; private set; }
    public int ProcessedFiles { get; private set; }
    public int TotalChunks { get; private set; }
    public int IndexedChunks { get; private set; }
    public string CurrentFile { get; private set; } = "";
    public IReadOnlyList<string> Log => _log;

    public void SetWatchPath(string path) { WatchPath = path; Notify(); }
    public void SetWatching(bool watching) { IsWatching = watching; Notify(); }

    public void StartIndexing(int totalFiles)
    {
        lock (_lock)
        {
            IsIndexing = true;
            TotalFiles = totalFiles;
            ProcessedFiles = 0;
            TotalChunks = 0;
            IndexedChunks = 0;
        }
        Notify();
    }

    public void FileStart(string file, int chunks)
    {
        lock (_lock)
        {
            CurrentFile = file;
            TotalChunks += chunks;
        }
        AddLog($"Indexing {Path.GetFileName(file)} ({chunks} chunks)");
    }

    public void ChunkIndexed()
    {
        lock (_lock) { IndexedChunks++; }
        Notify();
    }

    public void FileComplete()
    {
        lock (_lock) { ProcessedFiles++; }
        Notify();
    }

    public void FinishIndexing()
    {
        lock (_lock) { IsIndexing = false; CurrentFile = ""; }
        AddLog($"Done. {IndexedChunks} chunks across {ProcessedFiles} files.");
    }

    public void AddLog(string msg)
    {
        lock (_lock)
        {
            _log.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {msg}");
            if (_log.Count > 100) _log.RemoveAt(_log.Count - 1);
        }
        Notify();
    }

    private void Notify() => OnChanged?.Invoke();
}
