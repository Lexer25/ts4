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

        //public bool isTest = false;
        private readonly bool isTest;

        public COM(string uid, WorkerOptions options)
        {
            a = Activator.CreateInstance(Type.GetTypeFromCLSID(Guid.Parse(uid)));//load driver by guid
            isTest = options.IsTest;
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
            Console.WriteLine("50", isTest);
            if (isTest)
            {
                Console.WriteLine("53 mode isTest is TRUE");
                return "ERR";
            }
            else
            {
                Console.WriteLine("58 mode isTest is FALSE");
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
