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

        // Переменные для управления подпиской
        private volatile bool isSubscribed = false;
        private readonly object subscriptionLock = new object();
        private DateTime _lastSubscriptionCheck = DateTime.MinValue;
        private const int SubscriptionCheckIntervalSec = 180; // проверка подписки раз в 3 мин

        // Переменные для отслеживания состояния
        private volatile bool _isShuttingDown = false;
        private int _consecutiveCheckFailures = 0;
        private DateTime _lastSuccessfulCheck = DateTime.Now;

        // НЕЗАВИСИМЫЙ МОНИТОР СОЕДИНЕНИЯ
        private Task? _connectionMonitorTask;
        private CancellationTokenSource? _connectionMonitorCts;
        private readonly object _monitorLock = new object();
        private const int ConnectionCheckIntervalSeconds = 300; // 5 минут
        private int _consecutiveConnectionFailures = 0;
        private const int MaxConnectionFailures = 3;

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
            // P3: фактический режим работы виден в логе при любом уровне логирования
            logger.LogCritical("51a Режим работы: isTest={IsTest}, mode={Mode}, интервал между циклами={Interval}",
                options.IsTest,
                string.IsNullOrWhiteSpace(options.mode) ? "async" : options.mode,
                WorkerTime.ParseOrDefault(options.timeout));

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException e)
            {
                logger.LogWarning(e, "Message={Message}, IsCancellationRequested={IsCancelled}",
                    e.Message,
                    stoppingToken.IsCancellationRequested);
            }
            logger.LogWarning("52 [время запуска {Time}] [мс={ElapsedMs}] Worker4: задержка 1 сек завершена, подключаем FbRemoteEvent",
                DateTime.Now.ToString(timeFmt), workerStartSw.ElapsedMilliseconds);

            // ЗАПУСКАЕМ НЕЗАВИСИМЫЙ МОНИТОРИНГ СОЕДИНЕНИЯ
            StartIndependentConnectionMonitor(stoppingToken);

            // Таймер для периодической проверки подписки (увеличен интервал)
            var subscriptionCheckTimer = new Timer(
                async _ => await CheckAndReconnectSubscriptionSafeAsync(),
                null,
                TimeSpan.FromMinutes(10),
                TimeSpan.FromMinutes(10)
            );

            // Внешний цикл: при любой критической ошибке не завершаем сервис — ждём и перезапускаем мониторинг
            while (!stoppingToken.IsCancellationRequested && !_isShuttingDown)
            {
                try
                {
                    bool cancelRequested = false;
                    while (!stoppingToken.IsCancellationRequested && !cancelRequested && !_isShuttingDown)
                    {
                        try
                        {
                            if (!isConnected)
                                await ConnectToEventMonitor();
                            else if ((DateTime.UtcNow - _lastSubscriptionCheck).TotalSeconds >= SubscriptionCheckIntervalSec)
                            {
                                _lastSubscriptionCheck = DateTime.UtcNow;

                                // Пропускаем проверку, если идёт обработка или недавно была успешная проверка
                                if (IsBusy || (DateTime.Now - _lastSuccessfulCheck).TotalMinutes < 2)
                                {
                                    logger.LogDebug("Worker4: Пропуск проверки подписки (IsBusy={IsBusy})", IsBusy);
                                    continue;
                                }

                                bool subscriptionActive = CheckSubscriptionStatusSafe();
                                if (!subscriptionActive)
                                {
                                    logger.LogWarning("Worker4: Подписка потеряна, пытаемся восстановить...");
                                    await ReconnectSubscriptionAsync();
                                }
                                else
                                {
                                    _lastSuccessfulCheck = DateTime.Now;
                                    _consecutiveCheckFailures = 0;
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

            // ОСТАНАВЛИВАЕМ НЕЗАВИСИМЫЙ МОНИТОР СОЕДИНЕНИЯ
            StopIndependentConnectionMonitor();

            try
            {
                await subscriptionCheckTimer.DisposeAsync();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Worker4: Ошибка при освобождении таймера подписки: {Message}", LogHelper.Sanitize(ex.Message));
            }
        }

        #region НЕЗАВИСИМЫЙ МОНИТОР СОЕДИНЕНИЯ

        /// <summary>
        /// Запускает независимый монитор соединения в отдельном Task
        /// </summary>
        private void StartIndependentConnectionMonitor(CancellationToken stoppingToken)
        {
            lock (_monitorLock)
            {
                if (_connectionMonitorTask != null && !_connectionMonitorTask.IsCompleted)
                {
                    logger.LogWarning("Worker4: Монитор соединения уже запущен");
                    return;
                }

                _connectionMonitorCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var token = _connectionMonitorCts.Token;

                _connectionMonitorTask = Task.Run(async () =>
                {
                    logger.LogCritical("1050 Worker4: [МОНИТОР] ЗАПУЩЕН НЕЗАВИСИМЫЙ МОНИТОР СОЕДИНЕНИЯ");

                    // Первая проверка через 30 секунд
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(30), token);
                    }
                    catch (OperationCanceledException)
                    {
                        logger.LogInformation("1055 Worker4: [МОНИТОР] Отмена перед первой проверкой");
                        return;
                    }

                    while (!token.IsCancellationRequested && !_isShuttingDown)
                    {
                        try
                        {
                            logger.LogDebug("1060 Worker4: [МОНИТОР] Запуск проверки соединения...");

                            // Проверяем соединение с БД
                            bool isConnectedOk = await CheckDatabaseConnectionSafeAsync();

                            if (isConnectedOk)
                            {
                                logger.LogInformation("1065 Worker4: [МОНИТОР] Соединение с Firebird УСПЕШНО");
                                _consecutiveConnectionFailures = 0;
                                _lastSuccessfulCheck = DateTime.Now;

                                // Если соединение восстановилось, но флаг isConnected false - исправляем
                                if (!isConnected)
                                {
                                    logger.LogWarning("1070 Worker4: [МОНИТОР] Соединение восстановлено, но флаг isConnected=false. Восстанавливаем подписку...");
                                    isConnected = true;
                                    await ReconnectSubscriptionAsync();
                                }
                                else if (!isSubscribed)
                                {
                                    // Проверяем и восстанавливаем подписку, если она потеряна
                                    logger.LogWarning("1071 Worker4: [МОНИТОР] Соединение есть, но подписка потеряна. Восстанавливаем...");
                                    await ReconnectSubscriptionAsync();
                                }
                                else
                                {
                                    // Дополнительная проверка подписки - отправляем тестовый запрос
                                    bool subscriptionActive = CheckSubscriptionStatusSafe();
                                    if (!subscriptionActive)
                                    {
                                        logger.LogWarning("1072 Worker4: [МОНИТОР] Подписка неактивна. Пересоздаём...");
                                        await ReconnectSubscriptionAsync();
                                    }
                                }
                            }
                            else
                            {
                                logger.LogWarning("1075 Worker4: [МОНИТОР] Соединение с Firebird НЕУДАЧНО");
                                _consecutiveConnectionFailures++;

                                // Если несколько неудачных проверок подряд - пробуем восстановить
                                if (_consecutiveConnectionFailures >= MaxConnectionFailures)
                                {
                                    logger.LogCritical("1080 Worker4: [МОНИТОР] {Failures} неудачных проверок подряд. Принудительное восстановление...",
                                        _consecutiveConnectionFailures);

                                    // Сбрасываем состояние
                                    isConnected = false;
                                    isSubscribed = false;

                                    // Пытаемся переподключиться
                                    bool reconnected = await ReconnectSubscriptionAsync();

                                    if (reconnected)
                                    {
                                        logger.LogInformation("1085 Worker4: [МОНИТОР] Соединение успешно восстановлено");
                                        _consecutiveConnectionFailures = 0;
                                    }
                                    else
                                    {
                                        logger.LogError("1090 Worker4: [МОНИТОР] НЕ УДАЛОСЬ восстановить соединение");
                                        // Сбрасываем счетчик, чтобы попробовать снова через интервал
                                        _consecutiveConnectionFailures = 0;
                                    }
                                }
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            logger.LogInformation("1095 Worker4: [МОНИТОР] Получен сигнал остановки");
                            break;
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "1100 Worker4: [МОНИТОР] Ошибка в цикле проверки: {Message}", ex.Message);
                        }

                        // Ждем следующий интервал проверки
                        try
                        {
                            await Task.Delay(TimeSpan.FromSeconds(ConnectionCheckIntervalSeconds), token);
                        }
                        catch (OperationCanceledException)
                        {
                            logger.LogInformation("1105 Worker4: [МОНИТОР] Остановка ожидания");
                            break;
                        }
                    }

                    logger.LogCritical("1110 Worker4: [МОНИТОР] ЗАВЕРШЕН");
                }, token);
            }
        }

        /// <summary>
        /// Останавливает независимый монитор соединения
        /// </summary>
        private void StopIndependentConnectionMonitor()
        {
            lock (_monitorLock)
            {
                try
                {
                    if (_connectionMonitorCts != null)
                    {
                        _connectionMonitorCts.Cancel();
                        _connectionMonitorCts.Dispose();
                        _connectionMonitorCts = null;
                    }

                    if (_connectionMonitorTask != null && !_connectionMonitorTask.IsCompleted)
                    {
                        // Ждем завершения задачи с таймаутом
                        _connectionMonitorTask.Wait(TimeSpan.FromSeconds(5));
                    }
                }
                catch (AggregateException agEx)
                {
                    logger.LogWarning(agEx, "1115 Worker4: Ошибка при остановке монитора: {Message}", agEx.Message);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "1120 Worker4: Ошибка при остановке монитора: {Message}", ex.Message);
                }
                finally
                {
                    _connectionMonitorTask = null;
                }
            }
        }

        /// <summary>
        /// Безопасная проверка соединения с логгированием
        /// </summary>
        private async Task<bool> CheckDatabaseConnectionSafeAsync()
        {
            try
            {
                string connectionString = options.Firebird_db_config;

                // Запускаем проверку с таймаутом
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

                return await Task.Run(() =>
                {
                    try
                    {
                        using (var connection = new FbConnection(connectionString))
                        {
                            connection.Open();

                            using (var command = new FbCommand("SELECT 1 FROM RDB$DATABASE", connection))
                            {
                                var result = command.ExecuteScalar();
                                return result != null && Convert.ToInt32(result) == 1;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogTrace("Worker4: [МОНИТОР] Ошибка проверки: {Message}", ex.Message);
                        return false;
                    }
                }, cts.Token);
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning("1125 Worker4: [МОНИТОР] Таймаут проверки соединения");
                return false;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "1130 Worker4: [МОНИТОР] Критическая ошибка при проверке: {Message}", ex.Message);
                return false;
            }
        }

        #endregion

        #region РАБОТА С ПОДПИСКОЙ

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
                _consecutiveConnectionFailures = 0;
                _consecutiveCheckFailures = 0;
                _lastSuccessfulCheck = DateTime.Now;
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
            if (!isConnected || !isSubscribed || _isShuttingDown)
            {
                logger.LogWarning("Worker4: Получено событие, но подписка неактивна. Игнорируем.");

                if (!_isShuttingDown)
                {
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
                }

                return;
            }

            // Запускаем обработку асинхронно, чтобы не блокировать поток события Firebird.
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

                    logger.LogDebug("Worker4: Обработка события завершена");
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "192 Worker4: Ошибка обработки события: {Message}", LogHelper.Sanitize(ex.Message));
                }
            });
        }

        /// <summary>
        /// Проверяет статус подписки безопасно
        /// </summary>
        private bool CheckSubscriptionStatusSafe()
        {
            // Не проверяем при завершении работы
            if (_isShuttingDown)
            {
                logger.LogDebug("Worker4: Пропуск проверки подписки - сервис завершается");
                return false;
            }

            // Если много ошибок подряд - увеличиваем интервал
            if (_consecutiveCheckFailures > 3)
            {
                logger.LogWarning("Worker4: Слишком много ошибок проверки ({Failures}), временно отключаем проверки",
                    _consecutiveCheckFailures);
                return isSubscribed;
            }

            lock (subscriptionLock)
            {
                try
                {
                    if (remoteEvent == null || !isConnected)
                    {
                        logger.LogDebug("Worker4: Проверка подписки - remoteEvent={RemoteEvent}, isConnected={Connected}",
                            remoteEvent != null, isConnected);
                        isSubscribed = false;
                        return false;
                    }

                    string[] testEvents = { "CARDINDEV_INSERT" };

                    try
                    {
                        remoteEvent.QueueEvents(testEvents);
                        isSubscribed = true;
                        logger.LogDebug("Worker4: Проверка подписки - подписка активна");
                        return true;
                    }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("already running") || ex.Message.Contains("Events are already running"))
                    {
                        // Это нормально - подписка активна
                        isSubscribed = true;
                        logger.LogDebug("Worker4: Проверка подписки - подписка активна (события уже запущены)");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    _consecutiveCheckFailures++;
                    logger.LogWarning(ex, "Worker4: Ошибка при проверке подписки (попытка {Attempt}): {Message}",
                        _consecutiveCheckFailures, ex.Message);
                    isSubscribed = false;
                    return false;
                }
            }
        }

        /// <summary>
        /// Проверка подписки из таймера
        /// </summary>
        private async Task CheckAndReconnectSubscriptionSafeAsync()
        {
            // Не проверяем при завершении работы или активной обработке
            if (_isShuttingDown || IsBusy)
            {
                logger.LogDebug("Worker4: Пропуск плановой проверки подписки (Shutdown={Shutdown}, Busy={Busy})",
                    _isShuttingDown, IsBusy);
                return;
            }

            try
            {
                if (isConnected)
                {
                    bool subscriptionActive = CheckSubscriptionStatusSafe();
                    if (!subscriptionActive && _consecutiveCheckFailures >= 2)
                    {
                        logger.LogWarning("Worker4: Плановая проверка: подписка неактивна ({Failures} ошибок), восстанавливаем...",
                            _consecutiveCheckFailures);
                        await ReconnectSubscriptionAsync();
                    }
                    else if (subscriptionActive)
                    {
                        _consecutiveCheckFailures = 0;
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Worker4: Ошибка при плановой проверке подписки: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// Переподключение подписки с полным восстановлением
        /// </summary>
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

            while (retryCount < maxRetries && !isSubscribed && !_isShuttingDown)
            {
                retryCount++;
                logger.LogInformation($"Worker4: Попытка восстановления {retryCount}/{maxRetries}...");

                try
                {
                    // Сначала проверяем, что БД доступна
                    bool dbAvailable = await CheckDatabaseConnectionSafeAsync();

                    if (!dbAvailable)
                    {
                        logger.LogWarning($"Worker4: База данных недоступна (попытка {retryCount}). Ожидание...");
                        await Task.Delay(TimeSpan.FromSeconds(10 * retryCount));
                        continue;
                    }

                    // Пытаемся подключиться к событиям
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
                    await Task.Delay(TimeSpan.FromSeconds(5 * retryCount));
                }
            }

            logger.LogError("Worker4: Не удалось восстановить подписку после {MaxRetries} попыток", maxRetries);
            return false;
        }

        #endregion

        #region ОСНОВНАЯ ЛОГИКА

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

                // P8: разбор без исключений — отсутствие или опечатка в timeout не должны
                // приводить к исключению внутри блокировки и остановке авто-запуска
                if (!WorkerTime.IsValid(options.timeout))
                {
                    logger.LogWarning("274 Worker4: Неверный формат timeout в конфигурации ({Value}). Используется значение по умолчанию: 5 минут", options.timeout);
                }
                TimeSpan timeoutDuration = WorkerTime.ParseOrDefault(options.timeout);

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
                        if (!token.IsCancellationRequested && !_isShuttingDown)
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

        public async Task card_in_dev()
        {
            try
            {
                const string timeFmt = "HH:mm:ss.fff";
                var t0 = DateTime.Now;
                logger.LogWarning("500 [время запуска {Time}] card_in_dev: Начало обработки карт (до MainLine), время выполнения: 0 мс", t0.ToString(timeFmt));

                // Путь к MDB и TEMP проверяем заранее: иначе Jet отвечает неинформативной
                // «Неопознанная ошибка» (0x80004005), по которой причину не найти.
                string? mdbPath = DBAccess.GetDataSourcePath(options.Access_db_config);
                if (string.IsNullOrWhiteSpace(mdbPath))
                {
                    logger.LogCritical("504 card_in_dev: в Access_db_config не задан путь к MDB (Data Source) — карты не будут обработаны");
                    return;
                }
                if (!File.Exists(mdbPath))
                {
                    logger.LogCritical("504 card_in_dev: файл Access не найден: {Path}. Проверьте Access_db_config в appsettings.json", mdbPath);
                    return;
                }
                string? tempIssue = DBAccess.CheckTempFolder();
                if (tempIssue != null)
                {
                    logger.LogCritical("504 card_in_dev: {Issue} — Jet не сможет открыть Access (симптом «Неопознанная ошибка», 0x80004005)", tempIssue);
                    return;
                }

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
                        // Список устройств, найденных в Access (подробно — уровень Trace)
                        if (table != null && logger.IsEnabled(LogLevel.Trace))
                        {
                            logger.LogTrace("508 card_in_dev: устройства из Access, найдено {Count}", table.Rows.Count);
                            for (int i = 0; i < table.Rows.Count; i++)
                            {
                                DataRow row = table.Rows[i];
                                logger.LogTrace("508 card_in_dev: устройство #{Num}: ID={Id}, Name={Name}, IP={Ip}, Driver={Driver}",
                                    i + 1,
                                    row["ID"]?.ToString(),
                                    row["Name"]?.ToString(),
                                    row["ConnectionString"]?.ToString(),
                                    row["Driver"]?.ToString());
                            }
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
                        logger.LogError(oleEx, "428 Worker4: Ошибка Access DB ({Path}): {Message}. TEMP={Temp}", mdbPath, oleEx.Message, Path.GetTempPath());
                        if (resourceError && attempt <= accessRetries)
                        {
                            logger.LogWarning("Worker4: Повтор через 30 сек (попытка {Attempt}/{Total})", attempt, accessRetries + 1);
                            Thread.Sleep(30000);
                            continue;
                        }
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
                const int packetSizeForLoad = 32767;
                try
                {
                    connectionStringBuilder.PacketSize = packetSizeForLoad;
                }
                catch { }
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

                    if (string.Equals(options.mode, "async", StringComparison.OrdinalIgnoreCase))
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
                                Name = $"Worker4-{devLocal.controllerName}-{devLocal.ip}",
                                // P7: фоновый поток не мешает процессу завершиться, если контроллер завис
                                IsBackground = true
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

        #endregion

        #region ОСТАНОВКА И ОСВОБОЖДЕНИЕ РЕСУРСОВ

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation("204 Worker4: Остановка мониторинга событий");
            _isShuttingDown = true;

            // ОСТАНАВЛИВАЕМ НЕЗАВИСИМЫЙ МОНИТОР
            StopIndependentConnectionMonitor();

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

            lock (subscriptionLock)
            {
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
                        logger.LogError(ex, "Worker4: Ошибка при отключении от событий: {Message}", LogHelper.Sanitize(ex.Message));
                    }
                    finally
                    {
                        remoteEvent = null;
                    }
                }
                isConnected = false;
                isSubscribed = false;
            }

            logger.LogInformation("211 Worker4: Отключен от FbRemoteEvent");

            // Даем время на завершение всех потоков
            await Task.Delay(1000, cancellationToken);

            await base.StopAsync(cancellationToken);
        }

        public override void Dispose()
        {
            try
            {
                _isShuttingDown = true;

                // ОСТАНАВЛИВАЕМ НЕЗАВИСИМЫЙ МОНИТОР
                StopIndependentConnectionMonitor();

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

        #endregion
    }
}