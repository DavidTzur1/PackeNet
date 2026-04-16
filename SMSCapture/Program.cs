using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using SMSCapture;

var builder = WebApplication.CreateBuilder(args);

// Serilog
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .CreateLogger();

builder.Host.UseSerilog();

// Swagger
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// App services
builder.Services.Configure<CaptureOptions>(
    builder.Configuration.GetSection("Capture"));

builder.Services.AddHostedService<CaptureWorker>();

var app = builder.Build();

// Swagger
app.UseSwagger();
app.UseSwaggerUI();

// Simple endpoints
app.MapGet("/", () => Results.Ok("SMS capture worker is running"));

app.MapGet("/health", () => Results.Ok(new
{
    Status = "OK",
    TimeUtc = DateTime.UtcNow
}));

app.Run();