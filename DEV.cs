using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TS4
{
    class DEV
    {
        public string ip;
        public string controllerName;
        public string driver;
        
        public DEV(DataRow row)
        {
            this.ip = (string)row["ConnectionString"];
            this.controllerName = (string)row["Name"];
            this.driver = (string)row["Driver"];
        }

    }
}
