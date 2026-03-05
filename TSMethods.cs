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

		/// <summary>Ответ устройства указывает на отсутствие связи (udprecv, связи нет) или таймаут драйвера — поток нужно завершить.</summary>
		private static bool IsNoConnectionAnswer(string? answer)
		{
			if (string.IsNullOrEmpty(answer)) return false;
			string a = answer.Trim().ToUpperInvariant();
			return a.Contains("UDPRECV") || a.Contains("UDP RECV") || a.Contains("СВЯЗИ НЕТ") || a.Contains("ENETWORKERROR") || a.StartsWith("ERR TIMEOUT");
		}

		public TSMethods(ILogger<TSMethods> logger, WorkerOptions options)
		{
			this.logger = logger;
			this.options = options;
		}

		public void MainLine(DEV dev, string db_config)
		{
			DateTime start = DateTime.Now;
			logger.LogInformation("600 MainLine: Начало для устройства {Name} IP={Ip}", dev.controllerName, dev.ip);

		FbConnection con = null;

		for (int attempt = 1; attempt <= MaxConnectionRetries; attempt++)
		{
			try
			{
				con = DBFirebird.Connect(db_config, logger);
				var tOpen = DateTime.Now;
				con.Open();
				logger.LogWarning("605 MainLine: Подключение к Firebird, мс={Ms:F0}", (DateTime.Now - tOpen).TotalMilliseconds);
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
					logger.LogError(fbEx, $"Ошибка Firebird при подключении к БД для устройства: {dev.controllerName} IP: {dev.ip}. Сообщение: {LogHelper.Sanitize(fbEx.Message)}");
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
				logger.LogError(fbEx, $"Ошибка Firebird при подключении к БД для устройства: {dev.controllerName} IP: {dev.ip}. Сообщение: {LogHelper.Sanitize(fbEx.Message)}");
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
					logger.LogError("147 name=" + dev.controllerName + " IP " + dev.ip + " mess " + LogHelper.Sanitize(ex.Message));
				}
				else
				{
					logger.LogError(ex, $"Ошибка при подключении к БД для устройства: {dev.controllerName} IP: {dev.ip}. Сообщение: {LogHelper.Sanitize(ex.Message)}");
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
			var tCom = DateTime.Now;
			COM com = new COM(dev.driver, options);
			com.SetupString(dev.ip);
			var (reportOk, reportMessage) = com.ReportStatusWithMessage();
			if (reportOk)
				logger.LogWarning("610 MainLine: COM создан, ReportStatus=Ok, мс={Ms:F0} для {Name} IP={Ip}", (DateTime.Now - tCom).TotalMilliseconds, dev.controllerName, dev.ip);

			if (reportOk)
			{
				if (con.State != ConnectionState.Open)
				{
					logger.LogWarning($"Соединение разорвано перед обработкой устройства: {dev.controllerName} IP: {dev.ip}");
					return;
				}
				OneDev(con, dev, com);
				logger.LogWarning("620 MainLine: Завершено для {Name} IP={Ip}, всего мс={Ms:F0}", dev.controllerName, dev.ip, (DateTime.Now - start).TotalMilliseconds);
			}
			else
			{
				logger.LogWarning("625 MainLine: Нет связи с контроллером {Name} IP={Ip}. Ответ контроллера по UDP: {Response}", dev.controllerName, dev.ip, LogHelper.Sanitize(reportMessage));
				// reportMessage — ответ устройства (ReportStatus) или ex.Message; в load_result обрезается до 100 символов в DB
				int updated = DBFirebird.UpdateCardIdxNoConnectionByControllerName(con, dev.controllerName, reportMessage);
				if (updated > 0)
					logger.LogTrace("626 MainLine: cardidx обновлён для контроллера {Name}, записей={Count}, load_result={Message}", dev.controllerName, updated, LogHelper.Sanitize(reportMessage));
				else
					logger.LogTrace("626 MainLine: cardidx для контроллера {Name} — обновлено 0 строк", dev.controllerName);
			}
		}
		catch (FbException fbEx)
		{
			logger.LogError(fbEx, $"Ошибка Firebird при обработке устройства: {dev.controllerName} IP: {dev.ip}. Сообщение: {LogHelper.Sanitize(fbEx.Message)}");
		}
		catch (System.IO.IOException ioEx)
		{
			logger.LogError(ioEx, $"Ошибка сети при обработке устройства: {dev.controllerName} IP: {dev.ip}. Соединение разорвано. Сообщение: {LogHelper.Sanitize(ioEx.Message)}");
		}
		catch (Exception ex)
		{
			logger.LogError(ex, $"Общая ошибка при обработке устройства: {dev.controllerName} IP: {dev.ip}. Сообщение: {LogHelper.Sanitize(ex.Message)}");
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
				logger.LogTrace("Ошибка при закрытии соединения для устройства: {Name} IP: {Ip}. {Message}", dev.controllerName, dev.ip, LogHelper.Sanitize(ex.Message));
			}
			finally
			{
				try
				{
					con?.Dispose();
				}
				catch (Exception ex)
				{
					logger.LogTrace("Ошибка при освобождении соединения для устройства: {Name} IP: {Ip}. {Message}", dev.controllerName, dev.ip, LogHelper.Sanitize(ex.Message));
				}
			}
		}
		logger.LogWarning("223 Time_execute: {Ms:F0} мс", (DateTime.Now - start).TotalMilliseconds);
	}

	private void OneDev(FbConnection con, DEV dev, COM com)
	{
		DateTime start = DateTime.Now;
		logger.LogInformation("700 OneDev: Начало потока для {Name} IP={Ip}", dev.controllerName, dev.ip);

		if (con == null || con.State != ConnectionState.Open)
		{
			logger.LogWarning("701 OneDev: Соединение с БД не открыто для {Name} IP={Ip}", dev.controllerName, dev.ip);
			return;
		}

		DataTable table;
		try
		{
			var tGet = DateTime.Now;
			table = DBFirebird.GetComandForDevice(con, dev.controllerName);
			logger.LogWarning("710 OneDev: Firebird GetComandForDevice (очередь карт), мс={Ms:F0}, строк={Count}", (DateTime.Now - tGet).TotalMilliseconds, table.Rows.Count);
		}
		catch (FbException fbEx)
		{
			if (IsConsistencyError(fbEx))
				logger.LogWarning("Ошибка целостности Firebird (bugcheck). Проверьте логи Firebird и при необходимости gfix. Устройство: {Dev} IP: {Ip}", dev.controllerName, dev.ip);
			else
				logger.LogError(fbEx, "Ошибка Firebird при получении команд для устройства: {Dev} IP: {Ip}. {Message}", dev.controllerName, dev.ip, LogHelper.Sanitize(fbEx.Message));
			return;
		}
		catch (System.IO.IOException ioEx)
		{
			logger.LogError(ioEx, $"Ошибка сети при получении команд для устройства: {dev.controllerName} IP: {dev.ip}. Соединение разорвано. Сообщение: {LogHelper.Sanitize(ioEx.Message)}");
			return;
		}
		catch (Exception ex)
		{
			logger.LogDebug($"sql no comand for device: {LogHelper.Sanitize(ex.Message)}");
			return;
		}
			var tBuild = DateTime.Now;
			List<Command> cmds = new List<Command>();
			foreach (DataRow row in table.Rows)
			{
				string comand = ComandBuilder(row);
				if (!string.IsNullOrEmpty(comand))
					cmds.Add(new Command(row, comand));
			}
			logger.LogWarning("720 OneDev: Построение списка команд, мс={Ms:F0}, команд={Count} (Write={W}/Delete={D})",
				(DateTime.Now - tBuild).TotalMilliseconds, cmds.Count,
				cmds.Count(c => (int)c.dataRow["operation"] == 1), cmds.Count(c => (int)c.dataRow["operation"] == 2));

		DateTime loopStart = DateTime.Now;
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

				string operationType = ((int)cmd.dataRow["operation"] == 1) ? "Запись" : "Удаление";
				string ToLogSafe(string s) => (s ?? "").Replace("\"", "'");

				// Для записи (operation=1) проверяем обязательный параметр TZ; при отсутствии — не отправляем в контроллер, удаляем из cardindev, cardidx не трогаем
				if ((int)cmd.dataRow["operation"] == 1)
				{
					object tzVal = cmd.dataRow["timezones"];
					bool tzMissing = tzVal == null || tzVal == DBNull.Value || string.IsNullOrWhiteSpace(tzVal.ToString());
					if (tzMissing)
					{
						logger.LogWarning("Команда {Command} не указан обязательный параметр TZ", ToLogSafe(cmd.command ?? ""));
						try
						{
							DBFirebird.DeleteCardInDev(con, cmd.dataRow);
						}
						catch (Exception ex)
						{
							logger.LogError(ex, "Ошибка при удалении из cardindev (нет TZ) для устройства: {Dev} IP: {Ip}", dev.controllerName, dev.ip);
						}
						cmdPrevTime = DateTime.Now;
						cmdIndex++;
						continue;
					}
				}

				var tDevice = DateTime.Now;
				string answer = "";
				bool udpErrorAfterRetry = false;
				for (int attempt = 0; attempt <= 1; attempt++)
				{
					answer = com.ComandExecute(cmd.command);

					if (IsNoConnectionAnswer(answer))
					{
						if (attempt == 0)
						{
							logger.LogWarning("735 OneDev: Command: {Cmd} | Answer: {Ans} (Повторная попытка 1 раз)", ToLogSafe(cmd.command), ToLogSafe(answer));
							continue;
						}
						udpErrorAfterRetry = true;
						break;
					}
					break;
				}

				if (udpErrorAfterRetry)
				{
					DBFirebird.UpdateCardIdxNoConnectionByControllerName(con, dev.controllerName, answer ?? "no connect");
					logger.LogWarning("735 OneDev: Command: {Cmd} | Answer: {Ans} (Завершаем поток)", ToLogSafe(cmd.command), ToLogSafe(answer));
					break;
				}

				var tDeviceMs = (DateTime.Now - tDevice).TotalMilliseconds;
				logger.LogCritical("730 OneDev: Command: {Cmd} | Answer: {Ans}", ToLogSafe(cmd.command), ToLogSafe(answer));

				var tFb = DateTime.Now;
				AfterComand(answer, con, cmd.dataRow, dev, operationType);
				var tFbMs = (DateTime.Now - tFb).TotalMilliseconds;
				logger.LogWarning("740 OneDev: Firebird AfterComand (UpdateIdxCard+DeleteCardInDev), мс={Ms:F0}", tFbMs);

				var cmdDurationMs = (DateTime.Now - cmdStartTime).TotalMilliseconds;
				logger.LogWarning("745 OneDev: Команда #{Num} итого мс={Ms:F0} (устройство={Dev}мс + Firebird={Fb}мс)", cmdIndex + 1, cmdDurationMs, tDeviceMs, tFbMs);

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
				logger.LogError(fbEx, "Ошибка Firebird при обработке команды для устройства: {Dev} IP: {Ip}. {Message}", dev.controllerName, dev.ip, LogHelper.Sanitize(fbEx.Message));
				continue;
			}
			catch (System.IO.IOException ioEx)
			{
				string driverResp = com.GetLastDriverResponse();
				if (!string.IsNullOrWhiteSpace(driverResp))
					logger.LogError(ioEx, "Ошибка сети при обработке команды для устройства: {Dev} IP: {Ip}. Ответ контроллера по UDP: {DriverResponse}", dev.controllerName, dev.ip, LogHelper.Sanitize(driverResp));
				else
					logger.LogError(ioEx, "Ошибка сети при обработке команды для устройства: {Dev} IP: {Ip}. Соединение разорвано.", dev.controllerName, dev.ip);
				// Обрыв связи при команде — записываем ошибку в cardidx и завершаем поток
				string msgForDb = !string.IsNullOrWhiteSpace(driverResp) ? driverResp : (ioEx.Message ?? "обрыв связи");
				DBFirebird.UpdateCardIdxNoConnectionByControllerName(con, dev.controllerName, msgForDb);
				break;
			}
			catch (Exception ex)
			{
				string driverResp = com.GetLastDriverResponse();
				if (!string.IsNullOrWhiteSpace(driverResp))
					logger.LogError(ex, "Общая ошибка при обработке команды для устройства: {Dev} IP: {Ip}. Ответ контроллера по UDP: {DriverResponse}. Исключение: {Message}", dev.controllerName, dev.ip, LogHelper.Sanitize(driverResp), LogHelper.Sanitize(ex.Message));
				else
					logger.LogError(ex, "Общая ошибка при обработке команды для устройства: {Dev} IP: {Ip}. {Message}", dev.controllerName, dev.ip, LogHelper.Sanitize(ex.Message));
				// Ошибка при выполнении команды — записываем в cardidx и завершаем поток (приоритет — ответ драйвера)
				string msgForDb = !string.IsNullOrWhiteSpace(driverResp) ? driverResp : (ex.Message ?? "ошибка");
				DBFirebird.UpdateCardIdxNoConnectionByControllerName(con, dev.controllerName, msgForDb);
				break;
			}
		}

		var loopTotalMs = (DateTime.Now - loopStart).TotalMilliseconds;
		logger.LogWarning("750 OneDev: Цикл завершён {Name} IP={Ip} команд={Count} всего_мс={Ms:F0} ({PerSec:F1} карт/сек)", dev.controllerName, dev.ip, cmdIndex, loopTotalMs, cmdIndex > 0 ? cmdIndex * 1000.0 / loopTotalMs : 0);
		logger.LogWarning("755 OneDev: Итого для устройства {Name} IP={Ip} мс={Ms:F0}", dev.controllerName, dev.ip, (DateTime.Now - start).TotalMilliseconds);

		// После завершения обработки устройства явно разрываем соединение с БД
		try
		{
			if (con != null && con.State == ConnectionState.Open)
			{
				con.Close();
				logger.LogTrace("760 OneDev: Завершение потока, соединение с БД закрыто для {Name} IP={Ip}", dev.controllerName, dev.ip);
			}
		}
		catch (Exception ex)
		{
			logger.LogTrace(ex, "761 OneDev: Ошибка завершения потока, ошибка при закрытии соединения для {Name} IP={Ip}: {Message}", dev.controllerName, dev.ip, LogHelper.Sanitize(ex.Message));
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
						try
						{
							var t1 = DateTime.Now;
							DBFirebird.UpdateIdxCard(con, row, anser, true);
							logger.LogWarning("760 AfterComand: UpdateIdxCard мс={Ms:F0}", (DateTime.Now - t1).TotalMilliseconds);
							var t2 = DateTime.Now;
							DBFirebird.DeleteCardInDev(con, row);
							logger.LogWarning("761 AfterComand: DeleteCardInDev мс={Ms:F0}", (DateTime.Now - t2).TotalMilliseconds);
						}
						catch (FbException fbEx)
						{
							if (IsConsistencyError(fbEx))
								logger.LogWarning("Ошибка целостности Firebird (bugcheck). Проверьте логи Firebird и при необходимости gfix. Устройство: {Dev} IP: {Ip}", dev.controllerName, dev.ip);
							else if (fbEx.Message != null && (fbEx.Message.Contains("lock conflict") || fbEx.Message.Contains("deadlock") || fbEx.Message.Contains("update conflicts")))
								logger.LogDebug("Конфликт блокировки при обновлении данных после записи для устройства: {Dev} IP: {Ip}", dev.controllerName, dev.ip);
							else
								logger.LogError(fbEx, "Ошибка Firebird при обновлении данных после записи для устройства: {Dev} IP: {Ip}. {Message}", dev.controllerName, dev.ip, LogHelper.Sanitize(fbEx.Message));
						}
						catch (System.IO.IOException ioEx)
						{
							logger.LogError(ioEx, $"Ошибка сети при обновлении данных после записи для устройства: {dev.controllerName} IP: {dev.ip}. Сообщение: {LogHelper.Sanitize(ioEx.Message)}");
						}
					}
					else
					{
						logger.LogWarning("770 AfterComand: Запись не удалась id_dev={Id} card={Card:l} Answer={Ans:l}", row["id_dev"], LogHelper.Sanitize(row["id_card"]?.ToString()), LogHelper.Sanitize(anser));
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
								logger.LogError(fbEx, "Ошибка Firebird при обновлении данных после неудачной записи для устройства: {Dev} IP: {Ip}. {Message}", dev.controllerName, dev.ip, LogHelper.Sanitize(fbEx.Message));
						}
						catch (System.IO.IOException ioEx)
						{
							logger.LogError(ioEx, $"Ошибка сети при обновлении данных после неудачной записи для устройства: {dev.controllerName} IP: {dev.ip}. Сообщение: {LogHelper.Sanitize(ioEx.Message)}");
						}
					}
					break;
				case 2:
					if (anser.ToUpper().Contains("OK"))
					{
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
								logger.LogError(fbEx, "Ошибка Firebird при удалении данных после удаления карты для устройства: {Dev} IP: {Ip}. {Message}", dev.controllerName, dev.ip, LogHelper.Sanitize(fbEx.Message));
						}
						catch (System.IO.IOException ioEx)
						{
							logger.LogError(ioEx, $"Ошибка сети при удалении данных после удаления карты для устройства: {dev.controllerName} IP: {dev.ip}. Сообщение: {LogHelper.Sanitize(ioEx.Message)}");
						}
					}
					else
					{
						logger.LogWarning("780 AfterComand: Удаление не удалось id_dev={Id} card={Card:l} Answer={Ans:l}", row["id_dev"], LogHelper.Sanitize(row["id_card"]?.ToString()), LogHelper.Sanitize(anser));
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
								logger.LogError(fbEx, "Ошибка Firebird при обновлении данных после неудачного удаления для устройства: {Dev} IP: {Ip}. {Message}", dev.controllerName, dev.ip, LogHelper.Sanitize(fbEx.Message));
						}
						catch (System.IO.IOException ioEx)
						{
							logger.LogError(ioEx, $"Ошибка сети при обновлении данных после неудачного удаления для устройства: {dev.controllerName} IP: {dev.ip}. Сообщение: {LogHelper.Sanitize(ioEx.Message)}");
						}
					}
					break;
			}
		}
		catch (Exception ex)
		{
			logger.LogError(ex, $"Критическая ошибка в AfterComand для устройства: {dev.controllerName} IP: {dev.ip}. Сообщение: {LogHelper.Sanitize(ex.Message)}");
		}
	}
	}
}

