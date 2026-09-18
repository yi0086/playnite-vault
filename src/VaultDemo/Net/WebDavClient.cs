using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using VaultDemo.Models;
using VaultDemo.Services;

namespace VaultDemo.Net
{
    /// <summary>PROPFIND 列出来的一个条目。</summary>
    public class WebDavEntry
    {
        public string Name { get; set; }
        public string Href { get; set; }
        public bool IsCollection { get; set; }
        public long Length { get; set; }
    }

    /// <summary>
    /// 极简 WebDAV 客户端，只依赖 System.Net，避免引入第三方库。
    /// 支持：HEAD 探测 / GET 下载 / PUT 上传 / MKCOL 建目录 / PROPFIND 连通性测试。
    /// </summary>
    public class WebDavClient
    {
        private const int BufferSize = 128 * 1024;

        private readonly string baseUrl;
        private readonly string credential;
        private readonly int timeoutMs;
        private readonly bool useSystemProxy;

        static WebDavClient()
        {
            try
            {
                // HttpWebRequest 默认每个 endpoint 只允许 2 条并发连接。
                // 分片并发下载如果不放开这个值，会被 ServicePoint 静默串行化，
                // 表面上看「开了多线程」但速度毫无变化。
                // 取 32：要盖过设置界面允许的最大并发路数（16），留足余量，
                // 免得用户把并发调上去却仍被这里卡住。
                if (ServicePointManager.DefaultConnectionLimit < 32)
                {
                    ServicePointManager.DefaultConnectionLimit = 32;
                }

                // HttpWebRequest 对带 body 的请求默认会先发 Expect: 100-continue 等一轮，
                // 大文件 PUT 白等一个 RTT。我们已经主动带 Authorization，不需要这个协商。
                ServicePointManager.Expect100Continue = false;
            }
            catch
            {
                // 静态初始化的失败不该拖垮整个插件
            }
        }

        public WebDavClient(string baseUrl, string username, string password, int timeoutSeconds, bool useSystemProxy = false)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                throw new ArgumentException("WebDAV 地址不能为空", "baseUrl");
            }

            var url = baseUrl.Trim();
            if (!url.EndsWith("/", StringComparison.Ordinal))
            {
                url += "/";
            }

            this.baseUrl = url;
            this.timeoutMs = Math.Max(5, timeoutSeconds) * 1000;
            this.useSystemProxy = useSystemProxy;

            if (!string.IsNullOrEmpty(username))
            {
                var raw = username + ":" + (password ?? string.Empty);
                this.credential = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
            }

