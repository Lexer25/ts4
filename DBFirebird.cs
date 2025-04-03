using FirebirdSql.Data.FirebirdClient;
using FirebirdSql.Data.Isql;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace TS4
{

    class DBFirebird
    {
        public static ILogger _logger;
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
            string id_cardindev = "";
            if (s_e)
            {
                id_cardindev = ",cdx.id_cardindev=null";
            }
            string sql = $@"update cardidx cdx
                set cdx.load_time='now',
                cdx.load_result='{result.Substring(0, Math.Min(result.Length, 100))}'
                {id_cardindev}
                where cdx.id_cardindev={row["id_cardindev"]}";
            FbCommand getcomand = new FbCommand(sql, con);
            var reader = getcomand.ExecuteReader();
            DataTable table = new DataTable();
            table.Load(reader);
            return true;
        }
        public static bool DeleteCardInDev(FbConnection con, DataRow row)
        {
            FbCommand getcomand = new FbCommand($@"delete from cardindev cd
            where cd.id_cardindev ={row["id_cardindev"]}", con);
            //cdx.id_cardindev=null
            var reader = getcomand.ExecuteReader();
            DataTable table = new DataTable();
            table.Load(reader);
            return true;
        }
        public static bool UpdateCardInDevIncrement(FbConnection con, DataRow row)
        {
            FbCommand getcomand = new FbCommand($@"update cardindev cd
            set cd.attempts=cd.attempts+1
            where cd.id_cardindev={row["id_cardindev"]}", con);
            //cdx.id_cardindev=null
            var reader = getcomand.ExecuteReader();
            DataTable table = new DataTable();
            table.Load(reader);
            return true;
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

        /**Получаю список "дочек" - точек прохода
         * 
         */



    }
}