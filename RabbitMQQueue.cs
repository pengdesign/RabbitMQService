using Dapper;
using NLog;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data.SqlClient;
using System.Linq;

namespace RabbitMQService
{
    public class RabbitMQQueue : IDisposable
    {
        private readonly Logger _logger = LogManager.GetCurrentClassLogger();
        private RabbitMQConsumer _rabbitMqConsumer;

        /// <summary>
        /// 从数据库读取队列配置(队列名+并发数)
        /// </summary>
        private List<QueueConfig> GetQueueConfigs(string config)
        {
            if (string.IsNullOrEmpty(config))
            {
                _logger.Error("SqlServer_Config 连接字符串为空");
                return new List<QueueConfig>();
            }
            _logger.Info($"SqlServer_Config: {MaskConnectionString(config)}");

            // 并发数由 DB 配置列驱动,非法值回落到 5,避免写死的 5 导致改配置无效
            const string sql = @"SELECT QueueName, ISNULL(MaxConcurrent, 5) AS MaxConcurrent
                                 FROM dbo.Tb_BPM_QueueSet";

            try
            {
                using (var conn = new SqlConnection(config))
                {
                    conn.Open();
                    var list = conn.Query<QueueConfig>(sql).ToList();

                    return list
                        .Where(q => !string.IsNullOrWhiteSpace(q.QueueName))
                        .GroupBy(q => q.QueueName)
                        .Select(g => g.First())
                        .Select(q => new QueueConfig
                        {
                            QueueName = q.QueueName,
                            // 钳位 1~64:防 DB 配 0/-1 导致消费停滞,防配过大打爆 DB
                            MaxConcurrent = q.MaxConcurrent <= 0 ? 5 : Math.Min(q.MaxConcurrent, 64)
                        })
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "读取队列配置失败");
                return new List<QueueConfig>();
            }
        }

        // 连接串脱敏:日志只留 data source/database/user id,去掉 PWD,防密码进日志文件
        private static string MaskConnectionString(string config)
        {
            if (string.IsNullOrEmpty(config)) return string.Empty;
            try
            {
                var builder = new SqlConnectionStringBuilder(config);
                string pwd = builder.Password;
                if (!string.IsNullOrEmpty(pwd))
                    builder.Password = "******";
                return builder.ToString();
            }
            catch
            {
                return "***";
            }
        }

        // 环境变量优先于 App.config,便于容器/保密配置下让明文密码不落盘
        private static string GetSetting(string key)
        {
            string env = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(env)) return env;
            return ConfigurationManager.AppSettings[key];
        }

        public void Start()
        {
            try
            {
                string config = GetSetting("SqlServer_Config");
                string hostName = GetSetting("RabbitMQ_HostName");
                string userName = GetSetting("RabbitMQ_UserName");
                string password = GetSetting("RabbitMQ_Password");
                if (string.IsNullOrWhiteSpace(config)) throw new InvalidOperationException("缺少配置 SqlServer_Config");
                if (string.IsNullOrWhiteSpace(hostName)) throw new InvalidOperationException("缺少配置 RabbitMQ_HostName");
                var queueConfigs = GetQueueConfigs(config);
                if (queueConfigs.Count == 0)
                {
                    _logger.Error("未查询到队列配置,不启动监听");
                    throw new InvalidOperationException("未查询到队列配置,不启动监听");
                }
                _rabbitMqConsumer = new RabbitMQConsumer(
                    _logger,
                    new DefaultMessageHandler(config, _logger),
                    queueConfigs,
                    hostName,
                    userName,
                    password
                );

                _rabbitMqConsumer.Start();
                _logger.Info("服务启动成功,开始监听...");
            }
            catch (Exception ex)
            {
                // 启动失败必须抛给 Topshelf,不吞异常,否则服务显示启动成功但实际没在监听
                _logger.Error(ex, "服务启动异常");
                throw;
            }
        }

        public void Stop()
        {
            try
            {
                _rabbitMqConsumer?.Dispose();
                _rabbitMqConsumer = null;
                _logger.Info("服务已停止...");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "服务停止异常");
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
