using NLog;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using RabbitMQ.Client.Exceptions;
using RabbitMQService;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace RabbitMQService
{
    public class RabbitMQConsumer : IDisposable
    {
        private readonly Logger _logger;
        private readonly IMessageHandler _handler;
        private readonly List<QueueConfig> _queueConfigs; // 队列 -> 并发数
        private readonly string _hostName;
        private readonly string _userName;
        private readonly string _password;
        private readonly int _port;
        private readonly int _messageTimeout;

        private IConnection _connection;
        private ConnectionFactory _factory;
        private readonly ConcurrentDictionary<string, IModel> _channels = new ConcurrentDictionary<string, IModel>();
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _queueSemaphores = new ConcurrentDictionary<string, SemaphoreSlim>();
        private readonly ConcurrentDictionary<string, string> _consumerTags = new ConcurrentDictionary<string, string>();

        private readonly object _lock = new object();
        private bool _isReconnecting = false;
        private bool _disposed = false;

        public RabbitMQConsumer(Logger logger, IMessageHandler handler,
            List<QueueConfig> queueConfigs, 
            string hostName, string userName, string password,
            int port = 5672, int messageTimeout = 30000)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            _queueConfigs = queueConfigs ?? throw new ArgumentNullException(nameof(queueConfigs));
            _hostName = hostName;
            _userName = userName;
            _password = password;
            _port = port;
            _messageTimeout = messageTimeout;
        }

        public void Start()
        {
            InitializeConnection();
            StartListening();
        }

        private void InitializeConnection()
        {
            _factory = new ConnectionFactory
            {
                HostName = _hostName,
                UserName = _userName,
                Password = _password,
                Port = _port,
                DispatchConsumersAsync = true
            };

            Connect();
        }

        private void Connect()
        {
            int retry = 0;
            while (retry < 5)
            {
                try
                {
                    _logger.Info($"尝试连接 RabbitMQ: {_hostName}:{_port}");
                    _connection = _factory.CreateConnection();
                    _connection.ConnectionShutdown += OnConnectionShutdown;
                    _logger.Info("成功连接 RabbitMQ");
                    return;
                }
                catch (BrokerUnreachableException ex)
                {
                    retry++;
                    int delay = retry * 2000;
                    _logger.Error(ex, $"连接失败，第{retry}次重试, 延迟{delay}ms");
                    Thread.Sleep(delay);
                }
            }

            throw new Exception("无法连接 RabbitMQ");
        }

        private void OnConnectionShutdown(object sender, ShutdownEventArgs e)
        {
            _logger.Warn($"连接断开: {e.ReplyText}");
            Task.Run(() => ReconnectAsync());
        }

        private async Task ReconnectAsync()
        {
            lock (_lock)
            {
                if (_isReconnecting || _disposed) return;
                _isReconnecting = true;
            }

            try
            {
                if (_disposed) return;

                CleanupChannels();
                if (_connection != null)
                {
                    _connection.Close();
                }

                if (_disposed) return;

                _logger.Info("等待5秒后重连...");
                await Task.Delay(5000);

                if (_disposed) return;

                try
                {
                    Connect();
                    StartListening();
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "重连 RabbitMQ 失败");
                }
            }
            finally
            {
                lock (_lock)
                {
                    _isReconnecting = false;
                }
            }
        }

        private void StartListening()
        {
            CleanupChannels();

            foreach (var kv in _queueConfigs)
            {
                CreateConsumerChannel(kv.QueueName, kv.MaxConcurrent);
            }
        }

        private void CreateConsumerChannel(string queueName, int maxConcurrent)
        {
            if (_connection == null || !_connection.IsOpen)
            {
                _logger.Error("RabbitMQ 连接不可用，无法创建通道");
                return;
            }

            var channel = _connection.CreateModel();
            ushort prefetch = (ushort)Math.Min(maxConcurrent, ushort.MaxValue);
            channel.BasicQos(0, prefetch, false);
            _channels[queueName] = channel;

            channel.QueueDeclare(queueName, true, false, false);

            if (maxConcurrent <= 0) maxConcurrent = 1;
            var semaphore = new SemaphoreSlim(maxConcurrent, maxConcurrent);
            _queueSemaphores[queueName] = semaphore;

            var consumer = new AsyncEventingBasicConsumer(channel);
            string consumerTag = channel.BasicConsume(queueName, false, consumer);
            _consumerTags[queueName] = consumerTag;

            channel.CallbackException += (s, e) =>
            {
                _logger.Error(e.Exception, $"队列[{queueName}] 通道异常，将重建通道...");
                Task.Run(() => RecreateChannel(queueName));
            };

            consumer.Received += async (model, ea) =>
            {
                var sem = _queueSemaphores[queueName];
                await sem.WaitAsync();
                var cts = new CancellationTokenSource(_messageTimeout);

                try
                {
                    var message = Encoding.UTF8.GetString(ea.Body.ToArray());
                    await _handler.HandleAsync(queueName, message, cts.Token);

                    channel.BasicAck(ea.DeliveryTag, false);
                    _logger.Info($"队列[{queueName}] 消息处理成功, DeliveryTag={ea.DeliveryTag}");
                }
                catch (OperationCanceledException)
                {
                    channel.BasicNack(ea.DeliveryTag, false, true);
                    _logger.Warn($"队列[{queueName}] 消息处理超时，已退回队列, DeliveryTag={ea.DeliveryTag}");
                }
                catch (Exception ex)
                {
                    channel.BasicNack(ea.DeliveryTag, false, true);
                    _logger.Error(ex, $"队列[{queueName}] 消息处理失败, DeliveryTag={ea.DeliveryTag}");
                }
                finally
                {
                    cts.Dispose();
                    sem.Release();
                }
            };

            _logger.Info($"已开始监听队列: {queueName}, 并发限制={maxConcurrent}");
        }

        private void RecreateChannel(string queueName)
        {
            try
            {
                var config = _queueConfigs.FirstOrDefault(q => q.QueueName == queueName);
                if (config == null)
                {
                    _logger.Warn($"队列 {queueName} 配置已不存在，跳过重建");
                    return;
                }

                // 清理旧通道
                if (_channels.TryRemove(queueName, out var oldChannel))
                {
                    try
                    {
                        if (_consumerTags.TryRemove(queueName, out string consumerTag))
                        {
                            oldChannel.BasicCancel(consumerTag);
                        }
                        oldChannel.Close();
                        oldChannel.Dispose();
                    }
                    catch { }
                }

                if (_queueSemaphores.TryRemove(queueName, out var oldSemaphore))
                {
                    oldSemaphore.Dispose();
                }

                // 重建通道
                _logger.Info($"正在为队列[{queueName}] 重建通道...");
                CreateConsumerChannel(queueName, config.MaxConcurrent);
                _logger.Info($"队列[{queueName}] 通道重建完成");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"重建队列[{queueName}] 通道失败");
            }
        }

        private void CleanupChannels()
        {
            foreach (var kv in _channels.ToList())
            {
                try
                {
                    if (_consumerTags.TryRemove(kv.Key, out string consumerTag))
                    {
                        kv.Value.BasicCancel(consumerTag);
                    }
                    kv.Value?.Close();
                    kv.Value?.Dispose();
                }
                catch { }
                finally { _channels.TryRemove(kv.Key, out _); }
            }

            foreach (var kv in _queueSemaphores.ToList())
            {
                kv.Value.Dispose();
                _queueSemaphores.TryRemove(kv.Key, out _);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            CleanupChannels();
            if (_connection != null)
            {
                _connection.Close();
                _connection.Dispose();
            }
        }
    }
}