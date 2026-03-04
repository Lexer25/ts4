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
}
