namespace RabbitMQService
{
    public class Message
    {
        /// <summary>
        /// 
        /// </summary>
        public string InstanceId { get; set; }
        /// <summary>
        /// 
        /// </summary>
        public string Table { get; set; }
        /// <summary>
        /// 完成
        /// </summary>
        public string BpmType { get; set; }
        /// <summary>
        /// 
        /// </summary>
        public string BussType { get; set; }
    }

    /// <summary>
    /// 队列配置
    /// </summary>
    public class QueueConfig
    {
        public string QueueName { get; set; }
        public int MaxConcurrent { get; set; }
    }
}
