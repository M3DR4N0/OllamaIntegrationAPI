using UglyToad.PdfPig;
using ImageMagick;
using System.Drawing;
using System.Text;
using Xceed.Words.NET;

namespace OllamaIntegrationAPI.Services
{
    public interface IOcrService
    {
        string? ExtractTextFromPdfAsync(Stream pdfStream);
        string ReadDocx(Stream stream);

        //Task<string> PdfStreamToOcrAllPagesAsync(Stream pdfStream);

        //Task<string> ImageStreamToOcrAsync(Stream imageStream);
        IEnumerable<string> GetUriImages(Stream imageStream);
        IEnumerable<string> GetUriImages(List<MemoryStream> imageStreams); 
    }

    public class OcrService : IOcrService
    {
        public string? ExtractTextFromPdfAsync(Stream pdfStream)
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

        public string ReadDocx(Stream stream)
        {
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            using var doc = DocX.Load(ms);
            return doc.Text;
        }

        //public async Task<string> PdfStreamToOcrAllPagesAsync(Stream pdfStream)
        //{
        //    pdfStream.Position = 0;
        //    var sb = new StringBuilder();

        //    // Magick.NET: configurar density para mejor resolución (ej. 150–300 DPI)
        //    var settings = new MagickReadSettings()
        //    {
        //        Density = new Density(200, 200) // sube si quieres mejor OCR, pero más memoria
        //    };

        //    // MagickImageCollection puede leer desde el stream
        //    using (var images = new MagickImageCollection())
        //    {
        //        pdfStream.Position = 0;
        //        images.Read(pdfStream, settings);

        //        int pageNum = 0;
        //        foreach (var img in images)
        //        {
        //            pageNum++;

        //            // Convertir MagickImage a Bitmap para Tesseract
        //            var pageText = DoTesseractOcr(img.ToByteArray());
        //            sb.AppendLine(pageText ?? "");
        //        }
        //    }

        //    return await Task.FromResult(sb.ToString());
        //}

        //public async Task<string> ImageStreamToOcrAsync(Stream imageStream)
        //{
        //    imageStream.Position = 0;

        //    // Leemos la imagen con Magick para normalizarla (por si viene en TIFF multipágina, etc.)
        //    using (var images = new MagickImageCollection())
        //    {
        //        images.Read(imageStream);

        //        // Si hay varias imágenes (multipage TIFF) procesarlas todas concatenadas
        //        var sb = new StringBuilder();
        //        foreach (var img in images)
        //        {             
        //            //var pageText = DoTesseractOcr(img.ToByteArray());
        //            sb.AppendLine(pageText ?? "");                  
        //        }

        //        return await Task.FromResult(sb.ToString());
        //    }
        //}

        public IEnumerable<string> GetUriImages(Stream imageStream)
        {
            using (var collection = new MagickImageCollection(imageStream))
            {
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

        public IEnumerable<string> GetUriImages(List<MemoryStream> imageStreams)
        {
            using (var collection = new MagickImageCollection(imageStreams.Select(x => new MagickImage(x))))
            {
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
}
