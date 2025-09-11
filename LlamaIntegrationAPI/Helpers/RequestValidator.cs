using LlamaIntegrationAPI.Models;
using System;
using System.ComponentModel.DataAnnotations;

namespace LlamaIntegrationAPI.Helpers
{
    public class LlamaRequestValidation
    {
        public static bool IsValid(LlamaRequest request, out string errorMessage)
        {
            foreach (var rule in RequestValidators.Rules)
            {
                if (!rule.Check(request))
                {
                    errorMessage = rule.ErrorMessage;
                    return false;
                }
            }
            errorMessage = string.Empty;
            return true;
        }

    }

    public record ValidationRule(Predicate<LlamaRequest> Check, string ErrorMessage);

    public static class RequestValidators
    {
        private static readonly List<string> SupportedContentTypes = new()
        {
            "application/pdf",
            "application/msword",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            "image/jpeg",
            "image/png",
            "image/tiff",
            "image/tif"
        };

        public static readonly List<ValidationRule> Rules =
        [
            new ValidationRule(request => request.File != null || (request.TiffFile != null && request.TiffFile.Count != 0),
                "El archivo es requerido."),

            new ValidationRule((request) =>
            {
                bool isValid = true;

                isValid = request.File == null || SupportedContentTypes.Contains(request.File.ContentType);

                isValid = isValid && (request.TiffFile == null || request.TiffFile.All(f => SupportedContentTypes.Contains(f.ContentType)));

                return isValid;
            }, "Formato de archivo no soportado.")
        ];
    }

}
