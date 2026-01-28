using TS4;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddHostedService<Worker3>();

var host = builder.Build();
host.Run();
