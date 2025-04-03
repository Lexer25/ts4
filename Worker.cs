using FirebirdSql.Data.FirebirdClient;
using Microsoft.Extensions.Logging;
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
        public TimeSpan timeout;
        public TimeSpan timestart;
        public TimeSpan deltasleep;

        public Worker(ILogger<Worker> logger, WorkerOptions options)
        {
            this.logger = logger;
            this.options = options;
            var time = options.timeout.Split(':');
            timeout = new TimeSpan(Int32.Parse(time[0]), Int32.Parse(time[1]), Int32.Parse(time[2]));
            time = options.timeout.Split(':');
            timestart = new TimeSpan(Int32.Parse(time[0]), Int32.Parse(time[1]), Int32.Parse(time[2]));
            var now = new TimeSpan(DateTime.Now.TimeOfDay.Hours, DateTime.Now.TimeOfDay.Minutes, DateTime.Now.TimeOfDay.Seconds);
            deltasleep = (options.run_now) ? TimeSpan.Zero :
                (timestart >= now) ? timestart - now : timestart - now + new TimeSpan(1, 0, 0, 0);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            logger.LogTrace(@$"time run basip: {timestart} deltasleep: {deltasleep}");
            await Task.Delay(deltasleep);
            while (!stoppingToken.IsCancellationRequested)
            {
                logger.LogTrace($@"Старт итерации");
                try
                {
                   run();
                }
                catch (Exception ex)
                {
                    logger.LogError("Something crash restart everything");
                    logger.LogError(ex.ToString());
                    continue;
                }
                logger.LogTrace($@"timeout basip: {timeout}");
                await Task.Delay(timeout, stoppingToken);
            }
        }
        private void run()
        {
            List<DEV> devs = new List<DEV>();//список контроллеров, с которыми программа будет работать.
            List<DEV> devListNoIP = new List<DEV>();//список контроллеров, для которых не указан IP адрес.
            logger.LogTrace($@"26 Подключение к базе данных {options.Access_db_config}");


            OleDbConnection con = DBAccess.Connect(options.Access_db_config);//config_log.db_config);
            try
            {
                con.Open();
            }
            catch (Exception Ex)
            {
                logger.LogCritical("48 Не могу подключиться к базе данных Access " + options.Access_db_config + ". Проверьте строку подключения. Программа TS3 завершает работу.");
                logger.LogCritical("82 \n" + Ex.Message.ToString());
                con.Close();
                return;
            }
            DateTime startMain = DateTime.Now;
            //Получаю список контроллеров, для которых есть очередь.


            DataTable table = DBAccess.GetDevice(con);
            logger.LogDebug("55 Имеется " + table.Rows.Count + " контроллеров. Время выполнения " + (DateTime.Now - startMain));
            con.Close();//закрыть соединение 
            logger.LogTrace("Закрыть соединение с бд");



            //теперь проверяю настройки: разделяю список на тех, у кого есть IP адрес. и у кого нет IP адреса.
            foreach (DataRow row in table.Rows)
            {
               
                if (row["ConnectionString"].ToString() != "")
                {
                    DEV newdev = new DEV(row);
                    foreach (DEV dev in devs)
                        if (newdev.ip == dev.ip)
                        {
                            logger.LogError("Повторяется: " + newdev.ip);
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


            List<Thread> threads = new List<Thread>();
           // List<Task> tasks = new List<Task>();
            foreach (DEV dev in devs)
            {
                //MainLine(dev,_logger, db_config);
                // Thread thread = new Thread(() => GetVersion(dev));
                Thread thread = new Thread(() => MainLine(dev, options.Firebird_db_config));
                threads.Add(thread);
                thread.Start();
               // tasks.Add(MainLine(dev, logger, options.Firebird_db_config));
            }
            //ждем завершение всех потоков.
            foreach (Thread thread in threads)  thread.Join();
           // Task.WaitAll(tasks.ToArray());

            logger.LogTrace($@"167 Завершаю работу основного потока.");
            logger.LogTrace("92 Завершение работы TS3. Время выполнения " + (DateTime.Now - startMain));
            return;
        }

        //остновной цикл обработки очереди 16.12.2024
        private void MainLine(DEV dev, string db_config)
        {
            DateTime start = DateTime.Now;
            string lineStat = "132 Start thread name:" + dev.controllerName + "|IP:" + dev.ip;
            DateTime _start = DateTime.Now;


            //создаю подключение к базе данных. Оно потребуется даже если нет связи с контроллером.
            FbConnection con = DBFirebird.Connect(db_config, logger);
            try
            {
                con.Open();
                //lineStat = lineStat + "|conOpen:" + (DateTime.Now - _start);
            }
            catch (Exception Ex)
            {
                logger.LogError("144 Не могу подключиться к базе данных в потоке mainLine для id_dev=" + dev.controllerName + " IP " + dev.ip + " время выполнения " + (DateTime.Now - start) + ". Завершаю  работу с этим устройством.");
                logger.LogError("147 name=" + dev.controllerName + " IP " + dev.ip + "mess " + Ex.Message.ToString());
                return;
            }
            if (con.State != ConnectionState.Open)
            {
                con.Close();
                logger.LogError("179 no connect db " + db_config + ". Завершаю поток для name=" + dev.controllerName + " IP " + dev.ip + " время выполнения " + (DateTime.Now - start), LogLevel.Error);
                return;
            }
            if (!DBFirebird.ProcedurChec(con))
            {
                logger.LogError("procedur cardindev_ts4 not found");
                return;
            }
            //lineStat += "|DBConnectOk:" + (DateTime.Now - _start);

            //создаю экземпляр контроллера
            COM com = new COM(dev.driver);
            com.SetupString(dev.ip);
            if (com.ReportStatus())//если связь с контроллером имеется, то продолжаю работу
            {

                // выполнение команд для указанного контролллера.
                OneDev(con, dev, com);

            }
            else // если нет связи, то увеличиваяю количество попыток с указанием, что нет связи
            {
                //нет связи - это происходит на 2,2 сек после старта программы
                //lineStat += "|DevConnectNo:" + (DateTime.Now - _start);
                lineStat += "|DevConnectNo:";



            }
            con.Close();//закрыл подключение к БД СКУД
            //lineStat += "|conClose:" + (DateTime.Now - _start);

            logger.LogDebug("223  " + lineStat + "|Time_execute:" + (DateTime.Now - start));
        }


        /**
         * работа с указанным контроллером: выборка списка команда, их запись и фиксация результата.
         * 
         */

        private void OneDev(FbConnection con, DEV dev, COM com)
        {

            DateTime start = DateTime.Now;

            //беру список карт для точек прохода указанного контроллера
            DataTable table;
            try
            {
               table = DBFirebird.GetComandForDevice(con, dev.controllerName);
            }
            catch (Exception ex)
            {
                logger.LogDebug("sql no comand for device");
                return;
            }
            //Console.WriteLine(@$"281 sql GetComandForDevice_{DateTime.Now - start}");
            logger.LogDebug(@$"281 sql GetComandForDevice name= {dev.controllerName} count {table.Rows.Count} time_exec:{DateTime.Now - start}");
            start = DateTime.Now;

            //собираю команды в один список cmds: на входе массив данных из БД, на выходе - массив строк с готовыми командами.
            List<Command> cmds = new List<Command>();
            foreach (DataRow row in table.Rows)
            {
                string comand = ComandBuilder(row);

                cmds.Add(new Command(row, comand));

            }

            logger.LogDebug(@$"258 Для name = {dev.controllerName} имеется {cmds.Count()} команд записи/удаления");

            //а теперь обрабаываю список команд cmds
            foreach (Command cmd in cmds)
            {
                string answer = "";
                // anser = com.ComandExclude(cmd.command);//выполнил команду
                answer = com.ComandExecute(cmd.command);//выполнил команду
                AfterComand(answer, con, cmd.dataRow);//зафиксировал результат в базе данных
                string log = $@"288 name={dev.controllerName}  | reader  {cmd.dataRow["id_reader"]} | IP {dev.ip} | {cmd.command} > {answer}";
                logger.LogDebug(log);//зафиксировал результат в лог-файле
            }

        }

        /**формирую тектовую команду
         * @input строка из базы данных
         * @output текстовая строка - команда для драйвера Artonit2.dll
         */
        private string ComandBuilder(DataRow? row)
        {
            string command = "";
            switch ((int)row["operation"])
            {
                case 1:
                    command = $@"writekey door={row["id_reader"]}, key=""{row["id_card"]}"", TZ={row["timezones"]}, status={0}";
                    break;
                case 2:
                    command = $@"deletekey door={row["id_reader"]}, key=""{row["id_card"]}""";
                    break;
            }
            return command;
        }
        private void AfterComand(string anser, FbConnection con, DataRow? row)
        {
            //string log = $@"300 {anser}";
            //_logger.LogTrace(log, LogLevel.Trace);//зафиксировал результат в лог-файле
            switch ((int)row["operation"])
            {
                case 1://1 - добавление карты в контроллер.
                    if (anser.ToUpper().Contains("OK"))
                    {
                        DBFirebird.UpdateIdxCard(con, row, anser, true);//заполнить load_result, load_time, id_card_in_dev=null
                        DBFirebird.DeleteCardInDev(con, row);//удалить строку из cardindev
                    }
                    else
                    {
                        DBFirebird.UpdateIdxCard(con, row, anser, false);
                        DBFirebird.UpdateCardInDevIncrement(con, row);//attempt+1 cardindev
                    }
                    break;
                case 2://удаление карты из контроллера

                    if (anser.ToUpper().Contains("OK"))
                    {
                        DBFirebird.DeleteCardInDev(con, row);
                    }
                    else
                    {
                        DBFirebird.UpdateCardInDevIncrement(con, row);//attempt+1
                    }
                    break;
            }
        }
    }

}