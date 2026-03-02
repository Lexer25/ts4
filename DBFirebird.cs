using FirebirdSql.Data.FirebirdClient;
using FirebirdSql.Data.Isql;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;
using MELogger = Microsoft.Extensions.Logging.ILogger;

namespace TS4
{

    class DBFirebird
    {
        public static MELogger? _logger;

        public static FbConnection Connect(string connectionString, MELogger? logger)
        {
            _logger = logger;
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return new FbConnection(connectionString);
        }

       
        public static bool ProcedurChec(FbConnection con)
        {
            const string sql = "SELECT FIRST 1 1 FROM RDB$PROCEDURES WHERE RDB$PROCEDURE_NAME='CARDINDEV_TS4'";
            using (var cmd = new FbCommand(sql, con))
            {
                var r = cmd.ExecuteScalar();
                return r != null && r != DBNull.Value;
            }
        }

        /// <summary>
        /// Проверяет наличие триггера CARDINDEV_AI0 (событие CARDINDEV_INSERT).
        /// </summary>
        public static bool TriggerCheck(FbConnection con)
        {
            const string sql = "SELECT FIRST 1 1 FROM RDB$TRIGGERS WHERE RDB$TRIGGER_NAME='CARDINDEV_AI0'";
            try
            {
                using (var cmd = new FbCommand(sql, con))
                {
                    var r = cmd.ExecuteScalar();
                    return r != null && r != DBNull.Value;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"TriggerCheck error: {LogHelper.Sanitize(ex.Message)}");
                return false;
            }
        }
        public static DataTable GetComandForDevice(FbConnection con, string name)
        {
            // Параметр 1=Write, 2=Delete. Сначала Write, затем Delete (param 2 может не поддерживаться в старых версиях процедуры).
            string nameEscaped = (name ?? "").Replace("'", "''");
            var table = new DataTable();

            foreach (int opParam in new[] { 1, 2 })
            {
                try
                {
                    string sql = "select first 500 cg.id_dev, cg.id_reader, cg.id_card, cg.timezones, cg.operation, cg.id_cardindev from cardindev_ts4(" + opParam + ", '" + nameEscaped + "') cg order by cg.id_cardindev";
                    using (var cmd = new FbCommand(sql, con))
                    using (var reader = cmd.ExecuteReader())
                    {
                        var dt = new DataTable();
                        dt.Load(reader);
                        if (dt.Rows.Count > 0)
                        {
                            if (table.Rows.Count == 0)
                                table = dt.Clone();
                            var keys = new HashSet<string>();
                            foreach (DataRow r in table.Rows)
                                keys.Add($"{r["id_dev"]}_{r["id_reader"]}_{r["id_card"]}_{r["operation"]}");
                            foreach (DataRow r in dt.Rows)
                            {
                                var key = $"{r["id_dev"]}_{r["id_reader"]}_{r["id_card"]}_{r["operation"]}";
                                if (!keys.Contains(key))
                                {
                                    keys.Add(key);
                                    table.Rows.Add(r.ItemArray);
                                }
                            }
                        }
                    }
                }
                catch (FbException ex) when (opParam == 2)
                {
                    _logger?.LogDebug(ex, "GetComandForDevice: cardindev_ts4(2, name) не поддерживается или ошибка — используем только Write. {Message}", LogHelper.Sanitize(ex.Message));
                }
            }

            if (table.Rows.Count > 0)
            {
                var dv = table.DefaultView;
                dv.Sort = "id_cardindev";
                table = dv.ToTable();
            }
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

            using (var cmd = new FbCommand(sql, con))
            {
                int rowsAffected = cmd.ExecuteNonQuery();
                return rowsAffected > 0;
            }
        }
        public static bool DeleteCardInDev(FbConnection con, DataRow row)
        {
            int idCardInDev = Convert.ToInt32(row["id_cardindev"]);
            string sql = $@"delete from cardindev cd
            where cd.id_cardindev ={idCardInDev} and cd.id_db=1";

            using (var cmd = new FbCommand(sql, con))
            {
                int rowsAffected = cmd.ExecuteNonQuery();
                return rowsAffected > 0;
            }
        }
        public static bool UpdateCardInDevIncrement(FbConnection con, DataRow row)
        {
            int idCardInDev = Convert.ToInt32(row["id_cardindev"]);
            string sql = $@"update cardindev cd
            set cd.attempts=cd.attempts+1
            where cd.id_cardindev={idCardInDev}";

            using (var cmd = new FbCommand(sql, con))
            {
                int rowsAffected = cmd.ExecuteNonQuery();
                return rowsAffected > 0;
            }
        }

