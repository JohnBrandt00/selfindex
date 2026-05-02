using CodeIndexer.Models;
using System.Text.RegularExpressions;

namespace CodeIndexer.Services;

public static class CodeChunker
{
    private static readonly Regex ClassRegex = new(
        @"(?:public|internal|private|protected|static|abstract|sealed|partial)[\s\r\n]+(?:class|record|struct|interface|enum)\s+(\w+)",
        RegexOptions.Compiled);

    private static readonly Regex MethodRegex = new(
        @"(?:public|internal|private|protected|static|virtual|override|abstract|async)[\s\r\n]+(?:[\w<>\[\]?,\s]+)\s+(\w+)\s*\(",
        RegexOptions.Compiled);

    public static List<CodeChunk> Chunk(string filePath, string repoRoot, int maxLines = 60)
    {
        var chunks = new List<CodeChunk>();
        string[] lines;
        try { lines = File.ReadAllLines(filePath); }
        catch { return chunks; }

        var relative = Path.GetRelativePath(repoRoot, filePath);

        // Always add a whole-file chunk for small files
        if (lines.Length <= maxLines)
        {
            chunks.Add(new CodeChunk
            {
                FilePath = filePath,
                RelativePath = relative,
                ChunkType = "file",
                Symbol = Path.GetFileName(filePath),
                Content = string.Join('\n', lines),
                StartLine = 1,
                EndLine = lines.Length
            });
            return chunks;
        }

        // Find class and method boundaries
        var boundaries = new List<(int line, string type, string name)>();
        for (int i = 0; i < lines.Length; i++)
        {
            var cm = ClassRegex.Match(lines[i]);
            if (cm.Success) boundaries.Add((i, "class", cm.Groups[1].Value));

            var mm = MethodRegex.Match(lines[i]);
            if (mm.Success && !lines[i].TrimStart().StartsWith("//"))
                boundaries.Add((i, "method", mm.Groups[1].Value));
        }

        if (boundaries.Count == 0)
        {
            // No recognizable boundaries: slide window
            for (int i = 0; i < lines.Length; i += maxLines / 2)
            {
                int end = Math.Min(i + maxLines, lines.Length);
                chunks.Add(new CodeChunk
                {
                    FilePath = filePath,
                    RelativePath = relative,
                    ChunkType = "block",
                    Symbol = $"lines {i + 1}-{end}",
                    Content = string.Join('\n', lines[i..end]),
                    StartLine = i + 1,
                    EndLine = end
                });
            }
            return chunks;
        }

        // Emit a chunk per boundary region
        for (int b = 0; b < boundaries.Count; b++)
        {
            int start = boundaries[b].line;
            int end = b + 1 < boundaries.Count ? boundaries[b + 1].line - 1 : lines.Length - 1;

            // Cap chunk size
            if (end - start > maxLines) end = start + maxLines;

            var content = string.Join('\n', lines[start..(end + 1)]);
            chunks.Add(new CodeChunk
            {
                FilePath = filePath,
                RelativePath = relative,
                ChunkType = boundaries[b].type,
                Symbol = boundaries[b].name,
                Content = content,
                StartLine = start + 1,
                EndLine = end + 1
            });
        }

        return chunks;
    }
}
