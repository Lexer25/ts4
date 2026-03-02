using TS4;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Reflection;
using System.Diagnostics;
using FirebirdSql.Data.FirebirdClient;
using Newtonsoft.Json.Linq;
using Serilog;
using Serilog.Core;

// Получаем версию из FileVersion
var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "Unknown";

// Путь к appsettings.json (сначала текущая папка, затем ProgramData/TS4)
string programDataTs4Path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TS4");
string appsettingsPath = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json");
string appsettingsProgramDataPath = Path.Combine(programDataTs4Path, "appsettings.json");

// Содержимое appsettings.json по умолчанию (в ProgramData/TS4)
string GetDefaultAppsettingsContent()
{
    string logPathEscaped = programDataTs4Path.Replace("\\", "\\\\");
    return $@"{{
  //уровень логирования журнала windows
  ""Logging"": {{
    ""EventLog"": {{ ""LogLevel"": {{ ""Default"": ""Critical"" }} }},
    ""LogLevel"": {{ ""Default"": ""Trace"" }},
    ""Console"": {{ ""LogLevel"": {{ ""Default"": ""Trace"" }} }}
  }},
  ""Log"": {{
    // путь для логов
    ""LogFolerPath"": ""{logPathEscaped}"",
    ""LogLevel"": ""Trace"",
    // количество лог файлов
    ""retainedFileCountLimit"": 7
  }},
  ""Service"": {{
    //настройка подключения к Firebird
    ""Firebird_db_config"": ""User = SYSDBA; Password = temp; Database = C:\\Program Files (x86)\\Cardsoft\\DuoSE\\Access\\ShieldPro_rest.GDB; DataSource = 127.0.0.1; Port = 3050; Dialect = 3; Charset = win1251; Role =;Connection lifetime = 5; Pooling = true; MinPoolSize = 0; MaxPoolSize = 500; Packet Size = 1024; ServerType = 0;"",
    //настройка подключения к Access
    ""Access_db_config"": ""Provider = Microsoft.Jet.OLEDB.4.0; Data Source = C:\\Program Files (x86)\\Cardsoft\\DuoSE\\TS2\\TS2.mdb"",
    //запустить сейчас игнорируя время первой итерации
    ""run_now"": true,
    //время первой итерации
    ""timestart"": ""9:10:0"",
    //время между итерациями
    ""timeout"": ""0:05:0"",
    //режим тестирования
    ""isTest"": false,
    //задержка (сек) перед повтором при ошибке подключения
    ""retryDelaySeconds"": 5,
    //режим работы: async - многопоточный, sync - последовательный
    ""mode"": ""async""
  }}
}}";
}

// Имя файла лога: TS4_yyyy-MM-dd.log
static string GetLogFileName(string folder) =>
    Path.Combine(folder, $"TS4_{DateTime.Now:yyyy-MM-dd}.log");

// Удаление старых логов (оставляет retainedFileCountLimit последних)
static void CleanupOldLogs(string folder, int retainCount)
{
    try
    {
        var files = Directory.GetFiles(folder, "TS4_*.log")
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTime)
            .ToList();
        foreach (var f in files.Skip(retainCount))
            try { f.Delete(); } catch { }
    }
    catch { }
}

// Инициализация fallback-логирования (когда appsettings отсутствует или повреждён)
void EnsureFallbackLogger(string reason, string? appsettingsUsedPath = null, int retryDelaySec = 5)
{
    try
    {
        try { Log.CloseAndFlush(); } catch { }

        Directory.CreateDirectory(programDataTs4Path);
        string logFile = GetLogFileName(programDataTs4Path);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(logFile,
                outputTemplate: "{Timestamp:dd-MM-yyyy HH:mm:ss.fff}\t-\t[{Level:u3}] {Message}{NewLine}{Exception}",
                flushToDiskInterval: TimeSpan.FromSeconds(1))
            .CreateLogger();

        Log.Warning("100 {Reason}. {PathInfo} Повтор через {Delay} сек.", reason,
            appsettingsUsedPath != null ? $"Используется: {appsettingsUsedPath}. " : "", retryDelaySec);
        Console.WriteLine($"{reason}. Повтор через {retryDelaySec} сек...");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Ошибка настройки логов: {ex.Message}");
    }
}

