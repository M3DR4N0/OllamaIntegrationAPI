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

            try
            {
                using var pdf = PdfDocument.Open(pdfStream);

                foreach (var page in pdf.GetPages())
                {
                    var words = page.GetWords().OrderBy(w => w.BoundingBox.Bottom).ToList();

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

                    if (words.Count == 0)
                    {
                        // Lazy-create a single engine for all OCR pages
                        engine ??= new TesseractEngine(@"./tessdata", "spa", EngineMode.Default);

                        foreach (var image in page.GetImages())
                        {
                            // RawBytes are compressed (JPEG, CCITT, etc.) — decode via ImageMagick
                            // into raw PNG so Tesseract can load them correctly.
                            byte[] pngBytes;
                            try
                            {
                                using var magick = new MagickImage([.. image.RawBytes]);
                                magick.Format = MagickFormat.Png;
                                pngBytes = magick.ToByteArray();
                            }
                            catch (Exception ex)
                            {
                                logger.LogWarning(ex, "Skipping embedded PDF image — ImageMagick could not decode it.");
                                continue;
                            }

                            using var pix = Pix.LoadFromMemory(pngBytes);
                            using var result = engine.Process(pix);
                            sb.AppendLine(result.GetText());
                            sb.AppendLine();
                        }
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
