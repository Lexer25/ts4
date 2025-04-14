using TS4;
using Microsoft.Extensions.Logging;

await Host.CreateDefaultBuilder(args).ConfigureServices((context, services) =>
{
    IConfiguration configuration = context.Configuration;

    WorkerOptions options = configuration.GetSection("Service").Get<WorkerOptions>();

    services.AddSingleton(options);
    services.AddHostedService<Worker>();
}).ConfigureLogging((context, logging) =>
{
    logging.ClearProviders();
    logging.AddConfiguration(context.Configuration.GetSection("Logging"));
    logging.AddConsole();
    string path = context.Configuration.GetSection("Log").GetValue<string>("LogFolerPath");
    int? retainedFileCountLimit = context.Configuration.GetSection("Log").GetValue<int?>("retainedFileCountLimit");
    
    logging.AddFile(
        pathFormat: $"{path}\\Artonit2Tools.log",
        minimumLevel: context.Configuration.GetSection("Log").GetValue<LogLevel>("LogLevel"),
        retainedFileCountLimit: retainedFileCountLimit,
        outputTemplate: "{Timestamp:dd-MM-yyyy HH:mm:ss.fff}\t-\t[{Level:u3}] {Message}{NewLine}{Exception}");

}).UseWindowsService().Build().RunAsync();