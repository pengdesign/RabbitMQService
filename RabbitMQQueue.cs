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
        private readonly Logger logger = LogManager.GetCurrentClassLogger();
        private RabbitMQConsumer rabbitMqConsumer;
      
        /// <summary>
        /// 获取队列配置（名称+并发数）
        /// </summary>
        private List<QueueConfig> GetQueueConfigs(string config)
        {
            if (string.IsNullOrEmpty(config))
            {
                logger.Error("SqlServer_Config 配置项未设置");
                return new List<QueueConfig>();
            }
            logger.Info($"SqlServer_Config: {config}");

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
                logger.Error(ex, "获取队列配置异常");
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
                    logger.Error("未查询到队列配置");
                    return;
                }
                rabbitMqConsumer = new RabbitMQConsumer(
                    logger,
                    new DefaultMessageHandler(config, logger),
                    queueConfigs,
                    hostName,
                    userName,
                    password
                );

                rabbitMqConsumer.Start();
                logger.Info("服务启动成功...");
            }
            catch (Exception ex)
            {
                logger.Error(ex, "服务启动异常");
            }
        }

        public void Stop()
        {
            try
            {
                rabbitMqConsumer?.Dispose();
                rabbitMqConsumer = null;
                logger.Info("服务停止成功...");
            }
            catch (Exception ex)
            {
                logger.Error(ex, "服务停止异常");
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
