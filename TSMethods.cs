using FirebirdSql.Data.FirebirdClient;
using Microsoft.Extensions.Logging;
using System;
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
			try { con.Close(); } catch { }
			try { con.Dispose(); } catch { }
			logger.LogInformation($"299 Поток завершен (ошибка подключения к БД) для устройства: {dev.controllerName} IP: {dev.ip}");
			return;
		}

		try
		{
			if (con.State != ConnectionState.Open)
			{
				try { con.Close(); } catch { }
				try { con.Dispose(); } catch { }
				logger.LogError("179 no connect db " + db_config + ". Завершаю поток для name=" + dev.controllerName + " IP " + dev.ip + " время выполнения " + (DateTime.Now - start), LogLevel.Error);
				logger.LogInformation($"300 Поток завершен (нет подключения к БД) для устройства: {dev.controllerName} IP: {dev.ip}");
				return;
			}

			// Проверка наличия процедуры CARDINDEV_TS4
			bool procedureExists = false;
			try
			{
				// Проверяем состояние соединения перед использованием
				if (con.State != ConnectionState.Open)
				{
					logger.LogWarning($"Соединение разорвано перед проверкой процедуры для устройства: {dev.controllerName} IP: {dev.ip}");
					throw new InvalidOperationException("Соединение с базой данных закрыто");
				}
				procedureExists = DBFirebird.ProcedurChec(con);
			}
			catch (FbException fbEx)
			{
				logger.LogError(fbEx, $"Ошибка Firebird при проверке процедуры для устройства: {dev.controllerName} IP: {dev.ip}. Сообщение: {fbEx.Message}");
				try { con.Close(); } catch { }
				try { con.Dispose(); } catch { }
				logger.LogInformation($"301 Поток завершен (ошибка при проверке процедуры) для устройства: {dev.controllerName} IP: {dev.ip}");
				return;
			}
			catch (System.IO.IOException ioEx)
			{
				logger.LogError(ioEx, $"Ошибка сети при проверке процедуры для устройства: {dev.controllerName} IP: {dev.ip}. Соединение разорвано. Сообщение: {ioEx.Message}");
				try { con.Close(); } catch { }
				try { con.Dispose(); } catch { }
				logger.LogInformation($"301 Поток завершен (разрыв соединения при проверке процедуры) для устройства: {dev.controllerName} IP: {dev.ip}");
				return;
			}
			catch (Exception ex)
			{
				logger.LogError(ex, $"Общая ошибка при проверке процедуры для устройства: {dev.controllerName} IP: {dev.ip}. Сообщение: {ex.Message}");
				try { con.Close(); } catch { }
				try { con.Dispose(); } catch { }
				logger.LogInformation($"301 Поток завершен (ошибка при проверке процедуры) для устройства: {dev.controllerName} IP: {dev.ip}");
				return;
			}

			if (procedureExists)
			{
				logger.LogInformation($"Проверка процедуры: процедура CARDINDEV_TS4 найдена в базе данных");
			}
			else
			{
				logger.LogError("Проверка процедуры: процедура CARDINDEV_TS4 не найдена в базе данных");
				try { con.Close(); } catch { }
				try { con.Dispose(); } catch { }
				logger.LogInformation($"301 Поток завершен (отсутствует процедура) для устройства: {dev.controllerName} IP: {dev.ip}");
				return;
			}

			// Проверка наличия триггера CARDINDEV_INSERT
			bool triggerExists = false;
			try
			{
				// Проверяем состояние соединения перед использованием
				if (con.State != ConnectionState.Open)
				{
					logger.LogWarning($"Соединение разорвано перед проверкой триггера для устройства: {dev.controllerName} IP: {dev.ip}");
					throw new InvalidOperationException("Соединение с базой данных закрыто");
				}
				triggerExists = DBFirebird.TriggerCheck(con);
			}
			catch (FbException fbEx)
			{
				logger.LogError(fbEx, $"Ошибка Firebird при проверке триггера для устройства: {dev.controllerName} IP: {dev.ip}. Сообщение: {fbEx.Message}");
				try { con.Close(); } catch { }
				try { con.Dispose(); } catch { }
				logger.LogInformation($"302 Поток завершен (ошибка при проверке триггера) для устройства: {dev.controllerName} IP: {dev.ip}");
				return;
			}
			catch (System.IO.IOException ioEx)
			{
				logger.LogError(ioEx, $"Ошибка сети при проверке триггера для устройства: {dev.controllerName} IP: {dev.ip}. Соединение разорвано. Сообщение: {ioEx.Message}");
				try { con.Close(); } catch { }
				try { con.Dispose(); } catch { }
				logger.LogInformation($"302 Поток завершен (разрыв соединения при проверке триггера) для устройства: {dev.controllerName} IP: {dev.ip}");
				return;
			}
			catch (Exception ex)
			{
				logger.LogError(ex, $"Общая ошибка при проверке триггера для устройства: {dev.controllerName} IP: {dev.ip}. Сообщение: {ex.Message}");
				try { con.Close(); } catch { }
				try { con.Dispose(); } catch { }
				logger.LogInformation($"302 Поток завершен (ошибка при проверке триггера) для устройства: {dev.controllerName} IP: {dev.ip}");
				return;
			}

			if (triggerExists)
			{
				logger.LogInformation($"Проверка триггера: триггер для события CARDINDEV_INSERT найден в базе данных");
			}
			else
			{
				logger.LogError("Проверка триггера: триггер для события CARDINDEV_INSERT не найден в базе данных");
				try { con.Close(); } catch { }
				try { con.Dispose(); } catch { }
				logger.LogInformation($"302 Поток завершен (отсутствует триггер) для устройства: {dev.controllerName} IP: {dev.ip}");
				return;
			}
		}
		catch (Exception ex)
		{
			logger.LogError(ex, $"Критическая ошибка в MainLine для устройства: {dev.controllerName} IP: {dev.ip}. Сообщение: {ex.Message}");
			try { con.Close(); } catch { }
			try { con.Dispose(); } catch { }
			logger.LogInformation($"Поток завершен (критическая ошибка) для устройства: {dev.controllerName} IP: {dev.ip}");
			return;
		}

		try
		{
			COM com = new COM(dev.driver, options);
			com.SetupString(dev.ip);
			if (com.ReportStatus())
			{
				// Проверяем состояние соединения перед использованием
				if (con.State != ConnectionState.Open)
				{
					logger.LogWarning($"Соединение разорвано перед обработкой устройства: {dev.controllerName} IP: {dev.ip}");
					return;
				}
				//Console.WriteLine("196, ПОПАЛИ СЮДА");
				OneDev(con, dev, com);
				//logger.LogInformation($"302 Поток успешно завершен для устройства: {dev.controllerName} IP: {dev.ip}");
			}
			else
			{
				lineStat += "|DevConnectNo:";
				logger.LogInformation($"303 Поток завершен (нет связи с контроллером) для устройства: {dev.controllerName} IP: {dev.ip}");
			}
		}
		catch (FbException fbEx)
		{
			logger.LogError(fbEx, $"Ошибка Firebird при обработке устройства: {dev.controllerName} IP: {dev.ip}. Сообщение: {fbEx.Message}");
		}
		catch (System.IO.IOException ioEx)
		{
			logger.LogError(ioEx, $"Ошибка сети при обработке устройства: {dev.controllerName} IP: {dev.ip}. Соединение разорвано. Сообщение: {ioEx.Message}");
		}
		catch (Exception ex)
		{
			logger.LogError(ex, $"Общая ошибка при обработке устройства: {dev.controllerName} IP: {dev.ip}. Сообщение: {ex.Message}");
		}
		finally
		{
			// Гарантированно закрываем соединение
			try
			{
				if (con != null && con.State == ConnectionState.Open)
				{
					con.Close();
				}
			}
			catch (Exception ex)
			{
				logger.LogDebug($"Ошибка при закрытии соединения для устройства: {dev.controllerName} IP: {dev.ip}. {ex.Message}");
			}
			finally
			{
				try
				{
					con?.Dispose();
				}
				catch (Exception ex)
				{
					logger.LogDebug($"Ошибка при освобождении соединения для устройства: {dev.controllerName} IP: {dev.ip}. {ex.Message}");
				}
			}
		}
		//logger.LogTrace("223  " + lineStat + "|Time_execute:" + (DateTime.Now - start));
	}

	private void OneDev(FbConnection con, DEV dev, COM com)
	{
		DateTime start = DateTime.Now;

		// Проверяем состояние соединения перед использованием
		if (con == null || con.State != ConnectionState.Open)
		{
			logger.LogWarning($"Соединение с БД не открыто для устройства: {dev.controllerName} IP: {dev.ip}");
			return;
		}

		DataTable table;
		try
		{
			table = DBFirebird.GetComandForDevice(con, dev.controllerName);
		}
		catch (FbException fbEx)
		{
			logger.LogError(fbEx, $"Ошибка Firebird при получении команд для устройства: {dev.controllerName} IP: {dev.ip}. Сообщение: {fbEx.Message}");
			return;
		}
		catch (System.IO.IOException ioEx)
		{
			logger.LogError(ioEx, $"Ошибка сети при получении команд для устройства: {dev.controllerName} IP: {dev.ip}. Соединение разорвано. Сообщение: {ioEx.Message}");
			return;
		}
		catch (Exception ex)
		{
			logger.LogDebug($"sql no comand for device: {ex.Message}");
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
			try
			{
				// Проверяем состояние соединения перед каждой операцией
				if (con == null || con.State != ConnectionState.Open)
				{
					logger.LogWarning($"Соединение с БД разорвано во время обработки команды для устройства: {dev.controllerName} IP: {dev.ip}");
					break;
				}

				string operationType = ((int)cmd.dataRow["operation"] == 1) ? "Write" : "Delete";
				logger.LogInformation(@$"{operationType} operation: id_dev={cmd.dataRow["id_dev"]} name={dev.controllerName} reader={cmd.dataRow["id_reader"]} card={cmd.dataRow["id_card"]} IP={dev.ip} Command: {cmd.command}");
				
				string answer = "";
				answer = com.ComandExecute(cmd.command);
				AfterComand(answer, con, cmd.dataRow, dev, operationType);
				logger.LogCritical(@$"Answer destination:  {(((int)cmd.dataRow["operation"] == 1) ? "AddCard " : "DeleteCard")} id_dev={cmd.dataRow["id_dev"]} name={dev.controllerName} reader={cmd.dataRow["id_reader"]} IP={dev.ip} Command: {cmd.command} Answer: {answer}");
			}
			catch (FbException fbEx)
			{
				logger.LogError(fbEx, $"Ошибка Firebird при обработке команды для устройства: {dev.controllerName} IP: {dev.ip}. Сообщение: {fbEx.Message}");
				// Продолжаем обработку следующих команд
				continue;
			}
			catch (System.IO.IOException ioEx)
			{
				logger.LogError(ioEx, $"Ошибка сети при обработке команды для устройства: {dev.controllerName} IP: {dev.ip}. Соединение разорвано. Сообщение: {ioEx.Message}");
				// Прерываем обработку, так как соединение разорвано
				break;
			}
			catch (Exception ex)
			{
				logger.LogError(ex, $"Общая ошибка при обработке команды для устройства: {dev.controllerName} IP: {dev.ip}. Сообщение: {ex.Message}");
				// Продолжаем обработку следующих команд
				continue;
			}
		}
		}

	private string ComandBuilder(DataRow? row)
	{
		if (row == null)
		{
			throw new ArgumentNullException(nameof(row));
		}
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
		try
		{
			// Проверяем состояние соединения перед операциями с БД
			if (con == null || con.State != ConnectionState.Open)
			{
				logger.LogWarning($"Соединение с БД не открыто при обновлении данных для устройства: {dev.controllerName} IP: {dev.ip}");
				return;
			}

			if (row == null)
			{
				logger.LogWarning($"DataRow равен null при обновлении данных для устройства: {dev.controllerName} IP: {dev.ip}");
				return;
			}

			switch ((int)row["operation"]) 
			{
				case 1:
					if (anser.ToUpper().Contains("OK"))
					{
						logger.LogInformation(@$"Write operation completed successfully: id_dev={row["id_dev"]} name={dev.controllerName} reader={row["id_reader"]} card={row["id_card"]} IP={dev.ip}");
						try
						{
							DBFirebird.UpdateIdxCard(con, row, anser, true);
							DBFirebird.DeleteCardInDev(con, row);
						}
						catch (FbException fbEx)
						{
							logger.LogError(fbEx, $"Ошибка Firebird при обновлении данных после записи для устройства: {dev.controllerName} IP: {dev.ip}. Сообщение: {fbEx.Message}");
						}
						catch (System.IO.IOException ioEx)
						{
							logger.LogError(ioEx, $"Ошибка сети при обновлении данных после записи для устройства: {dev.controllerName} IP: {dev.ip}. Сообщение: {ioEx.Message}");
						}
					}
					else
					{
						logger.LogWarning(@$"Write operation failed: id_dev={row["id_dev"]} name={dev.controllerName} reader={row["id_reader"]} card={row["id_card"]} IP={dev.ip} Answer: {anser}");
						try
						{
							DBFirebird.UpdateIdxCard(con, row, anser, false);
							DBFirebird.UpdateCardInDevIncrement(con, row);
						}
						catch (FbException fbEx)
						{
							logger.LogError(fbEx, $"Ошибка Firebird при обновлении данных после неудачной записи для устройства: {dev.controllerName} IP: {dev.ip}. Сообщение: {fbEx.Message}");
						}
						catch (System.IO.IOException ioEx)
						{
							logger.LogError(ioEx, $"Ошибка сети при обновлении данных после неудачной записи для устройства: {dev.controllerName} IP: {dev.ip}. Сообщение: {ioEx.Message}");
						}
					}
					break;
				case 2:
					if (anser.ToUpper().Contains("OK"))
					{
						logger.LogInformation(@$"Delete operation completed successfully: id_dev={row["id_dev"]} name={dev.controllerName} reader={row["id_reader"]} card={row["id_card"]} IP={dev.ip}");
						try
						{
							DBFirebird.DeleteCardInDev(con, row);
						}
						catch (FbException fbEx)
						{
							logger.LogError(fbEx, $"Ошибка Firebird при удалении данных после удаления карты для устройства: {dev.controllerName} IP: {dev.ip}. Сообщение: {fbEx.Message}");
						}
						catch (System.IO.IOException ioEx)
						{
							logger.LogError(ioEx, $"Ошибка сети при удалении данных после удаления карты для устройства: {dev.controllerName} IP: {dev.ip}. Сообщение: {ioEx.Message}");
						}
					}
					else
					{
						logger.LogWarning(@$"Delete operation failed: id_dev={row["id_dev"]} name={dev.controllerName} reader={row["id_reader"]} card={row["id_card"]} IP={dev.ip} Answer: {anser}");
						try
						{
							DBFirebird.UpdateCardInDevIncrement(con, row);
						}
						catch (FbException fbEx)
						{
							logger.LogError(fbEx, $"Ошибка Firebird при обновлении данных после неудачного удаления для устройства: {dev.controllerName} IP: {dev.ip}. Сообщение: {fbEx.Message}");
						}
						catch (System.IO.IOException ioEx)
						{
							logger.LogError(ioEx, $"Ошибка сети при обновлении данных после неудачного удаления для устройства: {dev.controllerName} IP: {dev.ip}. Сообщение: {ioEx.Message}");
						}
					}
					break;
			}
		}
		catch (Exception ex)
		{
			logger.LogError(ex, $"Критическая ошибка в AfterComand для устройства: {dev.controllerName} IP: {dev.ip}. Сообщение: {ex.Message}");
		}
	}
	}
}