// Таймер для логирования этапов запуска (пик нагрузки на FB ~5 сек)
var startupSw = Stopwatch.StartNew();
var timeFmt = "HH:mm:ss.fff";
Console.WriteLine("Старт программы. Время запуска: {0}. Логирование этапов включено.", DateTime.Now.ToString(timeFmt));

// Проверка наличия и валидности appsettings.json (без исключений, с retry)
IConfiguration configuration;
while (true)
{
    string configPathToUse = appsettingsPath;
    string configDirToUse = Directory.GetCurrentDirectory();

    if (!File.Exists(appsettingsPath))
    {
        try
        {
            Directory.CreateDirectory(programDataTs4Path);
            if (!File.Exists(appsettingsProgramDataPath))
            {
                File.WriteAllText(appsettingsProgramDataPath, GetDefaultAppsettingsContent());
                Console.WriteLine($"Файл appsettings.json отсутствует. Создан по умолчанию: {appsettingsProgramDataPath}");
            }
            configPathToUse = appsettingsProgramDataPath;
            configDirToUse = programDataTs4Path;
        }
        catch (Exception ex)
        {
            EnsureFallbackLogger($"Файл appsettings.json отсутствует. Ошибка создания: {ex.Message}", null, 5);
            Thread.Sleep(5000);
            continue;
        }
    }

    try
    {
        var json = File.ReadAllText(configPathToUse);
        JObject.Parse(json, new JsonLoadSettings { CommentHandling = Newtonsoft.Json.Linq.CommentHandling.Ignore });
    }
    catch (Exception ex)
    {
        EnsureFallbackLogger($"Ошибка в формате JSON appsettings.json: {ex.Message}", configPathToUse, 5);
        Thread.Sleep(5000);
        continue;
    }

    try
    {
        var configBuilder = new ConfigurationBuilder()
            .SetBasePath(configDirToUse);

        configBuilder.Add(new JsonWithCommentsConfigurationSource
        {
            Path = Path.GetFileName(configPathToUse),
            BasePath = configDirToUse,
            Optional = false,
            ReloadOnChange = true
        });

        var devPath = Path.Combine(configDirToUse, "appsettings.Development.json");
        if (File.Exists(devPath))
        {
            configBuilder.Add(new JsonWithCommentsConfigurationSource
            {
                Path = "appsettings.Development.json",
                BasePath = configDirToUse,
                Optional = true,
                ReloadOnChange = true
            });
        }

        configBuilder.AddEnvironmentVariables().AddCommandLine(args);
        configuration = configBuilder.Build();
        Console.WriteLine("[время запуска {0}] [мс={1}] appsettings.json загружен ({2})", DateTime.Now.ToString(timeFmt), startupSw.ElapsedMilliseconds, configPathToUse);
        break;
    }
    catch (Exception ex)
    {
        EnsureFallbackLogger($"Ошибка загрузки конфигурации: {ex.Message}", configPathToUse, 5);
        Thread.Sleep(5000);
        continue;
    }
}

// Извлечь путь к MDB из строки подключения Access
static string? GetMdbPathFromAccessConfig(string? accessConfig)
{
    if (string.IsNullOrWhiteSpace(accessConfig)) return null;
    var parts = accessConfig.Split(';');
    foreach (var part in parts)
    {
        var idx = part.IndexOf('=');
        if (idx < 0) continue;
        var key = part.Substring(0, idx).Trim();
        if (key.Equals("Data Source", StringComparison.OrdinalIgnoreCase))
        {
            var path = part.Substring(idx + 1).Trim();
            if (path.StartsWith("\"") && path.EndsWith("\""))
                path = path[1..^1];
            return path;
        }
    }
    return null;
}

