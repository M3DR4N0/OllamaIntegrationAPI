using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using OllamaIntegrationAPI.Middlewares;
using OllamaIntegrationAPI.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.Configure<FormOptions>(options =>
{
    //options.MultipartBodyLengthLimit = 1000 * 1024 * 1024; // 200 MB en bytes
    //options.MultipartHeadersLengthLimit = 1000 * 1024 * 1024;

    options.MultipartBodyLengthLimit = long.MaxValue;
    options.MultipartHeadersLengthLimit = int.MaxValue;
    options.ValueCountLimit = int.MaxValue;
});

// Configuración de Kestrel para aumentar el límite de tamaño de la solicitud
builder.Services.Configure<KestrelServerOptions>(options =>
{
    //options.Limits.MaxRequestBodySize = 1000 * 1024 * 1024;
    options.Limits.MaxRequestBufferSize = 1000 * 1024 * 1024; // 200 MB
    options.Limits.MaxRequestHeadersTotalSize = 1000 * 1024 * 1024; // 

    options.Limits.MaxResponseBufferSize = 1000 * 1024 * 1024; // 200 MB

    options.Limits.MaxRequestBodySize = long.MaxValue;
    //options.Limits.MaxRequestBufferSize = long.MaxValue;
    //options.Limits.MaxRequestHeadersTotalSize = int.MaxValue;

    //options.Limits.MaxResponseBufferSize = long.MaxValue;

});

builder.Services.AddScoped<IOcrService, OcrService>();
builder.Services.AddHttpClient<IOllamaService, OllamaService>();
builder.Services.AddCors(o => o.AddPolicy("AllowAll", b => b.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// do error middleware

app.UseErrorHandlerMiddleware();

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
