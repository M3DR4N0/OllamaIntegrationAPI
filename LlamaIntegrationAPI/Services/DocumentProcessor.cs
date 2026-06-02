using ImageMagick;
using LlamaIntegrationAPI.Models;
using System;
using System.Text;
using Tesseract;
using UglyToad.PdfPig;
using Xceed.Words.NET;

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

            // Usa stream principal si hay file
            await using var stream = request.File?.OpenReadStream();

            // Si hay TIFFs, conviértelos a MemoryStream
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

            // Magick.NET necesita leer el PDF desde bytes (no desde stream que ya usa PdfPig)
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
                        // Texto digital — extraer directamente
                        double currentLineY = double.MinValue;
                        foreach (var word in words)
                        {
                            if (Math.Abs(word.BoundingBox.Bottom - currentLineY) > 5)
                            {
                                if (sb.Length > 0) sb.AppendLine();
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

                    // Página sin texto — intentar con imágenes embebidas primero
                    var embeddedImages = page.GetImages().ToList();
                    logger?.LogInformation("[PDF] Página {PageNum} sin texto — {ImageCount} imagen(es) embebida(s).", page.Number, embeddedImages.Count);

                    if (embeddedImages.Count > 0)
                    {
                        // Inicializar Tesseract si no está listo
                        if (engine == null)
                        {
                            try { engine = new TesseractEngine(@"./tessdata", "spa", EngineMode.Default); }
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

                        if (ocrFromEmbedded) continue;
                    }

                    // Fallback: renderizar la página entera con Magick.NET (requiere Ghostscript)
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

            using var doc = DocX.Load(stream);

            foreach (var para in doc.Paragraphs)
            {
                if (!string.IsNullOrWhiteSpace(para.Text))
                    sb.AppendLine(para.Text);
            }

            if (doc.Pictures.Count == 0)
                return sb.ToString();

            // Single engine for all pictures
            using var engine = new TesseractEngine(@"./tessdata", "spa", EngineMode.Default);

            foreach (var pic in doc.Pictures)
            {
                using var ms = new MemoryStream();
                pic.Stream.CopyTo(ms);
                ms.Position = 0;

                using var img = Pix.LoadFromMemory(ms.ToArray());
                using var page = engine.Process(img);
                sb.AppendLine(page.GetText());
                sb.AppendLine();
            }

            return sb.ToString();
        }

        internal static string ExtractTextFromImage(Stream imageStream)
        {
            StringBuilder sb = new();

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
            StringBuilder sb = new();

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

        private static IEnumerable<string> GetUriImages(Stream imageStream)
        {
            using var collection = new MagickImageCollection(imageStream);

            foreach (var frame in collection)
            {
                // Limpieza básica (opcional)
                frame.ColorSpace = ColorSpace.sRGB;
                frame.Alpha(AlphaOption.Remove);

                using var mem = new MemoryStream();
                // PNG para texto (o cambia a Jpeg si prefieres)
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
                // Limpieza básica (opcional)
                frame.ColorSpace = ColorSpace.sRGB;
                frame.Alpha(AlphaOption.Remove);

                using var mem = new MemoryStream();
                // PNG para texto (o cambia a Jpeg si prefieres)
                frame.Format = MagickFormat.Png;
                frame.Write(mem);

                var b64 = Convert.ToBase64String(mem.ToArray());

                var dataUri = $"data:image/png;base64,{b64}";

                yield return dataUri;
            }
        }
    }
}
