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

        // 幂等写入:MQ 红eliver/超时重试会导致同一 InstanceId 重复投递,
        // 以 task_id 去重,重复投递视为成功(直接 Ack),避免下游 BPM 重复执行。
        // 前提:tb_task_bpm_wait_exec.task_id 需有唯一约束/唯一索引:
        //   CREATE UNIQUE INDEX UX_tb_task_bpm_wait_exec_task_id ON tb_task_bpm_wait_exec(task_id);
        const string sql = @"
                IF NOT EXISTS (SELECT 1 FROM [tb_task_bpm_wait_exec] WHERE [task_id] = @InstanceId)
                BEGIN
                    INSERT INTO [tb_task_bpm_wait_exec]
                    (
                        task_name,
                        task_id,
                        task_result,
                        task_is_complete
                    )
                    VALUES
                    (@QueueName, @InstanceId, @BpmType, @TaskIsComplete)
                END
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
                if (rows == 0)
                    _logger.Info($"消息重复投递已去重: InstanceId={modelMessage.InstanceId}, 队列={queueName}");
                else
                    _logger.Info($"消息入库成功: InstanceId={modelMessage.InstanceId}, 队列={queueName}");
            }
        }
        catch (OperationCanceledException)
        {
            _logger.Warn($"消息处理被取消: InstanceId={modelMessage.InstanceId}, 队列={queueName}");
            throw;
        }
        catch (SqlException ex) when (ex.Number == 2627 || ex.Number == 2601)
        {
            // 唯一索引兜底:高并发下 IF NOT EXISTS 仍可能撞唯一键,视为重复投递成功,保证 Ack 不进死循环
            _logger.Info($"消息重复投递已去重(唯一键冲突): InstanceId={modelMessage.InstanceId}, 队列={queueName}");
        }
        catch (Exception ex)
        {
            _logger.Error(ex, $"处理消息异常，队列: {queueName}, 内容: {message}");
            throw;
        }
    }
}
