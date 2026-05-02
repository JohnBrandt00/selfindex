using CodeIndexer.Models;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using HtmlAgilityPack;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace CodeIndexer.Services;

/// <summary>
/// Extracts text (and image descriptions via Ollama vision) from non-code files.
/// Mirrors Azure AI Search document cracking: PDF, Office, HTML, and image formats.
/// Falls back to CodeChunker for source code files.
/// </summary>
public class DocumentCracker(VisionService vision, ILogger<DocumentCracker> logger)
{
    private static readonly HashSet<string> ImageExts = [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tiff", ".tif"];
    private static readonly HashSet<string> CodeExts  = [
        ".cs", ".ts", ".tsx", ".js", ".jsx", ".py", ".go", ".rs", ".java", ".kt",
        ".cpp", ".c", ".h", ".hpp", ".md", ".txt", ".json", ".yaml", ".yml",
        ".xml", ".sql", ".toml", ".sh", ".ps1", ".bat", ".razor", ".css", ".scss",
        ".rb", ".php", ".swift", ".fs", ".fsx", ".vue", ".svelte"
    ];

    public async Task<List<CodeChunk>> CrackAsync(string filePath, string repoRoot, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        var rel = Path.GetRelativePath(repoRoot, filePath);

        var cracker = ext switch
        {
            ".pdf"                         => "pdf",
            ".docx"                        => "docx",
            ".xlsx"                        => "xlsx",
            ".pptx"                        => "pptx",
            ".html" or ".htm"              => "html",
            _ when ImageExts.Contains(ext) => "image",
            _                              => "code"
        };
        logger.LogDebug("Cracking {File} via {Cracker}", rel, cracker);

        try
        {
            var chunks = cracker switch
            {
                "pdf"   => await CrackPdfAsync(filePath, rel, ct),
                "docx"  => await CrackDocxAsync(filePath, rel, ct),
                "xlsx"  => CrackXlsx(filePath, rel),
                "pptx"  => await CrackPptxAsync(filePath, rel, ct),
                "html"  => CrackHtml(filePath, rel),
                "image" => await CrackImageAsync(filePath, rel, ct),
                _       => CodeChunker.Chunk(filePath, repoRoot)
            };
            logger.LogDebug("  {File}: {Count} chunks", rel, chunks.Count);
            return chunks;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "DocumentCracker ({Cracker}) failed for {File}: {Message}",
                cracker, rel, ex.Message);
            return [];
        }
    }

    // ── PDF ────────────────────────────────────────────────────────────────────

    private async Task<List<CodeChunk>> CrackPdfAsync(string filePath, string rel, CancellationToken ct)
    {
        var chunks = new List<CodeChunk>();
        using var pdf = PdfDocument.Open(filePath);

        foreach (var page in pdf.GetPages())
        {
            ct.ThrowIfCancellationRequested();

            var text = ContentOrderTextExtractor.GetText(page).Trim();

            if (vision.IsConfigured)
            {
                // Describe meaningful images on every page (skip tiny icons: < 64×64)
                var imgs = page.GetImages()
                    .Where(i => i.WidthInSamples >= 64 && i.HeightInSamples >= 64)
                    .Take(5) // cap per-page to avoid runaway cost on image-heavy pages
                    .ToList();

                if (imgs.Count > 0)
                    logger.LogDebug("  PDF {Rel} p{Page}: {TextLen} chars, {ImgCount} images",
                        rel, page.Number, text.Length, imgs.Count);

                foreach (var img in imgs)
                {
                    var bytes = TryRenderImageToPng(img);
                    if (bytes is null)
                    {
                        img.TryGetBytesAsMemory(out var dbgMem);
                        int rawLen = img.RawBytes.ToArray().Length;
                        logger.LogDebug("    Skipped w={W} h={H} raw={Raw} dec={Dec}",
                            img.WidthInSamples, img.HeightInSamples, rawLen, dbgMem.Length);
                        continue;
                    }
                    logger.LogDebug("    Sending {Len} bytes PNG to vision (w={W} h={H})",
                        bytes.Length, img.WidthInSamples, img.HeightInSamples);
                    var desc = await vision.DescribeAsync(bytes, ct);
                    if (desc is not null)
                    {
                        logger.LogDebug("    Vision returned {Len} chars for p{Page}", desc.Length, page.Number);
                        text = string.IsNullOrEmpty(text) ? desc : text + "\n\n[Image: " + desc + "]";
                    }
                }
            }

            if (text.Length == 0) continue;

            chunks.Add(new CodeChunk
            {
                FilePath     = filePath,
                RelativePath = rel,
                ChunkType    = "page",
                Symbol       = $"Page {page.Number}",
                Content      = text,
                StartLine    = page.Number,
                EndLine      = page.Number
            });
        }

        return chunks;
    }

