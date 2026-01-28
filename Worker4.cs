using System;
using System.Collections.Generic;
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
        
        // Семафор для предотвращения одновременного выполнения card_in_dev из разных потоков
        private readonly SemaphoreSlim cardProcessingSemaphore = new SemaphoreSlim(5, 5);
        
        // Флаг для отслеживания обработки событий
        private volatile bool isProcessing = false;
        
        // Таймер для автоматического запуска card_in_dev при отсутствии событий
        private CancellationTokenSource? timeoutTimerCts;
        private readonly object timeoutTimerLock = new object();
        private Task? timeoutTimerTask;

        //08.12.2025 - добавлены две переменные

        private volatile bool isSubscribed = false;//переменная проверки подписки, которая благодаря volatile может измениться в любой момент, даже если код не делает явных изменений
        private readonly object subscriptionLock = new object();//объект синхронизации, который необходим для предотвращения гонки данных

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
            logger.LogInformation("50 Worker4 запущен - мониторинг событий Firebird через FbRemoteEvent");
            logger.LogInformation($"51 Версия программы: {version}");

            // Проверка наличия процедуры и триггера в начале программы
            //await CheckDatabaseObjects(stoppingToken);

            await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);

            // Таймер для периодической проверки подписки (каждые 2 минуты)
            var subscriptionCheckTimer = new Timer(
                async _ => await CheckAndReconnectSubscriptionAsync(),
                null,
                TimeSpan.FromMinutes(2),
                TimeSpan.FromMinutes(2)
            );

            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    try
                    {
                        if (!isConnected)
                        {
                            await ConnectToEventMonitor();
                        }
                        else
                        {
                            // Периодически проверяем подписку каждые 30 секунд
                            bool subscriptionActive = CheckSubscriptionStatus();
                            if (!subscriptionActive && isConnected)
                            {
                                logger.LogWarning("Worker4: Подписка потеряна, пытаемся восстановить...");
                                await ReconnectSubscriptionAsync();
                            }
                        }

                        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                    }
                    catch (OperationCanceledException)
                    {
                        // Корректное завершение работы - не логируем как ошибку
                        logger.LogInformation("70 Worker4: Получен сигнал завершения работы");
                        break;
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "75 Worker4: Ошибка при мониторинге событий: {Message}", ex.Message);
                        isConnected = false;
                        isSubscribed = false;

                        try
                        {
                            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
                        }
                        catch (OperationCanceledException)
                        {
                            // Корректное завершение работы - не логируем как ошибку
                            logger.LogInformation("85 Worker4: Получен сигнал завершения работы");
                            break;
                        }
                    }
                }
            }
            finally
            {
                // Очищаем таймер проверки подписки
                await subscriptionCheckTimer.DisposeAsync();
            }
        }



        private async Task<bool> ConnectToEventMonitor()
        {
            try
            {
                logger.LogInformation("96 Worker4: Подключение к FbRemoteEvent Firebird");
                logger.LogInformation("97 Worker4: Connection string: {ConnStr}", options.Firebird_db_config);

                var connectionStringBuilder = new FbConnectionStringBuilder(options.Firebird_db_config);
                if (connectionStringBuilder.Charset != null && connectionStringBuilder.Charset.ToUpper() != "UTF8")
                {
                    connectionStringBuilder.Charset = "UTF8";
                }
                string connectionString = connectionStringBuilder.ToString();

                logger.LogInformation("106 Worker4: Modified connection string: {ConnStr}", connectionString);

                remoteEvent = new FbRemoteEvent(connectionString);
                remoteEvent.RemoteEventCounts += OnRemoteEventCounts;

                logger.LogInformation("111 Worker4: Открываем соединение...");
                await Task.Run(() => remoteEvent.Open());
                logger.LogInformation("113 Worker4: Соединение открыто");

                string[] eventsToMonitor = { "CARDINDEV_INSERT" };

                logger.LogInformation("117 Worker4: Подписываемся на события...");
                await Task.Run(() => remoteEvent.QueueEvents(eventsToMonitor));
                logger.LogInformation("119 Worker4: Подписка активна");

                isConnected = true;
                isSubscribed = true;
                logger.LogInformation("122 Worker4: Успешно подключен к FbRemoteEvent Firebird");
                
                // Если run_now == true, запускаем card_in_dev один раз сразу без таймера
                if (options.run_now)
                {
                    logger.LogInformation("127 Worker4: run_now = true. Запускаем card_in_dev сразу после подключения");
                    await ExecuteCardInDevAsync("run_now", TimeSpan.FromSeconds(30));
                }
                
                // Запускаем таймер после успешного подключения (или после запуска при run_now)
                RestartTimeoutTimer();
                return true;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "136 Worker4: Ошибка подключения к FbRemoteEvent: {Message}", ex.Message);
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
                        logger.LogError(ex, "Worker4: Ошибка при восстановлении после получения события: {Message}", ex.Message);
                    }
                });

                return;
            }

            // Запускаем обработку асинхронно, чтобы не блокировать поток события
            // но используем семафор для предотвращения одновременных вызовов
            _ = Task.Run(async () =>
            {
                // Если уже обрабатывается событие, пропускаем новое (debouncing)
                if (isProcessing)
                {
                    logger.LogDebug("151 Worker4: Событие получено, но обработка уже выполняется. Пропускаем.");
                    return;
                }

                try
                {
                    logger.LogInformation("165 Worker4: Получено событие 'CARDINDEV_INSERT' от Firebird");

                    // Устанавливаем флаг о срабатывании триггера
                    TriggerFired = true;

                    await ExecuteCardInDevAsync("событие Firebird", TimeSpan.FromSeconds(1));



                    // Перезапускаем таймер после обработки обоих запусков
                    RestartTimeoutTimer();

                    //if (remoteEvent != null && isConnected)
                    //{
                    //string[] eventsToMonitor = { "CARDINDEV_INSERT" };
                    //  Task.Run(() => remoteEvent.QueueEvents(eventsToMonitor));
                    //}
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
                                logger.LogWarning(ex, "Worker4: Ошибка при переподписке: {Message}", ex.Message);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "192 Worker4: Ошибка обработки события: {Message}", ex.Message);
                }
                finally
                {
                    isProcessing = false;
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
                            logger.LogError(ex, "Worker4: Ошибка при отключении от событий: {Message}", ex.Message);
                        }
                    }
                    isConnected = false;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "216 Worker4: Ошибка при отключении: {Message}", ex.Message);
            }

            await base.StopAsync(cancellationToken);
        }

        private async Task ExecuteCardInDevAsync(string triggerContext, TimeSpan waitTimeout)
        {
            if (!await cardProcessingSemaphore.WaitAsync(waitTimeout))
            {
                logger.LogWarning("227 Worker4: Не удалось получить доступ для запуска card_in_dev ({Context})", triggerContext);
                return;
            }

            try
            {
                isProcessing = true;
                logger.LogInformation("236 Worker4: Запуск card_in_dev (источник: {Context})", triggerContext);
                card_in_dev();
                logger.LogInformation("246 Worker4: Завершен card_in_dev (источник: {Context})", triggerContext);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "250 Worker4: Ошибка при запуске card_in_dev ({Context}): {Message}", triggerContext, ex.Message);
            }
            finally
            {
                isProcessing = false;
                cardProcessingSemaphore.Release();
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

                // Парсим timeout из конфигурации (формат "H:M:S")
                var timeoutParts = options.timeout.Split(':');
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
                
                logger.LogInformation($"294 Worker4: Таймер запущен. Автоматический запуск через {totalMinutes} минут при отсутствии событий. Время следующего запуска: {timeFormat}");

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
                            logger.LogInformation($"309 Worker4: Истек таймер ({timeoutDuration.TotalMinutes} минут). Событий не было. Запуск card_in_dev() автоматически.");

                            // Проверяем, не обрабатывается ли уже событие
                            if (isProcessing)
                            {
                                logger.LogDebug("314 Worker4: Пропускаем автоматический запуск - идет обработка события");
                                return;
                            }

                            await ExecuteCardInDevAsync("таймер простоя", TimeSpan.FromSeconds(1));
                            
                            // После автоматического запуска снова запускаем таймер
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
                cardProcessingSemaphore?.Dispose();
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "391 Worker4: Ошибка при освобождении ресурсов: {Message}", ex.Message);
            }

            base.Dispose();
        }


        


        //1. Получить список контроллеров куда грузить карты GetListForLoad
        //2. Для каждого устройства собрать параметры для запуска.
        //3. Запустить Mainline в режиме async
        public async Task card_in_dev()
        {
            try
            {
                // 1) Читаем Access-таблицу устройств
                // Добавляем дополнительную защиту от ошибок доступа к памяти
                OleDbConnection? accCon = null;
                DataTable? table = null;

                try
                {
                    accCon = DBAccess.Connect(options.Access_db_config);
                    accCon.Open();
                    table = DBAccess.GetDevice(accCon);
                    accCon.Close();
                    accCon = null; // Указываем, что соединение уже закрыто
                }
                catch (System.AccessViolationException avEx)
                {
                    logger.LogError(avEx, "418 Worker4: Ошибка доступа к памяти при работе с Access DB: {Message}", avEx.Message);
                    if (accCon != null)
                    {
                        try { accCon.Close(); } catch { }
                        accCon.Dispose();
                    }
                    throw;
                }
                catch (Exception accEx)
                {
                    logger.LogError(accEx, "428 Worker4: Ошибка при подключении к Access DB: {Message}", accEx.Message);
                    if (accCon != null)
                    {
                        try { accCon.Close(); } catch { }
                        accCon.Dispose();
                    }
                    throw;
                }

                if (table == null)
                {
                    logger.LogError("439 Worker4: Не удалось получить таблицу устройств из Access DB");
                    return;
                }

                // 2) Читаем из Firebird пары (idDev, name)
                // Исправляем строку подключения: используем UTF8 или корректную кодировку
                var connectionStringBuilder = new FbConnectionStringBuilder(options.Firebird_db_config);
                // Используем UTF8 для совместимости, можно также попробовать WIN1251 если UTF8 не работает
                if (connectionStringBuilder.Charset != null && connectionStringBuilder.Charset.ToUpper() != "UTF8")
                {
                    connectionStringBuilder.Charset = "UTF8";
                }
                string modifiedConnectionString = connectionStringBuilder.ToString();

                using (var conFB = DBFirebird.Connect(modifiedConnectionString, logger))
                {
                    conFB.Open();
                    var loadList = DBFirebird.GetListForLoad(conFB); // теперь (idDev, name)

                    // 3) Собираем итоговый список DEV по имени
                    var devs = Device.BuildFromAccessAndFirebird(table, loadList);

                    if (devs.Count == 0)
                    {
                        logger.LogInformation("463 card_in_dev: нет карт для записи/удаления, очередь пуста");
                        return;
                    }

                    logger.LogInformation($"467 card_in_dev: устройств к обработке = {devs.Count}");

                    // Добавьте логирование для отладки
                    logger.LogInformation($"card_in_dev: Начинаем обработку устройств. Options.mode = {options.mode}");

                    // Логируем каждое устройство перед обработкой
                    for (int i = 0; i < devs.Count; i++)
                    {
                        var dev = devs[i];
                        logger.LogInformation($"Device #{i + 1}: Name={dev.controllerName}, Id={dev.id}");
                    }

                    logger.LogInformation($"card_in_dev: устройств к обработке = {devs.Count}");

                    if (options.mode.ToLower() == "async")
                    {
                        // УБЕРИТЕ threads list и Thread.Join()!

                        for (int i = 0; i < devs.Count; i++)
                        {
                            var devLocal = devs[i];

                            // Запускаем каждый поток и НЕ ЖДЕМ его завершения!
                            Thread thread = new Thread(() =>
                            {
                                try
                                {
                                    ts.MainLine(devLocal, modifiedConnectionString);
                                }
                                catch (Exception ex)
                                {
                                    logger.LogDebug("card_in_dev thread error: " + ex);
                                }
                            })
                            {
                                Name = $"Worker4-{devLocal.controllerName}-{devLocal.ip}"
                            };

                            thread.Start(); // Просто запускаем и забываем!
                            logger.LogDebug($"Запущен поток для устройства: {devLocal.controllerName}");
                        }

                        logger.LogInformation($"Запущено {devs.Count} потоков для обработки устройств");

                        // НЕ ВЫЗЫВАЙТЕ Thread.Join()! Пусть потоки работают параллельно!
                    }
                    else
                    {
                        // Синхронно (если нужно)
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
                        logger.LogDebug("Подписка отсуствует");
                        isSubscribed = false;
                        return false;
                    }

                    if (!isConnected)
                    {
                        logger.LogDebug("Подключение к базе данных отсутствует");
                        isSubscribed = false;
                        return false;
                    }

                    //string[] testEvents = { "CARDINDEV_INSERT" };
                    //remoteEvent.QueueEvents(testEvents);
                    isSubscribed = true;
                    logger.LogDebug("Подписка активна");
                    return true;
                }
                catch (InvalidOperationException ex)
                {
                    logger.LogWarning("Worker4: Подписка неактивна (InvalidOperationException): {Message}", ex.Message);
                    isSubscribed = false;
                    return false;
                }
                catch (FbException ex)
                {
                    logger.LogWarning("Worker4: Ошибка Firebird при проверке подписки: {Message}", ex.Message);
                    isSubscribed = false;
                    return false;
                }
                catch (Exception ex)
                {
                    logger.LogWarning("Worker4: Ошибка при проверке подписки: {Message}", ex.Message);
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
