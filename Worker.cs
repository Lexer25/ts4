using FirebirdSql.Data.FirebirdClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Configuration;
using System.Data;
using System.Data.Common;
using System.Data.Odbc;
using System.Data.OleDb;
using System.Threading;

namespace TS4
{
        public class Worker : BackgroundService
    {
        public readonly ILogger logger;
        private WorkerOptions options;
        private string version;
        public TimeSpan timeout;
        public TimeSpan timestart;
        public TimeSpan deltasleep;
        private readonly TSMethods ts;
            

        public Worker(ILogger<Worker> logger, WorkerOptions options, string version, TSMethods ts)
        {
            this.logger = logger;
            this.options = options;
            this.version = version;
            this.ts = ts;
            // P8: без исключений при отсутствии/опечатке в timeout. Раньше здесь падал
            // конструктор, а вместе с ним и запуск всей службы (Worker — зависимость Worker4).
            timeout = WorkerTime.ParseOrDefault(options.timeout);
            timestart = timeout;
            var now = new TimeSpan(DateTime.Now.TimeOfDay.Hours, DateTime.Now.TimeOfDay.Minutes, DateTime.Now.TimeOfDay.Seconds);
            deltasleep = (options.run_now) ? TimeSpan.Zero :
                (timestart >= now) ? timestart - now : timestart - now + new TimeSpan(1, 0, 0, 0);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            logger.LogInformation($"Версия программы: {version}");
            Console.WriteLine($"Версия программы: {version}");
            
            logger.LogDebug(@$"time run basip: {timestart} deltasleep: {deltasleep}");
            await Task.Delay(deltasleep);
            while (!stoppingToken.IsCancellationRequested)
            {
                logger.LogDebug($@" ");
                try
                {
                   run();
                }
                catch (Exception ex)
                {
                    logger.LogError("45 Something crash restart everything");
                    logger.LogDebug("46 "+ex.ToString());
                    continue;
                }
                logger.LogTrace($@"timeout ts4 sleep on: {timeout}");
                
                var endTime = DateTime.Now.Add(timeout);
                logger.LogInformation($"Пауза до: {endTime:dd.MM.yyyy HH:mm:ss}");
                Console.WriteLine($"Пауза до: {endTime:dd.MM.yyyy HH:mm:ss}");
                
                await Task.Delay(timeout, stoppingToken);
            }
        }
        public void run()
        {
            List<DEV> devs = new List<DEV>();//список контроллеров, с которыми программа будет работать.
            List<DEV> devListNoIP = new List<DEV>();//список контроллеров, для которых не указан IP адрес.
            Console.WriteLine("71 дошли сюда");
            logger.LogTrace($@"26 Подключение к базе данных Firebird {options.Firebird_db_config}");
            //using (var con1 = DBFirebird.Connect(options.Firebird_db_config, logger))
            //{
            //    con1.Open();
            //    Console.WriteLine("76 дошли сюда");
            //    // Получаем список d2.id_dev
            //    List<int> loadIds = DBFirebird.GetListForLoad(con1);
            //
            //    // Пример: вывод в консоль
            //    foreach (var id in loadIds)
            //    {
            //        Console.WriteLine($"ID девайса: {id}");
            //        logger.LogInformation($"80 ID девайса: {id}");
            //    }
            //}
            logger.LogTrace($@"86 Подключение к базе данных Access MDB {options.Access_db_config}");
            OleDbConnection con = DBAccess.Connect(options.Access_db_config);//config_log.db_config);
            try
            {
                con.Open();
                Console.WriteLine("92 дошли сюда");
            }
            catch (Exception Ex)
            {
                logger.LogCritical("48 Не могу подключиться к базе данных Access " + options.Access_db_config + ". Проверьте строку подключения. Программа TS3 завершает работу.");
                logger.LogCritical("82 " + Ex.Message.ToString());
                con.Close();
                return;
            }
            DateTime startMain = DateTime.Now;
            //Получаю список контроллеров, для которых есть очередь.


            DataTable table = DBAccess.GetDevice(con);
            logger.LogDebug("55 Имеется " + table.Rows.Count + " контроллеров. Время выполнения " + (DateTime.Now - startMain));
            con.Close();//закрыть соединение 
            logger.LogTrace($@"186 Закрыть соединение с бд {options.Access_db_config}");



            //теперь проверяю настройки: разделяю список на тех, у кого есть IP адрес. и у кого нет IP адреса.
            foreach (DataRow row in table.Rows)
            {
               
                if (row["ConnectionString"].ToString() != "")
                {
                    DEV newdev = new DEV(row);
                    foreach (DEV dev in devs)
                        if (newdev.ip == dev.ip)
                        {
                            logger.LogError(": " + newdev.ip);
                            newdev = null;
                        }
                    if (!(newdev is null)) devs.Add(newdev);
                    //у этих есть IP адрес, и далее буду работать с ними.
                }
                else
                {
                    // devListNoIP.Add(new DEV(row));//тут собраны контроллеры без IP адреса
                }

            }

            logger.LogDebug("72 Есть ip адреса для " + devs.Count + " контроллеров. А для " + (table.Rows.Count - devs.Count) + " контроллеров IP адресов нет.");


            if (devs.Count == 0)
            {
                string mess = "54 Нет данных для загрузки/удаления идентификаторов из контроллеров. Время выполнения " + (DateTime.Now - startMain);
                logger.LogError(mess);
                // Console.WriteLine(mess);

                mess = "58 Программа TS3 завершает работу: нет данных для работы. Время выполнения " + (DateTime.Now - startMain);
                logger.LogError(mess);
                //Console.WriteLine(mess);
                return;
            }

            logger.LogDebug("64 Имеются данных для загрузки/удаления идентификаторов в " + devs.Count + " контроллеров. Время выполнения " + (DateTime.Now - startMain), LogLevel.Debug);


            logger.LogTrace("70 Начинаю основной поток. Время выполнения " + (DateTime.Now - startMain));
            if (options.mode.ToLower() == "async")
            {
                Console.WriteLine("127 SYNC MODE");
                logger.LogInformation("128 МОД: "+ options.mode);
                //return ;
                // Асинхронный режим с пакетной обработкой
                int processedCount = 0;
                int totalDevices = devs.Count;

                while (processedCount < totalDevices)
                {
                    List<Thread> threads = new List<Thread>();

                    // Берем следующую партию устройств
                    int batchSize = Math.Min(options.maxThreads, totalDevices - processedCount);

                    for (int i = 0; i < batchSize; i++)
                    {
                        DEV dev = devs[processedCount + i];

                        Thread thread = new Thread(() => {
                            try
                            {
                                ts.MainLine(dev, options.Firebird_db_config);
                            }
                            catch (Exception ex)
                            {
                                logger.LogError($@"139  ip {dev.ip} name {dev.controllerName}  ");
                                logger.LogDebug("140 " + ex.ToString());
                            }
                        })
                        {
                            Name = $"Worker-{dev.controllerName}-{dev.ip}"
                        };

                        threads.Add(thread);
                        thread.Start();
                    }

                    // Ждем завершения текущей партии потоков
                    foreach (Thread thread in threads)
                    {
                        thread.Join();
                    }

                    processedCount += batchSize;
                    logger.LogTrace($@"Обработано устройств: {processedCount}/{totalDevices}");
                }
            }
            else
            {
                // Синхронный режим - последовательное выполнение
                Console.WriteLine("171 SERIAL MODE");
                logger.LogTrace("175 MODE: "+ options.mode);
                //return ;
                foreach (DEV dev in devs)
                {
                    try
                    {
                        ts.MainLine(dev, options.Firebird_db_config);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError($@"139 Поток девайса с ip {dev.ip} name {dev.controllerName} обрыв подключение к БД СКУД");
                        logger.LogDebug("140 " + ex.ToString());
                    }
                }
            }
            logger.LogTrace($@"167 Завершаю работу основного потока.");
            logger.LogTrace("92 Завершение работы TS3. Время выполнения " + (DateTime.Now - startMain));
            return;
        }

    }

}