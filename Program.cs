using Microsoft.Extensions.Hosting;
using SMSCapture;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((context, services) =>
    {
        // Bind CaptureOptions from configuration section
        services.Configure<CaptureOptions>(context.Configuration.GetSection("CaptureOptions"));

        // If DbConnectionString not set inside CaptureOptions section, use ConnectionStrings:Provisioning
        services.Configure<CaptureOptions>(opts =>
        {
            if (string.IsNullOrWhiteSpace(opts.DbConnectionString))
            {
                opts.DbConnectionString = context.Configuration.GetConnectionString("Provisioning");
            }
        });

        services.AddHostedService<CaptureWorker>();
    })
    .Build();

await host.RunAsync();  