            // .NET Framework 默认可能只协商 TLS 1.0，连现代 NAS 或反代会直接失败
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11;
            }
            catch
            {
                // 老系统上忽略
            }
        }

        public string BaseUrl
        {
            get { return baseUrl; }
        }

        /// <summary>
        /// 把相对路径拼成绝对 URL，逐段做百分号编码（处理中文与空格）。
        /// </summary>
        public string BuildUrl(string relative)
        {
            if (string.IsNullOrEmpty(relative))
            {
                return baseUrl;
            }

            var isDir = relative.EndsWith("/", StringComparison.Ordinal);
            var parts = relative.Replace('\\', '/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

            var sb = new StringBuilder(baseUrl);
            for (var i = 0; i < parts.Length; i++)
            {
                sb.Append(Uri.EscapeDataString(parts[i]));
                if (i < parts.Length - 1)
                {
                    sb.Append('/');
                }
            }

            if (isDir)
            {
                sb.Append('/');
            }

            return sb.ToString();
        }

        private HttpWebRequest CreateRequest(string relative, string method)
        {
            var request = (HttpWebRequest)WebRequest.Create(BuildUrl(relative));
            request.Method = method;
            request.Timeout = timeoutMs;
            request.ReadWriteTimeout = timeoutMs;
            request.AllowAutoRedirect = true;
            request.KeepAlive = false;
            request.UserAgent = "PlayniteVaultDemo/0.1";

            if (!useSystemProxy)
            {
                // 目标通常是内网 NAS。若走系统代理（Clash 等），请求会被打回本机代理端口而失败。
                request.Proxy = null;
            }

            if (credential != null)
            {
                // 主动带凭据，避免 401 后再重发导致 PUT 的 body 已被消费
                request.Headers["Authorization"] = credential;
                request.PreAuthenticate = true;
            }

            return request;
        }

        private static WebDavException Wrap(Exception ex, string action)
        {
            var web = ex as WebException;
            if (web != null && web.Response is HttpWebResponse)
            {
                var res = (HttpWebResponse)web.Response;
                return new WebDavException(
                    string.Format("{0} 失败：HTTP {1} {2}", action, (int)res.StatusCode, res.StatusDescription), ex);
            }
            return new WebDavException(string.Format("{0} 失败：{1}", action, ex.Message), ex);
        }

        /// <summary>探测文件或目录是否存在。</summary>
        public bool Exists(string relative)
        {
            try
            {
                using (var response = (HttpWebResponse)CreateRequest(relative, "HEAD").GetResponse())
                {
                    return response.StatusCode == HttpStatusCode.OK;
                }
            }
            catch (WebException ex)
            {
                if (ex.Response is HttpWebResponse)
                {
                    var code = ((HttpWebResponse)ex.Response).StatusCode;
                    if (code == HttpStatusCode.NotFound || code == HttpStatusCode.Gone)
                    {
                        return false;
                    }
                    if (code == HttpStatusCode.MethodNotAllowed)
                    {
                        // 部分 WebDAV 实现不支持 HEAD，退化为 PROPFIND
                        return ExistsByPropFind(relative);
                    }
                }
                throw Wrap(ex, "探测 " + relative);
            }
        }

        private bool ExistsByPropFind(string relative)
        {
            try
            {
                var request = CreateRequest(relative, "PROPFIND");
                request.Headers["Depth"] = "0";
                request.ContentLength = 0;
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    return (int)response.StatusCode == 207;
                }
            }
            catch (WebException ex)
            {
                if (ex.Response is HttpWebResponse && ((HttpWebResponse)ex.Response).StatusCode == HttpStatusCode.NotFound)
                {
                    return false;
                }
                throw Wrap(ex, "PROPFIND " + relative);
            }
        }

        /// <summary>连通性自检：返回仓库根目录下可见的条目数。</summary>
        public int TestConnection()
        {
            var request = CreateRequest(string.Empty, "PROPFIND");
            request.Headers["Depth"] = "1";
            request.ContentLength = 0;

            using (var response = (HttpWebResponse)request.GetResponse())
            using (var stream = response.GetResponseStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                var body = reader.ReadToEnd();
                var count = 0;
                var idx = 0;
                while ((idx = body.IndexOf("<D:response", idx, StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    count++;
                    idx += 10;
                }
                return Math.Max(0, count - 1); // 去掉自身
            }
        }

        /// <summary>
        /// 列出某个集合（目录）下的一级条目。
        /// 走 PROPFIND Depth:1 而不是 HEAD：飞牛 fnOS 对目录的 HEAD 返回 405。
        /// </summary>
        public List<WebDavEntry> List(string relative)
        {
            var path = string.IsNullOrEmpty(relative) ? string.Empty : relative.TrimStart('/');
            var request = CreateRequest(path, "PROPFIND");
            request.Headers["Depth"] = "1";
            request.ContentLength = 0;

            var entries = new List<WebDavEntry>();
            string body;

            try
            {
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var stream = response.GetResponseStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    body = reader.ReadToEnd();
                }
            }
            catch (Exception ex)
            {
                throw Wrap(ex, "列举 " + path);
            }

            // PROPFIND 会把被查询的集合自己也算进来，按绝对路径比对把它排掉
            var selfPath = Uri.UnescapeDataString(new Uri(BuildUrl(path)).AbsolutePath).TrimEnd('/');

            var blocks = Regex.Matches(body,
                "<[^>]*?response[^>]*?>(.*?)</[^>]*?response>",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);

            foreach (Match block in blocks)
            {
                var inner = block.Groups[1].Value;
                var href = ExtractTag(inner, "href");
                if (string.IsNullOrEmpty(href))
                {
                    continue;
                }

                var decoded = Uri.UnescapeDataString(href);
                if (decoded.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    decoded = new Uri(decoded).AbsolutePath;
                }

                var decodedPath = decoded.TrimEnd('/');
                if (decodedPath.Length == 0
                    || string.Equals(decodedPath, selfPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var name = decodedPath.Substring(decodedPath.LastIndexOf('/') + 1);
                if (name.Length == 0)
                {
                    continue;
                }

                var entry = new WebDavEntry
                {
                    Name = name,
                    Href = href,
                    IsCollection = inner.IndexOf("collection", StringComparison.OrdinalIgnoreCase) >= 0
                };

                var lengthText = ExtractTag(inner, "getcontentlength");
                long length;
                if (!string.IsNullOrEmpty(lengthText) &&
                    long.TryParse(lengthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out length))
                {
                    entry.Length = length;
                }

                entries.Add(entry);
            }

            return entries;
        }

        private static string ExtractTag(string xml, string tag)
        {
            var match = Regex.Match(xml,
                "<[^>]*?" + tag + "[^>]*?>(.*?)</[^>]*?" + tag + ">",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.Trim() : null;
        }

        /// <summary>读取文本文件（UTF-8）。</summary>
        public string DownloadString(string relative)
        {
            try
            {
                using (var response = (HttpWebResponse)CreateRequest(relative, "GET").GetResponse())
                using (var stream = response.GetResponseStream())
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
            catch (Exception ex)
            {
                throw Wrap(ex, "读取 " + relative);
            }
        }

        /// <summary>
        /// 流式下载，带断点续传与失败重试。
        /// 先写 .part 临时文件，完整落盘后再改名；中断时保留 .part 供下次续传。
        /// </summary>
        public void DownloadFile(string relative, string localPath, Action<long, long> onProgress,
            CancellationToken cancelToken, SyncOptions options = null)
        {
            var opt = options ?? new SyncOptions();
            var attempts = Math.Max(1, opt.MaxRetries);
            Exception last = null;

            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                cancelToken.ThrowIfCancellationRequested();
                try
                {
                    DownloadOnce(relative, localPath, onProgress, cancelToken, opt);
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    last = ex;
                    if (attempt >= attempts)
                    {
                        break;
                    }

                    // 指数退避，等待期间保持可取消
                    var delay = Math.Min(800 * attempt * attempt, 8000);
                    if (cancelToken.WaitHandle.WaitOne(delay))
                    {
                        cancelToken.ThrowIfCancellationRequested();
                    }
                }
            }

            throw Wrap(last, "下载 " + relative);
        }

        private void DownloadOnce(string relative, string localPath, Action<long, long> onProgress,
            CancellationToken cancelToken, SyncOptions opt)
        {
            var dir = Path.GetDirectoryName(localPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var tempPath = localPath + ".part";
            var startAt = 0L;

            if (opt.ResumePartial && File.Exists(tempPath))
            {
                startAt = new FileInfo(tempPath).Length;
            }

            var request = CreateRequest(relative, "GET");
            if (startAt > 0)
            {
                request.AddRange(startAt);
            }

            var resuming = false;
            try
            {
                using (cancelToken.Register(request.Abort))
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    // 只有服务器真的返回 206 才能接着写，否则必须从头来
                    resuming = startAt > 0 && response.StatusCode == HttpStatusCode.PartialContent;
                    if (!resuming)
                    {
                        startAt = 0;
                    }

                    var total = response.ContentLength >= 0 ? response.ContentLength + startAt : -1;

                    using (var remote = response.GetResponseStream())
                    using (var local = new FileStream(tempPath,
                        resuming ? FileMode.Append : FileMode.Create,
                        FileAccess.Write, FileShare.None, BufferSize))
                    {
                        var buffer = new byte[BufferSize];
                        var done = startAt;
                        int read;
                        while ((read = remote.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            cancelToken.ThrowIfCancellationRequested();
                            local.Write(buffer, 0, read);
                            done += read;
                            if (onProgress != null)
                            {
                                onProgress(done, total);
                            }
                        }
                    }
                }
            }
            catch (WebException ex)
            {
                // 起点正好等于文件长度时，部分服务器回 416，说明其实已经下完了
                var response = ex.Response as HttpWebResponse;
                if (startAt > 0 && response != null && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    VaultLog.Info(string.Format("{0} 的 .part 已是完整长度，直接收尾", relative));
                }
                else
                {
                    throw;
                }
            }

            if (File.Exists(localPath))
            {
                File.Delete(localPath);
            }
            File.Move(tempPath, localPath);
        }

        /// <summary>取远端文件大小；不存在返回 -1。</summary>
        public long GetFileSize(string relative)
        {
            try
            {
                using (var response = (HttpWebResponse)CreateRequest(relative, "HEAD").GetResponse())
                {
                    return response.ContentLength;
                }
            }
            catch (WebException ex)
            {
                var response = ex.Response as HttpWebResponse;
                if (response != null && response.StatusCode == HttpStatusCode.NotFound)
                {
                    return -1;
                }
                throw Wrap(ex, "探测大小 " + relative);
            }
        }

        /// <summary>删除远端文件。</summary>
        public void Delete(string relative)
        {
            try
            {
                using ((HttpWebResponse)CreateRequest(relative, "DELETE").GetResponse())
                {
                }
            }
            catch (WebException ex)
            {
                var response = ex.Response as HttpWebResponse;
                if (response != null && response.StatusCode == HttpStatusCode.NotFound)
                {
                    return;
                }
                throw Wrap(ex, "删除 " + relative);
            }
        }

        /// <summary>上传本地文件（PUT）。</summary>
        public void UploadFile(string localPath, string relative, Action<long, long> onProgress, CancellationToken cancelToken)
        {
            var info = new FileInfo(localPath);
            var request = CreateRequest(relative, "PUT");
            request.ContentLength = info.Length;
            request.ContentType = "application/octet-stream";

            try
            {
                using (cancelToken.Register(request.Abort))
                using (var remote = request.GetRequestStream())
                using (var local = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize))
                {
                    var buffer = new byte[BufferSize];
                    long done = 0;
                    int read;
                    while ((read = local.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        cancelToken.ThrowIfCancellationRequested();
                        remote.Write(buffer, 0, read);
                        done += read;
                        if (onProgress != null)
                        {
                            onProgress(done, info.Length);
                        }
                    }
                }

                using ((HttpWebResponse)request.GetResponse())
                {
                }
            }
            catch (Exception ex)
            {
                if (cancelToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancelToken);
                }
                throw Wrap(ex, "上传 " + relative);
            }
        }

        /// <summary>上传文本（PUT）。</summary>
        public void UploadString(string content, string relative)
        {
            var bytes = new UTF8Encoding(false).GetBytes(content ?? string.Empty);
            var request = CreateRequest(relative, "PUT");
            request.ContentLength = bytes.Length;
            request.ContentType = "application/json; charset=utf-8";

            try
            {
                using (var remote = request.GetRequestStream())
                {
                    remote.Write(bytes, 0, bytes.Length);
                }
                using ((HttpWebResponse)request.GetResponse())
                {
                }
            }
            catch (Exception ex)
            {
                throw Wrap(ex, "写入 " + relative);
            }
        }

        /// <summary>创建集合（目录）。已存在时 WebDAV 返回 405，视为成功。</summary>
        public void EnsureDirectory(string relative)
        {
            if (string.IsNullOrEmpty(relative))
            {
                return;
            }

            var path = relative;
            if (!path.EndsWith("/", StringComparison.Ordinal))
            {
                path += "/";
            }

            var request = CreateRequest(path, "MKCOL");
            request.ContentLength = 0;

            try
            {
                using ((HttpWebResponse)request.GetResponse())
                {
                }
            }
            catch (WebException ex)
            {
                var response = ex.Response as HttpWebResponse;
                if (response != null)
                {
                    var code = response.StatusCode;
                    if (code == HttpStatusCode.MethodNotAllowed || code == HttpStatusCode.Conflict || code == HttpStatusCode.OK)
                    {
                        return; // 目录已存在
                    }
                }
                throw Wrap(ex, "创建目录 " + relative);
            }
        }

        /// <summary>逐级创建目录。</summary>
        public void EnsureDirectoryRecursive(string relative)
        {
            var parts = (relative ?? string.Empty).Replace('\\', '/')
                .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            var current = string.Empty;
            foreach (var part in parts)
            {
                current = current.Length == 0 ? part : current + "/" + part;
                EnsureDirectory(current);
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
            }
        }
    }

    public class WebDavException : Exception
    {
        public WebDavException(string message, Exception inner) : base(message, inner)
        {
        }
    }
}
