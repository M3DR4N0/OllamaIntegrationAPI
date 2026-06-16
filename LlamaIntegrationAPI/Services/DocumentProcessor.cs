using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using ImageMagick;
using LlamaIntegrationAPI.Models;
using System.Text;
using Tesseract;
using UglyToad.PdfPig;

namespace OllamaIntegrationAPI.Services
{
    public interface IDocumentProcessor
    {
        Task<string> ProcessAsync(ExtractFromFileRequest request);
    }

    public class DocumentProcessor(ILogger<DocumentProcessor> logger) : IDocumentProcessor
    {
        public async Task<string> ProcessAsync(ExtractFromFileRequest request)
        {
            string documentText;

            await using var stream = request.File?.OpenReadStream();

            var msList = new List<MemoryStream>();
            try
            {
                if (request.Files != null)
                {
                    foreach (var file in request.Files)
                    {
                        var ms = new MemoryStream();
                        await file.CopyToAsync(ms);
                        ms.Position = 0;
                        msList.Add(ms);
                    }
                }

                var contentType = request.File?.ContentType ?? request.Files?.FirstOrDefault()?.ContentType;
                logger.LogInformation("Procesando documento con ContentType: {ContentType}", contentType);

                switch (contentType)
                {
                    case "application/msword":
                    case "application/vnd.openxmlformats-officedocument.wordprocessingml.document":
                        documentText = ExtractTextFromWord(stream!);
                        break;

                    case "application/pdf":
                        documentText = ExtractTextFromPdf(stream!, logger);
                        break;

                    case "image/jpeg":
                    case "image/png":
                    case "image/tiff":
                    case "image/tif":
                        if (stream is null)
                        {
                            documentText = ExtractTextFromImage(msList);
                            break;
                        }

                        documentText = ExtractTextFromImage(stream);
                        break;

                    default:
                        throw new NotSupportedException($"Formato no soportado: {contentType}");
                }
            }
            finally
            {
                foreach (var ms in msList)
                    ms.Dispose();
            }

            return documentText;
        }

