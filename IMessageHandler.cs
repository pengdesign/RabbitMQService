using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RabbitMQService
{
    public interface IMessageHandler
    {
        Task HandleAsync(string queueName, string message, CancellationToken token);
    }
}
