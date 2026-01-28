using FirebirdSql.Data.FirebirdClient;
using Microsoft.Extensions.Logging;
using System.Data;

namespace TS4
{
	public class TSMethods
	{
		private readonly ILogger<TSMethods> logger;
		private readonly WorkerOptions options;

		public TSMethods(ILogger<TSMethods> logger, WorkerOptions options)
		{
			this.logger = logger;
			this.options = options;
		}

		public void MainLine(DEV dev, string db_config)
		{
			DateTime start = DateTime.Now;
			string lineStat = "132 Start thread name:" + dev.controllerName + "|IP:" + dev.ip;
			logger.LogTrace(lineStat);
			DateTime _start = DateTime.Now;

			FbConnection con = DBFirebird.Connect(db_config, logger);
			try
			{
				con.Open();
			}
			catch (Exception Ex)
			{
				logger.LogError("144 Не могу подключиться к базе данных в потоке mainLine для id_dev=" + dev.controllerName + " IP " + dev.ip + " время выполнения " + (DateTime.Now - start) + ". Завершаю  работу с этим устройством.");
				logger.LogError("147 name=" + dev.controllerName + " IP " + dev.ip + "mess " + Ex.Message.ToString());
				con.Close();
				logger.LogInformation($"299 Поток завершен (ошибка подключения к БД) для устройства: {dev.controllerName} IP: {dev.ip}");
				return;
			}

			if (con.State != ConnectionState.Open)
			{
				con.Close();
				logger.LogError("179 no connect db " + db_config + ". Завершаю поток для name=" + dev.controllerName + " IP " + dev.ip + " время выполнения " + (DateTime.Now - start), LogLevel.Error);
				logger.LogInformation($"300 Поток завершен (нет подключения к БД) для устройства: {dev.controllerName} IP: {dev.ip}");
				return;
			}

			// Проверка наличия процедуры CARDINDEV_TS4
			bool procedureExists = DBFirebird.ProcedurChec(con);
			if (procedureExists)
			{
				logger.LogInformation($"Проверка процедуры: процедура CARDINDEV_TS4 найдена в базе данных");
			}
			else
			{
				logger.LogError("Проверка процедуры: процедура CARDINDEV_TS4 не найдена в базе данных");
				con.Close();
				logger.LogInformation($"301 Поток завершен (отсутствует процедура) для устройства: {dev.controllerName} IP: {dev.ip}");
				return;
			}

			// Проверка наличия триггера CARDINDEV_INSERT
			bool triggerExists = DBFirebird.TriggerCheck(con);
			if (triggerExists)
			{
				logger.LogInformation($"Проверка триггера: триггер для события CARDINDEV_INSERT найден в базе данных");
			}
			else
			{
				logger.LogError("Проверка триггера: триггер для события CARDINDEV_INSERT не найден в базе данных");
				con.Close();
				logger.LogInformation($"302 Поток завершен (отсутствует триггер) для устройства: {dev.controllerName} IP: {dev.ip}");
				return;
			}

			COM com = new COM(dev.driver, options);
			com.SetupString(dev.ip);
			if (com.ReportStatus())
			{
				//Console.WriteLine("196, ПОПАЛИ СЮДА");
				OneDev(con, dev, com);
				//logger.LogInformation($"302 Поток успешно завершен для устройства: {dev.controllerName} IP: {dev.ip}");
			}
			else
			{
				lineStat += "|DevConnectNo:";
				logger.LogInformation($"303 Поток завершен (нет связи с контроллером) для устройства: {dev.controllerName} IP: {dev.ip}");
			}

			con.Close();
			//logger.LogTrace("223  " + lineStat + "|Time_execute:" + (DateTime.Now - start));
		}

		private void OneDev(FbConnection con, DEV dev, COM com)
		{
			DateTime start = DateTime.Now;

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
			//logger.LogTrace(@$"281 sql GetComandForDevice name= {dev.controllerName} count {table.Rows.Count} time_exec:{DateTime.Now - start}");
			start = DateTime.Now;

			List<Command> cmds = new List<Command>();
			foreach (DataRow row in table.Rows)
			{
				string comand = ComandBuilder(row);
				cmds.Add(new Command(row, comand));
			}

			//logger.LogTrace(@$"258  name = {dev.controllerName} {cmds.Count()}  /");

			foreach (Command cmd in cmds)
			{
				string operationType = ((int)cmd.dataRow["operation"] == 1) ? "Write" : "Delete";
				logger.LogInformation(@$"{operationType} operation: id_dev={cmd.dataRow["id_dev"]} name={dev.controllerName} reader={cmd.dataRow["id_reader"]} card={cmd.dataRow["id_card"]} IP={dev.ip} Command: {cmd.command}");
				
				string answer = "";
				answer = com.ComandExecute(cmd.command);
				AfterComand(answer, con, cmd.dataRow, dev, operationType);
				logger.LogCritical(@$"Answer destination:  {(((int)cmd.dataRow["operation"] == 1) ? "AddCard " : "DeleteCard")} id_dev={cmd.dataRow["id_dev"]} name={dev.controllerName} reader={cmd.dataRow["id_reader"]} IP={dev.ip} Command: {cmd.command} Answer: {answer}");
			}
		}

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

		private void AfterComand(string anser, FbConnection con, DataRow? row, DEV dev, string operationType)
		{
			switch ((int)row["operation"]) 
			{
				case 1:
					if (anser.ToUpper().Contains("OK"))
					{
						logger.LogInformation(@$"Write operation completed successfully: id_dev={row["id_dev"]} name={dev.controllerName} reader={row["id_reader"]} card={row["id_card"]} IP={dev.ip}");
						DBFirebird.UpdateIdxCard(con, row, anser, true);
						DBFirebird.DeleteCardInDev(con, row);
					}
					else
					{
						logger.LogWarning(@$"Write operation failed: id_dev={row["id_dev"]} name={dev.controllerName} reader={row["id_reader"]} card={row["id_card"]} IP={dev.ip} Answer: {anser}");
						DBFirebird.UpdateIdxCard(con, row, anser, false);
						DBFirebird.UpdateCardInDevIncrement(con, row);
					}
					break;
				case 2:
					if (anser.ToUpper().Contains("OK"))
					{
						logger.LogInformation(@$"Delete operation completed successfully: id_dev={row["id_dev"]} name={dev.controllerName} reader={row["id_reader"]} card={row["id_card"]} IP={dev.ip}");
						DBFirebird.DeleteCardInDev(con, row);
					}
					else
					{
						logger.LogWarning(@$"Delete operation failed: id_dev={row["id_dev"]} name={dev.controllerName} reader={row["id_reader"]} card={row["id_card"]} IP={dev.ip} Answer: {anser}");
						DBFirebird.UpdateCardInDevIncrement(con, row);
					}
					break;
			}
		}
	}
}

