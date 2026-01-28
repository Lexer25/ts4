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
        public int maxThreads { get; set; } = 5;
        public string mode { get; set; } = "async";
    }
}
