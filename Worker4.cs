using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using FirebirdSql.Data.FirebirdClient;
using System.Threading;
using System.Data;
using System.Data.OleDb;

namespace TS4
{
    internal class Worker4 : BackgroundService
    {
        // Статический флаг для уведомления о срабатывании триггера
        public static volatile bool TriggerFired = false;

        private readonly ILogger<Worker4> logger;
        private readonly WorkerOptions options;
        private readonly Worker worker;
        private FbRemoteEvent? remoteEvent;
        private bool isConnected = false;
        private readonly TSMethods ts;
        private readonly string version;
        
        // Количество активных потоков MainLine (обработка устройств)
        private int activeWorkers = 0;
        // Устройства, для которых уже запущен поток MainLine (ключ: "Name|IP") — не запускать второй поток для того же устройства
        private readonly ConcurrentDictionary<string, byte> _devicesInProgress = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        // Флаг для отслеживания выполнения card_in_dev / обработки события
        private volatile bool isProcessing = false;
        // Занятость: идёт card_in_dev или работает хотя бы один поток устройства
        private bool IsBusy => isProcessing || activeWorkers > 0;
        
        // Таймер для автоматического запуска card_in_dev при отсутствии событий
        private CancellationTokenSource? timeoutTimerCts;
        private readonly object timeoutTimerLock = new object();
        private Task? timeoutTimerTask;

        //08.12.2025 - добавлены две переменные

        private volatile bool isSubscribed = false;//переменная проверки подписки, которая благодаря volatile может измениться в любой момент, даже если код не делает явных изменений
        private readonly object subscriptionLock = new object();//объект синхронизации, который необходим для предотвращения гонки данных
        private DateTime _lastSubscriptionCheck = DateTime.MinValue;
        private const int SubscriptionCheckIntervalSec = 180; // проверка подписки раз в 3 мин (снижает нагрузку на fbserver от QueueEvents)

        public Worker4(ILogger<Worker4> logger, WorkerOptions options, Worker worker, TSMethods ts, string version)
        {
            this.logger = logger;
            this.options = options;
            this.worker = worker;
            this.ts = ts;
            this.version = version;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var workerStartSw = Stopwatch.StartNew();
            const string timeFmt = "HH:mm:ss.fff";
            logger.LogCritical("50 [время запуска {Time}] Worker4 запущен - мониторинг событий Firebird через FbRemoteEvent", DateTime.Now.ToString(timeFmt));
            logger.LogCritical("51 Версия программы: {Version}", version);

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            logger.LogWarning("52 [время запуска {Time}] [мс={ElapsedMs}] Worker4: задержка 1 сек завершена, подключаем FbRemoteEvent", DateTime.Now.ToString(timeFmt), workerStartSw.ElapsedMilliseconds);

            // Таймер для периодической проверки подписки (каждые 5 минут — реже = меньше нагрузка на fbserver)
            var subscriptionCheckTimer = new Timer(
                async _ => await CheckAndReconnectSubscriptionAsync(),
                null,
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(5)
            );

            // Внешний цикл: при любой критической ошибке не завершаем сервис — ждём и перезапускаем мониторинг
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    bool cancelRequested = false;
                    while (!stoppingToken.IsCancellationRequested && !cancelRequested)
                    {
                        try
                        {
                            if (!isConnected)
                                await ConnectToEventMonitor();
                            else if ((DateTime.UtcNow - _lastSubscriptionCheck).TotalSeconds >= SubscriptionCheckIntervalSec)
                            {
                                _lastSubscriptionCheck = DateTime.UtcNow;
                                bool subscriptionActive = CheckSubscriptionStatus();
                                if (!subscriptionActive)
                                {
                                    logger.LogWarning("Worker4: Подписка потеряна, пытаемся восстановить...");
                                    await ReconnectSubscriptionAsync();
                                }
                            }
                            await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
                        }
                        catch (OperationCanceledException)
                        {
                            logger.LogInformation("70 Worker4: Получен сигнал завершения работы");
                            cancelRequested = true;
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "75 Worker4: Ошибка при мониторинге событий: {Message}", LogHelper.Sanitize(ex.Message));
                            isConnected = false;
                            isSubscribed = false;
                            try
                            {
                                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                            }
                            catch (OperationCanceledException)
                            {
                                logger.LogInformation("85 Worker4: Получен сигнал завершения работы");
                                cancelRequested = true;
                            }
                        }
                    }
                    logger.LogInformation("Worker4: Цикл мониторинга завершён (остановка или отмена)");
                    break;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogCritical(ex, "Worker4: Критическая ошибка в цикле мониторинга. Перезапуск через 60 сек: {Message}", LogHelper.Sanitize(ex.Message));
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(60), stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }

