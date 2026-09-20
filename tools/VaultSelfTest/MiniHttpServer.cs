using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace VaultSelfTest
{
    /// <summary>一条假路由。</summary>
    public class MockRoute
    {
        /// <summary>响应体。</summary>
        public byte[] Body;

        public string ContentType = "application/json; charset=utf-8";

        /// <summary>HTTP 状态码。</summary>
        public int Status = 200;

        /// <summary>每个写块的字节数。0 = 一次性写完。</summary>
        public int ChunkSize;

        /// <summary>每块之间睡多久（毫秒）。用来模拟「慢得没法用的源」。</summary>
        public int ChunkDelayMs;

        /// <summary>写响应头之前先睡多久（毫秒）。用来制造「这个源探测延迟更高」。</summary>
        public int HeadDelayMs;

        /// <summary>true = 直接把连接掐掉（模拟被墙 / 连不通）。</summary>
        public bool Drop;

        public static MockRoute Text(string text, string contentType = "application/json; charset=utf-8")
        {
            return new MockRoute
            {
                Body = new UTF8Encoding(false).GetBytes(text),
                ContentType = contentType
            };
        }

        public static MockRoute Throttled(byte[] body, int chunkSize, int delayMs)
        {
            return new MockRoute
            {
                Body = body,
                ContentType = "application/octet-stream",
                ChunkSize = chunkSize,
                ChunkDelayMs = delayMs
            };
        }
    }

    /// <summary>
    /// 极简 HTTP 服务（TcpListener + 手写响应），只为自检服务。
    ///
    /// 用 TCP 而不是 HttpListener：HttpListener 走 http.sys，会有 URL ACL 的额外变量；
    /// 手写响应还能精确控制「每块多少字节、隔多久写一次」，这正是测速度地板需要的。
    /// </summary>
    public class MiniHttpServer : IDisposable
    {
        private readonly TcpListener listener;
        private readonly Thread acceptThread;
        private readonly ConcurrentDictionary<string, MockRoute> routes =
            new ConcurrentDictionary<string, MockRoute>(StringComparer.OrdinalIgnoreCase);

        /// <summary>每个路径被请求了几次 —— 用来断言「确实换了源」。</summary>
        public readonly ConcurrentDictionary<string, int> Hits =
            new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        private volatile bool running = true;

        public int Port { get; private set; }

        public string BaseUrl
        {
            get { return "http://127.0.0.1:" + Port + "/"; }
        }

        public MiniHttpServer()
        {
            // 从 18080 往上找一个能用的端口：非管理员进程绑定 http 端口也要避开别的东西占用
            for (var port = 18080; port < 18180; port++)
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
                    // 换下一个
                }
            }

            if (listener == null)
            {
                throw new InvalidOperationException("找不到可用端口（18080-18179 都被占了）");
            }

            acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "mock-http" };
            acceptThread.Start();
        }

        /// <summary>
        /// 统一路径口径：注册时写 "a/b"，请求行里来的是 "/a/b"，两边必须归一化，
        /// 否则路由永远命中不了（只会看到莫名其妙的 404）。
        /// </summary>
        private static string Norm(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return "/";
            }
            return path[0] == '/' ? path : "/" + path;
        }

        public void Set(string path, MockRoute route)
        {
            routes[Norm(path)] = route;
        }

        public void Remove(string path)
        {
            MockRoute ignored;
            routes.TryRemove(Norm(path), out ignored);
        }

        public int HitCount(string path)
        {
            int count;
            return Hits.TryGetValue(Norm(path), out count) ? count : 0;
        }

        public void ResetHits()
        {
            Hits.Clear();
        }

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
                    return;   // listener 关掉了
                }

                ThreadPool.QueueUserWorkItem(_ => HandleClient(client));
            }
        }

        private void HandleClient(TcpClient client)
        {
            try
            {
                using (client)
                using (var stream = client.GetStream())
                {
                    var bytes = ReadRequest(stream);
                    if (bytes == null)
                    {
                        WriteResponse(stream, "400 Bad Request", "text/plain; charset=utf-8",
                            new UTF8Encoding(false).GetBytes("bad request line"));
                        return;
                    }

                    Hits.AddOrUpdate(Norm(bytes), 1, (k, v) => v + 1);

                    MockRoute route;
                    if (!routes.TryGetValue(Norm(bytes), out route))
                    {
                        WriteResponse(stream, "404 Not Found", "text/plain; charset=utf-8",
                            new UTF8Encoding(false).GetBytes("no such route: " + Norm(bytes)));
                        return;
                    }

                    if (route.Drop)
                    {
                        client.Client.Close();
                        return;
                    }

                    if (route.Status != 200)
                    {
                        WriteResponse(stream, route.Status + " Error", "text/plain; charset=utf-8",
                            new UTF8Encoding(false).GetBytes("mock error"));
                        return;
                    }

                    WriteBody(stream, route);
                }
            }
            catch
            {
                // 自检服务出错就让客户端自己超时/重试，不要污染测试输出
            }
        }

        private static string ReadRequest(NetworkStream stream)
        {
            var buffer = new byte[4096];
            var text = new StringBuilder();

            for (var i = 0; i < 8; i++)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }
                text.Append(Encoding.ASCII.GetString(buffer, 0, read));
                if (text.ToString().Contains("\r\n\r\n"))
                {
                    break;
                }
            }

            var head = text.ToString();
            var lineEnd = head.IndexOf('\n');
            if (lineEnd < 0)
            {
                return null;
            }

            var parts = head.Substring(0, lineEnd).Trim().Split(' ');
            if (parts.Length < 2)
            {
                return null;
            }

            var url = parts[1];
            var query = url.IndexOf('?');
            if (query >= 0)
            {
                url = url.Substring(0, query);
            }
            return url;
        }

        private static void WriteResponse(NetworkStream stream, string status, string contentType, byte[] body)
        {
            var header = new StringBuilder();
            header.Append("HTTP/1.1 ").Append(status).Append("\r\n");
            header.Append("Content-Type: ").Append(contentType).Append("\r\n");
            header.Append("Content-Length: ").Append(body.Length).Append("\r\n");
            header.Append("Connection: close\r\n\r\n");

            var head = Encoding.ASCII.GetBytes(header.ToString());
            stream.Write(head, 0, head.Length);
            stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        private static void WriteBody(NetworkStream stream, MockRoute route)
        {
            if (route.HeadDelayMs > 0)
            {
                Thread.Sleep(route.HeadDelayMs);
            }

            var body = route.Body ?? new byte[0];
            var header = new StringBuilder();
            header.Append("HTTP/1.1 200 OK\r\n");
            header.Append("Content-Type: ").Append(route.ContentType).Append("\r\n");
            header.Append("Content-Length: ").Append(body.Length).Append("\r\n");
            header.Append("Connection: close\r\n\r\n");

            var head = Encoding.ASCII.GetBytes(header.ToString());
            stream.Write(head, 0, head.Length);

            if (route.ChunkSize <= 0)
            {
                stream.Write(body, 0, body.Length);
                stream.Flush();
                return;
            }

            var offset = 0;
            while (offset < body.Length)
            {
                var count = Math.Min(route.ChunkSize, body.Length - offset);
                stream.Write(body, offset, count);
                stream.Flush();
                offset += count;
                if (route.ChunkDelayMs > 0)
                {
                    Thread.Sleep(route.ChunkDelayMs);
                }
            }
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
