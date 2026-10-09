using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

namespace RabbitMQService
{
    /// <summary>
    /// 注意:此类当前无生产调用,保留仅作工具备用。
    /// 与旧版的差异:资源全部释放、有超时、参数 UrlEncode、不再全局关闭证书校验、
    /// 失败抛异常而非返回错误字符串(调用方可区分成功/失败)。
    /// </summary>
    public static class HttpHelper
    {
        private const int DefaultTimeoutMs = 30000;
        private static readonly object _fileLock = new object();

        // postData 为 JSON 字符串,posturl 为目标地址
        public static string GetPage(string posturl, string postData)
        {
            return GetPage(posturl, postData, DefaultTimeoutMs);
        }

        public static string GetPage(string posturl, string postData, int timeoutMs)
        {
            if (string.IsNullOrEmpty(posturl)) throw new ArgumentNullException(nameof(posturl));
            byte[] data = Encoding.UTF8.GetBytes(postData ?? string.Empty);
            var request = WebRequest.Create(posturl) as HttpWebRequest;
            request.Method = "POST";
            request.ContentType = "application/json";
            request.ContentLength = data.Length;
            request.Timeout = timeoutMs;
            request.ReadWriteTimeout = timeoutMs;
            request.AllowAutoRedirect = true;

            using (Stream outstream = request.GetRequestStream())
            {
                outstream.Write(data, 0, data.Length);
            }
            using (var response = request.GetResponse() as HttpWebResponse)
            using (var instream = response.GetResponseStream())
            using (var sr = new StreamReader(instream, Encoding.UTF8))
            {
                return sr.ReadToEnd();
            }
        }

        /// <summary>
        /// POST请求(表单)
        /// </summary>
        public static HttpWebResponse CreatePostHttpResponse(string url, IDictionary<string, string> parameters, Encoding charset)
        {
            return CreatePostHttpResponse(url, parameters, charset, DefaultTimeoutMs);
        }

        public static HttpWebResponse CreatePostHttpResponse(string url, IDictionary<string, string> parameters, Encoding charset, int timeoutMs)
        {
            if (string.IsNullOrEmpty(url)) throw new ArgumentNullException(nameof(url));
            if (charset == null) charset = Encoding.UTF8;
            var request = WebRequest.Create(url) as HttpWebRequest;
            request.ProtocolVersion = HttpVersion.Version11;
            request.Method = "POST";
            request.ContentType = "application/x-www-form-urlencoded;charset=utf-8";
            request.Timeout = timeoutMs;
            request.ReadWriteTimeout = timeoutMs;

            if (!(parameters == null || parameters.Count == 0))
            {
                var buffer = new StringBuilder();
                int i = 0;
                foreach (string key in parameters.Keys)
                {
                    if (i > 0) buffer.Append('&');
                    // 参数必须 UrlEncode,否则中文/&/= 会破坏报文
                    buffer.Append(Uri.EscapeDataString(key)).Append('=').Append(Uri.EscapeDataString(parameters[key] ?? string.Empty));
                    i++;
                }
                byte[] data = charset.GetBytes(buffer.ToString());
                request.ContentLength = data.Length;
                using (Stream stream = request.GetRequestStream())
                {
                    stream.Write(data, 0, data.Length);
                }
            }
            else
            {
                request.ContentLength = 0;
            }
            // 调用方负责 Dispose 返回的 response
            return request.GetResponse() as HttpWebResponse;
        }

        public static void SetFile(string msg)
        {
            string fileName = "AF" + DateTime.Now.ToString("yyyy-MM-dd") + ".txt";
            string filePath = AppDomain.CurrentDomain.BaseDirectory;
            string fileAbstractPath = Path.Combine(filePath, fileName);
            string time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            string line = time + "，" + msg + Environment.NewLine;
            // 加锁串行化并发写,避免多线程 IOException;File.AppendAllText 内部即 using 释放
            lock (_fileLock)
            {
                File.AppendAllText(fileAbstractPath, line, Encoding.UTF8);
            }
        }

        public static string Get(string url)
        {
            return Get(url, Encoding.UTF8);
        }

        /// <summary>
        /// GET请求,失败抛异常(不再返回 e.Message 冒充成功)
        /// </summary>
        public static string Get(string url, Encoding encoding)
        {
            return Get(url, encoding, DefaultTimeoutMs);
        }

        public static string Get(string url, Encoding encoding, int timeoutMs)
        {
            if (string.IsNullOrEmpty(url)) throw new ArgumentNullException(nameof(url));
            if (encoding == null) encoding = Encoding.UTF8;
            var request = WebRequest.Create(url) as HttpWebRequest;
            request.Method = "GET";
            request.Timeout = timeoutMs;
            request.ReadWriteTimeout = timeoutMs;
            using (var response = request.GetResponse() as HttpWebResponse)
            using (var stream = response.GetResponseStream())
            using (var sr = new StreamReader(stream, encoding))
            {
                return sr.ReadToEnd();
            }
        }

        public static string Post(string url, string paramData)
        {
            return Post(url, paramData, Encoding.UTF8);
        }

        /// <summary>
        /// POST JSON,失败抛异常;不再全局禁用证书校验
        /// </summary>
        public static string Post(string url, string paramData, Encoding encoding)
        {
            return Post(url, paramData, encoding, DefaultTimeoutMs);
        }

        public static string Post(string url, string paramData, Encoding encoding, int timeoutMs)
        {
            if (string.IsNullOrEmpty(url)) throw new ArgumentNullException(nameof(url));
            if (encoding == null) encoding = Encoding.UTF8;
            byte[] data = encoding.GetBytes(paramData ?? string.Empty);
            var request = WebRequest.Create(url) as HttpWebRequest;
            request.Method = "POST";
            request.ContentType = "application/json";
            request.ContentLength = data.Length;
            request.Timeout = timeoutMs;
            request.ReadWriteTimeout = timeoutMs;
            using (Stream stream = request.GetRequestStream())
            {
                stream.Write(data, 0, data.Length);
            }
            using (var response = request.GetResponse() as HttpWebResponse)
            using (var respStream = response.GetResponseStream())
            using (var sr = new StreamReader(respStream, encoding))
            {
                return sr.ReadToEnd();
            }
        }
    }
}
