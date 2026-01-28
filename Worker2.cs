using FirebirdSql.Data.FirebirdClient;
using Microsoft.Extensions.Logging;
using System.Data;
using System.Data.Common;
using System.Data.Odbc;
using System.Data.OleDb;
using System.Threading;

namespace TS4
{
    public class WorkerTest : BackgroundService
    {
        public readonly ILogger logger;
        private WorkerOptions options;
        public TimeSpan timeout;
        public TimeSpan timestart;
        public TimeSpan deltasleep;

        public WorkerTest(ILogger<Worker> logger, WorkerOptions options)
        {
            this.logger = logger;
            this.options = options;
            Console.WriteLine("23 constructor");
           
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            //logger.LogDebug(@$"time run basip:  deltasleep:");
            //Console.WriteLine("execute async");
            //await Task.Delay(1000);
            int i = 0;

            while (!stoppingToken.IsCancellationRequested)
            {
                Console.WriteLine("I=" + i++);
                logger.LogTrace("LogTrace Worker running at: {time}", DateTimeOffset.Now);
                logger.LogWarning("LogWarning Worker running at: {time}", DateTimeOffset.Now);
                logger.LogCritical("LogCritical Worker running at: {time}", DateTimeOffset.Now);
                logger.LogDebug("LogDebug Worker running at: {time}", DateTimeOffset.Now);
                logger.LogError("LogError Worker running at: {time}", DateTimeOffset.Now);
                logger.LogInformation("LogInformation Worker running at: {time}", DateTimeOffset.Now);
                logger.LogCritical("\n");
                await Task.Delay(2000, stoppingToken);
            }



        }
       

    }

}