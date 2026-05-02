namespace CodeIndexer.Models;

public class SearchResult
{
    public string Id { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public string ChunkType { get; set; } = "";
    public string Symbol { get; set; } = "";
    public string Content { get; set; } = "";
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public float BM25Score { get; set; }
    public float VectorScore { get; set; }
    public float HybridScore { get; set; }
}