    // Converts a PdfPig image to a PNG byte array suitable for sending to Ollama vision.
    // Handles three cases in order of preference:
    //   1. PdfPig can export a direct PNG (embedded JPEG/PNG that decoded cleanly)
    //   2. Raw bytes are a recognised image format ImageSharp can load (JPEG with CMYK, etc.)
    //   3. Raw bytes are headerless raw RGB pixels — reconstruct using PdfPig dimension metadata
    private static byte[]? TryRenderImageToPng(IPdfImage img)
    {
        try
        {
            // Case 1: PdfPig already gives us a valid PNG
            if (img.TryGetPng(out var directPng) && directPng is { Length: > 1024 })
                return directPng;

            var raw = img.RawBytes.ToArray();
            if (raw.Length < 100) return null;

            // Case 2: raw bytes are a self-describing format (JPEG, GIF, etc.)
            var decoded = NormalizeToPng(raw);
            if (decoded is not null) return decoded;

            // Case 3: try decoded (filter-decompressed) bytes — PdfPig applies FlateDecode/etc.
            byte[]? decompressed = null;
            if (img.TryGetBytesAsMemory(out var mem))
                decompressed = mem.ToArray();

            if (decompressed is { Length: > 100 })
            {
                var fromDecompressed = NormalizeToPng(decompressed);
                if (fromDecompressed is not null) return fromDecompressed;
            }

            // Case 4: headerless uncompressed pixels — reconstruct using PdfPig dimension metadata
            var pixels = decompressed ?? raw;
            int w = img.WidthInSamples;
            int h = img.HeightInSamples;
            if (w <= 0 || h <= 0) return null;

            // 24-bit RGB (3 bytes/pixel) is the most common case for DeviceRGB images from PdfPig
            if (pixels.Length == w * h * 3)
            {
                using var bitmap = SixLabors.ImageSharp.Image.LoadPixelData<Rgb24>(pixels, w, h);
                using var ms = new MemoryStream();
                bitmap.Save(ms, new PngEncoder());
                return ms.ToArray();
            }

            // 8-bit grayscale (1 byte/pixel)
            if (pixels.Length == w * h)
            {
                using var bitmap = SixLabors.ImageSharp.Image.LoadPixelData<L8>(pixels, w, h);
                using var ms = new MemoryStream();
                bitmap.Save(ms, new PngEncoder());
                return ms.ToArray();
            }

            // 32-bit RGBA
            if (pixels.Length == w * h * 4)
            {
                using var bitmap = SixLabors.ImageSharp.Image.LoadPixelData<Rgba32>(pixels, w, h);
                using var ms = new MemoryStream();
                bitmap.Save(ms, new PngEncoder());
                return ms.ToArray();
            }

            return null;
        }
        catch { return null; }
    }

    // Re-encode anything ImageSharp can load (JPEG, GIF, BMP, WebP…) into a clean sRGB PNG.
    // Returns null if the bytes are not a recognised self-describing image format.
    private static byte[]? NormalizeToPng(byte[] input)
    {
        try
        {
            using var img = SixLabors.ImageSharp.Image.Load(input);
            using var ms  = new MemoryStream();
            img.Save(ms, new PngEncoder());
            return ms.ToArray();
        }
        catch { return null; }
    }

    // ── Word (.docx) ───────────────────────────────────────────────────────────

