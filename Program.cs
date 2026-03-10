using TS4;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Reflection;
using System.Diagnostics;
using FirebirdSql.Data.FirebirdClient;
using Newtonsoft.Json.Linq;
using Serilog;
using Serilog.Core;

// Версия из .csproj (свойство Version) — выводится при старте (Worker4, лог "51 Версия программы")
var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "Unknown";

// Только один экземпляр программы (консоль и служба видят один и тот же Mutex)
const string singleInstanceMutexName = "Global\\TS4_SingleInstance";
using var singleInstanceMutex = new Mutex(initiallyOwned: true, singleInstanceMutexName, out bool createdNew);
if (!createdNew)
{
    try
    {
        if (!singleInstanceMutex.WaitOne(0))
        {
            Console.WriteLine("Программа TS4 уже была запущена.");
            Console.Write("Нажмите любую клавишу для выхода...");
            Console.ReadKey(true);
            Environment.Exit(1);
        }
    }
    catch (AbandonedMutexException)
    {
        // Предыдущий процесс завершился без Release — владеем mutex
    }
}

// appsettings.json — только рядом с исполняемым файлом (нигде больше)
string programDataTs4Path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "TS4");
string exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
    ?? AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
string appsettingsPath = Path.Combine(exeDir, "appsettings.json");

