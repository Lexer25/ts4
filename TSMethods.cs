using FirebirdSql.Data.FirebirdClient;
using Microsoft.Extensions.Logging;
using System;
using System.Data;
using System.Threading;

namespace TS4
{
	public class TSMethods
	{
		private readonly ILogger<TSMethods> logger;
		private readonly WorkerOptions options;

		// Повторные попытки при разрыве соединения с Firebird
		private const int MaxConnectionRetries = 3;
		private const int ConnectionRetryDelayMs = 2000;

		/// <summary>Проверяет, является ли исключение ошибкой разрыва соединения (можно повторить попытку).</summary>
		private static bool IsConnectionError(Exception ex)
		{
			if (ex is System.IO.IOException) return true;
			if (ex is FbException fbEx)
			{
				string msg = fbEx.Message ?? "";
				return msg.Contains("Error writing data to the connection") ||
					   msg.Contains("Error reading data from the connection") ||
					   msg.Contains("Удаленный хост принудительно разорвал");
			}
			return false;
		}

		/// <summary>Ошибка целостности Firebird (bugcheck) — не повторять, проверить сервер и БД.</summary>
		private static bool IsConsistencyError(FbException ex)
		{
			string msg = ex.Message ?? "";
			return msg.Contains("consistency check") || msg.Contains("bugcheck");
		}

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

		// Проверка процедуры и триггера выполняется один раз при старте программы (Program.cs), здесь только открываем соединение
		FbConnection con = null;

		for (int attempt = 1; attempt <= MaxConnectionRetries; attempt++)
		{
			try
			{
				con = DBFirebird.Connect(db_config, logger);
				con.Open();
				if (con.State != ConnectionState.Open)
				{
					try { con.Close(); } catch { }
					try { con.Dispose(); } catch { }
					con = null;
					throw new InvalidOperationException("Соединение не открыто");
				}
				break;
			}
			catch (FbException fbEx) when (IsConnectionError(fbEx))
			{
				try { con?.Close(); } catch { }
				try { con?.Dispose(); } catch { }
				con = null;
				if (attempt < MaxConnectionRetries)
				{
					int delayMs = ConnectionRetryDelayMs * attempt;
					Thread.Sleep(delayMs);
				}
				else
				{
					logger.LogError(fbEx, $"Ошибка Firebird при подключении к БД для устройства: {dev.controllerName} IP: {dev.ip}. Сообщение: {fbEx.Message}");
					logger.LogInformation($"299 Поток завершен (разрыв соединения после {MaxConnectionRetries} попыток) для устройства: {dev.controllerName} IP: {dev.ip}");
					return;
				}
			}
			catch (System.IO.IOException ioEx)
			{
				try { con?.Close(); } catch { }
				try { con?.Dispose(); } catch { }
				con = null;
				if (attempt < MaxConnectionRetries)
				{
					int delayMs = ConnectionRetryDelayMs * attempt;
					logger.LogWarning($"Ошибка сети при подключении к БД для устройства: {dev.controllerName} IP: {dev.ip}, попытка {attempt}/{MaxConnectionRetries}, повтор через {delayMs} мс");
					Thread.Sleep(delayMs);
				}
				else
				{
					logger.LogError(ioEx, $"Ошибка сети при подключении к БД для устройства: {dev.controllerName} IP: {dev.ip}. Соединение разорвано.");
					logger.LogInformation($"299 Поток завершен (разрыв соединения после {MaxConnectionRetries} попыток) для устройства: {dev.controllerName} IP: {dev.ip}");
					return;
				}
			}
			catch (FbException fbEx)
			{
				try { con?.Close(); } catch { }
				try { con?.Dispose(); } catch { }
				logger.LogError(fbEx, $"Ошибка Firebird при подключении к БД для устройства: {dev.controllerName} IP: {dev.ip}. Сообщение: {fbEx.Message}");
				logger.LogInformation($"299 Поток завершен (ошибка подключения) для устройства: {dev.controllerName} IP: {dev.ip}");
				return;
			}
			catch (Exception ex)
			{
				try { con?.Close(); } catch { }
				try { con?.Dispose(); } catch { }
				con = null;
				if (attempt == 1 && ex is InvalidOperationException)
				{
					logger.LogError("144 Не могу подключиться к базе данных в потоке mainLine для id_dev=" + dev.controllerName + " IP " + dev.ip);
					logger.LogError("147 name=" + dev.controllerName + " IP " + dev.ip + " mess " + ex.Message);
				}
				else
				{
					logger.LogError(ex, $"Ошибка при подключении к БД для устройства: {dev.controllerName} IP: {dev.ip}. Сообщение: {ex.Message}");
				}
				logger.LogInformation($"299 Поток завершен (ошибка подключения к БД) для устройства: {dev.controllerName} IP: {dev.ip}");
				return;
			}
		}

