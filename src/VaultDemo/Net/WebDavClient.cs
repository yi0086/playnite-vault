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
    /// 三档超时。**不能再用一个数管全部** —— 这是实测踩出来的：
    ///
    /// `HttpWebRequest.Timeout` 语义上是「GetRequestStream() / GetResponse() 等多久」。
    /// 而 PUT 的 `GetResponse()` 要等服务端把**整个 body 收完、落盘、回包**，
    /// 所以拿它当「传输超时」时，任何大分片都必然撞上（实测 30 秒超时导致
    /// 1.93 GB 的分片 100% 失败）。真正对应「速度逐渐到 0」的判据是**停滞**。
    /// </summary>
    public class WebDavTimeouts
    {
        /// <summary>建连（含等到响应头）的上限。</summary>
        public int ConnectMs { get; set; } = 15000;

        /// <summary>连续多久没有字节流动就断开。落到 <c>ReadWriteTimeout</c> 上。</summary>
        public int StallMs { get; set; } = 30000;

        /// <summary>body 发完后等服务端回包的上限（只对 PUT 生效）。</summary>
        public int ResponseMs { get; set; } = 180000;

        /// <summary>
        /// 从旧的单值「超时秒数」映射过来。
        /// 旧的语义最接近停滞超时，所以拿它当 StallMs；
        /// 应答超时另给一个宽松的下限，否则大区块还是会失败。
        /// </summary>
        public static WebDavTimeouts FromSeconds(int seconds)
        {
            var ms = Math.Max(5, seconds) * 1000;
            return new WebDavTimeouts
            {
                ConnectMs = Math.Min(ms, 30000),
                StallMs = ms,
                ResponseMs = Math.Max(ms, 180000)
            };
        }

        /// <summary>
        /// 三档分开给（秒）。**这是推荐用法** —— 单值映射只是为了兼容老调用点，
        /// 它把建连/停滞/应答混在一个数上，正是当初 1.93GB 分片必然超时的根源。
        /// </summary>
        public static WebDavTimeouts FromSeconds(int connectSeconds, int stallSeconds, int responseSeconds)
        {
            return new WebDavTimeouts
            {
                ConnectMs = Math.Max(3, connectSeconds) * 1000,
                StallMs = Math.Max(5, stallSeconds) * 1000,
                ResponseMs = Math.Max(10, responseSeconds) * 1000
            };
        }
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
        private readonly WebDavTimeouts timeouts;
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

        public WebDavClient(string baseUrl, string username, string password, int timeoutSeconds,
            bool useSystemProxy = false)
            : this(baseUrl, username, password, WebDavTimeouts.FromSeconds(timeoutSeconds), useSystemProxy)
        {
        }

        public WebDavClient(string baseUrl, string username, string password, WebDavTimeouts timeouts,
            bool useSystemProxy = false)
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
            this.timeouts = timeouts ?? new WebDavTimeouts();
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

        public WebDavTimeouts Timeouts
        {
            get { return timeouts; }
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

            // 建连 / 等响应头的上限。PUT 在拿到写流之后会被改成应答超时（见 UploadOnce）。
            request.Timeout = timeouts.ConnectMs;

            // **这条才是「速度逐渐到 0」的判据**：单次 Read/Write 卡住超过这个时长就抛超时，
            // 于是停滞会被当成一次可重试的失败，而不是让整个归档挂在那里直到总超时。
            request.ReadWriteTimeout = timeouts.StallMs;

            request.AllowAutoRedirect = true;

            // 区块变小之后请求数会多一个量级（3GB 的游戏约 95 个块），
            // 每块都重新做一次 TLS 握手的代价就不能忽略了。
            request.KeepAlive = true;

            request.UserAgent = "PlayniteVaultDemo/0.1";
            request.ServicePoint.UseNagleAlgorithm = false;

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

        /// <summary>
        /// 上传本地文件（PUT），**带区块级重试与退避**。
        ///
        /// 这里以前一次重试都没有：任一分片失败就取消整条流水线，
        /// 而清单只在全部传完才写 → 重试从第 0 片重来，然后必然再撞同一堵墙
        /// （实测死亡细胞连着失败两次、都死在 part-0001）。
        /// </summary>
        public void UploadFile(string localPath, string relative, Action<long, long> onProgress,
            CancellationToken cancelToken, int maxAttempts = 3, Action<int, Exception> onRetry = null)
        {
            var attempts = Math.Max(1, maxAttempts);
            Exception last = null;

            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                cancelToken.ThrowIfCancellationRequested();

                try
                {
                    UploadOnce(localPath, relative, onProgress, cancelToken);
                    return;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    last = ex;

                    // 4xx 这类是确定性问题（权限 / 路径不对），重试只会白等
                    if (attempt >= attempts || !IsTransient(ex))
                    {
                        break;
                    }

                    if (onRetry != null)
                    {
                        onRetry(attempt, ex);
                    }

                    VaultLog.Warn(string.Format("上传 {0} 第 {1} 次失败（{2}），将重试：{3}",
                        relative, attempt, attempt >= attempts ? "最后一次" : "退避后重试", ex.Message));

                    var delay = Math.Min(1000 * attempt * attempt, 15000);
                    if (cancelToken.WaitHandle.WaitOne(delay))
                    {
                        cancelToken.ThrowIfCancellationRequested();
                    }
                }
            }

            throw Wrap(last, "上传 " + relative);
        }

        private void UploadOnce(string localPath, string relative, Action<long, long> onProgress,
            CancellationToken cancelToken)
        {
            var info = new FileInfo(localPath);
            var request = CreateRequest(relative, "PUT");
            request.ContentLength = info.Length;
            request.ContentType = "application/octet-stream";

            try
            {
                using (cancelToken.Register(request.Abort))
                {
                    // 这一段用 ConnectMs 兜（建连 + 拿到写流），
                    // 单次写卡住由 ReadWriteTimeout（= StallMs）兜。
                    using (var remote = request.GetRequestStream())
                    using (var local = new FileStream(localPath, FileMode.Open, FileAccess.Read,
                        FileShare.Read, BufferSize))
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

                    // body 已经发完，接下来是服务端收尾（落盘 / 合并临时文件）。
                    // **这一段必须用宽松的应答超时**：用 30 秒的话，
                    // 一个 1.93GB 的 body 在服务端那边还没落完就已经被判超时了。
                    request.Timeout = timeouts.ResponseMs;
                    using ((HttpWebResponse)request.GetResponse())
                    {
                    }
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

        /// <summary>
        /// 这个失败值不值得重试。
        /// 超时 / 连接被掐 / 收发失败 / 5xx / 429 都是瞬时的；
        /// 其余 4xx（401、403、404、405…）是确定性的，重试没有意义。
        /// </summary>
        public static bool IsTransient(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                var web = e as WebException;
                if (web != null)
                {
                    var response = web.Response as HttpWebResponse;
                    if (response != null)
                    {
                        var code = (int)response.StatusCode;
                        return code == 408 || code == 429 || code >= 500;
                    }

                    switch (web.Status)
                    {
                        case WebExceptionStatus.Timeout:
                        case WebExceptionStatus.ConnectionClosed:
                        case WebExceptionStatus.ConnectFailure:
                        case WebExceptionStatus.SendFailure:
                        case WebExceptionStatus.ReceiveFailure:
                        case WebExceptionStatus.PipelineFailure:
                        case WebExceptionStatus.KeepAliveFailure:
                        case WebExceptionStatus.RequestCanceled:
                            return true;
                        case WebExceptionStatus.ProtocolError:
                            return false;
                    }
                    return true;
                }

                if (e is System.Net.Sockets.SocketException || e is IOException)
                {
                    return true;
                }
            }
            return false;
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

    /// <summary>
    /// 把 WebDAV 请求异常翻译成「用户能照着动手排查」的中文说明。
    ///
    /// 重点是 401：**光看 401 分不清「密码填错」还是「服务端认证后端坏了」**。
    /// 可靠的区分办法是拿浏览器直接打开仓库地址来对照 ——
    /// 如果浏览器里用同一个账号也进不去（或被反复弹认证框），那就是服务端的问题，
    /// 此时改本工具的任何配置都不会有用。
    /// </summary>
    public static class WebDavDiagnostics
    {
        public static string Describe(Exception ex, string url, string username)
        {
            if (ex == null)
            {
                return "未知错误。";
            }

            var inner = ex;
            while (inner is AggregateException && inner.InnerException != null)
            {
                inner = inner.InnerException;
            }

            var web = inner as System.Net.WebException;
            if (web != null)
            {
                var response = web.Response as System.Net.HttpWebResponse;
                if (response != null)
                {
                    return DescribeStatus(response, url, username);
                }

                switch (web.Status)
                {
                    case System.Net.WebExceptionStatus.NameResolutionFailure:
                        return "域名解析失败 —— 主机名或 IP 写错了，或 DNS 不通。\n当前地址：" + url;

                    case System.Net.WebExceptionStatus.ConnectFailure:
                        return "连接被拒绝 —— 地址/端口不对，或 NAS 上的 WebDAV 服务没启动。\n当前地址：" + url;

                    case System.Net.WebExceptionStatus.Timeout:
                        return "连接超时 —— 网络不通，或者本机代理把请求截走了。\n"
                             + "访问内网 NAS 时请在本页**取消勾选「走系统代理」**。\n当前地址：" + url;

                    case System.Net.WebExceptionStatus.TrustFailure:
                        return "TLS 证书校验失败 —— NAS 用的是自签证书，系统不信任它。\n"
                             + "可以给 NAS 换正式证书，或改用 http 访问。\n当前地址：" + url;

                    default:
                        return "网络错误（" + web.Status + "）：" + web.Message;
                }
            }

            if (inner is TimeoutException)
            {
                return "操作超时 —— 服务端迟迟没有响应。\n当前地址：" + url;
            }

            return inner.Message;
        }

        private static string DescribeStatus(System.Net.HttpWebResponse response, string url, string username)
        {
            var code = (int)response.StatusCode;
            var server = Header(response, "Server");
            var suffix = string.IsNullOrEmpty(server) ? string.Empty : "\n（服务器标识：" + server + "）";

            if (code == 401)
            {
                var text = "HTTP 401 未授权 —— 服务端拒绝了这次凭据。\n\n"
                         + "按这个顺序排查：\n"
                         + "1) 用户名 / 密码是否正确。注意有些 NAS 的 WebDAV 需要「应用密码 / 独立密码」，"
                         + "并不是你登录管理后台的那个密码。\n"
                         + "2) **服务端的认证后端是否有问题**。怎么区分：用浏览器直接打开仓库地址，"
                         + "如果同一个账号在浏览器里也进不去（或反复弹认证框），那就是服务端的问题，"
                         + "改本工具的任何设置都不会有用。\n"
                         + "3) 账号是否被禁用，或 WebDAV 服务没对当前用户开放。";

                if (string.IsNullOrWhiteSpace(username))
                {
                    text += "\n\n另外：当前用户名是空的，这本身就足以导致 401。";
                }

                return text + suffix;
            }

            if (code == 403)
            {
                return "HTTP 403 禁止访问 —— 账号密码可能是对的，但这个账号没有该目录的权限。\n"
                     + "检查 WebDAV 共享目录的读写权限，或换一个有权限的账号。" + suffix;
            }

            if (code == 404)
            {
                return "HTTP 404 找不到 —— 地址写错了，或者这个目录还不存在。\n当前地址：" + url + suffix;
            }

            if (code == 405)
            {
                return "HTTP 405 方法不被允许 —— 服务器不接受 PROPFIND / PUT 这些 WebDAV 方法。\n"
                     + "确认这个地址确实是 WebDAV 入口（有些 NAS 需要先在设置里开启 WebDAV 服务）。" + suffix;
            }

            if (code >= 500)
            {
                return "HTTP " + code + " 服务端错误 —— NAS 侧的 WebDAV 服务出问题了，先去看 NAS 的日志。" + suffix;
            }

            return "HTTP " + code + " " + response.StatusDescription + suffix;
        }

        private static string Header(System.Net.HttpWebResponse response, string name)
        {
            try
            {
                return response.Headers[name];
            }
            catch
            {
                return null;
            }
        }
    }
}
