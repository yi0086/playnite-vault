using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;

namespace VaultDemo.Net
{
    /// <summary>请求走不走系统代理。内网 NAS 必须直连，访问 GitHub 往往正好相反。</summary>
    public enum ProxyMode
    {
        /// <summary>强制直连（<c>request.Proxy = null</c>）。</summary>
        Direct = 0,

        /// <summary>用 IE/系统里配的代理（Clash 等）。</summary>
        System = 1
    }

    /// <summary>一次探测的结果（用来选镜像）。</summary>
    public class HttpProbe
    {
        public bool Ok { get; set; }
        public int LatencyMs { get; set; }
        public string Error { get; set; }
    }

    /// <summary>
    /// 更新器专用的极简 HTTP 客户端。
    ///
    /// 为什么不用 <see cref="WebDavClient"/>：那个是 WebDAV 语义（PROPFIND / MKCOL / 逐段转义），
    /// 而这里要的是「取一个 JSON」「下一个 zip」，还要能**按代理模式分别探测**——
    /// 这恰恰是「国内/国外自动切换镜像」的判据。
    ///
    /// net462 上不引第三方 HTTP 库，继续用 <see cref="HttpWebRequest"/>。
    /// </summary>
    public static class HttpFetch
    {
        private const int BufferSize = 64 * 1024;

        /// <summary>GitHub API 强制要求带 UA，没有就 403。</summary>
        public const string UserAgent = "VaultDemo-Playnite-Plugin";

        static HttpFetch()
        {
            try
            {
                // net462 默认可能只协商 TLS 1.0，连 GitHub / Gitee 都会直接失败
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11;
                if (ServicePointManager.DefaultConnectionLimit < 8)
                {
                    ServicePointManager.DefaultConnectionLimit = 8;
                }
            }
            catch
            {
                // 静态初始化失败不该拖垮插件
            }
        }

