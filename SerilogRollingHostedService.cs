using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Serilog;

namespace TS4
{
	/// <summary>Раз в минуту проверяет смену дня и переключает лог-файл на новый (TS4_yyyy-MM-dd.log).</summary>
	internal sealed class SerilogRollingHostedService : IHostedService, IDisposable
	{
		private readonly IConfiguration _config;
		private Timer? _timer;
		private DateTime _currentLogDate;

		public SerilogRollingHostedService(IConfiguration config)
		{
			_config = config;
			_currentLogDate = DateTime.Now.Date;
		}

		public Task StartAsync(CancellationToken cancellationToken)
		{
			_timer = new Timer(RollIfNewDay, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
			return Task.CompletedTask;
		}

		public Task StopAsync(CancellationToken cancellationToken)
		{
			_timer?.Change(Timeout.Infinite, 0);
			return Task.CompletedTask;
		}

		public void Dispose() => _timer?.Dispose();

		private void RollIfNewDay(object? state)
		{
			if (DateTime.Now.Date <= _currentLogDate)
				return;

			try
			{
				Log.CloseAndFlush();
			}
			catch { }

			string folder = _config.GetSection("Log").GetValue<string>("LogFolerPath") ?? Directory.GetCurrentDirectory();
			int retainedFileCountLimit = _config.GetSection("Log").GetValue<int?>("retainedFileCountLimit") ?? 31;
			string path = Path.Combine(folder, $"TS4_{DateTime.Now:yyyy-MM-dd}.log");
			var level = GetSerilogLevel(_config);
			var outputTemplate = "{Timestamp:dd-MM-yyyy HH:mm:ss.fff}\t-\t[{Level:u3}] {Message}{NewLine}{Exception}";

			Log.Logger = new LoggerConfiguration()
				.MinimumLevel.Is(level)
				.MinimumLevel.Override("Microsoft", level)
				.MinimumLevel.Override("Microsoft.Hosting.Lifetime", level)
				.WriteTo.Console(outputTemplate: outputTemplate)
				.WriteTo.Async(a => a.File(
					path,
					outputTemplate: outputTemplate,
					buffered: true,
					flushToDiskInterval: TimeSpan.FromSeconds(1),
					retainedFileCountLimit: retainedFileCountLimit))
				.CreateLogger();

			_currentLogDate = DateTime.Now.Date;
		}

		private static Serilog.Events.LogEventLevel GetSerilogLevel(IConfiguration config)
		{
			var minLevel = config.GetSection("Log").GetValue<LogLevel?>("LogLevel") ?? LogLevel.Information;
			if (!Enum.IsDefined(typeof(LogLevel), minLevel))
				minLevel = LogLevel.Information;
			return minLevel switch
			{
				LogLevel.Trace => Serilog.Events.LogEventLevel.Verbose,
				LogLevel.Debug => Serilog.Events.LogEventLevel.Debug,
				LogLevel.Information => Serilog.Events.LogEventLevel.Information,
				LogLevel.Warning => Serilog.Events.LogEventLevel.Warning,
				LogLevel.Error => Serilog.Events.LogEventLevel.Error,
				LogLevel.Critical => Serilog.Events.LogEventLevel.Fatal,
				_ => Serilog.Events.LogEventLevel.Information
			};
		}
	}
}
