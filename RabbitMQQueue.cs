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
        /// 获取队列配置（名称+并发数）
        /// </summary>
        private List<QueueConfig> GetQueueConfigs(string config)
        {
            if (string.IsNullOrEmpty(config))
            {
                _logger.Error("SqlServer_Config 配置项未设置");
                return new List<QueueConfig>();
            }
            _logger.Info($"SqlServer_Config: {config}");

            const string sql = @"SELECT QueueName, 5 AS MaxConcurrent
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
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "获取队列配置异常");
                return new List<QueueConfig>();
            }
        }

        public void Start()
        {
            try
            {
                string config = ConfigurationManager.AppSettings["SqlServer_Config"];
                string hostName = ConfigurationManager.AppSettings["RabbitMQ_HostName"];
                string userName = ConfigurationManager.AppSettings["RabbitMQ_UserName"];
                string password = ConfigurationManager.AppSettings["RabbitMQ_Password"];
                var queueConfigs = GetQueueConfigs(config);
                if (queueConfigs.Count == 0)
                {
                    _logger.Error($"未查询到队列配置");
                    return;
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
                _logger.Info("服务启动成功...");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "服务启动异常");
            }
        }

        public void Stop()
        {
            try
            {
                _rabbitMqConsumer?.Dispose();
                _rabbitMqConsumer = null;
                _logger.Info("服务停止成功...");
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