// Инициализация логгера из конфигурации (для фазы проверок до запуска Host)
void EnsureConfigLogger(IConfiguration config)
{
    try
    {
        try { Log.CloseAndFlush(); } catch { }
        string logFolderPath = config.GetSection("Log").GetValue<string>("LogFolerPath") ?? Directory.GetCurrentDirectory();
        int retainedFileCountLimit = config.GetSection("Log").GetValue<int?>("retainedFileCountLimit") ?? 31;
        if (retainedFileCountLimit > 0)
            CleanupOldLogs(logFolderPath, retainedFileCountLimit);

        string logPath = GetLogFileName(logFolderPath);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(logPath,
                outputTemplate: "{Timestamp:dd-MM-yyyy HH:mm:ss.fff}\t-\t[{Level:u3}] {Message}{NewLine}{Exception}",
                flushToDiskInterval: TimeSpan.FromSeconds(1))
            .CreateLogger();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Ошибка настройки логов: {ex.Message}");
    }
}

// Проверка связи с Firebird и наличия MDB (retry с задержкой из appsettings)
bool appsettingsLogged = false;
while (true)
{
    bool fbOk = false;
    bool mdbOk = false;
    int retryDelaySec = configuration.GetSection("Service").GetValue<int?>("retryDelaySeconds") ?? 5;

    try
    {
        EnsureConfigLogger(configuration);
        var options = configuration.GetSection("Service").Get<WorkerOptions>();

        if (!appsettingsLogged)
        {
            Log.Information("105 [запуск мс={ElapsedMs}] appsettings.json загружен, переходим к проверке Firebird и MDB", startupSw.ElapsedMilliseconds);
            appsettingsLogged = true;
        }

        // 1. Проверка Firebird
        Log.Information("108 [время запуска {Time}] [мс={ElapsedMs}] Проверка Firebird: начало", DateTime.Now.ToString(timeFmt), startupSw.ElapsedMilliseconds);
        if (string.IsNullOrWhiteSpace(options?.Firebird_db_config))
        {
            Log.Warning("110 Нет настроек Firebird_db_config в appsettings.json. Повтор через {Delay} сек.", retryDelaySec);
            Console.WriteLine($"Нет настроек Firebird в конфигурации. Повтор через {retryDelaySec} сек...");
        }
        else
        {
            try
            {
                var tFbCheck = startupSw.ElapsedMilliseconds;
                var csBuilder = new FbConnectionStringBuilder(options.Firebird_db_config);
                if (csBuilder.Charset != null && csBuilder.Charset.ToUpper() != "UTF8")
                    csBuilder.Charset = "UTF8";
                Log.Information("112 [запуск мс={ElapsedMs}] Firebird: создаём соединение (Connect)", startupSw.ElapsedMilliseconds);
                using (var con = DBFirebird.Connect(csBuilder.ToString(), null))
                {
                    Log.Information("114 [запуск мс={ElapsedMs}] Firebird: Open()...", startupSw.ElapsedMilliseconds);
                    con.Open();
                    Log.Information("116 [запуск мс={ElapsedMs}] Firebird: Open выполнен за {Ms} мс", startupSw.ElapsedMilliseconds, startupSw.ElapsedMilliseconds - tFbCheck);
                    con.Close();
                    fbOk = true;
                    Log.Information("120 [запуск мс={ElapsedMs}] Firebird: проверка пройдена, связь установлена", startupSw.ElapsedMilliseconds);
                }
            }
            catch (Exception ex)
            {
                string msg = LogHelper.Sanitize(ex.Message);
                Log.Warning("130 Нет связи с Firebird: {Message}. Повтор через {Delay} сек.", msg, retryDelaySec);
                Console.WriteLine($"Нет связи с Firebird: {msg}. Повтор через {retryDelaySec} сек...");
            }
        }

        Log.Information("125 [время запуска {Time}] [мс={ElapsedMs}] Проверка Firebird завершена, fbOk={FbOk}", DateTime.Now.ToString(timeFmt), startupSw.ElapsedMilliseconds, fbOk);
        // 2. Проверка MDB (только если Firebird OK)
        if (fbOk && !string.IsNullOrWhiteSpace(options?.Access_db_config))
        {
            string? mdbPath = GetMdbPathFromAccessConfig(options.Access_db_config);
            if (string.IsNullOrEmpty(mdbPath))
            {
                Log.Warning("140 Не удалось извлечь путь к MDB из Access_db_config. Повтор через {Delay} сек.", retryDelaySec);
                Console.WriteLine($"Не найден путь к MDB в конфигурации. Повтор через {retryDelaySec} сек...");
            }
            else if (!File.Exists(mdbPath))
            {
                Log.Warning("150 Файл MDB отсутствует: {Path}. Повтор через {Delay} сек.", mdbPath, retryDelaySec);
                Console.WriteLine($"Файл MDB отсутствует: {mdbPath}. Повтор через {retryDelaySec} сек...");
            }
            else
            {
                mdbOk = true;
                Log.Information("160 [запуск мс={ElapsedMs}] MDB: проверка пройдена, файл присутствует: {Path}", startupSw.ElapsedMilliseconds, mdbPath);
            }
        }
        else if (fbOk && string.IsNullOrWhiteSpace(options?.Access_db_config))
        {
            Log.Warning("145 Нет настроек Access_db_config. Повтор через {Delay} сек.", retryDelaySec);
            Console.WriteLine($"Нет настроек Access в конфигурации. Повтор через {retryDelaySec} сек...");
        }
    }
    catch (Exception ex)
    {
        EnsureFallbackLogger($"Ошибка при проверке: {ex.Message}", null, retryDelaySec);
    }

    if (fbOk && mdbOk)
        break;

    Thread.Sleep(retryDelaySec * 1000);
}

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

