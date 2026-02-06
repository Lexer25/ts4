using TS4;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Reflection;
using FirebirdSql.Data.FirebirdClient;
using Serilog;
using Serilog.Core;

// Получаем версию из FileVersion
var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "Unknown";

// Глобальные обработчики необработанных исключений — программа не должна завершаться сама по себе
AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
{
    var ex = (Exception)e.ExceptionObject;
    try { Serilog.Log.Fatal(ex, "Необработанное исключение в потоке: {Message}", ex?.Message); } catch { }
    Console.WriteLine($"Необработанное исключение: {ex?.Message}");
};
TaskScheduler.UnobservedTaskException += (sender, e) =>
{
    try { Serilog.Log.Warning(e.Exception, "Необработанное исключение в Task: {Message}", e.Exception?.Message); } catch { }
    Console.WriteLine($"Необработанное исключение в Task: {e.Exception?.Message}");
    e.SetObserved();
};

// Обработка Ctrl+C для корректного завершения
Console.CancelKeyPress += (sender, e) =>
{
    e.Cancel = true;
    Console.WriteLine("\nПолучен сигнал завершения (Ctrl+C). Корректное завершение работы...");
};

// Проверка наличия процедуры и триггера — один раз при запуске программы (в MainLine для каждого контроллера проверка не выполняется)
try
{
    var configuration = new ConfigurationBuilder()
        .SetBasePath(Directory.GetCurrentDirectory())
        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
        .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: true)
        .AddEnvironmentVariables()
        .AddCommandLine(args)
        .Build();

    WorkerOptions options = configuration.GetSection("Service").Get<WorkerOptions>();

    if (options != null && !string.IsNullOrEmpty(options.Firebird_db_config))
    {
        Console.WriteLine("Проверка наличия необходимых объектов базы данных...");

        var connectionStringBuilder = new FbConnectionStringBuilder(options.Firebird_db_config);
        if (connectionStringBuilder.Charset != null && connectionStringBuilder.Charset.ToUpper() != "UTF8")
        {
            connectionStringBuilder.Charset = "UTF8";
        }
        string connectionString = connectionStringBuilder.ToString();

        using (var con = DBFirebird.Connect(connectionString, null))
        {
            try
            {
                con.Open();

                bool procedureExists = DBFirebird.ProcedurChec(con);
                Console.WriteLine(procedureExists
                    ? "Проверка процедуры: процедура CARDINDEV_TS4 найдена в базе данных"
                    : "Проверка процедуры: процедура CARDINDEV_TS4 не найдена в базе данных\nЧтобы создать процедуру откройте файл \"C:\\TS4\\create procedure.bat\"");

                bool triggerExists = DBFirebird.TriggerCheck(con);
                Console.WriteLine(triggerExists
                    ? "Проверка триггера: триггер для события CARDINDEV_INSERT найден в базе данных"
                    : "Проверка триггера: триггер для события CARDINDEV_INSERT не найден в базе данных\nЧтобы создать триггер откройте файл \"C:\\TS4\\create trigger.bat\"");

                con.Close();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка при проверке объектов базы данных: {ex.Message}");
            }
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Ошибка при чтении конфигурации для проверки БД: {ex.Message}");
}

try
{
    await Host.CreateDefaultBuilder(args)
        .ConfigureServices((context, services) =>
        {
            IConfiguration configuration = context.Configuration;
            WorkerOptions options = configuration.GetSection("Service").Get<WorkerOptions>();

            services.AddSingleton(options);
            services.AddSingleton(version);
            services.AddSingleton<TSMethods>();
            services.AddSingleton<Worker>();
            services.AddHostedService<Worker4>();
        })
        .ConfigureLogging((context, logging) =>
        {
            logging.ClearProviders();

            logging.AddConsole(options =>
            {
                options.IncludeScopes = true;
                options.TimestampFormat = "dd-MM-yyyy HH:mm:ss.fff ";
            });

            string logFolderPath = context.Configuration.GetSection("Log").GetValue<string>("LogFolerPath")
                                   ?? Directory.GetCurrentDirectory();

            int? retainedFileCountLimit = context.Configuration.GetSection("Log").GetValue<int?>("retainedFileCountLimit");

            LogLevel minLevel = context.Configuration.GetSection("Log").GetValue<LogLevel>("LogLevel");
            if (!Enum.IsDefined(typeof(LogLevel), minLevel))
            {
                minLevel = LogLevel.Information;
            }

            var serilogLevel = minLevel switch
            {
                LogLevel.Trace => Serilog.Events.LogEventLevel.Verbose,
                LogLevel.Debug => Serilog.Events.LogEventLevel.Debug,
                LogLevel.Information => Serilog.Events.LogEventLevel.Information,
                LogLevel.Warning => Serilog.Events.LogEventLevel.Warning,
                LogLevel.Error => Serilog.Events.LogEventLevel.Error,
                LogLevel.Critical => Serilog.Events.LogEventLevel.Fatal,
                _ => Serilog.Events.LogEventLevel.Information
            };

            var loggerConfig = new LoggerConfiguration()
                .MinimumLevel.Is(serilogLevel);

            // КЛЮЧЕВОЕ ИСПРАВЛЕНИЕ: Асинхронная буферизированная запись
            if (retainedFileCountLimit.HasValue)
            {
                loggerConfig.WriteTo.Async(a => a.File(
                    path: $"{logFolderPath}\\ts4-.log",
                    outputTemplate: "{Timestamp:dd-MM-yyyy HH:mm:ss.fff}\t-\t[{Level:u3}] {Message}{NewLine}{Exception}",
                    rollingInterval: Serilog.RollingInterval.Day,
                    retainedFileCountLimit: retainedFileCountLimit.Value,
                    buffered: true,                     // Буферизация включена — пишем блоками
                    flushToDiskInterval: TimeSpan.FromSeconds(1)  // Flush каждую секунду — быстро и безопасно
                ));
            }
            else
            {
                loggerConfig.WriteTo.Async(a => a.File(
                    path: $"{logFolderPath}\\ts4.log",
                    outputTemplate: "{Timestamp:dd-MM-yyyy HH:mm:ss.fff}\t-\t[{Level:u3}] {Message}{NewLine}{Exception}",
                    buffered: true,
                    flushToDiskInterval: TimeSpan.FromSeconds(1)
                ));
            }

            Log.Logger = loggerConfig.CreateLogger();
            logging.AddSerilog(Log.Logger, dispose: true);
        })
        .UseWindowsService()
        .Build()
        .RunAsync();
}
catch (OperationCanceledException)
{
    Console.WriteLine("Приложение корректно завершено.");
}
catch (Exception ex)
{
    Console.WriteLine($"Критическая ошибка: {ex.Message}");
    Environment.Exit(1);
}
finally
{
    Log.CloseAndFlush();
}