using System.Text;
using Serilog.Core;
using Serilog.Events;

namespace TS4
{
	/// <summary>Пишет логи в файл TS4_yyyy-MM-dd.log; при смене дня переключается на новый файл без замены логгера.</summary>
	public sealed class DateRollingFileSink : ILogEventSink, IDisposable
	{
		private readonly string _folder;
		private readonly object _lock = new object();
		private DateTime _currentDate;
		private StreamWriter? _writer;

		public DateRollingFileSink(string folder)
		{
			_folder = folder ?? throw new ArgumentNullException(nameof(folder));
			_currentDate = DateTime.Now.Date;
		}

		public void Emit(LogEvent logEvent)
		{
			// P6: сбой записи в файл (нет папки, нет прав, диск занят, файл заблокирован)
			// не должен выбрасываться наружу: исключение из синка логов роняет процесс.
			try
			{
				lock (_lock)
				{
					var today = DateTime.Now.Date;
					if (_writer == null || today != _currentDate)
					{
						_writer?.Dispose();
						_currentDate = today;
						Directory.CreateDirectory(_folder);
						string path = Path.Combine(_folder, $"TS4_{_currentDate:yyyy-MM-dd}.log");
						_writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), Encoding.UTF8)
						{
							AutoFlush = true
						};
					}
					string line = Format(logEvent);
					_writer.WriteLine(line);
					if (logEvent.Exception != null)
						_writer.WriteLine(logEvent.Exception.ToString());
				}
			}
			catch (Exception ex)
			{
				try
				{
					lock (_lock)
					{
						_writer?.Dispose();
						_writer = null;
					}
				}
				catch { }

				Serilog.Debugging.SelfLog.WriteLine("DateRollingFileSink: не удалось записать лог: {0}", ex.Message);
			}
		}

		private static string Format(LogEvent logEvent)
		{
			var sb = new StringBuilder();
			sb.Append(logEvent.Timestamp.ToString("dd-MM-yyyy HH:mm:ss.fff"));
			sb.Append("\t-\t[");
			sb.Append(logEvent.Level.ToString().ToUpperInvariant().PadRight(3));
			sb.Append("] ");
			sb.Append(logEvent.RenderMessage());
			return sb.ToString();
		}

		public void Dispose()
		{
			lock (_lock)
			{
				_writer?.Dispose();
				_writer = null;
			}
		}
	}
}