    private async Task<List<CodeChunk>> CrackDocxAsync(string filePath, string rel, CancellationToken ct)
    {
        var chunks = new List<CodeChunk>();
        using var doc = WordprocessingDocument.Open(filePath, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null) return chunks;

        // Extract paragraphs and tables as sliding text windows
        var paragraphs = body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Paragraph>()
            .Select(p => p.InnerText.Trim())
            .Where(t => t.Length > 0)
            .ToList();

        var tables = body.Descendants<DocumentFormat.OpenXml.Wordprocessing.Table>()
            .Select(TableToText)
            .Where(t => t.Length > 0)
            .ToList();

        var allText = paragraphs.Concat(tables).ToList();
        foreach (var (chunk, idx) in SlidingChunks(allText, 30, filePath, rel, "section"))
        {
            chunks.Add(chunk);
        }

        // Image parts
        if (vision.IsConfigured && doc.MainDocumentPart?.ImageParts is { } imgParts)
        {
            int imgNum = 0;
            foreach (var imgPart in imgParts)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var stream = imgPart.GetStream();
                    var bytes = ReadStream(stream);
                    var desc = await vision.DescribeAsync(bytes, ct);
                    if (desc is null) continue;
                    imgNum++;
                    chunks.Add(new CodeChunk
                    {
                        FilePath     = filePath,
                        RelativePath = rel,
                        ChunkType    = "image",
                        Symbol       = $"Image {imgNum}",
                        Content      = desc,
                        StartLine    = imgNum,
                        EndLine      = imgNum
                    });
                }
                catch { /* skip unreadable image */ }
            }
        }

        return chunks;
    }

    private static string TableToText(DocumentFormat.OpenXml.Wordprocessing.Table table)
    {
        var rows = table.Descendants<DocumentFormat.OpenXml.Wordprocessing.TableRow>()
            .Select(r => string.Join(" | ", r.Descendants<DocumentFormat.OpenXml.Wordprocessing.TableCell>()
                .Select(c => c.InnerText.Trim())));
        return string.Join("\n", rows);
    }

    // ── Excel (.xlsx) ──────────────────────────────────────────────────────────

    private static List<CodeChunk> CrackXlsx(string filePath, string rel)
    {
        var chunks = new List<CodeChunk>();
        using var doc = SpreadsheetDocument.Open(filePath, false);
        var workbook = doc.WorkbookPart;
        if (workbook is null) return chunks;

        var sheets = workbook.Workbook.Descendants<Sheet>().ToList();
        var sharedStrings = workbook.SharedStringTablePart?.SharedStringTable
            .Descendants<SharedStringItem>()
            .Select(s => s.InnerText)
            .ToArray();

        int sheetNum = 0;
        foreach (var sheet in sheets)
        {
            sheetNum++;
            var part = workbook.GetPartById(sheet.Id!) as WorksheetPart;
            if (part is null) continue;

            var rows = part.Worksheet.Descendants<Row>()
                .Select(r => string.Join(" | ", r.Descendants<Cell>()
                    .Select(c => CellValue(c, sharedStrings))))
                .Where(r => r.Trim().Length > 0)
                .ToList();

            if (rows.Count == 0) continue;

            foreach (var (chunk, _) in SlidingChunks(rows, 50, filePath, rel, "sheet",
                symbolPrefix: sheet.Name?.Value ?? $"Sheet{sheetNum}",
                startOffset: sheetNum * 10000))
            {
                chunks.Add(chunk);
            }
        }

        return chunks;
    }

    private static string CellValue(Cell cell, string[]? sharedStrings)
    {
        var val = cell.CellValue?.Text ?? "";
        if (cell.DataType?.Value == CellValues.SharedString &&
            int.TryParse(val, out var idx) &&
            sharedStrings is not null && idx < sharedStrings.Length)
        {
            return sharedStrings[idx];
        }
        return val;
    }

    // ── PowerPoint (.pptx) ────────────────────────────────────────────────────

    private async Task<List<CodeChunk>> CrackPptxAsync(string filePath, string rel, CancellationToken ct)
    {
        var chunks = new List<CodeChunk>();
        using var prs = PresentationDocument.Open(filePath, false);
        var presentationPart = prs.PresentationPart;
        if (presentationPart is null) return chunks;

        var slideIds = presentationPart.Presentation.SlideIdList?
            .Descendants<DocumentFormat.OpenXml.Presentation.SlideId>()
            .ToList() ?? [];

        for (int i = 0; i < slideIds.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var slidePart = presentationPart.GetPartById(slideIds[i].RelationshipId!) as SlidePart;
            if (slidePart is null) continue;

            var texts = slidePart.Slide.Descendants<DocumentFormat.OpenXml.Drawing.Text>()
                .Select(t => t.Text.Trim())
                .Where(t => t.Length > 0)
                .ToList();

            var slideText = string.Join("\n", texts);
            var slideNum  = i + 1;

            // Vision for images embedded in the slide
            if (vision.IsConfigured)
            {
                int imgNum = 0;
                foreach (var imgPart in slidePart.ImageParts)
                {
                    try
                    {
                        using var stream = imgPart.GetStream();
                        var bytes = ReadStream(stream);
                        var desc = await vision.DescribeAsync(bytes, ct);
                        if (desc is not null)
                        {
                            imgNum++;
                            slideText = string.IsNullOrEmpty(slideText)
                                ? desc
                                : slideText + "\n\n[Image: " + desc + "]";
                        }
                    }
                    catch { /* skip */ }
                }
            }

            if (slideText.Length == 0) continue;

            chunks.Add(new CodeChunk
            {
                FilePath     = filePath,
                RelativePath = rel,
                ChunkType    = "slide",
                Symbol       = $"Slide {slideNum}",
                Content      = slideText,
                StartLine    = slideNum,
                EndLine      = slideNum
            });
        }

        return chunks;
    }

    // ── HTML ───────────────────────────────────────────────────────────────────

    private static List<CodeChunk> CrackHtml(string filePath, string rel)
    {
        var chunks = new List<CodeChunk>();
        var htmlDoc = new HtmlDocument();
        htmlDoc.Load(filePath);

        // Remove script and style noise
        var noiseNodes = htmlDoc.DocumentNode.SelectNodes("//script|//style");
        if (noiseNodes != null)
            foreach (var node in noiseNodes.ToList())
                node.Remove();

        var paragraphNodes = htmlDoc.DocumentNode
            .SelectNodes("//p|//h1|//h2|//h3|//h4|//h5|//h6|//li|//td|//th|//pre|//blockquote");
        var paragraphs = paragraphNodes ?? new HtmlNodeCollection(null);

        var lines = paragraphs
            .Select(n => HtmlEntity.DeEntitize(n.InnerText).Trim())
            .Where(t => t.Length > 0)
            .ToList();

        if (lines.Count == 0)
        {
            // Fallback: just grab all text from the body
            var body = htmlDoc.DocumentNode.SelectSingleNode("//body") ?? htmlDoc.DocumentNode;
            lines = [HtmlEntity.DeEntitize(body.InnerText).Trim()];
        }

        foreach (var (chunk, _) in SlidingChunks(lines, 30, filePath, rel, "section"))
            chunks.Add(chunk);

        return chunks;
    }

    // ── Standalone images ──────────────────────────────────────────────────────

    private async Task<List<CodeChunk>> CrackImageAsync(string filePath, string rel, CancellationToken ct)
    {
        if (!vision.IsConfigured) return [];

        var raw   = await File.ReadAllBytesAsync(filePath, ct);
        var bytes = NormalizeToPng(raw) ?? raw;
        var desc  = await vision.DescribeAsync(bytes, ct);
        if (desc is null) return [];

        return [new CodeChunk
        {
            FilePath     = filePath,
            RelativePath = rel,
            ChunkType    = "image",
            Symbol       = Path.GetFileName(filePath),
            Content      = desc,
            StartLine    = 1,
            EndLine      = 1
        }];
    }

    // ── Utilities ──────────────────────────────────────────────────────────────

    private static IEnumerable<(CodeChunk chunk, int index)> SlidingChunks(
        List<string> lines, int windowSize,
        string filePath, string rel, string chunkType,
        string symbolPrefix = "", int startOffset = 0)
    {
        int step = windowSize / 2;
        int idx  = 0;
        for (int i = 0; i < lines.Count; i += step)
        {
            int end  = Math.Min(i + windowSize, lines.Count);
            var text = string.Join("\n", lines[i..end]);
            if (text.Trim().Length == 0) continue;

            var lineStart = startOffset + i + 1;
            var label     = string.IsNullOrEmpty(symbolPrefix)
                ? $"Lines {lineStart}–{startOffset + end}"
                : $"{symbolPrefix} ({lineStart}–{startOffset + end})";

            yield return (new CodeChunk
            {
                FilePath     = filePath,
                RelativePath = rel,
                ChunkType    = chunkType,
                Symbol       = label,
                Content      = text,
                StartLine    = lineStart,
                EndLine      = startOffset + end
            }, idx++);
        }
    }

    private static byte[] ReadStream(Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}
