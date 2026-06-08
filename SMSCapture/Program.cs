using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using SMSCapture;
using PacketDotNet.SMS;

var builder = WebApplication.CreateBuilder(args);

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .CreateLogger();

builder.Host.UseSerilog();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.Configure<CaptureOptions>(
    builder.Configuration.GetSection("Capture"));

// SOAP HTTP client
builder.Services.AddHttpClient("SmsSoap", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Soap:BaseUrl"]!);
    client.Timeout = TimeSpan.FromSeconds(15);
});

// background services
builder.Services.AddHostedService<CaptureWorker>();
builder.Services.AddHostedService<SmsSoapSender>();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

app.MapGet("/", () => Results.Ok("SMS capture worker is running"));

app.MapGet("/health", () => Results.Ok(new
{
    Status = "OK",
    TimeUtc = DateTime.UtcNow
}));

app.Run();