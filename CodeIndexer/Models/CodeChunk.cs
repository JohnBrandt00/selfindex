namespace CodeIndexer.Models;

public class CodeChunk
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string FilePath { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string ChunkType { get; set; } = "file"; // file | class | method
    public string Symbol { get; set; } = "";        // class or method name
    public string Content { get; set; } = "";
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public float[]? Embedding { get; set; }
    public DateTime IndexedAt { get; set; } = DateTime.UtcNow;
}