// Проверка наличия процедуры и триггера — один раз при запуске программы
Log.Information("200 [время запуска {Time}] [мс={ElapsedMs}] Проверка объектов БД (процедура, триггер): начало", DateTime.Now.ToString(timeFmt), startupSw.ElapsedMilliseconds);
try
{
    WorkerOptions options = configuration.GetSection("Service").Get<WorkerOptions>();

    if (options != null && !string.IsNullOrEmpty(options.Firebird_db_config))
    {
        var tDbObj = startupSw.ElapsedMilliseconds;
        Log.Information("202 [время запуска {Time}] [мс={ElapsedMs}] Проверка объектов БД: создаём второе соединение с Firebird", DateTime.Now.ToString(timeFmt), startupSw.ElapsedMilliseconds);

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
                Log.Information("204 [время запуска {Time}] [мс={ElapsedMs}] Проверка объектов БД: Open()...", DateTime.Now.ToString(timeFmt), startupSw.ElapsedMilliseconds);
                con.Open();
                Log.Information("206 [время запуска {Time}] [мс={ElapsedMs}] Проверка объектов БД: Open выполнен за {Ms} мс", DateTime.Now.ToString(timeFmt), startupSw.ElapsedMilliseconds, startupSw.ElapsedMilliseconds - tDbObj);

                Log.Information("208 [время запуска {Time}] [мс={ElapsedMs}] Проверка процедуры CARDINDEV_TS4...", DateTime.Now.ToString(timeFmt), startupSw.ElapsedMilliseconds);
                bool procedureExists = DBFirebird.ProcedurChec(con);
                Log.Information("210 [время запуска {Time}] [мс={ElapsedMs}] Процедура CARDINDEV_TS4: {Result}", DateTime.Now.ToString(timeFmt), startupSw.ElapsedMilliseconds, procedureExists ? "найдена" : "не найдена");
                Console.WriteLine(procedureExists
                    ? "Проверка процедуры: процедура CARDINDEV_TS4 найдена в базе данных"
                    : "Проверка процедуры: процедура CARDINDEV_TS4 не найдена в базе данных\nЧтобы создать процедуру откройте файл \"C:\\TS4\\create procedure.bat\"");

                Log.Information("212 [время запуска {Time}] [мс={ElapsedMs}] Проверка триггера CARDINDEV_AI0...", DateTime.Now.ToString(timeFmt), startupSw.ElapsedMilliseconds);
                bool triggerExists = DBFirebird.TriggerCheck(con);
                Log.Information("214 [время запуска {Time}] [мс={ElapsedMs}] Триггер CARDINDEV_AI0: {Result}", DateTime.Now.ToString(timeFmt), startupSw.ElapsedMilliseconds, triggerExists ? "найден" : "не найден");
                Console.WriteLine(triggerExists
                    ? "Проверка триггера: триггер для события CARDINDEV_INSERT найден в базе данных"
                    : "Проверка триггера: триггер для события CARDINDEV_INSERT не найден в базе данных\nЧтобы создать триггер откройте файл \"C:\\TS4\\create trigger.bat\"");

                con.Close();
                Log.Information("220 [время запуска {Time}] [мс={ElapsedMs}] Проверка объектов БД завершена, соединение закрыто", DateTime.Now.ToString(timeFmt), startupSw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "222 [время запуска {Time}] [мс={ElapsedMs}] Ошибка при проверке объектов БД: {Message}", DateTime.Now.ToString(timeFmt), startupSw.ElapsedMilliseconds, ex.Message);
                Console.WriteLine($"Ошибка при проверке объектов базы данных: {ex.Message}");
            }
        }
    }
}
catch (Exception ex)
{
    Console.WriteLine($"Ошибка при чтении конфигурации для проверки БД: {ex.Message}");
}