        /// <summary>
        /// 系统里到底配没配代理。<see cref="WebRequest.DefaultWebProxy"/> 为 null 时不算数，
        /// 要用 <see cref="WebRequest.GetSystemWebProxy"/> + <c>GetProxy</c> 才能问出来。
        /// </summary>
        public static bool SystemProxyConfigured()
        {
            try
            {
                var proxy = WebRequest.GetSystemWebProxy();
                if (proxy == null)
                {
                    return false;
                }

                var probe = proxy.GetProxy(new Uri("https://api.github.com/"));
                // 没配代理时 GetProxy 原样返回目标地址
                return probe != null
                    && !string.Equals(probe.Host, "api.github.com", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>系统代理的地址描述，日志/界面里给用户看。</summary>
        public static string DescribeSystemProxy()
        {
            try
            {
                var proxy = WebRequest.GetSystemWebProxy();
                if (proxy == null)
                {
                    return "(未配置)";
                }
                var probe = proxy.GetProxy(new Uri("https://api.github.com/"));
                return probe == null ? "(未配置)" : probe.ToString();
            }
            catch
            {
                return "(读取失败)";
            }
        }

        public static HttpWebRequest CreateRequest(string url, ProxyMode mode, int timeoutMs)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.UserAgent = UserAgent;
            request.AllowAutoRedirect = true;
            request.MaximumAutomaticRedirections = 5;
            request.Timeout = Math.Max(1000, timeoutMs);
            request.ReadWriteTimeout = Math.Max(1000, timeoutMs);
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            request.KeepAlive = false;

            // 注意：Expect100Continue 不在 HttpWebRequest 上，它是 ServicePoint 的属性。
            // 已经在静态构造里用 ServicePointManager.Expect100Continue = false 全局关掉了。
            ServicePointManager.Expect100Continue = false;

            // 【关键】内网 / 直连模式必须显式置 null。
            // HttpWebRequest 默认会读系统代理，Clash 没开时请求会被打到 127.0.0.1:7897 直接失败。
            // 反过来，访问 GitHub 时走系统代理往往才是通的，所以这里由调用方决定。
            request.Proxy = mode == ProxyMode.Direct ? null : WebRequest.GetSystemWebProxy();

            return request;
        }

        /// <summary>
        /// GET 一个文本资源（JSON）。永远不抛：失败时返回 null，原因写进 <paramref name="error"/>。
        /// </summary>
        public static string GetString(string url, ProxyMode mode, int timeoutMs,
            out int latencyMs, out string error)
        {
            latencyMs = 0;
            error = null;
            var watch = Stopwatch.StartNew();

            try
            {
                var request = CreateRequest(url, mode, timeoutMs);
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var stream = response.GetResponseStream())
                {
                    if (stream == null)
                    {
                        error = "响应没有 body";
                        return null;
                    }

                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        var text = reader.ReadToEnd();
                        latencyMs = (int)watch.ElapsedMilliseconds;
                        return text;
                    }
                }
            }
            catch (WebException ex)
            {
                error = Describe(ex, url);
                return null;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
        }

        /// <summary>
        /// 下一个文件到本地。先写 <c>.part</c> 再改名 —— 半截文件绝不能被当成完整文件。
        ///
        /// <paramref name="onProgress"/> 返回 false 表示**主动放弃**（例如镜像太慢要换源），
        /// 此时方法返回 false 且 error 为 "已放弃"。
        /// </summary>
        public static bool DownloadToFile(string url, string destPath, ProxyMode mode,
            int connectTimeoutMs, int stallMs, long expectedBytes,
            Func<long, long, bool> onProgress, out string error)
        {
            error = null;
            var partPath = destPath + ".part";
            var watch = Stopwatch.StartNew();

            try
            {
                var request = CreateRequest(url, mode, connectTimeoutMs);
                request.ReadWriteTimeout = Math.Max(1000, stallMs);

                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    var total = response.ContentLength > 0 ? response.ContentLength : expectedBytes;

                    var dir = Path.GetDirectoryName(destPath);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    long done = 0;
                    var buffer = new byte[BufferSize];

                    using (var source = response.GetResponseStream())
                    using (var target = new FileStream(partPath, FileMode.Create, FileAccess.Write,
                               FileShare.None, BufferSize))
                    {
                        if (source == null)
                        {
                            error = "响应没有 body";
                            return false;
                        }

                        int read;
                        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            target.Write(buffer, 0, read);
                            done += read;

                            if (onProgress != null && !onProgress(done, total))
                            {
                                target.Close();
                                TryDelete(partPath);
                                error = "已放弃";
                                return false;
                            }
                        }

                        target.Flush(true);
                    }

                    if (done == 0)
                    {
                        TryDelete(partPath);
                        error = "下载到 0 字节（" + watch.ElapsedMilliseconds + " ms）";
                        return false;
                    }
                }

                TryDelete(destPath);
                File.Move(partPath, destPath);
                return true;
            }
            catch (WebException ex)
            {
                error = Describe(ex, url);
                TryDelete(partPath);
                return false;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                TryDelete(partPath);
                return false;
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // 清理失败无所谓，下次会覆盖
            }
        }

        /// <summary>把 WebException 翻译成「用户能照做」的一句话。</summary>
        public static string Describe(WebException ex, string url)
        {
            var host = "(未知主机)";
            try
            {
                host = new Uri(url).Host;
            }
            catch
            {
                // 保留兜底值
            }

            if (ex.Status == WebExceptionStatus.Timeout)
            {
                return "连接 " + host + " 超时（网络不可达或被墙）";
            }

            if (ex.Response is HttpWebResponse)
            {
                var response = (HttpWebResponse)ex.Response;
                var code = (int)response.StatusCode;

                if (code == 404)
                {
                    return host + " 返回 404 —— 还没发布过 release，或者仓库是私有的";
                }
                if (code == 403 || code == 429)
                {
                    return host + " 返回 " + code + " —— 触发限流，请稍后再试"
                         + (host.IndexOf("github", StringComparison.OrdinalIgnoreCase) >= 0
                             ? "（GitHub 未登录的 API 限流是每小时 60 次）"
                             : string.Empty);
                }
                return host + " 返回 HTTP " + code;
            }

            switch (ex.Status)
            {
                case WebExceptionStatus.NameResolutionFailure:
                    return "解析不了 " + host + " 的域名（DNS 失败）";
                case WebExceptionStatus.ConnectFailure:
                    return "连不上 " + host + "（端口被拦 / 代理没开）";
                case WebExceptionStatus.TrustFailure:
                case WebExceptionStatus.SecureChannelFailure:
                    return "与 " + host + " 的 TLS 握手失败";
                default:
                    return host + " 请求失败：" + ex.Status;
            }
        }
    }
}
