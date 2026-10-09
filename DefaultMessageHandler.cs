using Dapper;
using Newtonsoft.Json;
using NLog;
using RabbitMQService;
using System;
using System.Data.SqlClient;
using System.Threading;
using System.Threading.Tasks;

internal class DefaultMessageHandler : IMessageHandler
{
    private readonly Logger _logger;
    private readonly string _config;

    public DefaultMessageHandler(string config, Logger logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _config = config ?? throw new ArgumentNullException(nameof(config));
    }

    public async Task HandleAsync(string queueName, string message, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            _logger.Warn($"接收到空消息，队列: {queueName}");
            return;
        }

        Message modelMessage;
        try
        {
            modelMessage = JsonConvert.DeserializeObject<Message>(message);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"消息反序列化失败，队列: {queueName}, 内容: {message}");
            return;
        }

        if (modelMessage == null || string.IsNullOrEmpty(modelMessage.InstanceId))
        {
            _logger.Warn($"消息缺少 InstanceId，忽略。队列: {queueName}, 内容: {message}");
            return;
        }

        // 业务要求:每次投递都必须插入一行。同一个 InstanceId 在其生命周期内会产生
        // 多条状态事件(办结/驳回/待办等),task_id 非唯一,禁止按 task_id 去重。
        // 毒消息防护不在 DB 层做,由 RabbitMQConsumer 的有限重试(5 次后进死信)兜底。
        const string sql = @"
                INSERT INTO [tb_task_bpm_wait_exec]
                (
                    task_name,
                    task_id,
                    task_result,
                    task_is_complete
                )
                VALUES
                (@QueueName, @InstanceId, @BpmType, @TaskIsComplete)
        ";

        try
        {
            using (var connection = new SqlConnection(_config))
            {
                await connection.OpenAsync(token);
                int rows = await connection.ExecuteAsync(sql, new
                {
                    QueueName = queueName,
                    InstanceId = modelMessage.InstanceId,
                    BpmType = modelMessage.BpmType,
                    TaskIsComplete = 0
                });
                _logger.Info($"消息入库成功: InstanceId={modelMessage.InstanceId}, 队列={queueName}, 影响行数={rows}");
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Warn($"消息处理被取消: InstanceId={modelMessage.InstanceId}, 队列={queueName}");
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"处理消息异常，队列: {queueName}, 内容: {message}");
            throw;
        }
    }
}