        internal static string ExtractTextFromPdf(Stream pdfStream, ILogger? logger = null)
        {
            var sb = new StringBuilder();
            TesseractEngine? engine = null;

            byte[] pdfBytes;
            using (var ms = new MemoryStream())
            {
                pdfStream.CopyTo(ms);
                pdfBytes = ms.ToArray();
            }

            try
            {
                using var pdf = PdfDocument.Open(new MemoryStream(pdfBytes));
                var totalPages = pdf.NumberOfPages;
                logger?.LogInformation("[PDF] Abriendo PDF con {Pages} página(s).", totalPages);

                foreach (var page in pdf.GetPages())
                {
                    var words = page.GetWords().OrderBy(w => w.BoundingBox.Bottom).ToList();
                    logger?.LogInformation("[PDF] Página {PageNum}: {WordCount} palabras encontradas.", page.Number, words.Count);

                    if (words.Count > 0)
                    {
                        double currentLineY = double.MinValue;
                        foreach (var word in words)
                        {
                            if (Math.Abs(word.BoundingBox.Bottom - currentLineY) > 5)
                            {
                                if (sb.Length > 0)
                                    sb.AppendLine();

                                currentLineY = word.BoundingBox.Bottom;
                            }
                            else
                            {
                                sb.Append(' ');
                            }

                            sb.Append(word.Text);
                        }

                        sb.AppendLine();
                        continue;
                    }

                    var embeddedImages = page.GetImages().ToList();
                    logger?.LogInformation("[PDF] Página {PageNum} sin texto — {ImageCount} imagen(es) embebida(s).", page.Number, embeddedImages.Count);

                    if (embeddedImages.Count > 0)
                    {
                        if (engine == null)
                        {
                            try
                            {
                                engine = new TesseractEngine(@"./tessdata", "spa", EngineMode.Default);
                            }
                            catch (Exception ex)
                            {
                                logger?.LogError(ex, "[PDF] No se pudo inicializar Tesseract OCR.");
                                goto renderPage;
                            }
                        }

                        var ocrFromEmbedded = false;
                        foreach (var image in embeddedImages)
                        {
                            try
                            {
                                using var magick = new MagickImage([.. image.RawBytes]);
                                magick.Format = MagickFormat.Png;
                                var pngBytes = magick.ToByteArray();
                                using var pix = Pix.LoadFromMemory(pngBytes);
                                using var result = engine.Process(pix);
                                var ocrText = result.GetText();
                                if (!string.IsNullOrWhiteSpace(ocrText))
                                {
                                    sb.AppendLine(ocrText);
                                    ocrFromEmbedded = true;
                                }
                            }
                            catch (Exception ex)
                            {
                                logger?.LogWarning(ex, "[PDF] Fallo al OCR imagen embebida en página {PageNum}.", page.Number);
                            }
                        }

                        if (ocrFromEmbedded)
                            continue;
                    }

                renderPage:
                    logger?.LogInformation("[PDF] Página {PageNum} — renderizando con Magick.NET/Ghostscript para OCR.", page.Number);
                    try
                    {
                        var settings = new MagickReadSettings
                        {
                            Density = new Density(300, DensityUnit.PixelsPerInch),
                            FrameIndex = (uint)(page.Number - 1),
                            FrameCount = 1,
                            Format = MagickFormat.Pdf
                        };

                        using var rendered = new MagickImage(pdfBytes, settings);
                        rendered.Format = MagickFormat.Png;
                        var renderedPng = rendered.ToByteArray();

                        engine ??= new TesseractEngine(@"./tessdata", "spa", EngineMode.Default);
                        using var pix = Pix.LoadFromMemory(renderedPng);
                        using var result = engine.Process(pix);
                        var ocrText = result.GetText();
                        logger?.LogInformation("[PDF] Ghostscript+OCR extrajo {Chars} chars en página {PageNum}.", ocrText?.Length ?? 0, page.Number);
                        sb.AppendLine(ocrText);
                    }
                    catch (Exception ex)
                    {
                        logger?.LogError(ex, "[PDF] Fallback Ghostscript+OCR falló en página {PageNum}.", page.Number);
                    }
                }
            }
            finally
            {
                engine?.Dispose();
            }

            return sb.ToString();
        }

        internal static string ExtractTextFromWord(Stream stream)
        {
            var sb = new StringBuilder();
            stream.Position = 0;

            using var document = WordprocessingDocument.Open(stream, false);
            if (document.MainDocumentPart is null)
                return string.Empty;

            AppendBlockText(sb, document.MainDocumentPart.Document?.Body);

            foreach (var headerPart in document.MainDocumentPart.HeaderParts)
                AppendBlockText(sb, headerPart.Header);

            foreach (var footerPart in document.MainDocumentPart.FooterParts)
                AppendBlockText(sb, footerPart.Footer);

            var imageParts = EnumerateImageParts(document).ToList();
            if (imageParts.Count == 0)
                return sb.ToString();

            using var engine = new TesseractEngine(@"./tessdata", "spa", EngineMode.Default);

            foreach (var imagePart in imageParts)
            {
                using var imageStream = imagePart.GetStream();
                using var ms = new MemoryStream();
                imageStream.CopyTo(ms);
                ms.Position = 0;

                using var img = Pix.LoadFromMemory(ms.ToArray());
                using var page = engine.Process(img);
                var ocrText = page.GetText();

                if (string.IsNullOrWhiteSpace(ocrText))
                    continue;

                sb.AppendLine(ocrText.Trim());
                sb.AppendLine();
            }

            return sb.ToString();
        }

        internal static string ExtractTextFromImage(Stream imageStream)
        {
            var sb = new StringBuilder();

            using var collection = new MagickImageCollection(imageStream);
            using var engine = new TesseractEngine(@"./tessdata", "spa", EngineMode.Default);

            foreach (var frame in collection)
            {
                using var img = Pix.LoadFromMemory(frame.ToByteArray());
                using var page = engine.Process(img);
                sb.AppendLine(page.GetText());
                sb.AppendLine();
            }

            return sb.ToString();
        }

