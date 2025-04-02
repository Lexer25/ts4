using System;
using System.Collections.Generic;
using System.Data;
using System.Data.OleDb;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TS4
{
    internal class DBAccess
    {
        public static OleDbConnection Connect(string connectionString)
        {
            return new OleDbConnection(connectionString);
        }
        public static DataTable GetDevice(OleDbConnection con)
        {
            OleDbCommand command = new OleDbCommand("SELECT Name, ConnectionString, Driver from Device where Enabled=1", con);
            var reader = command.ExecuteReader();
            DataTable table = new DataTable();
            table.Load(reader);
            return table;
        }
    }
}
