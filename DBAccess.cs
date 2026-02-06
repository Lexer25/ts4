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
            Console.WriteLine(connectionString);
            return new OleDbConnection(connectionString);
        }
        public static DataTable GetDevice(OleDbConnection con)
        {
            var table = new DataTable();
            using (var command = new OleDbCommand("SELECT ID, Name, ConnectionString, Driver from Device where Enabled=1", con))
            using (var reader = command.ExecuteReader())
            {
                table.Load(reader);
            }
            return table;
        }
    }
}
