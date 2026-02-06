using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TS4
{
    public class DEV
    {
        public int? id; // ID из Firebird базы (может быть null если устройство не найдено)
        public string ip;
        public string controllerName;
        public string driver;
        
        public DEV(DataRow row)
        {
            this.id = null;
            this.ip = row["ConnectionString"].ToString().Trim();
            //this.ip = (string)row["ConnectionString"];
            this.controllerName = (string)row["Name"];
            this.driver = (string)row["Driver"];
        }

        public DEV(string ip, string controllerName, string driver, int? id = null)
        {
            this.id = id;
            this.ip = ip;
            this.controllerName = controllerName;
            this.driver = driver;
        }
    }
}
