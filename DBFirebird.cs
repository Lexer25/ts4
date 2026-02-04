using FirebirdSql.Data.FirebirdClient;
using FirebirdSql.Data.Isql;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace TS4
{

    class DBFirebird
    {
        public static ILogger _logger;
        
        // Константы для retry логики при deadlock
        private const int MaxRetryAttempts = 10; // Увеличено количество попыток
        private const int BaseRetryDelayMs = 100; // Увеличена базовая задержка в миллисекундах
        
        /// <summary>
        /// Выполняет операцию с повторными попытками при возникновении ошибок блокировки
        /// </summary>
        private static bool ExecuteWithRetry(Func<bool> operation, string operationName, int idCardInDev)
        {
            int attempt = 0;
            while (attempt < MaxRetryAttempts)
            {
                try
                {
                    return operation();
                }
                catch (FbException fbEx)
                {
                    // Проверяем, является ли это ошибкой блокировки/deadlock
                    bool isLockError = fbEx.Message.Contains("lock conflict") ||
                                      fbEx.Message.Contains("deadlock") ||
                                      fbEx.Message.Contains("update conflicts") ||
                                      fbEx.ErrorCode == 335544336; // isc_lock_conflict
                    
                    if (isLockError && attempt < MaxRetryAttempts - 1)
                    {
                        attempt++;
                        // Экспоненциальная задержка: 100ms, 200ms, 400ms, 800ms, 1600ms, 3200ms, 6400ms, 12800ms, 25600ms
                        int delay = BaseRetryDelayMs * (int)Math.Pow(2, attempt - 1);
                        //_logger?.LogDebug($"{operationName}: Конфликт блокировки для id_cardindev={idCardInDev}, попытка {attempt}/{MaxRetryAttempts}, ожидание {delay}ms");
                        Thread.Sleep(delay);
                        continue;
                    }
                    
                    // Если это не ошибка блокировки или исчерпаны попытки - пробрасываем исключение
                    throw;
                }
            }
            
            return false;
        }
        public static FbConnection Connect(string connectionString, ILogger logger)
        {
            _logger = logger;
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return new FbConnection(connectionString);
        }

       
        public static bool ProcedurChec(FbConnection con)
        {
            string sql = "select distinct * from RDB$PROCEDURES where RDB$PROCEDURE_name='CARDINDEV_TS4'";
            //_logger.LogDebug($@"121 " + sql);
            FbCommand getcomand = new FbCommand(sql, con);
            var reader = getcomand.ExecuteReader();
            int i = 0;
            while (reader.Read()) i++;
            return i>0;
        }

        /// <summary>
        /// Проверяет наличие триггера, который генерирует событие CARDINDEV_INSERT
        /// Триггер должен быть на таблице CARDINDEV и генерировать событие через POST_EVENT 'CARDINDEV_INSERT'
        /// </summary>
        public static bool TriggerCheck(FbConnection con)
        {
            // Проверяем наличие триггера на таблице CARDINDEV, который генерирует событие CARDINDEV_INSERT
            // Ищем триггеры на таблице CARDINDEV (BEFORE INSERT = 1 или AFTER INSERT = 2), которые содержат POST_EVENT 'CARDINDEV_INSERT'
            string sql = @"
                SELECT * 
                    FROM RDB$TRIGGERS 
                    WHERE RDB$TRIGGER_NAME = 'CARDINDEV_AI0'";
            
            try
            {
                FbCommand getcomand = new FbCommand(sql, con);
                var reader = getcomand.ExecuteReader();
                int i = 0;
                while (reader.Read()) i++;
                return i > 0;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"TriggerCheck error: {ex.Message}");
                return false;
            }
        }
        public static DataTable GetComandForDevice(FbConnection con, string name)
        {
            string sql = $@"select distinct cg.id_dev, cg.id_reader, cg.id_card, cg.timezones, cg.operation, cg.id_cardindev from cardindev_ts4(1, '{name}') cg
             order by cg.id_cardindev";

            //_logger.LogDebug($@"121 " + sql);
            FbCommand getcomand = new FbCommand(sql, con);
            var reader = getcomand.ExecuteReader();
            DataTable table = new DataTable();
            table.Load(reader);
            return table;
        }


        /**20.12.2024 Обновление таблицы cardidx по результатам записи/удаления карты.
         * при успешной записи/удалении карты фиксируется дата, время, ответ (ОК), id_cardindev ставится null
         * при неуспешной попытке записи/удалении фиксируется дата, время, ответ (err). id_cardindev остается без изменений, т.к. попытки записи будут продолжаться.
         * 
         */
        public static bool UpdateIdxCard(FbConnection con, DataRow row, string result, bool s_e)
        {
            int idCardInDev = Convert.ToInt32(row["id_cardindev"]);
            string id_cardindev = "";
            if (s_e)
            {
                id_cardindev = ",cdx.id_cardindev=null";
            }
            string sql = $@"update cardidx cdx
                set cdx.load_time='now',
                cdx.load_result='{result.Substring(0, Math.Min(result.Length, 100))}'
                {id_cardindev}
                where cdx.id_cardindev={idCardInDev}";
            
            return ExecuteWithRetry(() =>
            {
                using (FbCommand getcomand = new FbCommand(sql, con))
                {
                    int rowsAffected = getcomand.ExecuteNonQuery();
                    return rowsAffected > 0;
                }
            }, "UpdateIdxCard", idCardInDev);
        }
        public static bool DeleteCardInDev(FbConnection con, DataRow row)
        {
            int idCardInDev = Convert.ToInt32(row["id_cardindev"]);
            string sql = $@"delete from cardindev cd
            where cd.id_cardindev ={idCardInDev}";
            
            return ExecuteWithRetry(() =>
            {
                using (FbCommand getcomand = new FbCommand(sql, con))
                {
                    int rowsAffected = getcomand.ExecuteNonQuery();
                    return rowsAffected > 0;
                }
            }, "DeleteCardInDev", idCardInDev);
        }
        public static bool UpdateCardInDevIncrement(FbConnection con, DataRow row)
        {
            int idCardInDev = Convert.ToInt32(row["id_cardindev"]);
            string sql = $@"update cardindev cd
            set cd.attempts=cd.attempts+1
            where cd.id_cardindev={idCardInDev}";
            
            return ExecuteWithRetry(() =>
            {
                using (FbCommand getcomand = new FbCommand(sql, con))
                {
                    int rowsAffected = getcomand.ExecuteNonQuery();
                    return rowsAffected > 0;
                }
            }, "UpdateCardInDevIncrement", idCardInDev);
        }


        /** 20.12.2024 Обновление таблицы cardidx при отсутсвии связи с устройством.
         * в таблицу записывается дата, время и сообщение, что нет связи с устройством.
         * Особенность: для ускорения работы необходимо указывать id точек прохода.
         * 
         */

        /** 20.12.2024 Обновление таблицы cardindev при отсутсвии связи с устройством.
         * выполняется инкремент поля attempt.
         * Особенность: для ускорения работы необходимо указывать id точек прохода.
         * 
         */

        /*

        public static bool checkProc(FbConnection con, string procName)
        {
            string sql = $@"SELECT * FROM RDB$PROCEDURES WHERE RDB$PROCEDURE_NAME ='{procName}'";
            FbCommand getcomand = new FbCommand(sql, con);
            //cdx.id_cardindev=null
            var reader = getcomand.ExecuteReader();
            return reader.HasRows;
        }
        */

        /** 16.10.2025 Вставка статистики устройства в формате JSON через хранимую процедуру stat_insert
         * Паттерн вызова: select stat_insert(:order, :id_dev, :id_agent, :id_param, :value) from rdb$database
         */
        public static int StatInsert(FbConnection con, int order, int idDev, int idAgent, int idParam, string json)
        {
            // Проверяем существование ID устройства
            if (!CheckDeviceExists(con, idDev))
            {
                _logger?.LogWarning($"StatInsert: Device with id_dev={idDev} does not exist, skipping insert");
                return -2; // Код ошибки для несуществующего устройства
            }

            string sql = "select stat_insert(@order, @id_dev, @id_agent, @id_param, @value) from rdb$database";
            using (var cmd = new FbCommand(sql, con))
            {
                cmd.Parameters.AddWithValue("@order", order);
                cmd.Parameters.AddWithValue("@id_dev", idDev);
                cmd.Parameters.AddWithValue("@id_agent", idAgent);
                cmd.Parameters.AddWithValue("@id_param", idParam);
                cmd.Parameters.AddWithValue("@value", json);
                try
                {
                    var reader = cmd.ExecuteReader();
                    DataTable table = new DataTable();
                    table.Load(reader);
                    return 0;
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"StatInsert error for id_dev={idDev}: {ex.Message}");
                    return -1;
                }
            }
        }

        /** 21.10.2025 Прямая вставка статистики устройства в таблицу st_data
         * Альтернатива для случаев, когда хранимая процедура stat_insert недоступна
         */
        public static int StatInsertDirect(FbConnection con, int order, int idDev, int idAgent, int idParam, string json)
        {
            // Проверяем существование ID устройства
            if (!CheckDeviceExists(con, idDev))
            {
                _logger?.LogWarning($"StatInsertDirect: Device with id_dev={idDev} does not exist, skipping insert");
                return -2; // Код ошибки для несуществующего устройства
            }

            string sql = @"
                INSERT INTO st_data (
                    ID_ORDER, ID_DEV, ID_AGENT, ID_PARAM, FACTS
                ) VALUES (
                    @order, @id_dev, @id_agent, @id_param, @value
                )";
            using (var cmd = new FbCommand(sql, con))
            {
                cmd.Parameters.AddWithValue("@order", order);
                cmd.Parameters.AddWithValue("@id_dev", idDev);
                cmd.Parameters.AddWithValue("@id_agent", idAgent);
                cmd.Parameters.AddWithValue("@id_param", idParam);
                cmd.Parameters.AddWithValue("@value", json);
                try
                {
                    int rowsAffected = cmd.ExecuteNonQuery();
                    return rowsAffected > 0 ? 0 : -1;
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"StatInsertDirect error for id_dev={idDev}: {ex.Message}");
                    return -1;
                }
            }
        }

        /// <summary>
        /// Проверяет существование устройства с указанным ID в базе данных
        /// </summary>
        private static bool CheckDeviceExists(FbConnection con, int idDev)
        {
            try
            {
                string sql = "SELECT COUNT(*) FROM device WHERE ID_DEV = @id_dev";
                using (var cmd = new FbCommand(sql, con))
                {
                    cmd.Parameters.AddWithValue("@id_dev", idDev);
                    var result = cmd.ExecuteScalar();
                    return Convert.ToInt32(result) > 0;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"CheckDeviceExists error for id_dev={idDev}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Получает ID устройства по имени из таблицы device
        /// </summary>
        public static int? GetDeviceIdByName(FbConnection con, string deviceName)
        {
            try
            {
                string sql = "SELECT ID_DEV FROM device WHERE name = @device_name";
                using (var cmd = new FbCommand(sql, con))
                {
                    cmd.Parameters.AddWithValue("@device_name", deviceName);
                    var result = cmd.ExecuteScalar();
                    return result != null ? Convert.ToInt32(result) : (int?)null;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"GetDeviceIdByName error for device_name={deviceName}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Возвращает список устройств для загрузки: ID + имя.
        /// </summary>
        public static List<(int idDev, string name)> GetListForLoad(FbConnection con)
        {
            var result = new List<(int idDev, string name)>();
            string sql = @"select distinct d2.id_dev, d2.name
from cardindev cd
join device d on d.id_dev=cd.id_dev
join device d2 on d2.id_ctrl=d.id_ctrl and d2.id_reader is null
where d.""ACTIVE"">0
and d2.""ACTIVE"">0";
            using (var cmd = new FbCommand(sql, con))
            {
                try
                {
                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            if (!reader.IsDBNull(0))
                            {
                                int id = Convert.ToInt32(reader[0]);
                                string name = reader.IsDBNull(1) ? string.Empty : Convert.ToString(reader[1]) ?? string.Empty;
                                result.Add((id, name));
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"GetListForLoad error: {ex.Message}");
                }
            }
            return result;
        }

        



    }
}