Log.Information("300 [запуск мс={ElapsedMs}] Host: создание билдера (CreateDefaultBuilder)...", startupSw.ElapsedMilliseconds);
try
{
    IConfiguration configForHost = configuration;
    var host = Host.CreateDefaultBuilder(args)
        .ConfigureAppConfiguration((context, config) =>
        {
            config.Sources.Clear();
            config.AddConfiguration(configForHost);
        })
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
        })
        .UseSerilog((context, services, configuration) =>
        {
            try { Log.CloseAndFlush(); } catch { }

            string logFolderPath = context.Configuration.GetSection("Log").GetValue<string>("LogFolerPath")
                                   ?? Directory.GetCurrentDirectory();

            int retainedFileCountLimit = context.Configuration.GetSection("Log").GetValue<int?>("retainedFileCountLimit") ?? 31;
            if (retainedFileCountLimit > 0)
                CleanupOldLogs(logFolderPath, retainedFileCountLimit);

            string logPath = GetLogFileName(logFolderPath);

            LogLevel minLevel = context.Configuration.GetSection("Log").GetValue<LogLevel>("LogLevel");
            if (!Enum.IsDefined(typeof(LogLevel), minLevel))
                minLevel = LogLevel.Information;

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

            var outputTemplate = "{Timestamp:dd-MM-yyyy HH:mm:ss.fff}\t-\t[{Level:u3}] {Message}{NewLine}{Exception}";
            configuration
                .MinimumLevel.Is(serilogLevel)
                .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Information)
                .MinimumLevel.Override("Microsoft.Hosting.Lifetime", Serilog.Events.LogEventLevel.Information)
                .WriteTo.Console(outputTemplate: outputTemplate)
                .WriteTo.Async(a => a.File(
                    path: logPath,
                    outputTemplate: outputTemplate,
                    buffered: true,
                    flushToDiskInterval: TimeSpan.FromSeconds(1)
                ));
        })
        .UseWindowsService()
        .Build();

    Log.Information("310 [время запуска {Time}] [мс={ElapsedMs}] Host: Build завершён", DateTime.Now.ToString(timeFmt), startupSw.ElapsedMilliseconds);
    await host.StartAsync();
    Log.Information("320 [время запуска {Time}] [мс={ElapsedMs}] Host: StartAsync завершён, Hosting started", DateTime.Now.ToString(timeFmt), startupSw.ElapsedMilliseconds);
    await host.WaitForShutdownAsync();
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