using NLog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Topshelf;

namespace RabbitMQService
{
    internal class Program
    {
        static void Main(string[] args)
        {
            HostFactory.Run(x =>
            {
                x.Service<RabbitMQQueue>(s =>
                {
                    s.ConstructUsing(name => new RabbitMQQueue());
                    s.WhenStarted(tc => tc.Start());
                    s.WhenStopped(tc => tc.Stop());
                });

                x.RunAsLocalSystem();
                x.SetDescription("BPM RabbitMQ 多队列监听服务");
                x.SetDisplayName("RabbitMQService");
                x.SetServiceName("BPM_RabbitMQService");
            });
        }
    }
}
