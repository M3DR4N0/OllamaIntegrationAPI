using System.Net;
using static OllamaIntegrationAPI.Models.Response.Response;

namespace OllamaIntegrationAPI.Models.Response
{
    public class ResponseHandler
    {
        public static ResponseSuccess Success(object? data = null, string? message = null, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            return new ResponseSuccess(data, message, statusCode);
        }
        public static ResponseError Error(string? message = null, HttpStatusCode statusCode = HttpStatusCode.InternalServerError)
        {
            return new ResponseError(message, statusCode);
        }
    }
}