        /// <summary>
        /// Пакетное обновление cardidx (только для Write) и удаление из cardindev.
        /// successWrites — успешные записи (UpdateIdxCard + Delete). successDeletes — успешные удаления (только Delete).
        /// 2–3 round-trip вместо 2*N.
        /// </summary>
        public static void BatchSuccess(FbConnection con,
            List<(DataRow row, string result)> successWrites,
            List<DataRow> successDeletes)
        {
            var allIds = new List<int>();
            using (var tr = con.BeginTransaction())
            {
                try
                {
                    if (successWrites != null && successWrites.Count > 0)
                    {
                        var caseParts = successWrites.Select(x =>
                        {
                            int id = Convert.ToInt32(x.row["id_cardindev"]);
                            string res = (x.result ?? "").Length > 100 ? (x.result ?? "").Substring(0, 100) : (x.result ?? "");
                            res = res.Replace("'", "''");
                            return $"when {id} then '{res}'";
                        });
                        var idStr = string.Join(",", successWrites.Select(x => Convert.ToInt32(x.row["id_cardindev"])));
                        string caseExpr = "case cdx.id_cardindev " + string.Join(" ", caseParts) + " end";
                        using (var cmd = new FbCommand($@"update cardidx cdx set cdx.load_time='now', cdx.load_result={caseExpr}, cdx.id_cardindev=null where cdx.id_cardindev in ({idStr})", con, tr))
                            cmd.ExecuteNonQuery();
                        allIds.AddRange(successWrites.Select(x => Convert.ToInt32(x.row["id_cardindev"])));
                    }
                    if (successDeletes != null) allIds.AddRange(successDeletes.Select(x => Convert.ToInt32(x["id_cardindev"])));
                    if (allIds.Count == 0) { tr.Commit(); return; }
                    var delIds = string.Join(",", allIds.Distinct());
                    using (var cmd = new FbCommand($@"delete from cardindev cd where cd.id_cardindev in ({delIds})", con, tr))
                        cmd.ExecuteNonQuery();
                    tr.Commit();
                }
                catch { tr.Rollback(); throw; }
            }
        }

        /// <summary>
        /// Пакетное обновление cardidx (только для Write) и инкремент attempts (неуспешные записи/удаления).
        /// failWrites — неуспешные записи (UpdateIdxCard + Increment). failDeletes — неуспешные удаления (только Increment).
        /// </summary>
        public static void BatchFail(FbConnection con,
            List<(DataRow row, string result)> failWrites,
            List<DataRow> failDeletes)
        {
            var allIds = new List<int>();
            if (failWrites != null && failWrites.Count > 0)
            {
                var caseParts = failWrites.Select(x =>
                {
                    int id = Convert.ToInt32(x.row["id_cardindev"]);
                    string res = (x.result ?? "").Length > 100 ? (x.result ?? "").Substring(0, 100) : (x.result ?? "");
                    res = res.Replace("'", "''");
                    return $"when {id} then '{res}'";
                });
                var idStr = string.Join(",", failWrites.Select(x => Convert.ToInt32(x.row["id_cardindev"])));
                string caseExpr = "case cdx.id_cardindev " + string.Join(" ", caseParts) + " end";
                using (var cmd = new FbCommand($@"update cardidx cdx set cdx.load_time='now', cdx.load_result={caseExpr} where cdx.id_cardindev in ({idStr})", con))
                    cmd.ExecuteNonQuery();
                allIds.AddRange(failWrites.Select(x => Convert.ToInt32(x.row["id_cardindev"])));
            }
            if (failDeletes != null) allIds.AddRange(failDeletes.Select(x => Convert.ToInt32(x["id_cardindev"])));
            if (allIds.Count == 0) return;
            var incIds = string.Join(",", allIds.Distinct());
            using (var cmd = new FbCommand($@"update cardindev cd set cd.attempts=cd.attempts+1 where cd.id_cardindev in ({incIds})", con))
                cmd.ExecuteNonQuery();
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
                    _logger?.LogError($"StatInsertDirect error for id_dev={idDev}: {LogHelper.Sanitize(ex.Message)}");
                    return -1;
                }
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
                _logger?.LogError($"GetDeviceIdByName error for device_name={deviceName}: {LogHelper.Sanitize(ex.Message)}");
                return null;
            }
        }

        /// <summary>
        /// Возвращает список устройств для загрузки: ID + имя.
        /// </summary>
        public static List<(int idDev, string name)> GetListForLoad(FbConnection con)
        {
            const string sql = @"select distinct d2.id_dev, d2.name
from cardindev cd
join device d on d.id_dev=cd.id_dev
join device d2 on d2.id_ctrl=d.id_ctrl and d2.id_reader is null
where d.""ACTIVE"">0
and d2.""ACTIVE"">0";
            var result = new List<(int idDev, string name)>(64);
            var swTotal = Stopwatch.StartNew();
            using (var cmd = new FbCommand(sql, con))
            {
                try
                {
                    var swExec = Stopwatch.StartNew();
                    using (var reader = cmd.ExecuteReader())
                    {
                        swExec.Stop();
                        _logger?.LogInformation("360 GetListForLoad: ExecuteReader (курсор открыт), мс={Ms}", swExec.ElapsedMilliseconds);

                        var swRead = Stopwatch.StartNew();
                        while (reader.Read())
                        {
                            result.Add((reader.GetInt32(0), reader.IsDBNull(1) ? "" : reader.GetString(1)));
                        }
                        swRead.Stop();
                        _logger?.LogInformation("368 GetListForLoad: цикл Read() до конца, мс={Ms}, строк={Count}", swRead.ElapsedMilliseconds, result.Count);
                    }
                    swTotal.Stop();
                    _logger?.LogInformation("370 GetListForLoad: всего мс={Ms}, строк в результате={Count}", swTotal.ElapsedMilliseconds, result.Count);
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"GetListForLoad error: {LogHelper.Sanitize(ex.Message)}");
                }
            }
            return result;
        }

        



    }
}