		if (con == null)
		{
			logger.LogError($"Не удалось установить соединение с БД после всех попыток для устройства: {dev.controllerName} IP: {dev.ip}");
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
			if (IsConsistencyError(fbEx))
				logger.LogWarning("Ошибка целостности Firebird (bugcheck). Проверьте логи Firebird и при необходимости gfix. Устройство: {Dev} IP: {Ip}", dev.controllerName, dev.ip);
			else
				logger.LogError(fbEx, "Ошибка Firebird при получении команд для устройства: {Dev} IP: {Ip}. {Message}", dev.controllerName, dev.ip, fbEx.Message);
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

			logger.LogTrace(@$"258  name = {dev.controllerName} всего команд: {cmds.Count()}  /");

		var successWrites = new List<(DataRow row, string result)>();
		var successDeletes = new List<DataRow>();
		var failWrites = new List<(DataRow row, string result)>();
		var failDeletes = new List<DataRow>();

		DateTime batchStart = DateTime.Now;
		DateTime cmdPrevTime = DateTime.Now;
		int cmdIndex = 0;

		foreach (Command cmd in cmds)
		{
			try
			{
				if (con == null || con.State != ConnectionState.Open)
				{
					logger.LogWarning($"Соединение с БД разорвано во время обработки команды для устройства: {dev.controllerName} IP: {dev.ip}");
					break;
				}

				var cmdStartTime = DateTime.Now;
				var timeSincePrevMs = cmdIndex > 0 ? (cmdStartTime - cmdPrevTime).TotalMilliseconds : 0;

				string operationType = ((int)cmd.dataRow["operation"] == 1) ? "Write" : "Delete";
				logger.LogInformation($"259 {operationType} operation: id_dev={cmd.dataRow["id_dev"]} name={dev.controllerName} reader={cmd.dataRow["id_reader"]} card={cmd.dataRow["id_card"]} IP={dev.ip} Command: {cmd.command} | since_prev_ms={timeSincePrevMs:F0}");

				string answer = com.ComandExecute(cmd.command);

				var cmdDurationMs = (DateTime.Now - cmdStartTime).TotalMilliseconds;
				logger.LogCritical(@$"Answer: {(((int)cmd.dataRow["operation"] == 1) ? "AddCard " : "DeleteCard")} id_dev={cmd.dataRow["id_dev"]} name={dev.controllerName} reader={cmd.dataRow["id_reader"]} IP={dev.ip} Answer: {answer} | cmd_duration_ms={cmdDurationMs:F0}");

				bool isOk = answer?.ToUpper().Contains("OK") == true;
				if ((int)cmd.dataRow["operation"] == 1) // Write
				{
					if (isOk) successWrites.Add((cmd.dataRow, answer ?? ""));
					else failWrites.Add((cmd.dataRow, answer ?? ""));
				}
				else // Delete
				{
					if (isOk) successDeletes.Add(cmd.dataRow);
					else failDeletes.Add(cmd.dataRow);
				}

				cmdPrevTime = DateTime.Now;
				cmdIndex++;
			}
			catch (FbException fbEx)
			{
				if (IsConsistencyError(fbEx))
				{
					logger.LogWarning("Ошибка целостности Firebird (bugcheck). Прерываем обработку устройства. Устройство: {Dev} IP: {Ip}", dev.controllerName, dev.ip);
					break;
				}
				logger.LogError(fbEx, "Ошибка Firebird при обработке команды для устройства: {Dev} IP: {Ip}. {Message}", dev.controllerName, dev.ip, fbEx.Message);
				continue;
			}
			catch (System.IO.IOException ioEx)
			{
				logger.LogError(ioEx, "Ошибка сети при обработке команды для устройства: {Dev} IP: {Ip}. Соединение разорвано.", dev.controllerName, dev.ip);
				break;
			}
			catch (Exception ex)
			{
				logger.LogError(ex, "Общая ошибка при обработке команды для устройства: {Dev} IP: {Ip}. {Message}", dev.controllerName, dev.ip, ex.Message);
				continue;
			}
		}

		// Пакетное обновление Firebird (2–4 round-trip вместо 2*N)
		try
		{
			if (successWrites.Count > 0 || successDeletes.Count > 0)
			{
				DBFirebird.BatchSuccess(con, successWrites, successDeletes);
				foreach (var x in successWrites)
					logger.LogInformation("Write completed: id_dev={Id} name={Name} reader={Reader} card={Card} IP={Ip}", x.row["id_dev"], dev.controllerName, x.row["id_reader"], x.row["id_card"], dev.ip);
				foreach (var row in successDeletes)
					logger.LogInformation("Delete completed: id_dev={Id} name={Name} reader={Reader} card={Card} IP={Ip}", row["id_dev"], dev.controllerName, row["id_reader"], row["id_card"], dev.ip);
			}
			if (failWrites.Count > 0 || failDeletes.Count > 0)
			{
				DBFirebird.BatchFail(con, failWrites, failDeletes);
				foreach (var x in failWrites)
					logger.LogWarning("Write failed: id_dev={Id} name={Name} reader={Reader} card={Card} IP={Ip} Answer: {Ans}", x.row["id_dev"], dev.controllerName, x.row["id_reader"], x.row["id_card"], dev.ip, x.result);
				foreach (var row in failDeletes)
					logger.LogWarning("Delete failed: id_dev={Id} name={Name} reader={Reader} card={Card} IP={Ip}", row["id_dev"], dev.controllerName, row["id_reader"], row["id_card"], dev.ip);
			}
		}
		catch (FbException fbEx)
		{
			if (IsConsistencyError(fbEx))
				logger.LogWarning("Ошибка целостности Firebird при пакетном обновлении. Устройство: {Dev} IP: {Ip}", dev.controllerName, dev.ip);
			else
				logger.LogError(fbEx, "Ошибка Firebird при пакетном обновлении для устройства: {Dev} IP: {Ip}. {Message}", dev.controllerName, dev.ip, fbEx.Message);
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Ошибка при пакетном обновлении для устройства: {Dev} IP: {Ip}. {Message}", dev.controllerName, dev.ip, ex.Message);
		}

		var batchTotalMs = (DateTime.Now - batchStart).TotalMilliseconds;
		logger.LogInformation("Batch complete: name={Name} IP={Ip} commands={Count} total_ms={TotalMs:F0} ({PerSec:F1} cmd/sec)", dev.controllerName, dev.ip, cmdIndex, batchTotalMs, cmdIndex > 0 ? cmdIndex * 1000.0 / batchTotalMs : 0);
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
							if (IsConsistencyError(fbEx))
								logger.LogWarning("Ошибка целостности Firebird (bugcheck). Проверьте логи Firebird и при необходимости gfix. Устройство: {Dev} IP: {Ip}", dev.controllerName, dev.ip);
							else if (fbEx.Message != null && (fbEx.Message.Contains("lock conflict") || fbEx.Message.Contains("deadlock") || fbEx.Message.Contains("update conflicts")))
								logger.LogDebug("Конфликт блокировки при обновлении данных после записи для устройства: {Dev} IP: {Ip}", dev.controllerName, dev.ip);
							else
								logger.LogError(fbEx, "Ошибка Firebird при обновлении данных после записи для устройства: {Dev} IP: {Ip}. {Message}", dev.controllerName, dev.ip, fbEx.Message);
						}
						catch (System.IO.IOException ioEx)
						{
							logger.LogError(ioEx, $"Ошибка сети при обновлении данных после записи для устройства: {dev.controllerName} IP: {dev.ip}. Сообщение: {ioEx.Message}");
						}
					}
					else
					{
						logger.LogWarning(@$"358 Write operation failed: id_dev={row["id_dev"]} name={dev.controllerName} reader={row["id_reader"]} card={row["id_card"]} IP={dev.ip} Answer: {anser}");
						try
						{
							DBFirebird.UpdateIdxCard(con, row, anser, false);
							DBFirebird.UpdateCardInDevIncrement(con, row);
						}
						catch (FbException fbEx)
						{
							if (IsConsistencyError(fbEx))
								logger.LogWarning("Ошибка целостности Firebird (bugcheck). Проверьте логи Firebird и при необходимости gfix. Устройство: {Dev} IP: {Ip}", dev.controllerName, dev.ip);
							else if (fbEx.Message != null && (fbEx.Message.Contains("lock conflict") || fbEx.Message.Contains("deadlock") || fbEx.Message.Contains("update conflicts")))
								logger.LogDebug("Конфликт блокировки при обновлении данных после неудачной записи для устройства: {Dev} IP: {Ip}", dev.controllerName, dev.ip);
							else
								logger.LogError(fbEx, "Ошибка Firebird при обновлении данных после неудачной записи для устройства: {Dev} IP: {Ip}. {Message}", dev.controllerName, dev.ip, fbEx.Message);
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
						logger.LogInformation(@$" 382 Delete operation completed successfully: id_dev={row["id_dev"]} name={dev.controllerName} reader={row["id_reader"]} card={row["id_card"]} IP={dev.ip}");
						try
						{
							DBFirebird.DeleteCardInDev(con, row);
						}
						catch (FbException fbEx)
						{
							if (IsConsistencyError(fbEx))
								logger.LogWarning("Ошибка целостности Firebird (bugcheck). Проверьте логи Firebird и при необходимости gfix. Устройство: {Dev} IP: {Ip}", dev.controllerName, dev.ip);
							else if (fbEx.Message != null && (fbEx.Message.Contains("lock conflict") || fbEx.Message.Contains("deadlock") || fbEx.Message.Contains("update conflicts")))
								logger.LogDebug("Конфликт блокировки при удалении данных после удаления карты для устройства: {Dev} IP: {Ip}", dev.controllerName, dev.ip);
							else
								logger.LogError(fbEx, "Ошибка Firebird при удалении данных после удаления карты для устройства: {Dev} IP: {Ip}. {Message}", dev.controllerName, dev.ip, fbEx.Message);
						}
						catch (System.IO.IOException ioEx)
						{
							logger.LogError(ioEx, $"Ошибка сети при удалении данных после удаления карты для устройства: {dev.controllerName} IP: {dev.ip}. Сообщение: {ioEx.Message}");
						}
					}
					else
					{
						logger.LogWarning(@$" 403 Delete operation failed: id_dev={row["id_dev"]} name={dev.controllerName} reader={row["id_reader"]} card={row["id_card"]} IP={dev.ip} Answer: {anser}");
						try
						{
							DBFirebird.UpdateCardInDevIncrement(con, row);
						}
						catch (FbException fbEx)
						{
							if (IsConsistencyError(fbEx))
								logger.LogWarning("Ошибка целостности Firebird (bugcheck). Проверьте логи Firebird и при необходимости gfix. Устройство: {Dev} IP: {Ip}", dev.controllerName, dev.ip);
							else if (fbEx.Message != null && (fbEx.Message.Contains("lock conflict") || fbEx.Message.Contains("deadlock") || fbEx.Message.Contains("update conflicts")))
								logger.LogDebug("Конфликт блокировки при обновлении данных после неудачного удаления для устройства: {Dev} IP: {Ip}", dev.controllerName, dev.ip);
							else
								logger.LogError(fbEx, "Ошибка Firebird при обновлении данных после неудачного удаления для устройства: {Dev} IP: {Ip}. {Message}", dev.controllerName, dev.ip, fbEx.Message);
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

