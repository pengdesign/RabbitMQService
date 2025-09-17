using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Text;

namespace RabbitMQService
{
    public static class HttpHelper
    {

        //其中参数：postData 为提交参数
        // 形如：pastData=“username=aaa&userpwd=bbb”； 
        // posturl 为提交事件指定的路径
        public static string GetPage(string posturl, string postData)
        {
            Stream outstream = null;
            Stream instream = null;
            StreamReader sr = null;
            HttpWebResponse response = null;
            HttpWebRequest request = null;
            Encoding encoding = System.Text.Encoding.GetEncoding("utf-8");
            byte[] data = encoding.GetBytes(postData);
            try
            {
                // 设置参数
                request = WebRequest.Create(posturl) as HttpWebRequest;
                CookieContainer cookieContainer = new CookieContainer();
                request.CookieContainer = cookieContainer;
                request.AllowAutoRedirect = true;
                request.Method = "POST";
                request.ContentType = "application/json";
                request.ContentLength = data.Length;
                outstream = request.GetRequestStream();
                outstream.Write(data, 0, data.Length);
                outstream.Close();
                //发送请求并获取相应回应数据
                response = request.GetResponse() as HttpWebResponse;
                //直到request.GetResponse()程序才开始向目标网页发送Post请求
                instream = response.GetResponseStream();
                sr = new StreamReader(instream, encoding);
                string content = sr.ReadToEnd();
                string err = string.Empty;
                return content;
            }
            catch //(Exception ex)
            {

                return string.Empty;
            }
        }
      
        /// <summary>
        /// POST请求
        /// </summary>
        /// <param name="url"></param>
        /// <param name="parameters"></param>
        /// <param name="charset"></param>
        /// <returns></returns>
        public static HttpWebResponse CreatePostHttpResponse(string url, IDictionary<string, string> parameters, Encoding charset)
        {
            HttpWebRequest request = null;
            request = WebRequest.Create(url) as HttpWebRequest;
            request.ProtocolVersion = HttpVersion.Version10;
            request.Method = "POST";
            request.ContentType = "application/x-www-form-urlencoded;charset=utf8";

            if (!(parameters == null || parameters.Count == 0))
            {
                StringBuilder buffer = new StringBuilder();
                int i = 0;
                foreach (string key in parameters.Keys)
                {
                    if (i > 0)
                    {
                        buffer.AppendFormat("&{0}={1}", key, parameters[key]);
                    }
                    else
                    {
                        buffer.AppendFormat("{0}={1}", key, parameters[key]);
                    }
                    i++;
                }
                byte[] data = Encoding.UTF8.GetBytes(buffer.ToString());
                using (Stream stream = request.GetRequestStream())
                {
                    stream.Write(data, 0, data.Length);
                }
            }
            return request.GetResponse() as HttpWebResponse;
        }

        public static void SetFile(string msg)
        {
            string fileName = "AF" + DateTime.Now.ToString("yyyy-MM-dd");
            string filePath = AppDomain.CurrentDomain.BaseDirectory;
            if (Directory.Exists(filePath) == false)
            {
                Directory.CreateDirectory(filePath);
            }
            string fileAbstractPath = filePath + "\\" + fileName + ".txt";
            FileStream fs = new FileStream(fileAbstractPath, FileMode.Append);
            StreamWriter sw = new StreamWriter(fs);
            //开始写入     
            string time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            msg = time + "，" + msg + System.Environment.NewLine;

            sw.Write(msg);
            //清空缓冲区               
            sw.Flush();
            //关闭流               
            sw.Close();
            sw.Dispose();
            fs.Close();
            fs.Dispose();
        }




        public static string Get(string url)
        {
            return Get(url, Encoding.UTF8);
        }
        /// <summary>
        /// GET请求
        /// </summary>
        /// <param name="url"></param>
        /// <param name="encoding"></param>
        /// <returns></returns>
        public static string Get(string url, Encoding encoding)
        {
            try
            {
                var wc = new WebClient { Encoding = encoding };
                var readStream = wc.OpenRead(url);
                using (var sr = new StreamReader(readStream, encoding))
                {
                    var result = sr.ReadToEnd();
                    return result;
                }
            }
            catch (Exception e)
            {
                return e.Message;
            }
        }

        public static string Post(string url, string paramData)
        {
            return Post(url, paramData, Encoding.UTF8);
        }

        public static string Post(string url, string paramData, Encoding encoding)
        {
            string result;

            if (url.ToLower().IndexOf("https", System.StringComparison.Ordinal) > -1)
            {
                ServicePointManager.ServerCertificateValidationCallback =
                               new RemoteCertificateValidationCallback((sender, certificate, chain, errors) => { return true; });
            }

            try
            {
                var wc = new WebClient();
                if (string.IsNullOrEmpty(wc.Headers["Content-Type"]))
                    wc.Headers.Add("Content-Type", "application/json");
                wc.Encoding = encoding;
                result = wc.UploadString(url, "POST", paramData);
            }
            catch (Exception e)
            {
                result = e.Message + ">" + url + ">" + paramData;
            }

            return result;
        }

    }
}
