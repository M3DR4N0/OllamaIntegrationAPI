using System.Net;

namespace LlamaIntegrationAPI.Models.Response
{
    public interface IResponse
    {
        HttpStatusCode StatusCode { get; }
        bool Success { get; }
        object? Data { get; }
        string? Message { get; }
    }

    public record Response(
        HttpStatusCode StatusCode,
        bool Success,
        object? Data = null,
        string? Message = null) : IResponse
    {

        object? IResponse.Data => Data;

        public sealed record ResponseSuccess : Response 
        {
            public ResponseSuccess(object? data, string? message, HttpStatusCode statusCode)
                : base(statusCode, true, data, message)
            {
            }
        }

        public sealed record ResponseError : Response 
        {
            public ResponseError(string? message, HttpStatusCode statusCode)
                : base(statusCode, false, null, message)
            {
            }
        }
    }

    
}