// Содержимое appsettings.json по умолчанию (logFolder = папка для логов в настройках; если null — ProgramData/TS4)
string GetDefaultAppsettingsContent(string? logFolder = null)
{
    string folder = logFolder ?? programDataTs4Path;
    string logPathEscaped = folder.Replace("\\", "\\\\");
    return $@"{{
  ""Log"": {{
    // путь для логов
    ""LogFolerPath"": ""{logPathEscaped}"",
    //""LogLevel"": ""Debug"",
    //""LogLevel"": ""Information"",
    //""LogLevel"": ""Warning"",
    //""LogLevel"": ""Error"",
    //""LogLevel"": ""Trace"",
    ""LogLevel"": ""Critical"",
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

// Имя файла лога: TS4_yyyy-MM-dd.log; новый файл каждый день — переключение в SerilogRollingHostedService
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

        string exeDirForFallback = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string logFolderForFallback = Path.Combine(exeDirForFallback, "log");
        Directory.CreateDirectory(logFolderForFallback);
        string logFile = GetLogFileName(logFolderForFallback);

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

// Режим службы Windows: не блокировать старт на проверках (иначе ошибка 1053 — таймаут запуска)
bool isWindowsService = !Environment.UserInteractive;

// Проверка: appsettings.json только рядом с exe. Нет — предлагаем создать там же (не удаляем и не трогаем другие папки)
string configPathToUse = appsettingsPath;
string configDirToUse = exeDir;

if (!File.Exists(appsettingsPath))
{
    Console.WriteLine("Файл appsettings.json не найден рядом с программой.");
    if (isWindowsService)
    {
        try
        {
            string logFolderForNewConfig = Path.Combine(exeDir, "log");
            File.WriteAllText(appsettingsPath, GetDefaultAppsettingsContent(logFolderForNewConfig));
        }
        catch (Exception ex)
        {
            Console.WriteLine("Служба: ошибка создания appsettings.json: " + ex.Message);
            Environment.Exit(1);
        }
    }
    else
    {
        Console.Write("Создать appsettings.json рядом с программой? (y/n): ");
        string? answer = Console.ReadLine()?.Trim().ToLowerInvariant();
        if (answer == "n")
        {
            Console.WriteLine("Завершение работы.");
            Environment.Exit(0);
        }
        if (answer != "y")
        {
            Console.WriteLine("Введите y или n. Завершение работы.");
            Environment.Exit(1);
        }
        try
        {
            string logFolderForNewConfig = Path.Combine(exeDir, "log");
            File.WriteAllText(appsettingsPath, GetDefaultAppsettingsContent(logFolderForNewConfig));
            Console.WriteLine("Создан: " + appsettingsPath);
            Console.WriteLine("Папка логов в конфиге: " + logFolderForNewConfig);
        }
        catch (Exception ex)
        {
            Console.WriteLine("Ошибка создания файла: " + ex.Message);
            Environment.Exit(1);
        }
    }
}

// Загрузка и валидация конфигурации (с retry при ошибке формата/загрузки)
IConfiguration configuration;
while (true)
{
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

// Преобразование уровня из appsettings (Log:LogLevel) в уровень Serilog
static Serilog.Events.LogEventLevel GetSerilogLevel(IConfiguration config)
{
    var minLevel = config.GetSection("Log").GetValue<LogLevel?>("LogLevel") ?? LogLevel.Information;
    if (!Enum.IsDefined(typeof(LogLevel), minLevel))
        minLevel = LogLevel.Information;
    return minLevel switch
    {
        LogLevel.Trace => Serilog.Events.LogEventLevel.Verbose,
        LogLevel.Debug => Serilog.Events.LogEventLevel.Debug,
        LogLevel.Information => Serilog.Events.LogEventLevel.Information,
        LogLevel.Warning => Serilog.Events.LogEventLevel.Warning,
        LogLevel.Error => Serilog.Events.LogEventLevel.Error,
        LogLevel.Critical => Serilog.Events.LogEventLevel.Fatal,
        _ => Serilog.Events.LogEventLevel.Information
    };
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

        string logFile = GetLogFileName(logFolderPath);
        var minLevel = GetSerilogLevel(config);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Is(minLevel)
            .WriteTo.File(logFile,
                outputTemplate: "{Timestamp:dd-MM-yyyy HH:mm:ss.fff}\t-\t[{Level:u3}] {Message}{NewLine}{Exception}",
                flushToDiskInterval: TimeSpan.FromSeconds(1),
                retainedFileCountLimit: retainedFileCountLimit)
            .CreateLogger();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Ошибка настройки логов: {ex.Message}");
    }
}

// Проверка связи с Firebird и MDB: в режиме службы не ждём (иначе таймаут 1053), проверки будут в Worker4
if (isWindowsService)
{
    EnsureConfigLogger(configuration);
    Log.Warning("105 [запуск мс={ElapsedMs}] appsettings.json загружен (режим службы — проверка Firebird/MDB в фоне)", startupSw.ElapsedMilliseconds);
}
else
{
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
                Log.Warning("105 [запуск мс={ElapsedMs}] appsettings.json загружен, переходим к проверке Firebird и MDB", startupSw.ElapsedMilliseconds);
                appsettingsLogged = true;
            }

            // 1. Проверка Firebird
            Log.Warning("108 [время запуска {Time}] [мс={ElapsedMs}] Проверка Firebird: начало", DateTime.Now.ToString(timeFmt), startupSw.ElapsedMilliseconds);
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
                    Log.Warning("112 [запуск мс={ElapsedMs}] Firebird: создаём соединение (Connect)", startupSw.ElapsedMilliseconds);
                    using (var con = DBFirebird.Connect(csBuilder.ToString(), null))
                    {
                        Log.Warning("114 [запуск мс={ElapsedMs}] Firebird: Open()...", startupSw.ElapsedMilliseconds);
                        con.Open();
                        Log.Warning("116 [запуск мс={ElapsedMs}] Firebird: Open выполнен за {Ms} мс", startupSw.ElapsedMilliseconds, startupSw.ElapsedMilliseconds - tFbCheck);
                        con.Close();
                        fbOk = true;
                        Log.Warning("120 [запуск мс={ElapsedMs}] Firebird: проверка пройдена, связь установлена", startupSw.ElapsedMilliseconds);
                    }
                }
                catch (Exception ex)
                {
                    string msg = LogHelper.Sanitize(ex.Message);
                    Log.Warning("130 Нет связи с Firebird: {Message}. Повтор через {Delay} сек.", msg, retryDelaySec);
                    Console.WriteLine($"Нет связи с Firebird: {msg}. Повтор через {retryDelaySec} сек...");
                }
            }

            Log.Warning("125 [время запуска {Time}] [мс={ElapsedMs}] Проверка Firebird завершена, fbOk={FbOk}", DateTime.Now.ToString(timeFmt), startupSw.ElapsedMilliseconds, fbOk);
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
                    Log.Warning("160 [запуск мс={ElapsedMs}] MDB: проверка пройдена, файл присутствует: {Path}", startupSw.ElapsedMilliseconds, mdbPath);
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
Log.Warning("200 [время запуска {Time}] [мс={ElapsedMs}] Проверка объектов БД (процедура, триггер): начало", DateTime.Now.ToString(timeFmt), startupSw.ElapsedMilliseconds);
try
{
    WorkerOptions options = configuration.GetSection("Service").Get<WorkerOptions>();

    if (options != null && !string.IsNullOrEmpty(options.Firebird_db_config))
    {
        var tDbObj = startupSw.ElapsedMilliseconds;
        Log.Warning("202 [время запуска {Time}] [мс={ElapsedMs}] Проверка объектов БД: создаём второе соединение с Firebird", DateTime.Now.ToString(timeFmt), startupSw.ElapsedMilliseconds);

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
                Log.Warning("204 [время запуска {Time}] [мс={ElapsedMs}] Проверка объектов БД: Open()...", DateTime.Now.ToString(timeFmt), startupSw.ElapsedMilliseconds);
                con.Open();
                Log.Warning("206 [время запуска {Time}] [мс={ElapsedMs}] Проверка объектов БД: Open выполнен за {Ms} мс", DateTime.Now.ToString(timeFmt), startupSw.ElapsedMilliseconds, startupSw.ElapsedMilliseconds - tDbObj);

                Log.Warning("208 [время запуска {Time}] [мс={ElapsedMs}] Проверка процедуры CARDINDEV_TS4...", DateTime.Now.ToString(timeFmt), startupSw.ElapsedMilliseconds);
                bool procedureExists = DBFirebird.ProcedurChec(con);
                Log.Warning("210 [время запуска {Time}] [мс={ElapsedMs}] Процедура CARDINDEV_TS4: {Result}", DateTime.Now.ToString(timeFmt), startupSw.ElapsedMilliseconds, procedureExists ? "найдена" : "не найдена");
                Console.WriteLine(procedureExists
                    ? "Проверка процедуры: процедура CARDINDEV_TS4 найдена в базе данных"
                    : "Проверка процедуры: процедура CARDINDEV_TS4 не найдена в базе данных\nЧтобы создать процедуру откройте файл \"C:\\TS4\\create procedure.bat\"");

                Log.Warning("212 [время запуска {Time}] [мс={ElapsedMs}] Проверка триггера CARDINDEV_AI0...", DateTime.Now.ToString(timeFmt), startupSw.ElapsedMilliseconds);
                bool triggerExists = DBFirebird.TriggerCheck(con);
                Log.Warning("214 [время запуска {Time}] [мс={ElapsedMs}] Триггер CARDINDEV_AI0: {Result}", DateTime.Now.ToString(timeFmt), startupSw.ElapsedMilliseconds, triggerExists ? "найден" : "не найден");
                Console.WriteLine(triggerExists
                    ? "Проверка триггера: триггер для события CARDINDEV_INSERT найден в базе данных"
                    : "Проверка триггера: триггер для события CARDINDEV_INSERT не найден в базе данных\nЧтобы создать триггер откройте файл \"C:\\TS4\\create trigger.bat\"");

                con.Close();
                Log.Warning("220 [время запуска {Time}] [мс={ElapsedMs}] Проверка объектов БД завершена, соединение закрыто", DateTime.Now.ToString(timeFmt), startupSw.ElapsedMilliseconds);
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

Log.Warning("300 [запуск мс={ElapsedMs}] Host: создание билдера (CreateDefaultBuilder)...", startupSw.ElapsedMilliseconds);
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

            var serilogLevel = GetSerilogLevel(context.Configuration);

            var outputTemplate = "{Timestamp:dd-MM-yyyy HH:mm:ss.fff}\t-\t[{Level:u3}] {Message}{NewLine}{Exception}";
            configuration
                .MinimumLevel.Is(serilogLevel)
                .MinimumLevel.Override("Microsoft", serilogLevel)
                .MinimumLevel.Override("Microsoft.Hosting.Lifetime", serilogLevel)
                .WriteTo.Console(outputTemplate: outputTemplate)
                .WriteTo.Async(a => a.Sink(new DateRollingFileSink(logFolderPath)));
        })
        .UseWindowsService()
        .Build();

    // Путь к exe (ProcessPath — .exe; при dotnet run или Location может быть .dll)
    string exePath = Environment.ProcessPath ?? Assembly.GetExecutingAssembly().Location;
    if (string.IsNullOrEmpty(exePath) || exePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        exePath = Path.Combine(exeDir, "TS4.exe");

    string header1 = "Begin log: open application log";
    string header2 = "Транспортный сервер TS4 для работы с контроллерами серии Artonit версия {Version}. Срок службы не ограничен. www.artonit.ru";
    string header5 = "Регистрация службы";
    string header6 = $"sc create TS4 binPath=\"{exePath}\" start=auto DisplayName=TS4 type=own";
    string header7 = "sc description TS4 \"Транспортный сервер СКУД Артонит\"";

    Console.WriteLine(header1);
    Log.Fatal(header1);
    Console.WriteLine($"Транспортный сервер TS4 для работы с контроллерами серии Artonit версия {version}");
    Log.Fatal(header2, version);
    Log.Fatal(header5);
    Console.WriteLine(header6);
    Log.Fatal(header6);
    Console.WriteLine(header7);
    Log.Fatal(header7);

    Log.Fatal("Старт программы. Время запуска: {Time}", DateTime.Now.ToString(timeFmt));
    Log.Warning("310 [время запуска {Time}] [мс={ElapsedMs}] Host: Build завершён", DateTime.Now.ToString(timeFmt), startupSw.ElapsedMilliseconds);
    await host.StartAsync();
    Log.Warning("320 [время запуска {Time}] [мс={ElapsedMs}] Host: StartAsync завершён, Hosting started", DateTime.Now.ToString(timeFmt), startupSw.ElapsedMilliseconds);
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