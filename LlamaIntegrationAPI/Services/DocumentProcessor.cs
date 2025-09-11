using ImageMagick;
using LlamaIntegrationAPI.Models;
using System.Text;
using UglyToad.PdfPig;
using Xceed.Words.NET;

namespace OllamaIntegrationAPI.Services
{
    public interface IDocumentProcessor
    {
        Task<(string? text, IEnumerable<string>? images_url)> ProcessAsync(LlamaRequest request);   
    }

    public class DocumentProcessor(ILogger<DocumentProcessor> logger) : IDocumentProcessor 
    {
        public async Task<(string? text, IEnumerable<string>? images_url)> ProcessAsync(LlamaRequest request) 
        {
            string? documentText = null;
            IEnumerable<string>? uriImages = null;

            // Usa stream principal si hay file
            Stream? stream = request.File?.OpenReadStream();

            // Si hay TIFFs, conviértelos a MemoryStream
            var msList = new List<MemoryStream>();
            if (request.TiffFile != null)
            {
                foreach (var file in request.TiffFile)
                {
                    var ms = new MemoryStream();
                    await file.CopyToAsync(ms);
                    ms.Position = 0;
                    msList.Add(ms);
                }
            }

            var contentType = request.File?.ContentType ?? request.TiffFile?.FirstOrDefault()?.ContentType;
            logger.LogInformation("Procesando documento con ContentType: {ContentType}", contentType);

            switch (contentType)
            {
                case "application/msword":
                case "application/vnd.openxmlformats-officedocument.wordprocessingml.document":
                    documentText = ReadDocx(stream!);
                    break;

                case "application/pdf":
                    documentText = ExtractTextFromPdfAsync(stream!);
                    if (string.IsNullOrWhiteSpace(documentText))
                        uriImages = GetUriImages(stream!); 
                    break;

                case "image/jpeg":
                case "image/png":
                case "image/tiff":
                case "image/tif":
                    uriImages = GetUriImages(msList);
                    break;

                default:
                    throw new NotSupportedException($"Formato no soportado: {contentType}");
            }

            return (documentText, uriImages);

        }

        private static string? ExtractTextFromPdfAsync(Stream pdfStream)
        {
            var sb = new StringBuilder();

            // Primero intentar extraer texto con PdfPig (si el PDF tiene texto seleccionado)
            pdfStream.Position = 0;
            using (var pdf = PdfDocument.Open(pdfStream))
            {
                int pageIndex = 0;
                foreach (var page in pdf.GetPages())
                {
                    pageIndex++;
                    var pageText = page.Text;
                    if (!string.IsNullOrWhiteSpace(pageText))
                    {
                        sb.AppendLine(pageText);
                    }
                    else
                    {
                        return null;
                    }
                }
            }

            return sb.ToString();
        }

        private static string ReadDocx(Stream stream)
        {
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            using var doc = DocX.Load(ms);
            return doc.Text;
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
