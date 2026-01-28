using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net.Http;
using System.Net;
using System.Net.Sockets;
using System.Data;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace TS4
{
    public class Worker3 : BackgroundService
    {
        private readonly ILogger<Worker3> logger;
        private readonly WorkerOptions options;

        public Worker3(ILogger<Worker3> logger, WorkerOptions options)
        {
            this.logger = logger;
            this.options = options;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            logger.LogInformation("Worker3 запущен");
            
            // Пауза перед первым запуском
            await Task.Delay(TimeSpan.FromSeconds(0.5), stoppingToken);
            
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    logger.LogInformation("Worker3: Начинаю сбор статистики");
                    await CollectStatAsync();
                    logger.LogInformation("Worker3: Сбор статистики завершен успешно");
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Worker3: Ошибка при сборе статистики: {Message}", ex.Message);
                }
                
                // Пауза между запусками - каждые 10 минут
                logger.LogInformation("Worker3: Пауза 10 минут до следующего запуска");
                await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
            }
        }

        public async Task CollectStatAsync(int countDevice = 500)
        {
            DateTime t1 = DateTime.Now;
            List<DEV> devices = new List<DEV>();

            // Получаем устройства из Access и сразу ищем их ID в Firebird
            try
            {
                using (var con = DBAccess.Connect(options.Access_db_config))
                {
                    con.Open(); 
                    logger.LogDebug("Worker3: Подключение к Access базе данных успешно");
                    
                    var table = DBAccess.GetDevice(con);
                    logger.LogDebug($"Worker3: Найдено {table.Rows.Count} устройств в Access базе");
                    
                    // Подключаемся к Firebird для получения ID
                    using (var fb = DBFirebird.Connect(options.Firebird_db_config, logger))
                    {
                        fb.Open();
                        logger.LogDebug("Worker3: Подключение к Firebird базе данных успешно");
                        
                        foreach (DataRow row in table.Rows)
                        {
                            string ip = row["ConnectionString"]?.ToString() ?? "";
                            string name = row["Name"]?.ToString() ?? "";
                            
                            if (!string.IsNullOrWhiteSpace(ip) && !string.IsNullOrWhiteSpace(name))
                            {
                                // Получаем ID устройства из Firebird по имени
                                int? deviceId = DBFirebird.GetDeviceIdByName(fb, name);
                                if (deviceId.HasValue)
                                {
                                    devices.Add(new DEV(ip, name, "", deviceId.Value));
                                    logger.LogDebug($"Worker3: Найдено устройство: ID={deviceId}, Name={name}, IP={ip}");
                                }
                                else
                                {
                                    logger.LogWarning($"Worker3: Устройство {name} не найдено в Firebird базе");
                                }
                            }
                            else
                            {
                                logger.LogWarning($"Worker3: Устройство пропущено - нет IP или имени");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError("Worker3: Ошибка при получении устройств: " + ex.Message);
                return;
            }

            logger.LogDebug($"Collect stat START. Total {devices.Count} devices");
            
            // Обрабатываем устройства (максимум countDevice штук)
            int processedCount = 0;
            foreach (var dev in devices)
            {
                if (processedCount >= countDevice) 
                { 
                    logger.LogDebug($"Stop Collect - достигнут лимит {countDevice} устройств"); 
                    break; 
                }
                processedCount++;
                var t2 = DateTime.Now;
                
                // Проверяем, что у устройства есть ID из Firebird базы
                if (!dev.id.HasValue)
                {
                    logger.LogWarning($"Устройство \"{dev.controllerName}\" пропущено - нет ID в Firebird базе");
                    continue;
                }
                
                int idDev = dev.id.Value;
                string ip = dev.ip;
                
                logger.LogDebug($"Обрабатываем устройство: id_dev={idDev}, ip={ip}, name=\"{dev.controllerName}\"");

                var http = new ArtonitHttp(ip);
                long tt1 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                bool online = await http.CheckOnline();
                DeviceState state;
                if (online)
                {
                    await http.GetDeviceInfo();
                    
                    // Получаем количество карт через драйвер artonit2.dll
                    var keyCount = GetKeyCountViaDriver(dev.driver, ip);
                    
                    state = new DeviceState
                    {
                        id = idDev,
                        ip = ip,
                        mac = http.Mac,
                        Onl = true,
                        isWp = http.IsWp,
                        isTst = http.IsTest,
                        dMode_0 = http.DoorMode[0],
                        dMode_1 = http.DoorMode[1],
                        InputPortState = http.PortStateInput,
                        sver = http.SoftVersion,
                        kc = keyCount,
                        scud = http.Scud,
                        te = ((DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - tt1)/1000.0).ToString("0.000"),
                        timef = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        isNew = true,
                        lastTimef = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    };
                }
                else
                {
                    state = new DeviceState
                    {
                        id = idDev,
                        ip = ip,
                        mac = "00-00-00-00-00-00",
                        Onl = false,
                        isWp = false,
                        isTst = false,
                        dMode_0 = "",
                        dMode_1 = "",
                        InputPortState = new List<string>(),
                        sver = "n/a",
                        kc = new Dictionary<string, int>(),
                        scud = "d0",
                        te = ((DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - tt1)/1000.0).ToString("0.000"),
                        timef = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        isNew = false,
                        lastTimef = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                    };
                }

                // write to Firebird
                using (var fb = DBFirebird.Connect(options.Firebird_db_config, logger))
                {
                    try { fb.Open(); } catch (Exception ex) { logger.LogError("FB connect error: " + ex.Message); continue; }
                    int idParam = 113;
                    int idAgent = 1;
                    int order = 445;
                    
                    // Настраиваем JSON сериализатор для корректной работы с русскими символами
                    var options = new JsonSerializerOptions
                    {
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                        WriteIndented = false
                    };
                    string json = System.Text.Json.JsonSerializer.Serialize(state, options);
                    
                    // Конвертируем JSON в Windows1251 для отправки в базу данных
                    string jsonWindows1251 = ConvertToWindows1251(json);
                    
                    // Сначала пытаемся использовать прямой INSERT
                    var rc = DBFirebird.StatInsertDirect(fb, order, idDev, idAgent, idParam, jsonWindows1251);
                    if (rc != 0)
                    {
                        // Если прямой INSERT не сработал, пытаемся через хранимую процедуру
                        logger.LogDebug($"Прямой INSERT не удался (rc={rc}), пробуем через хранимую процедуру для id_dev={idDev}");
                        rc = DBFirebird.StatInsert(fb, order, idDev, idAgent, idParam, jsonWindows1251);
                    }
                    
                    if (rc == 0)
                        logger.LogDebug($"Статистика записана id_dev={idDev} ip={ip}");
                    else if (rc == -2)
                        logger.LogWarning($"Устройство с id_dev={idDev} не существует в базе данных, пропускаем запись");
                    else
                        logger.LogDebug($"ERR запись статистики id_dev={idDev} ip={ip} (код ошибки: {rc})");
                }

                // simple POST to local SSE endpoint as in PHP
                try
                {
                    using var client = new HttpClient();
                    var content = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        {"key", "collectStatFromDevice:" + System.Text.Json.JsonSerializer.Serialize(state)}
                    });
                    await client.PostAsync("http://localhost/city/index.php/sse/log", content);
                }
                catch { }

                logger.LogDebug($"get device info id_dev={idDev} time execute={(DateTime.Now - t2).TotalSeconds:0.000}");
            }

            logger.LogDebug($"Collect stat END. Time={(DateTime.Now - t1).TotalSeconds:0.000}");
        }

        /// <summary>
        /// Получает количество карт через драйвер artonit2.dll
        /// Перебирает каждую дверь (DOOR 0, DOOR 1, и т.д.) и получает количество карт для каждой
        /// </summary>
        private Dictionary<string, int> GetKeyCountViaDriver(string driver, string ip)
        {
            var keyCount = new Dictionary<string, int>();
            
            try
            {
                // GUID драйвера artonit2.dll из README.md
                string driverGuid = "EAE30322-9FA6-4466-B3AE-DFB1D58813D3";
                
                var com = new COM(driverGuid, options);
                com.SetupString(ip);
                
                // Перебираем двери (DOOR 0, DOOR 1, DOOR 2, и т.д.)
                for (int doorNumber = 0; doorNumber < 2; doorNumber++)
                {
                    try
                    {
                        // Формируем команду для получения количества карт для конкретной двери
                        // Команда: "getkeycount door=0" или "getkeycount door=1"
                        string command = $"getkeycount door={doorNumber}";
                        string result = com.ComandExecute(command);
                        
                        // Парсим результат
                        // Ожидаемый формат ответа: "123" (просто число) или "count=123"
                        int count = 0;
                        
                        if (!string.IsNullOrEmpty(result) && result != "ERR")
                        {
                            // Если результат содержит "=", извлекаем значение после знака равенства
                            if (result.Contains("="))
                            {
                                var parts = result.Split('=');
                                if (parts.Length > 1 && int.TryParse(parts[1], out int parsedCount))
                                {
                                    count = parsedCount;
                                }
                            }
                            // Если результат - просто число
                            else if (int.TryParse(result, out int directCount))
                            {
                                count = directCount;
                            }
                        }
                        
                        // Добавляем количество карт для этой двери в словарь
                        keyCount[doorNumber.ToString()] = count;
                        
                        logger.LogDebug($"DOOR {doorNumber}: получено {count} карт для {ip}");
                    }
                    catch (Exception ex)
                    {
                        // Если ошибка при получении данных для конкретной двери, записываем 0
                        keyCount[doorNumber.ToString()] = 0;
                        logger.LogWarning($"Ошибка получения количества карт для DOOR {doorNumber} устройства {ip}: {ex.Message}");
                    }
                }
                
                logger.LogDebug($"Итого получено количество карт через драйвер для {ip}: DOOR 0={keyCount["0"]}, DOOR 1={keyCount["1"]}");
                return keyCount;
            }
            catch (Exception ex)
            {
                logger.LogError($"Критическая ошибка получения количества карт через драйвер для {ip}: {ex.Message}");
                // Возвращаем значения по умолчанию в случае критической ошибки
                return new Dictionary<string, int> { { "0", 0 }, { "1", 0 } };
            }
        }

        /// <summary>
        /// Конвертирует строку в кодировку Windows1251
        /// </summary>
        private string ConvertToWindows1251(string input)
        {
            try
            {
                if (string.IsNullOrEmpty(input))
                    return input;
                
                // Получаем байты в UTF-8
                byte[] utf8Bytes = Encoding.UTF8.GetBytes(input);
                
                // Конвертируем в Windows1251
                Encoding windows1251 = Encoding.GetEncoding("windows-1251");
                return windows1251.GetString(utf8Bytes);
            }
            catch (Exception ex)
            {
                logger.LogError($"ConvertToWindows1251 error for '{input}': {ex.Message}");
                return input; // Возвращаем оригинальную строку в случае ошибки
            }
        }

    }
}
