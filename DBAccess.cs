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

        /// <summary>Извлекает путь к файлу базы (Data Source) из строки подключения OLE DB.</summary>
        public static string? GetDataSourcePath(string? connectionString)
        {
            if (string.IsNullOrWhiteSpace(connectionString)) return null;

            foreach (var part in connectionString.Split(';'))
            {
                int idx = part.IndexOf('=');
                if (idx < 0) continue;

                string key = part.Substring(0, idx).Trim();
                if (!key.Equals("Data Source", StringComparison.OrdinalIgnoreCase)) continue;

                string value = part.Substring(idx + 1).Trim().Trim('"');
                return value.Length == 0 ? null : value;
            }
            return null;
        }

        /// <summary>
        /// Проверяет каталог временных файлов. Jet не открывает базу, если TEMP недоступен,
        /// и возвращает при этом неинформативную «Неопознанная ошибка» (0x80004005).
        /// Возвращает описание проблемы или null, если всё в порядке.
        /// </summary>
        public static string? CheckTempFolder()
        {
            string probe = "";
            try
            {
                string temp = Path.GetTempPath();
                if (string.IsNullOrWhiteSpace(temp) || !Directory.Exists(temp))
                    return $"каталог временных файлов не найден: '{temp}'";

                probe = Path.Combine(temp, $"ts4_temp_probe_{Guid.NewGuid():N}.tmp");
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
                return null;
            }
            catch (Exception ex)
            {
                try { if (probe.Length > 0) File.Delete(probe); } catch { }
                return $"каталог временных файлов недоступен для записи: {ex.Message}";
            }
        }
    }
}