            try
            {
                await subscriptionCheckTimer.DisposeAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Worker4: Ошибка при освобождении таймера подписки: {Message}", LogHelper.Sanitize(ex.Message));
            }
        }



        private async Task<bool> ConnectToEventMonitor()
        {
            const string timeFmt = "HH:mm:ss.fff";
            var connectSw = Stopwatch.StartNew();
            try
            {
                logger.LogWarning("96 [время запуска {Time}] Worker4: Подключение к FbRemoteEvent Firebird (пик нагрузки на FB)", DateTime.Now.ToString(timeFmt));
                logger.LogCritical("97 Worker4: Connection string: {ConnStr}", LogHelper.Sanitize(options.Firebird_db_config));

                var connectionStringBuilder = new FbConnectionStringBuilder(options.Firebird_db_config);
                if (connectionStringBuilder.Charset != null && connectionStringBuilder.Charset.ToUpper() != "UTF8")
                {
                    connectionStringBuilder.Charset = "UTF8";
                }
                string connectionString = connectionStringBuilder.ToString();

                remoteEvent = new FbRemoteEvent(connectionString);
                remoteEvent.RemoteEventCounts += OnRemoteEventCounts;

                logger.LogWarning("111 Worker4: FbRemoteEvent.Open()... (ожидаемая задержка до ~5 сек)");
                var tOpen = connectSw.ElapsedMilliseconds;
                await Task.Run(() => remoteEvent.Open());
                logger.LogWarning("113 Worker4: FbRemoteEvent.Open выполнен за {Ms} мс", connectSw.ElapsedMilliseconds - tOpen);

                string[] eventsToMonitor = { "CARDINDEV_INSERT" };

                logger.LogWarning("117 Worker4: QueueEvents(CARDINDEV_INSERT)...");
                var tQueue = connectSw.ElapsedMilliseconds;
                await Task.Run(() => remoteEvent.QueueEvents(eventsToMonitor));
                logger.LogWarning("119 Worker4: QueueEvents выполнен за {Ms} мс", connectSw.ElapsedMilliseconds - tQueue);

                isConnected = true;
                isSubscribed = true;
                logger.LogWarning("122 Worker4: Успешно подключен к FbRemoteEvent, всего на подключение {Ms} мс", connectSw.ElapsedMilliseconds);
                
                // Если run_now == true — пауза 4 сек, чтобы пик FB (Open событий) успел спасть до card_in_dev
                if (options.run_now)
                {
                    const int delaySec = 4;
                    logger.LogInformation("127 Worker4: run_now = true. Пауза {Sec} сек перед card_in_dev", delaySec);
                    await Task.Delay(TimeSpan.FromSeconds(delaySec));
                    await ExecuteCardInDevAsync("run_now", TimeSpan.FromSeconds(30));
                    logger.LogInformation("128 Worker4: run_now card_in_dev завершён");
                }
                
                // Запускаем таймер после успешного подключения (или после запуска при run_now)
                RestartTimeoutTimer();
                return true;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "136 Worker4: Ошибка подключения к FbRemoteEvent: {Message}", LogHelper.Sanitize(ex.Message));
                isConnected = false;
                isSubscribed = false;

                if (remoteEvent != null)
                {
                    try
                    {
                        remoteEvent.RemoteEventCounts -= OnRemoteEventCounts;
                        remoteEvent.Dispose();
                    }
                    catch { }
                    remoteEvent = null;
                }
                return false;
            }
        }

        private void OnRemoteEventCounts(object? sender, FbRemoteEventCountsEventArgs e)
        {

            if (!isConnected || !isSubscribed)
            {
                logger.LogWarning("Worker4: Получено событие, но подписка неактивна. Игнорируем.");

                // Попытка восстановления при получении события
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ReconnectSubscriptionAsync();
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Worker4: Ошибка при восстановлении после получения события: {Message}", LogHelper.Sanitize(ex.Message));
                    }
                });

                return;
            }

            // Запускаем обработку асинхронно, чтобы не блокировать поток события Firebird.
            // При получении события всегда: останавливаем таймер, запускаем card_in_dev.
            // В card_in_dev потоки создаются только для устройств, для которых ещё нет потока (_devicesInProgress).
            _ = Task.Run(async () =>
            {
                try
                {
                    logger.LogInformation("165 Worker4: Получено событие 'CARDINDEV_INSERT' от Firebird");

                    TriggerFired = true;

                    // Таймер завершаем — запуск по событию, таймер не должен срабатывать
                    RestartTimeoutTimer();

                    isProcessing = true;
                    try
                    {
                        await ExecuteCardInDevAsync("событие Firebird", TimeSpan.FromSeconds(1));
                    }
                    finally
                    {
                        isProcessing = false;
                    }

                    // После окончания обработки карт — заново запускаем таймер (следующий запуск через timeout при отсутствии событий)
                    RestartTimeoutTimer();

                    if (remoteEvent != null && isConnected)
                    {
                        // Используем lock для предотвращения одновременного вызова QueueEvents
                        lock (subscriptionLock) // или можно создать отдельный lock объект
                        {
                            try
                            {
                                // Проверяем, что подписка еще активна (опционально)
                                if (isSubscribed)
                                {
                                    string[] eventsToMonitor = { "CARDINDEV_INSERT" };
                                    remoteEvent.QueueEvents(eventsToMonitor);
                                    logger.LogDebug("Worker4: Переподписка на события выполнена");
                                }
                            }
                            catch (InvalidOperationException ex) when (ex.Message.Contains("already running"))
                            {
                                // Это нормально - события уже запущены
                                logger.LogDebug("Worker4: События уже запущены, переподписка не требуется");
                            }
                            catch (Exception ex)
                            {
                                logger.LogWarning(ex, "Worker4: Ошибка при переподписке: {Message}", LogHelper.Sanitize(ex.Message));
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "192 Worker4: Ошибка обработки события: {Message}", LogHelper.Sanitize(ex.Message));
                }
            });
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation("204 Worker4: Остановка мониторинга событий");

            try
            {
                lock (subscriptionLock)
                {
                    if (remoteEvent != null && isConnected)
                    {
                        try
                        {
                            remoteEvent.CancelEvents();
                            isSubscribed = false;
                            logger.LogInformation("211 Worker4: Отключен от FbRemoteEvent");
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "Worker4: Ошибка при отключении от событий: {Message}", LogHelper.Sanitize(ex.Message));
                        }
                    }
                    isConnected = false;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "216 Worker4: Ошибка при отключении: {Message}", LogHelper.Sanitize(ex.Message));
            }

            await base.StopAsync(cancellationToken);
        }

        private async Task ExecuteCardInDevAsync(string triggerContext, TimeSpan waitTimeout)
        {
            try
            {
                isProcessing = true;
                logger.LogInformation("236 Worker4: Запуск card_in_dev (источник: {Context})", triggerContext);
                await card_in_dev();
                logger.LogInformation("246 Worker4: Завершен card_in_dev (источник: {Context})", triggerContext);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "250 Worker4: Ошибка при запуске card_in_dev ({Context}): {Message}", triggerContext, LogHelper.Sanitize(ex.Message));
            }
            finally
            {
                isProcessing = false;
            }
        }

        private void RestartTimeoutTimer()
        {
            lock (timeoutTimerLock)
            {
                // Отменяем предыдущий таймер, если он существует
                if (timeoutTimerCts != null)
                {
                    timeoutTimerCts.Cancel();
                    timeoutTimerCts.Dispose();
                }

                // Парсим timeout из конфигурации (формат "H:M:S" или "0:01:0" с ведущими нулями)
                var timeoutParts = options.timeout.Split(':').Select(p => p.Trim()).ToArray();
                if (timeoutParts.Length != 3)
                {
                    logger.LogWarning("274 Worker4: Неверный формат timeout в конфигурации. Используется значение по умолчанию: 5 минут");
                    timeoutParts = new[] { "0", "5", "0" };
                }

                int hours = int.Parse(timeoutParts[0]);
                int minutes = int.Parse(timeoutParts[1]);
                int seconds = int.Parse(timeoutParts[2]);
                TimeSpan timeoutDuration = new TimeSpan(hours, minutes, seconds);

                if (timeoutDuration.TotalSeconds <= 0)
                {
                    logger.LogWarning("285 Worker4: Timeout должен быть больше 0. Используется значение по умолчанию: 5 минут");
                    timeoutDuration = TimeSpan.FromMinutes(5);
                }

                // Вычисляем ориентировочное время запуска
                DateTime estimatedRunTime = DateTime.Now.Add(timeoutDuration);
                string timeFormat = estimatedRunTime.ToString("HH:mm:ss");
                double totalMinutes = timeoutDuration.TotalMinutes;
                
                logger.LogTrace("294 Worker4: Таймер запущен. Автоматический запуск через {TotalMin} минут при отсутствии событий. Время следующего запуска: {TimeFormat}", totalMinutes, timeFormat);

                // Создаем новый таймер
                timeoutTimerCts = new CancellationTokenSource();
                var token = timeoutTimerCts.Token;

                timeoutTimerTask = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(timeoutDuration, token);
                        
                        // Если таймер не был отменен, значит не было новых событий - запускаем card_in_dev
                        if (!token.IsCancellationRequested)
                        {
                            logger.LogInformation("309 Worker4: Истек таймер ({TotalMin} мин). Запуск по таймауту.", timeoutDuration.TotalMinutes);

                            if (IsBusy)
                            {
                                logger.LogInformation("314 Worker4: Обработка уже идёт (IsBusy=true) — откладываем запуск по таймеру. Таймер перезапущен, следующий запуск через {TotalMin} мин.", timeoutDuration.TotalMinutes);
                                RestartTimeoutTimer();
                                return;
                            }

                            _ = ExecuteCardInDevAsync("таймер простоя", TimeSpan.FromSeconds(30));
                            RestartTimeoutTimer();
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Таймер был отменен - это нормально, значит было новое событие
                        logger.LogDebug("357 Worker4: Таймер отменен - получено новое событие");
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "361 Worker4: Ошибка в таймере автоматического запуска: {Message}", ex.Message);
                    }
                }, token);
            }
        }

        public override void Dispose()
        {
            try
            {
                // Останавливаем таймер
                lock (timeoutTimerLock)
                {
                    if (timeoutTimerCts != null)
                    {
                        timeoutTimerCts.Cancel();
                        timeoutTimerCts.Dispose();
                        timeoutTimerCts = null;
                    }
                }

                if (remoteEvent != null)
                {
                    remoteEvent.RemoteEventCounts -= OnRemoteEventCounts;
                    remoteEvent.Dispose();
                }
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "391 Worker4: Ошибка при освобождении ресурсов: {Message}", ex.Message);
            }

            base.Dispose();
        }


        


        /// <summary>Вычисляет время следующего запуска по таймауту из конфига (формат H:M:S).</summary>
        private static DateTime GetNextRunTimeFromTimeout(string timeout)
        {
            try
            {
                var parts = timeout.Split(':').Select(p => p.Trim()).ToArray();
                if (parts.Length != 3) return DateTime.Now.AddMinutes(5);
                int h = int.Parse(parts[0]);
                int m = int.Parse(parts[1]);
                int s = int.Parse(parts[2]);
                return DateTime.Now.Add(new TimeSpan(h, m, s));
            }
            catch { return DateTime.Now.AddMinutes(5); }
        }

        //1. Получить список контроллеров куда грузить карты GetListForLoad
        //2. Для каждого устройства собрать параметры для запуска.
        //3. Запустить Mainline в режиме async
        public async Task card_in_dev()
        {
            try
            {
                const string timeFmt = "HH:mm:ss.fff";
                var t0 = DateTime.Now;
                logger.LogWarning("500 [время запуска {Time}] card_in_dev: Начало обработки карт (до MainLine), время выполнения: 0 мс", t0.ToString(timeFmt));

                DataTable? table = null;
                const int accessRetries = 2;
                int attempt = 0;

                while (attempt <= accessRetries)
                {
                    attempt++;
                    try
                    {
                        var tAcc = DateTime.Now;
                        logger.LogTrace("505 [время запуска {Time}] card_in_dev: Подключение к Access, GetDevice...", tAcc.ToString(timeFmt));
                        using (var accCon = DBAccess.Connect(options.Access_db_config))
                        {
                            accCon.Open();
                            table = DBAccess.GetDevice(accCon);
                        }
                        logger.LogWarning("510 [время запуска {Time}] card_in_dev: Чтение Access (устройства) завершено, время выполнения: {Ms:F0} мс", DateTime.Now.ToString(timeFmt), (DateTime.Now - tAcc).TotalMilliseconds);
                        break;
                    }
                    catch (System.AccessViolationException avEx)
                    {
                        logger.LogError(avEx, "418 Worker4: Ошибка доступа к памяти при работе с Access DB: {Message}", avEx.Message);
                        throw;
                    }
                    catch (System.Data.OleDb.OleDbException oleEx)
                    {
                        bool resourceError = (oleEx.Message ?? "").Contains("Недостаточно системных ресурсов", StringComparison.OrdinalIgnoreCase);
                        logger.LogError(oleEx, "428 Worker4: Ошибка Access DB: {Message}", oleEx.Message);
                        if (resourceError && attempt <= accessRetries)
                        {
                            logger.LogWarning("Worker4: Повтор через 30 сек (попытка {Attempt}/{Total})", attempt, accessRetries + 1);
                            Thread.Sleep(30000);
                            continue;
                        }
                        // Не пробрасываем дальше — сервис не должен завершаться из-за Access
                        return;
                    }
                    catch (Exception accEx)
                    {
                        logger.LogError(accEx, "428 Worker4: Ошибка при подключении к Access DB: {Message}", accEx.Message);
                        if (attempt <= accessRetries)
                        {
                            Thread.Sleep(5000);
                            continue;
                        }
                        return;
                    }
                }

                if (table == null)
                {
                    logger.LogError("439 Worker4: Не удалось получить таблицу устройств из Access DB");
                    return;
                }

                // 2) Читаем из Firebird пары (idDev, name)
                var connectionStringBuilder = new FbConnectionStringBuilder(options.Firebird_db_config);
                if (connectionStringBuilder.Charset != null && connectionStringBuilder.Charset.ToUpper() != "UTF8")
                    connectionStringBuilder.Charset = "UTF8";
                // Максимальный размер пакета (Firebird: 512..32767) — меньше round-trip при GetListForLoad
                const int packetSizeForLoad = 32767;
                try
                {
                    connectionStringBuilder.PacketSize = packetSizeForLoad;
                }
                catch { /* свойство недоступно — подменим в строке */ }
                string modifiedConnectionString = connectionStringBuilder.ToString();
                if (modifiedConnectionString.IndexOf("Packet Size", StringComparison.OrdinalIgnoreCase) >= 0)
                    modifiedConnectionString = System.Text.RegularExpressions.Regex.Replace(modifiedConnectionString, @"Packet\s*Size\s*=\s*\d+", $"Packet Size={packetSizeForLoad}", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                else
                    modifiedConnectionString = modifiedConnectionString.TrimEnd(' ', ';') + "; Packet Size=" + packetSizeForLoad;

                try
                {
                    var checkBuilder = new FbConnectionStringBuilder(modifiedConnectionString);
                    logger.LogInformation("518 card_in_dev: Packet Size в строке подключения = {PacketSize}", checkBuilder.PacketSize);
                }
                catch (Exception ex)
                {
                    logger.LogDebug("518 card_in_dev: не удалось прочитать Packet Size из строки: {Message}", ex.Message);
                }

                using (var conFB = DBFirebird.Connect(modifiedConnectionString, logger))
                {
                    var tFbConn = DateTime.Now;
                    conFB.Open();
                    logger.LogWarning("520 card_in_dev: Подключение к Firebird, мс={Ms:F0}", (DateTime.Now - tFbConn).TotalMilliseconds);

                    var tLoadList = DateTime.Now;
                    var loadList = DBFirebird.GetListForLoad(conFB);
                    logger.LogWarning("530 card_in_dev: Firebird GetListForLoad (список контроллеров), мс={Ms:F0}, найдено={Count}", (DateTime.Now - tLoadList).TotalMilliseconds, loadList.Count);

                    int totalCards = DBFirebird.GetTotalCardCountForLoad(conFB);

                    var tBuild = DateTime.Now;


                    
                    var devs = Device.BuildFromAccessAndFirebird(table, loadList);
                    logger.LogWarning("540 card_in_dev: Построение списка DEV (Access+Firebird), мс={Ms:F0}, устройств={Count}", (DateTime.Now - tBuild).TotalMilliseconds, devs.Count);

                    if (devs.Count == 0)
                    {
                        DateTime nextRun = GetNextRunTimeFromTimeout(options.timeout);
                        logger.LogCritical("545 Карт для удаления/записи 0. Время следующего запуска: {Time}", nextRun.ToString(timeFmt));
                        return;
                    }

                    DateTime nextRunTime = GetNextRunTimeFromTimeout(options.timeout);
                    logger.LogCritical("545 Карт для удаления/записи {Count}. Время следующего запуска: {Time}", totalCards, nextRunTime.ToString(timeFmt));

                    logger.LogInformation("550 card_in_dev: Запуск потоков для {Count} устройств", devs.Count);
                    for (int i = 0; i < devs.Count; i++)
                    {
                        var dev = devs[i];
                        logger.LogInformation("551 card_in_dev: Устройство #{Num} Name={Name} Id={Id} IP={Ip}", i + 1, dev.controllerName, dev.id, dev.ip);
                    }

                    if (options.mode.ToLower() == "async")
                    {
                        const int staggerMs = 450;
                        for (int i = 0; i < devs.Count; i++)
                        {
                            var devLocal = devs[i];
                            string deviceKey = $"{devLocal.controllerName}|{devLocal.ip}";
                            if (!_devicesInProgress.TryAdd(deviceKey, 0))
                            {
                                logger.LogInformation(
                                    "552 Worker4: Пропуск устройства {Name} IP={Ip} — для него уже выполняется поток (повторный запуск card_in_dev)",
                                    devLocal.controllerName, devLocal.ip);
                                continue;
                            }
                            Thread thread = new Thread(() =>
                            {
                                int currentStarted = System.Threading.Interlocked.Increment(ref activeWorkers);
                                logger.LogCritical(
                                    "555 Worker4: Старт MainLine для устройства {Name} IP={Ip}, активных потоков={Count}",
                                    devLocal.controllerName, devLocal.ip, currentStarted);
                                try
                                {
                                    ts.MainLine(devLocal, modifiedConnectionString);
                                }
                                catch (Exception ex)
                                {
                                    logger.LogError(ex, "Worker4: Ошибка в потоке устройства {Name} IP {Ip}: {Message}", devLocal.controllerName, devLocal.ip, ex.Message);
                                }
                                finally
                                {
                                    _devicesInProgress.TryRemove(deviceKey, out _);
                                    int currentFinished = System.Threading.Interlocked.Decrement(ref activeWorkers);
                                    logger.LogCritical(
                                        "556 Worker4: Завершение потока MainLine для устройства {Name} IP={Ip}, активных потоков={Count}",
                                        devLocal.controllerName, devLocal.ip, currentFinished);
                                }
                            })
                            {
                                Name = $"Worker4-{devLocal.controllerName}-{devLocal.ip}"
                            };

                            thread.Start();
                            if (i < devs.Count - 1)
                                Thread.Sleep(staggerMs);
                        }

                        logger.LogWarning("560 card_in_dev: MainLine запущен в {Count} потоках (интервал старта {StaggerMs} мс)", devs.Count, staggerMs);
                    }
                    else
                    {
                        logger.LogInformation("555 [время запуска {Time}] card_in_dev: MainLine (sync) для {Count} устройств", DateTime.Now.ToString(timeFmt), devs.Count);
                        foreach (var dev in devs)
                        {
                            try
                            {
                                ts.MainLine(dev, modifiedConnectionString);
                            }
                            catch (Exception ex)
                            {
                                logger.LogDebug("card_in_dev sync error: " + ex);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Критическая ошибка в card_in_dev: {Message}", ex.Message);
                throw;
            }
        }
        //08.12.2025 - добавлен метод для проверки статуса подписки на события Firebird.
        private bool CheckSubscriptionStatus()
        {
            lock (subscriptionLock)
            {
                try
                {
                    if (remoteEvent == null)
                    {
                        logger.LogDebug("Worker4: Проверка подписки - объект remoteEvent отсутствует");
                        isSubscribed = false;
                        return false;
                    }

                    if (!isConnected)
                    {
                        logger.LogDebug("Worker4: Проверка подписки - подключение к базе данных отсутствует (isConnected = false)");
                        isSubscribed = false;
                        return false;
                    }

                    // Активно проверяем состояние подписки, вызывая QueueEvents.
                    // Если соединение событий разорвано, здесь возникнет исключение,
                    // и мы пометим подписку как неактивную, чтобы запустить восстановление.
                    string[] testEvents = { "CARDINDEV_INSERT" };
                    remoteEvent.QueueEvents(testEvents);

                    isSubscribed = true;
                    logger.LogDebug("Worker4: Проверка подписки - подписка активна (успешный QueueEvents)");
                    return true;
                }
                catch (InvalidOperationException ex)
                {
                    // Если события уже запущены, это означает, что подписка активна и работает нормально
                    if (ex.Message.Contains("already running") || ex.Message.Contains("Events are already running"))
                    {
                        isSubscribed = true;
                        logger.LogDebug("Worker4: Проверка подписки - подписка активна (события уже запущены)");
                        return true;
                    }
                    // Для других InvalidOperationException считаем подписку неактивной
                    logger.LogWarning(ex, "Worker4: Подписка неактивна (InvalidOperationException) при проверке: {Message}", ex.Message);
                    isSubscribed = false;
                    return false;
                }
                catch (FbException ex)
                {
                    logger.LogWarning(ex, "Worker4: Ошибка Firebird при проверке подписки: {Message}", ex.Message);
                    isSubscribed = false;
                    return false;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Worker4: Общая ошибка при проверке подписки: {Message}", ex.Message);
                    isSubscribed = false;
                    return false;
                }
            }
        }

        private async Task<bool> ReconnectSubscriptionAsync()
        {
            lock (subscriptionLock)
            {
                logger.LogInformation("Worker4: Попытка восстановления подписки...");

                try
                {
                    // Сначала отписываемся и освобождаем ресурсы
                    if (remoteEvent != null)
                    {
                        try
                        {
                            remoteEvent.RemoteEventCounts -= OnRemoteEventCounts;
                            remoteEvent.CancelEvents();
                            remoteEvent.Dispose();
                        }
                        catch (Exception ex)
                        {
                            logger.LogDebug("Worker4: Ошибка при очистке старого подключения: {Message}", ex.Message);
                        }
                        remoteEvent = null;
                    }

                    isConnected = false;
                    isSubscribed = false;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Worker4: Ошибка при очистке перед восстановлением: {Message}", ex.Message);
                }
            }

            // Пытаемся переподключиться
            int retryCount = 0;
            int maxRetries = 3;

            while (retryCount < maxRetries && !isSubscribed)
            {
                retryCount++;
                logger.LogInformation($"Worker4: Попытка восстановления {retryCount}/{maxRetries}...");

                try
                {
                    bool connected = await ConnectToEventMonitor();
                    if (connected)
                    {
                        logger.LogInformation("Worker4: Подписка успешно восстановлена");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, $"Worker4: Ошибка при восстановлении подписки (попытка {retryCount}): {ex.Message}");
                }

                if (retryCount < maxRetries)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5 * retryCount)); // Увеличиваем задержку
                }
            }

            logger.LogError("Worker4: Не удалось восстановить подписку после {MaxRetries} попыток", maxRetries);
            return false;
        }

        private async Task CheckAndReconnectSubscriptionAsync()
        {
            try
            {
                if (isConnected)
                {
                    bool subscriptionActive = CheckSubscriptionStatus();
                    if (!subscriptionActive)
                    {
                        logger.LogWarning("Worker4: Проверка подписки: подписка неактивна, восстанавливаем...");
                        await ReconnectSubscriptionAsync();
                    }
                    else
                    {
                        logger.LogDebug("Worker4: Проверка подписки: подписка активна");
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Worker4: Ошибка при проверке подписки: {Message}", ex.Message);
            }
        }

    }
}
