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
        private const int MaxRedeliveries = 5;
        private const string RetryCountHeader = "x-retry-count";
        // 全队列共享一个死信交换机(名称固定),才能用 RabbitMQ policy 给已存在的老队列补死信配置。
        // policy 命令(在 MQ 服务器执行一次即可):
        //   rabbitmqctl set_policy bpm-wait-exec-dlx ".*" '{"dead-letter-exchange":"bpm.wait-exec.dlx"}' --apply-to queues
        // 或 Management UI -> Admin -> Policies 新增同样内容。死信路由键默认=原路由键(即队列名),
        // 与下面绑定的 {queue}.dead 队列 routing-key 对应,无需在 policy 里配 routing-key。
        private const string SharedDlx = "bpm.wait-exec.dlx";

        private readonly Logger _logger;
        private readonly IMessageHandler _handler;
        private readonly List<QueueConfig> _queueConfigs;
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
        // FIX#1: IModel 非线程安全,所有通道操作(BasicAck/BasicNack/BasicPublish/BasicCancel)必须串行化
        private readonly ConcurrentDictionary<string, object> _channelLocks = new ConcurrentDictionary<string, object>();
        // FIX#4: 重建通道串行化,避免 CallbackException 与 Reconnect 并发重建
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _rebuildLocks = new ConcurrentDictionary<string, SemaphoreSlim>();

        private readonly CancellationTokenSource _shutdownCts = new CancellationTokenSource();
        private int _isReconnecting = 0;
        private volatile bool _disposed = false;

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
            try
            {
                // 开机只做一次快速尝试(单次 TCP 超时 10 秒),避免 SCM 30 秒启动超时(1053)。
                // 瞬时网络故障转后台重连后立即返回;配置类错误(认证/队列为空)仍直接抛,快速失败。
                Connect(maxAttempts: 1);
                StartListening();
            }
            catch (Exception ex) when (IsTransient(ex))
            {
                _logger.Warn(ex, "启动时连接 RabbitMQ 失败,已转入后台重连,服务先行启动...");
                Task.Run(() => ReconnectLoopAsync());
            }
        }

        private void InitializeConnection()
        {
            _factory = new ConnectionFactory
            {
                HostName = _hostName,
                UserName = _userName,
                Password = _password,
                Port = _port,
                DispatchConsumersAsync = true,
                // FIX#4: 只保留手动重连,关闭 SDK 自动恢复,避免两套机制重复建通道打架
                AutomaticRecoveryEnabled = false,
                TopologyRecoveryEnabled = false,
                RequestedHeartbeat = TimeSpan.FromSeconds(30),
                NetworkRecoveryInterval = TimeSpan.FromSeconds(5),
                // 单次 TCP 建连超时 10 秒,避免开机/重连时一次尝试就卡住几十秒
                RequestedConnectionTimeout = TimeSpan.FromSeconds(10)
            };

            Connect();
        }

        private static bool IsTransient(Exception ex)
        {
            return ex is BrokerUnreachableException
                || ex is System.Net.Sockets.SocketException
                || ex is System.IO.IOException
                || ex is TimeoutException
                || (ex is InvalidOperationException && ex.Message.Contains("连接不可用"));
        }

        private void Connect()
        {
            Connect(maxAttempts: 5);
        }

        private void Connect(int maxAttempts)
        {
            int retry = 0;
            while (true)
            {
                ThrowIfDisposed();
                try
                {
                    _logger.Info($"尝试连接 RabbitMQ: {_hostName}:{_port}");
                    var conn = _factory.CreateConnection();
                    conn.ConnectionShutdown += OnConnectionShutdown;
                    var old = Interlocked.Exchange(ref _connection, conn);
                    SafeCloseConnection(old);
                    _logger.Info("成功连接 RabbitMQ");
                    return;
                }
                catch (Exception ex) when (IsTransient(ex))
                {
                    retry++;
                    // 达到上限后抛给调用方:开机走后台重连,重连循环里被捕获后继续退避
                    if (retry >= maxAttempts)
                        throw new Exception($"无法连接 RabbitMQ({_hostName}:{_port}),已重试{retry}次", ex);
                    int delay = Math.Min(retry * 2000, 15000);
                    _logger.Error(ex, $"连接失败,第{retry}次重试,延迟{delay}ms");
                    Thread.Sleep(delay);
                }
            }
        }

        private void OnConnectionShutdown(object sender, ShutdownEventArgs e)
        {
            if (_disposed || _shutdownCts.IsCancellationRequested) return;
            // 客户端主动 Close 会触发 Shutdown, ReplyCode=200 时不重连
            if (e.ReplyCode == 200) return;
            _logger.Warn($"连接断开: {e.ReplyText},启动重连...");
            Task.Run(() => ReconnectLoopAsync());
        }

        // FIX#4: 后台重连无限退避,直到成功或 Dispose;Interlocked 防重入风暴
        private async Task ReconnectLoopAsync()
        {
            if (Interlocked.CompareExchange(ref _isReconnecting, 1, 0) != 0) return;
            try
            {
                int attempt = 0;
                while (!_disposed && !_shutdownCts.IsCancellationRequested)
                {
                    if (_connection != null && _connection.IsOpen) return;
                    attempt++;
                    int delay = Math.Min(5000 + attempt * 2000, 30000);
                    _logger.Info($"等待{delay}ms后第{attempt}次重连...");
                    try { await Task.Delay(delay, _shutdownCts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    if (_disposed || _shutdownCts.IsCancellationRequested) return;
                    try
                    {
                        Connect();
                        StartListening();
                        _logger.Info("重连 RabbitMQ 成功");
                        return;
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex)
                    {
                        _logger.Error(ex, $"第{attempt}次重连失败");
                    }
                }
            }
            finally
            {
                Interlocked.Exchange(ref _isReconnecting, 0);
            }
        }

        private void StartListening()
        {
            ThrowIfDisposed();
            foreach (var kv in _queueConfigs)
            {
                CreateConsumerChannel(kv.QueueName, kv.MaxConcurrent);
            }
        }

        private object GetChannelLock(string queueName)
        {
            return _channelLocks.GetOrAdd(queueName, _ => new object());
        }

        private SemaphoreSlim GetRebuildLock(string queueName)
        {
            return _rebuildLocks.GetOrAdd(queueName, _ => new SemaphoreSlim(1, 1));
        }

        private void CreateConsumerChannel(string queueName, int maxConcurrent)
        {
            ThrowIfDisposed();
            if (_connection == null || !_connection.IsOpen)
                throw new InvalidOperationException("RabbitMQ 连接不可用,无法创建通道");

            if (maxConcurrent <= 0) maxConcurrent = 1;
            // FIX#4: 信号量常驻复用,不在重建/清理时 Dispose,避免在途消息 ObjectDisposedException
            var semaphore = _queueSemaphores.GetOrAdd(queueName, _ => new SemaphoreSlim(maxConcurrent, maxConcurrent));

            var channel = _connection.CreateModel();
            try
            {
                ushort prefetch = (ushort)Math.Min(maxConcurrent, ushort.MaxValue);
                channel.BasicQos(0, prefetch, false);

                try
                {
                    DeclareQueueWithDeadLetter(channel, queueName);
                }
                catch (OperationInterruptedException ex) when (ex.ShutdownReason?.ReplyCode == 406)
                {
                    _logger.Warn($"队列[{queueName}]声明参数冲突: {ex.ShutdownReason?.ReplyText},降级为仅消费模式");
                    CreateConsumerChannelNoDeclare(queueName, maxConcurrent, channel);
                    return;
                }

                var old = _channels.AddOrUpdate(queueName, channel, (_, prev) => { SafeDisposeChannel(queueName, prev); return channel; });

                var consumer = new AsyncEventingBasicConsumer(channel);
                string consumerTag = channel.BasicConsume(queueName, false, consumer);
                _consumerTags[queueName] = consumerTag;

                channel.CallbackException += (s, e) =>
                {
                    _logger.Error(e.Exception, $"队列[{queueName}] 通道异常,将重建通道...");
                    Task.Run(() => RecreateChannel(queueName));
                };

                consumer.Received += (model, ea) => HandleMessageAsync(queueName, consumer, ea);

                _logger.Info($"已开始监听队列: {queueName}, 并发限制={maxConcurrent}");
            }
            catch
            {
                // 若失败的 channel 已被登记(如 BasicConsume 阶段抛错),清掉残留引用,避免指向已 dispose 通道
                try
                {
                    if (_channels.TryGetValue(queueName, out var registered) && ReferenceEquals(registered, channel))
                        _channels.TryRemove(queueName, out _);
                }
                catch { }
                try { channel.Dispose(); } catch { }
                throw;
            }
        }

        private void DeclareQueueWithDeadLetter(IModel channel, string queueName)
        {
            // 共享 DLX + 每队列一个死信队列(routig-key=队列名)。Exchange/QueueDeclare 幂等,多队列重复声明无害。
            string dlq = queueName + ".dead";
            channel.ExchangeDeclare(SharedDlx, ExchangeType.Direct, true);
            channel.QueueDeclare(dlq, true, false, false);
            channel.QueueBind(dlq, SharedDlx, queueName);

            var args = new Dictionary<string, object>
            {
                { "x-dead-letter-exchange", SharedDlx },
                { "x-dead-letter-routing-key", queueName }
            };
            // 注意:若线上队列已存在且无 DLX 参数,此次声明会抛 406 且当前 channel 会被
            // broker 关闭,调用方捕获后会用新通道重试为“仅消费不声明”模式。
            // 根治:在 MQ 服务器上 set_policy 补 dead-letter-exchange(见 SharedDlx 注释),policy
            // 与此处参数等价,不冲突;policy 生效后重启服务即恢复完整死信链路。
            channel.QueueDeclare(queueName, true, false, false, args);
        }

        // 队列已存在但参数冲突(406)时的降级:新建通道,只消费不声明,避免通道被关闭后全崩
        private void CreateConsumerChannelNoDeclare(string queueName, int maxConcurrent, IModel brokenChannel)
        {
            try { brokenChannel?.Dispose(); } catch { }
            var channel = _connection.CreateModel();
            ushort prefetch = (ushort)Math.Min(maxConcurrent, ushort.MaxValue);
            channel.BasicQos(0, prefetch, false);
            _channels.AddOrUpdate(queueName, channel, (_, prev) => { SafeDisposeChannel(queueName, prev); return channel; });

            var consumer = new AsyncEventingBasicConsumer(channel);
            string consumerTag = channel.BasicConsume(queueName, false, consumer);
            _consumerTags[queueName] = consumerTag;

            channel.CallbackException += (s, e) =>
            {
                _logger.Error(e.Exception, $"队列[{queueName}] 通道异常,将重建通道...");
                Task.Run(() => RecreateChannel(queueName));
            };
            consumer.Received += (model, ea) => HandleMessageAsync(queueName, consumer, ea);

            _logger.Warn($"队列[{queueName}] 已存在且参数冲突,已降级为仅消费模式;请在 MQ 服务器执行 rabbitmqctl set_policy bpm-wait-exec-dlx \".*\" '{{\"dead-letter-exchange\":\"bpm.wait-exec.dlx\"}}' --apply-to queues, 然后重启服务。超限毒消息暂直接丢弃。");
        }

        private async Task HandleMessageAsync(string queueName, AsyncEventingBasicConsumer consumer, BasicDeliverEventArgs ea)
        {
            if (!_queueSemaphores.TryGetValue(queueName, out var sem)) return;
            bool acquired = false;
            try
            {
                await sem.WaitAsync(_shutdownCts.Token).ConfigureAwait(false);
                acquired = true;
            }
            catch (OperationCanceledException) { return; }

            // FIX#1: 不再捕获外层 channel 变量,每次从 consumer.Model 取当前通道
            var channel = consumer.Model;
            var channelLock = GetChannelLock(queueName);
            using (var cts = new CancellationTokenSource(_messageTimeout))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, _shutdownCts.Token))
            {
                try
                {
                    var message = Encoding.UTF8.GetString(ea.Body.ToArray());
                    await _handler.HandleAsync(queueName, message, linked.Token).ConfigureAwait(false);

                    lock (channelLock)
                    {
                        if (channel.IsOpen) channel.BasicAck(ea.DeliveryTag, false);
                    }
                }
                catch (OperationCanceledException) when (!_shutdownCts.IsCancellationRequested)
                {
                    // FIX#2: 超时也计入重试次数,而不是无脑 requeue
                    HandleFailure(queueName, channel, channelLock, ea, "处理超时");
                }
                catch (OperationCanceledException)
                {
                    // 服务正在停止:requeue 让别的消费者接手
                    SafeNack(channel, channelLock, ea.DeliveryTag, true);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, $"队列[{queueName}] 消息处理失败, DeliveryTag={ea.DeliveryTag}");
                    HandleFailure(queueName, channel, channelLock, ea, "处理失败");
                }
                finally
                {
                    // Dispose 可能已释放信号量,Release 需容错,避免未观察异常
                    if (acquired) { try { sem.Release(); } catch (ObjectDisposedException) { } catch (SemaphoreFullException) { } }
                }
            }
        }

        private int GetRetryCount(IBasicProperties props)
        {
            try
            {
                if (props?.Headers != null && props.Headers.TryGetValue(RetryCountHeader, out var v))
                {
                    if (v is int i) return i;
                    if (v is long l) return (int)l;
                    if (v is byte[] b && int.TryParse(Encoding.UTF8.GetString(b), out var n)) return n;
                    if (int.TryParse(v.ToString(), out var n2)) return n2;
                }
                // 兼容原生 x-death(队列已配 DLX 但 terrestrial 重声明失败的老队列)
                if (props?.Headers != null && props.Headers.TryGetValue("x-death", out var death)
                    && death is System.Collections.IList list && list.Count > 0
                    && list[0] is Dictionary<string, object> d
                    && d.TryGetValue("count", out var c)) return Convert.ToInt32(c);
            }
            catch { }
            return 0;
        }

        // FIX#2: 有限重试——重发时 retry-count+1 并 Ack 原消息;超限则 requeue:false 进死信/丢弃
        private void HandleFailure(string queueName, IModel channel, object channelLock, BasicDeliverEventArgs ea, string reason)
        {
            int retry = GetRetryCount(ea.BasicProperties);
            if (retry >= MaxRedeliveries)
            {
                SafeNack(channel, channelLock, ea.DeliveryTag, false);
                _logger.Error($"队列[{queueName}] 消息{reason}已达{MaxRedeliveries}次,已转死信/丢弃, DeliveryTag={ea.DeliveryTag}");
                return;
            }
            try
            {
                var props = channel.CreateBasicProperties();
                props.Persistent = true;
                props.ContentType = ea.BasicProperties?.ContentType;
                props.ContentEncoding = ea.BasicProperties?.ContentEncoding;
                props.MessageId = ea.BasicProperties?.MessageId;
                props.CorrelationId = ea.BasicProperties?.CorrelationId;
                // 保留原始 headers,再叠加重试计数,避免丢失上游自定义头
                var headers = new Dictionary<string, object>();
                if (ea.BasicProperties?.Headers != null)
                {
                    foreach (var kv in ea.BasicProperties.Headers)
                        headers[kv.Key] = kv.Value;
                }
                headers[RetryCountHeader] = retry + 1;
                props.Headers = headers;
                lock (channelLock)
                {
                    if (!channel.IsOpen) return;
                    channel.BasicPublish("", queueName, props, ea.Body.ToArray());
                    channel.BasicAck(ea.DeliveryTag, false);
                }
                _logger.Warn($"队列[{queueName}] 消息{reason},第{retry + 1}次重试, DeliveryTag={ea.DeliveryTag}");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"队列[{queueName}] 重试重发失败,回退为 requeue");
                SafeNack(channel, channelLock, ea.DeliveryTag, true);
            }
        }

        private void SafeNack(IModel channel, object channelLock, ulong deliveryTag, bool requeue)
        {
            try
            {
                lock (channelLock)
                {
                    if (channel.IsOpen) channel.BasicNack(deliveryTag, false, requeue);
                }
            }
            catch (Exception ex) { _logger.Error(ex, "BasicNack 失败"); }
        }

        private void RecreateChannel(string queueName)
        {
            var rebuildLock = GetRebuildLock(queueName);
            if (!rebuildLock.Wait(0)) return; // 已有重建在进行
            try
            {
                if (_disposed || _shutdownCts.IsCancellationRequested) return;
                var config = _queueConfigs.FirstOrDefault(q => q.QueueName == queueName);
                if (config == null) { _logger.Warn($"队列 {queueName} 配置已不存在,跳过重建"); return; }
                if (_connection == null || !_connection.IsOpen) { Task.Run(() => ReconnectLoopAsync()); return; }

                _logger.Info($"正在为队列[{queueName}] 重建通道...");
                // 关闭旧通道(加通道锁,避免与 Ack 并发)
                if (_channels.TryGetValue(queueName, out var oldChannel))
                {
                    var l = GetChannelLock(queueName);
                    lock (l)
                    {
                        try
                        {
                            if (_consumerTags.TryRemove(queueName, out string tag) && oldChannel.IsOpen)
                                oldChannel.BasicCancel(tag);
                        }
                        catch { }
                        SafeDisposeChannel(queueName, oldChannel);
                    }
                    _channels.TryRemove(queueName, out _);
                }
                CreateConsumerChannel(queueName, config.MaxConcurrent);
                _logger.Info($"队列[{queueName}] 通道重建完成");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"重建队列[{queueName}] 通道失败");
                Task.Run(() => ReconnectLoopAsync());
            }
            finally { rebuildLock.Release(); }
        }

        private void SafeDisposeChannel(string queueName, IModel channel)
        {
            try
            {
                if (_consumerTags.TryGetValue(queueName, out var tag) && channel.IsOpen)
                { try { channel.BasicCancel(tag); } catch { } }
            }
            catch { }
            try { if (channel.IsOpen) channel.Close(); } catch { }
            try { channel.Dispose(); } catch { }
        }

        private void SafeCloseConnection(IConnection conn)
        {
            if (conn == null) return;
            try { conn.ConnectionShutdown -= OnConnectionShutdown; } catch { }
            try { if (conn.IsOpen) conn.Close(); } catch { }
            try { conn.Dispose(); } catch { }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(RabbitMQConsumer));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _shutdownCts.Cancel(); } catch { }

            foreach (var kv in _channels.ToList())
            {
                // 与在途 BasicAck/BasicNack 共用同一把锁,避免 Close 与 Ack 帧交错
                var l = GetChannelLock(kv.Key);
                lock (l)
                {
                    try
                    {
                        if (_consumerTags.TryRemove(kv.Key, out string tag) && kv.Value.IsOpen)
                            kv.Value.BasicCancel(tag);
                        if (kv.Value.IsOpen) kv.Value.Close();
                        kv.Value.Dispose();
                    }
                    catch { }
                    finally { _channels.TryRemove(kv.Key, out _); }
                }
            }
            foreach (var kv in _queueSemaphores.ToList())
            {
                try { kv.Value.Dispose(); } catch { }
                _queueSemaphores.TryRemove(kv.Key, out _);
            }
            foreach (var kv in _rebuildLocks.ToList()) { try { kv.Value.Dispose(); } catch { } }

            if (_connection != null)
            {
                SafeCloseConnection(_connection);
                _connection = null;
            }
            try { _shutdownCts.Dispose(); } catch { }
        }
    }
}
