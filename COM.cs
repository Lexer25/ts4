using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace TS4
{
    public class COM : IDisposable
    {
        dynamic a;

        // P5: после таймаута вызов внутри драйвера продолжает выполняться, поэтому такой
        // объект нельзя ни использовать повторно, ни освобождать — только «бросить».
        private volatile bool _abandoned;

        //public bool isTest = false;
        private readonly bool isTest;
        private readonly int driverTimeoutSeconds;

        public COM(string uid, WorkerOptions options)
        {
            a = Activator.CreateInstance(Type.GetTypeFromCLSID(Guid.Parse(uid)));//load driver by guid
            isTest = options.IsTest;
            driverTimeoutSeconds = options.DriverTimeoutSeconds > 0 ? options.DriverTimeoutSeconds : 60;
        }
        public dynamic Getcom()
        {
            return a;
        }
        public void SetupString(string str)
        {
             a.SetupString = str;
        }

        /*
        private List<string> commands = new List<string>();
        public List<string> ComandsExclude()
        {
            List<string> lists = new List<string>();
            foreach (string comand in commands) lists.Add(a.Execute(comand));
            return lists;
        }

        */
        public string ComandExclude(string comand)
        {

            //commands.Add(comand);//30.01.2025 вот это непонятно зачем. Надо проверить и убрать, если не нужен.
            return a.Execute(comand);

        }

        public string ComandExecute(string comand)
        {
            if (_abandoned)
            {
                // Объект «брошен» после таймаута — обращаться к нему нельзя (см. Abandon).
                return "ERR TIMEOUT";
            }
            if (isTest)
            {
                Console.WriteLine("53 mode isTest is TRUE");
                return "ERR";
            }
            var task = Task.Run(() => (string)a.Execute(comand));
            if (task.Wait(TimeSpan.FromSeconds(driverTimeoutSeconds)))
                return task.Result ?? "";

            // Таймаут драйвера: вызов внутри COM-объекта ещё выполняется. Повторный вызов
            // (и даже чтение свойств) из другого потока даёт AccessViolationException,
            // который в .NET не перехватывается и роняет процесс.
            Abandon();
            return "ERR TIMEOUT";
        }


        public bool ReportStatus()
        {
            if (isTest)
            {
                return true;
            }
            var (ok, _) = ReportStatusWithMessage();
            return ok;
        }

        /// <summary>Проверка связи с контроллером; возвращает (успех, текст ответа для load_result — по возможности сырой ответ UDP/udprecv).</summary>
        public (bool ok, string message) ReportStatusWithMessage()
        {
            if (isTest)
                return (true, "Yes");
            if (_abandoned)
                return (false, "ERR TIMEOUT");
            try
            {
                var statusTask = Task.Run(() => (string)a.Execute(@"ReportStatus"));
                if (!statusTask.Wait(TimeSpan.FromSeconds(driverTimeoutSeconds)))
                {
                    // Тот же риск, что и в ComandExecute: объект больше не трогаем.
                    Abandon();
                    return (false, "ERR TIMEOUT");
                }
                string response = statusTask.Result ?? "";
                if (string.IsNullOrEmpty(response))
                {
                    string rawResp = GetRawUdpResponseOrFallback(null);
                    return (false, (string.IsNullOrEmpty(rawResp) || IsDriverStatusOnly(rawResp)) ? "no connect" : rawResp);
                }
                string value = response.Split('=').Length > 1 ? response.Split('=')[1].Trim() : response;
                bool ok = value.Equals("Yes", StringComparison.OrdinalIgnoreCase);
                if (ok) return (ok, value);
                // Нет связи — только сырой ответ контроллера (udprecv). Служебный "OK Online=No" не отдаём.
                string rawMsg = GetRawUdpResponseOrFallback(null);
                if (!string.IsNullOrWhiteSpace(rawMsg) && !IsDriverStatusOnly(rawMsg))
                    return (false, rawMsg);
                if (IsDriverStatusOnly(response))
                    return (false, "no connect");
                return (false, rawMsg ?? "no connect");
            }
            catch (Exception ex)
            {
                string rawEx = GetRawUdpResponseOrFallback(null);
                if (!string.IsNullOrWhiteSpace(rawEx) && !IsDriverStatusOnly(rawEx))
                    return (false, rawEx);
                return (false, ex.Message ?? "no connect");
            }
        }

        private static bool IsDriverStatusOnly(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return true;
            string u = s.Trim().ToUpperInvariant();
            return u == "NO" || u == "YES"
                || u.Contains("ONLINE=NO") || u.Contains("ONLINE=YES")
                || u.StartsWith("REPORTSTATUS=") || u.StartsWith("OK REPORTSTATUS=") || u.StartsWith("OK ONLINE=");
        }

        /// <summary>Пытается получить сырой ответ UDP/udprecv у драйвера (свойства/методы LastResponse, LastError и т.п.), иначе fallback.</summary>
        private string GetRawUdpResponseOrFallback(string? fallback)
        {
            try
            {
                object obj = a;
                Type t = obj.GetType();
                string[] tryProps = { "LastResponse", "LastError", "LastUdpResponse", "Response", "ErrorMessage", "LastRecv", "UdpRecv" };
                foreach (string name in tryProps)
                {
                    try
                    {
                        var pi = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
                        if (pi != null)
                        {
                            var v = pi.GetValue(obj);
                            if (v != null)
                            {
                                string s = (v as string) ?? v.ToString() ?? "";
                                if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
                            }
                        }
                    }
                    catch { }
                }
                string[] tryMethods = { "GetLastResponse", "GetLastError", "GetLastUdpResponse" };
                foreach (string name in tryMethods)
                {
                    try
                    {
                        var mi = t.GetMethod(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase, null, Type.EmptyTypes, null);
                        if (mi != null)
                        {
                            var v = mi.Invoke(obj, null);
                            if (v != null)
                            {
                                string s = (v as string) ?? v.ToString() ?? "";
                                if (!string.IsNullOrWhiteSpace(s)) return s.Trim();
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
            return fallback ?? "no connect";
        }

        /// <summary>Возвращает последний сырой ответ драйвера/контроллера по UDP (для логирования при ошибках).</summary>
        public string GetLastDriverResponse()
        {
            if (_abandoned) return "ERR TIMEOUT";
            return GetRawUdpResponseOrFallback(null);
        }

        /// <summary>
        /// Помечает COM-объект как непригодный после таймаута вызова. Объект больше не используется
        /// и не освобождается (внутри него ещё выполняется вызов), а GC не даёт его собрать.
        /// </summary>
        private void Abandon()
        {
            if (_abandoned) return;
            _abandoned = true;
            try
            {
                // Отдельно удерживать объект не нужно: пока брошенный вызов выполняется,
                // его задача держит ссылку на этот объект, поэтому GC не освободит COM
                // «на ходу»; после завершения вызова объект спокойно соберётся.
                Serilog.Log.Warning("COM: вызов драйвера не уложился в {Timeout} сек — объект больше не используется, будет создан новый", driverTimeoutSeconds);
            }
            catch { }
        }

        /// <summary>Освобождает COM-объект. Брошенный объект не освобождается (в нём может идти вызов).</summary>
        public void Dispose()
        {
            if (_abandoned) return;
            object? obj = a;
            a = null!;
            if (obj == null) return;
            try { Marshal.FinalReleaseComObject(obj); } catch { }
        }
    }
}
