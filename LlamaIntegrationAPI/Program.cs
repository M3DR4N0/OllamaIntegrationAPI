using LlamaIntegrationAPI.Extensions;
using LlamaIntegrationAPI.Middlewares;
using LlamaIntegrationAPI.Services;
using LlamaIntegrationAPI.Services.Implementations;
using LlamaIntegrationAPI.Services.Interfaces;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using OllamaIntegrationAPI.Helpers;
using OllamaIntegrationAPI.Services;
using Swashbuckle.AspNetCore.SwaggerUI;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.AddEndpointsApiExplorer();

builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = long.MaxValue;
    options.MultipartHeadersLengthLimit = int.MaxValue;
    options.ValueCountLimit = int.MaxValue;
});

// Configuracion de Kestrel para aumentar el limite de tamano de la solicitud
builder.Services.Configure<KestrelServerOptions>(options =>
{
    options.Limits.MaxRequestBufferSize = 1000 * 1024 * 1024;
    options.Limits.MaxRequestHeadersTotalSize = 1000 * 1024 * 1024;
    options.Limits.MaxResponseBufferSize = 1000 * 1024 * 1024;
    options.Limits.MaxRequestBodySize = long.MaxValue;
});

builder.Services.AddScoped<IDocumentProcessor, DocumentProcessor>();
builder.Services.AddScoped<IPayloadBuilder, PayloadBuilder>();
builder.Services.AddHttpClient<IOllamaService, OllamaService>();

// RAG pipeline services
builder.Services.AddScoped<IDocumentParserService, DocumentParserService>();
builder.Services.AddSingleton<IChunkingService, ChunkingService>();
builder.Services.AddSingleton<IVectorStoreService, QdrantVectorStoreService>();
builder.Services.AddHttpClient<IEmbeddingService, EmbeddingService>();
builder.Services.AddHttpClient<ILLMService, LLMService>();
builder.Services.AddScoped<IRerankingService, RerankingService>();
builder.Services.AddScoped<IAnalysisService, AnalysisService>();
builder.Services.AddScoped<IOrchestratorService, OrchestratorService>();

builder.Services.AddCors(o => o.AddPolicy("AllowAll", b => b.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
builder.Services.AddAiServices(builder.Configuration);

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    // Serve Swagger UI at /swagger for easy testing before connecting Laserfiche
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/openapi/v1.json", "OllamaIntegrationAPI v1");
    });
}

// do error middleware

app.UseErrorHandlerMiddleware();

// Only redirect to HTTPS when an HTTPS port is actually configured (not in Docker)
if (!app.Environment.IsDevelopment() || app.Configuration["ASPNETCORE_HTTPS_PORTS"] is not null)
{
    app.UseHttpsRedirection();
}

app.UseCors("AllowAll");

app.UseAuthorization();

app.MapControllers();

app.Run();
