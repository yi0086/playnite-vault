using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace VaultSelfTest
{
    /// <summary>
    /// 内存版 WebDAV 服务，够主题同步自检用。
    ///
    /// 支持 HEAD / GET / PUT / DELETE / MKCOL / PROPFIND(Depth 0|1)。
    /// 几个刻意「像真的」的地方：
    ///   · 对**目录**的 HEAD 返回 405（飞牛 fnOS 就是这个行为，客户端会自动退化成 PROPFIND）；
    ///   · MKCOL 已存在的目录返回 405（真实 WebDAV 就是这么回的，客户端把它当成功）；
    ///   · PUT 到不存在的父目录返回 409（第一次跑容易漏建目录，这条能抓住）。
    /// 用 TcpListener 手写响应而不是 HttpListener：HttpListener 走 http.sys，要 URL ACL，
    /// 在非管理员进程里会莫名 503。
    /// </summary>
    public class MockWebDavServer : IDisposable
    {
        private readonly TcpListener listener;
        private readonly Thread acceptThread;
        private readonly object gate = new object();
        private readonly Dictionary<string, byte[]> files =
            new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>请求计数（路径 → 次数），用来断言「确实没发 DELETE」这类事。</summary>
        public readonly Dictionary<string, int> Requests =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        /// <summary>被 DELETE 过的路径 —— 用来断言「这套同步确实从没删过远端东西」。</summary>
        public readonly List<string> Deleted = new List<string>();

        private volatile bool running = true;

        public int Port { get; private set; }

        public string BaseUrl
        {
            get { return "http://127.0.0.1:" + Port + "/"; }
        }

        public MockWebDavServer()
        {
            for (var port = 18180; port < 18280; port++)
            {
                try
                {
                    listener = new TcpListener(IPAddress.Loopback, port);
                    listener.Start();
                    Port = port;
                    break;
                }
                catch (SocketException)
                {
                    // 换下一个端口
                }
            }

            if (listener == null)
            {
                throw new InvalidOperationException("找不到可用端口（18180-18279 都被占了）");
            }

            acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "mock-webdav" };
            acceptThread.Start();
        }

        // ---------- 检查用的读写口 ----------

        /// <summary>把一个文件放进「远端」。父目录会自动补齐。</summary>
        public void Seed(string path, byte[] content)
        {
            var key = Norm(path);
            lock (gate)
            {
                EnsureParents(key);
                files[key] = content ?? new byte[0];
            }
        }

        public void SeedText(string path, string text)
        {
            Seed(path, new UTF8Encoding(false).GetBytes(text ?? string.Empty));
        }

        public bool Has(string path)
        {
            lock (gate)
            {
                return files.ContainsKey(Norm(path));
            }
        }

        public byte[] Get(string path)
        {
            lock (gate)
            {
                byte[] value;
                return files.TryGetValue(Norm(path), out value) ? value : null;
            }
        }

        public string GetText(string path)
        {
            var bytes = Get(path);
            return bytes == null ? null : new UTF8Encoding(false).GetString(bytes);
        }

        public List<string> AllFiles()
        {
            lock (gate)
            {
                return new List<string>(files.Keys);
            }
        }

        /// <summary>路径前缀下有哪几个目录（用于断言 .conflict- 副本确实建出来了）。</summary>
        public List<string> DirsUnder(string prefix)
        {
            var norm = Norm(prefix).TrimEnd('/');
            var list = new List<string>();
            lock (gate)
            {
                foreach (var dir in dirs)
                {
                    if (dir.StartsWith(norm + "/", StringComparison.OrdinalIgnoreCase))
                    {
                        list.Add(dir.Substring(norm.Length + 1));
                    }
                }
            }
            return list;
        }

        public int Count(string path)
        {
            lock (gate)
            {
                int value;
                return Requests.TryGetValue(Norm(path), out value) ? value : 0;
            }
        }

        private static string Norm(string path)
        {
            var value = (path ?? string.Empty).Replace('\\', '/');
            var query = value.IndexOf('?');
            if (query >= 0)
            {
                value = value.Substring(0, query);
            }
            value = Uri.UnescapeDataString(value);
            while (value.StartsWith("/", StringComparison.Ordinal))
            {
                value = value.Substring(1);
            }
            return value.TrimEnd('/');
        }

        private void EnsureParents(string key)
        {
            var idx = key.LastIndexOf('/');
            while (idx > 0)
            {
                dirs.Add(key.Substring(0, idx));
                key = key.Substring(0, idx);
                idx = key.LastIndexOf('/');
            }
        }

        private bool DirExists(string key)
        {
            return key.Length == 0 || dirs.Contains(key);
        }

        // ---------- 服务端 ----------

        private void AcceptLoop()
        {
            while (running)
            {
                TcpClient client;
                try
                {
                    client = listener.AcceptTcpClient();
                }
                catch
                {
                    return;
                }

                ThreadPool.QueueUserWorkItem(_ => Handle(client));
            }
        }

        private void Handle(TcpClient client)
        {
            try
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    string method, path, depth;
                    byte[] body;
                    if (!Read(stream, out method, out path, out depth, out body))
                    {
                        Respond(stream, 400, "Bad Request", null, null);
                        return;
                    }

                    var key = Norm(path);
                    lock (gate)
                    {
                        int count;
                        Requests.TryGetValue(key, out count);
                        Requests[key] = count + 1;
                    }

                    Dispatch(stream, method, key, depth, body);
                }
            }
            catch
            {
                // 自检服务出错就让客户端自己超时，别污染测试输出
            }
        }

        private void Dispatch(NetworkStream stream, string method, string key, string depth, byte[] body)
        {
            switch (method)
            {
                case "MKCOL":
                    lock (gate)
                    {
                        if (!DirExists(Parent(key)))
                        {
                            Respond(stream, 409, "Conflict", null, null);
                            return;
                        }
                        if (dirs.Contains(key))
                        {
                            // 真实 WebDAV 对已存在的集合回 405，客户端把它当成功
                            Respond(stream, 405, "Method Not Allowed", null, null);
                            return;
                        }
                        dirs.Add(key);
                        dirs.Add(key + "/");
                    }
                    Respond(stream, 201, "Created", null, null);
                    return;

                case "PUT":
                    lock (gate)
                    {
                        var parent = Parent(key);
                        if (!DirExists(parent))
                        {
                            Respond(stream, 409, "Conflict", null, null);
                            return;
                        }
                        files[key] = body ?? new byte[0];
                        dirs.Add(parent);
                    }
                    Respond(stream, 201, "Created", null, null);
                    return;

                case "DELETE":
                    lock (gate)
                    {
                        Deleted.Add(key);
                        if (files.Remove(key))
                        {
                            Respond(stream, 204, "No Content", null, null);
                            return;
                        }
                    }
                    Respond(stream, 404, "Not Found", null, null);
                    return;

                case "HEAD":
                    lock (gate)
                    {
                        byte[] value;
                        if (files.TryGetValue(key, out value))
                        {
                            Respond(stream, 200, "OK", null, value.Length, true);
                            return;
                        }
                        if (dirs.Contains(key) || dirs.Contains(key + "/"))
                        {
                            // fnOS 对目录的 HEAD 就是这个；客户端应当退化成 PROPFIND
                            Respond(stream, 405, "Method Not Allowed", null, null);
                            return;
                        }
                    }
                    Respond(stream, 404, "Not Found", null, null);
                    return;

                case "GET":
                    lock (gate)
                    {
                        byte[] value;
                        if (files.TryGetValue(key, out value))
                        {
                            Respond(stream, 200, "OK", value, value.Length, false);
                            return;
                        }
                    }
                    Respond(stream, 404, "Not Found", null, null);
                    return;

                case "PROPFIND":
                    Respond(stream, 207, "Multi-Status",
                        new UTF8Encoding(false).GetBytes(PropFind(key, depth)), null, false);
                    return;

                default:
                    Respond(stream, 405, "Method Not Allowed", null, null);
                    return;
            }
        }

        private string PropFind(string key, string depth)
        {
            var xml = new StringBuilder();
            xml.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
            xml.Append("<D:multistatus xmlns:D=\"DAV:\">\n");

            lock (gate)
            {
                xml.Append(Entry(key, true));
                if (!string.Equals(depth, "0", StringComparison.Ordinal))
                {
                    var prefix = key.Length == 0 ? string.Empty : key + "/";
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var dir in dirs)
                    {
                        if (!dir.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        var rest = dir.Substring(prefix.Length).Trim('/');
                        if (rest.Length == 0 || rest.IndexOf('/') >= 0 || !seen.Add(rest))
                        {
                            continue;
                        }
                        xml.Append(Entry(prefix + rest, true));
                    }

                    foreach (var file in files.Keys)
                    {
                        if (file.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                            && file.Substring(prefix.Length).IndexOf('/') < 0)
                        {
                            xml.Append(Entry(file, false));
                        }
                    }
                }
            }

            xml.Append("</D:multistatus>");
            return xml.ToString();
        }

        private string Entry(string key, bool collection)
        {
            var href = "/" + key;
            var length = collection ? 0 : (files.ContainsKey(key) ? files[key].Length : 0);
            var type = collection ? "<D:collection/>" : string.Empty;

            return string.Format(CultureInfo.InvariantCulture,
                "<D:response><D:href>{0}</D:href><D:propstat><D:prop>"
                + "<D:resourcetype>{1}</D:resourcetype>"
                + "<D:getcontentlength>{2}</D:getcontentlength>"
                + "</D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>\n",
                href, type, length);
        }

        private static string Parent(string key)
        {
            var idx = key.LastIndexOf('/');
            return idx <= 0 ? string.Empty : key.Substring(0, idx);
        }

        // ---------- HTTP 解析 / 输出 ----------

        private static bool Read(NetworkStream stream, out string method, out string path,
            out string depth, out byte[] body)
        {
            method = null;
            path = null;
            depth = null;
            body = null;

            var buffer = new byte[8192];
            var head = new StringBuilder();
            var raw = new MemoryStream();

            // 1) 先读到空行为止
            var end = -1;
            while (end < 0)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    return false;
                }
                raw.Write(buffer, 0, read);
                head.Append(Encoding.ASCII.GetString(buffer, 0, read));
                end = head.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (head.Length > 64 * 1024)
                {
                    return false;
                }
            }

            var text = head.ToString();
            var lineEnd = text.IndexOf('\n');
            if (lineEnd < 0)
            {
                return false;
            }

            var parts = text.Substring(0, lineEnd).Trim().Split(' ');
            if (parts.Length < 2)
            {
                return false;
            }

            method = parts[0].ToUpperInvariant();
            path = parts[1];

            var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4;
            long length = 0;
            foreach (var line in text.Substring(0, headerEnd).Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    long.TryParse(trimmed.Substring(15).Trim(), out length);
                }
                else if (trimmed.StartsWith("Depth:", StringComparison.OrdinalIgnoreCase))
                {
                    depth = trimmed.Substring(6).Trim();
                }
            }

            // 2) 再按 Content-Length 把 body 收完
            var all = raw.ToArray();
            if (length <= 0)
            {
                body = new byte[0];
                return true;
            }

            var content = new MemoryStream();
            content.Write(all, headerEnd, all.Length - headerEnd);

            while (content.Length < length)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }
                content.Write(buffer, 0, read);
            }

            body = content.ToArray();
            return true;
        }

        private static void Respond(NetworkStream stream, int status, string reason, byte[] body,
            long? contentLength = null, bool headerOnly = false)
        {
            var payload = body ?? new byte[0];
            var header = new StringBuilder();
            header.Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n");
            header.Append("Content-Type: text/plain; charset=utf-8\r\n");
            header.Append("Content-Length: ").Append(contentLength ?? payload.Length).Append("\r\n");
            header.Append("Connection: close\r\n\r\n");

            var bytes = Encoding.ASCII.GetBytes(header.ToString());
            stream.Write(bytes, 0, bytes.Length);
            if (!headerOnly)
            {
                stream.Write(payload, 0, payload.Length);
            }
            stream.Flush();
        }

        public void Dispose()
        {
            running = false;
            try
            {
                listener.Stop();
            }
            catch
            {
            }
        }
    }
}