        internal static string ExtractTextFromImage(List<MemoryStream> imageStreams)
        {
            var sb = new StringBuilder();

            using var engine = new TesseractEngine(@"./tessdata", "spa", EngineMode.Default);

            foreach (var imgStream in imageStreams)
            {
                imgStream.Position = 0;
                using var img = Pix.LoadFromMemory(imgStream.ToArray());
                using var page = engine.Process(img);
                sb.AppendLine(page.GetText());
                sb.AppendLine();
            }

            return sb.ToString();
        }

        private static void AppendBlockText(StringBuilder sb, OpenXmlElement? root)
        {
            if (root is null)
                return;

            foreach (var child in root.ChildElements)
            {
                switch (child)
                {
                    case Paragraph paragraph:
                        AppendLineIfPresent(sb, paragraph.InnerText);
                        break;

                    case Table table:
                        AppendTableText(sb, table);
                        break;

                    case SdtBlock sdtBlock:
                        if (sdtBlock.SdtContentBlock is not null)
                            AppendBlockText(sb, sdtBlock.SdtContentBlock);
                        else
                            AppendBlockText(sb, sdtBlock);
                        break;

                    default:
                        if (child.HasChildren)
                            AppendBlockText(sb, child);
                        break;
                }
            }
        }

        private static void AppendTableText(StringBuilder sb, Table table)
        {
            var appendedAnyRow = false;

            foreach (var row in table.Elements<TableRow>())
            {
                var cells = row.Elements<TableCell>()
                    .Select(cell => NormalizeWhitespace(cell.InnerText))
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .ToList();

                if (cells.Count == 0)
                    continue;

                appendedAnyRow = true;
                sb.AppendLine(string.Join(" | ", cells));
            }

            if (appendedAnyRow)
                sb.AppendLine();
        }

        private static void AppendLineIfPresent(StringBuilder sb, string? text)
        {
            var normalized = NormalizeWhitespace(text);
            if (string.IsNullOrWhiteSpace(normalized))
                return;

            sb.AppendLine(normalized);
        }

        private static string NormalizeWhitespace(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            return string.Join(" ",
                text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        }

        private static IEnumerable<ImagePart> EnumerateImageParts(WordprocessingDocument document)
        {
            if (document.MainDocumentPart is null)
                yield break;

            foreach (var imagePart in document.MainDocumentPart.ImageParts)
                yield return imagePart;

            foreach (var headerPart in document.MainDocumentPart.HeaderParts)
            {
                foreach (var imagePart in headerPart.ImageParts)
                    yield return imagePart;
            }

            foreach (var footerPart in document.MainDocumentPart.FooterParts)
            {
                foreach (var imagePart in footerPart.ImageParts)
                    yield return imagePart;
            }
        }

        private static IEnumerable<string> GetUriImages(Stream imageStream)
        {
            using var collection = new MagickImageCollection(imageStream);

            foreach (var frame in collection)
            {
                frame.ColorSpace = ColorSpace.sRGB;
                frame.Alpha(AlphaOption.Remove);

                using var mem = new MemoryStream();
                frame.Format = MagickFormat.Png;
                frame.Write(mem);

                var b64 = Convert.ToBase64String(mem.ToArray());
                var dataUri = $"data:image/png;base64,{b64}";

                yield return dataUri;
            }
        }

        private static IEnumerable<string> GetUriImages(List<MemoryStream> imageStreams)
        {
            using var collection = new MagickImageCollection(imageStreams.Select(x => new MagickImage(x)));

            foreach (var frame in collection)
            {
                frame.ColorSpace = ColorSpace.sRGB;
                frame.Alpha(AlphaOption.Remove);

                using var mem = new MemoryStream();
                frame.Format = MagickFormat.Png;
                frame.Write(mem);

                var b64 = Convert.ToBase64String(mem.ToArray());
                var dataUri = $"data:image/png;base64,{b64}";

                yield return dataUri;
            }
        }
    }
}
