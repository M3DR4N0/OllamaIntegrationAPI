using Tesseract;
using UglyToad.PdfPig;
using ImageMagick;
using System.Drawing;
using System.Text;
using Xceed.Words.NET;

namespace OllamaIntegrationAPI.Services
{
    public interface IOcrService
    {
        Task<string> ExtractTextAsync(Stream fileStream, string fileExtension);
    }

    public class OcrService : IOcrService
    {
        private readonly string _tessDataPath;
        private readonly string _defaultLang;

        public OcrService(IWebHostEnvironment env, string tessLang = "spa")
        {
            // Ajusta la ruta según cómo quieras distribuir tessdata
            _tessDataPath = Path.Combine(env.ContentRootPath, "tessdata");
            _defaultLang = tessLang;

            if (!Directory.Exists(_tessDataPath))
                throw new DirectoryNotFoundException($"No se encontró la carpeta tessdata en {_tessDataPath}. Coloca los .traineddata ahí.");
        }

        public async Task<string> ExtractTextAsync(Stream fileStream, string fileExtension)
        {
            if (fileStream == null)
                throw new ArgumentNullException(nameof(fileStream));

            fileStream.Position = 0;
            var ext = (fileExtension ?? "").ToLowerInvariant();

            return ext switch
            {
                //".txt" => ReadTxt(fileStream),
                ".docx" => ReadDocx(fileStream),
                ".pdf" => await ExtractTextFromPdfAsync(fileStream),
                ".png" or ".jpg" or ".jpeg" => await ImageStreamToOcrAsync(fileStream),
                _ => throw new NotSupportedException("Formato no soportado.")
            };
        }

        private async Task<string> ExtractTextFromPdfAsync(Stream pdfStream)
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
                        // Página sin texto -> renderizarla como imagen y hacer OCR
                        // Para esto necesitamos volver a leer el pdf y renderizar la página específica.
                        // Magick.NET puede leer el stream original sólo si lo posicionamos al inicio y leemos todas las páginas,
                        // así que vamos a renderizar el PDF completo vía Magick y luego procesar página por página.
                        // Para simplificar, rompemos y procesamos con Magick.NET todo el PDF:
                        sb.AppendLine(await PdfStreamToOcrAllPagesAsync(pdfStream));
                        break; // ya procesamos con Magick, salimos del loop de PdfPig
                    }
                }
            }

            return sb.ToString();
        }

        private string ReadDocx(Stream stream)
        {
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            using var doc = DocX.Load(ms);
            return doc.Text;
        }

        private async Task<string> PdfStreamToOcrAllPagesAsync(Stream pdfStream)
        {
            pdfStream.Position = 0;
            var sb = new StringBuilder();

            // Magick.NET: configurar density para mejor resolución (ej. 150–300 DPI)
            var settings = new MagickReadSettings()
            {
                Density = new Density(200, 200) // sube si quieres mejor OCR, pero más memoria
            };

            // MagickImageCollection puede leer desde el stream
            using (var images = new MagickImageCollection())
            {
                pdfStream.Position = 0;
                images.Read(pdfStream, settings);

                int pageNum = 0;
                foreach (var img in images)
                {
                    pageNum++;

                    // Convertir MagickImage a Bitmap para Tesseract
                    var pageText = DoTesseractOcr(img.ToByteArray());
                    sb.AppendLine(pageText ?? "");
                }
            }

            return await Task.FromResult(sb.ToString());
        }

        private async Task<string> ImageStreamToOcrAsync(Stream imageStream)
        {
            imageStream.Position = 0;

            // Leemos la imagen con Magick para normalizarla (por si viene en TIFF multipágina, etc.)
            using (var images = new MagickImageCollection())
            {
                images.Read(imageStream);

                // Si hay varias imágenes (multipage TIFF) procesarlas todas concatenadas
                var sb = new StringBuilder();
                foreach (var img in images)
                {             
                    var pageText = DoTesseractOcr(img.ToByteArray());
                    sb.AppendLine(pageText ?? "");                  
                }

                return await Task.FromResult(sb.ToString());
            }
        }

        private string DoTesseractOcr(byte[] bytes)
        {
            // Ajustes: podrías convertir a escala de grises, aplicar binarización, etc. si hace falta mejorar OCR.
            using var engine = new TesseractEngine(_tessDataPath, _defaultLang, EngineMode.Default);      

            // With the following corrected code:
            using var pix = Pix.LoadFromMemory(bytes);
            using var page = engine.Process(pix);
            var text = page.GetText();
            return text ?? string.Empty;
        }
    }
}
