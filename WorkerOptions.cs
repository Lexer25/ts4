using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TS4
{
    public class WorkerOptions
    {
        public string Firebird_db_config { get; set; }
        public string Access_db_config { get; set; }
        public string timestart { get; set; }
        public string timeout { get; set; }
        public bool run_now { get; set; }
        public bool IsTest { get; set; }
        /// <summary>Максимум одновременных потоков к Firebird (1 = минимальная нагрузка, 2–5 = выше).</summary>
        public int maxThreads { get; set; } = 1;
        /// <summary>Задержка (мс) между стартом потоков устройств при maxThreads &gt; 1.</summary>
        public int deviceStaggerMs { get; set; } = 400;
        /// <summary>Пауза (мс) после обработки каждого устройства перед стартом следующего. Увеличение убирает подскоки нагрузки (например 40 сек по 40%) — 8000–12000.</summary>
        public int devicePauseMs { get; set; } = 8000;
        /// <summary>Задержка (мс) после каждой операции с Firebird внутри устройства. Увеличение распределяет нагрузку во времени, снижает пики — 800–1200.</summary>
        public int deviceCommandDelayMs { get; set; } = 1000;
        public string mode { get; set; } = "async";
        /// <summary>Таймаут (сек) вызова драйвера (Execute/ReportStatus). По истечении вызов прерывается, программа не зависает. 0 = 60 сек.</summary>
        public int DriverTimeoutSeconds { get; set; } = 60;
    }

    /// <summary>Разбор интервалов из appsettings (формат H:M:S) без исключений.</summary>
    public static class WorkerTime
    {
        /// <summary>Интервал по умолчанию, если в конфиге значение отсутствует или задано неверно.</summary>
        public static readonly TimeSpan Default = TimeSpan.FromMinutes(5);

        /// <summary>true, если строка задана и разбирается в положительный интервал.</summary>
        public static bool IsValid(string? value) => TryParse(value, out _);

        /// <summary>Разбирает "H:M:S"; при любой ошибке возвращает fallback (исключений не бросает).</summary>
        public static TimeSpan ParseOrDefault(string? value, TimeSpan? fallback = null)
            => TryParse(value, out var result) ? result : (fallback ?? Default);

        private static bool TryParse(string? value, out TimeSpan result)
        {
            result = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(value)) return false;

            var parts = value.Split(':');
            if (parts.Length != 3) return false;
            if (!int.TryParse(parts[0].Trim(), out int hours)) return false;
            if (!int.TryParse(parts[1].Trim(), out int minutes)) return false;
            if (!int.TryParse(parts[2].Trim(), out int seconds)) return false;

            try
            {
                result = new TimeSpan(hours, minutes, seconds);
            }
            catch
            {
                return false;
            }

            return result > TimeSpan.Zero;
        }
    }
}
