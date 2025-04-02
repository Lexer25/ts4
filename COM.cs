using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TS4
{
    public class COM
    {
        dynamic a;

        public bool isTest = false;

        public COM(string uid)
        {
            a = Activator.CreateInstance(Type.GetTypeFromCLSID(Guid.Parse(uid)));//load driver by guid
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
            if (isTest)
            {
                return "Ok";
            }
            else
            {
                //commands.Add(comand);//30.01.2025 вот это непонятно зачем. Надо проверить и убрать, если не нужен.
                return a.Execute(comand);
            }
        }


        public bool ReportStatus()
        {
            if (isTest)
            {
                return true;
            }
            else
            {
                return  ((string) (a.Execute($@"ReportStatus"))).Split('=')[1] == "Yes";
            }
        }
    }
}
