using System;

namespace VaultDemo.Services
{
    /// <summary>
    /// 插件配置，序列化到插件私有数据目录的 settings.json。
    /// </summary>
    public class VaultSettings
    {
        /// <summary>WebDAV 仓库根地址，必须以 / 结尾，例如 https://nas.local:5006/vault/ 。</summary>
        public string WebDavUrl { get; set; } = string.Empty;

        public string Username { get; set; } = string.Empty;

        public string Password { get; set; } = string.Empty;

        /// <summary>本地应用安装根目录。</summary>
        public string LocalRoot { get; set; } = string.Empty;

        public int TimeoutSeconds { get; set; } = 60;

        /// <summary>
        /// 是否对归档内容做压缩。默认关闭：游戏资源（贴图/音频/视频）本身多为
        /// 已压缩格式，Deflate 基本省不下空间，反而吃 CPU、拖慢打包。
        /// </summary>
        public bool CompressOnArchive { get; set; } = false;

        /// <summary>
        /// 分片大小（MB）。归档会把应用切成这个大小的分片逐个上传 / 下载，
        /// 下载时逐片解包（边下边解），因此单次临时占用不会超过这个值。
        /// </summary>
        public int PartSizeMB { get; set; } = 256;

        /// <summary>下载时是否边下边解（下载分片与解包并行）。</summary>
        public bool PipelineExtract { get; set; } = true;

        private int concurrency = 6;

        /// <summary>
        /// 并发传输的分片数。
        ///
        /// 为什么需要它：.NET Framework 的 SslStream 在**单条连接上**只用一个线程做
        /// TLS 解密，实测单路 HTTPS 天花板约 27 MB/s（≈215 Mbps），远跑不满千兆；
        /// 开多路后每个连接各占一个核，就能把总吞吐抬到网卡上限。
        /// 实测（千兆内网，17 个 32MB 分片，HTTPS/WebDAV）：
        ///   1 路 26.9 | 2 路 49.4 | 3 路 72.0 | 4 路 93.0
        ///   6 路 96.1 | 8 路 85.8 | 12 路 95.4  (MB/s)
        /// 4 路是拐点，6 路到顶（≈770 Mbps）且最稳，再多只会互相抢带宽。
        /// 走明文 HTTP 时单路就能跑满千兆，此时设 1~2 即可。
        ///
        /// 注意：分片是并发传输的（上传下载都是），
        /// 峰值临时磁盘占用 ≈ (并发数 + 2) × 分片大小。
        /// </summary>
        public int Concurrency
        {
            get { return concurrency; }
            // 钳位放在 setter 上：settings.json 里手改的越界值也会在反序列化时被纠正
            set { concurrency = value < 1 ? 1 : (value > 16 ? 16 : value); }
        }

        /// <summary>
        /// 是否让 WebDAV 请求走系统代理。访问内网 NAS 时必须关闭，
        /// 否则请求会被本机代理（Clash 等）截走而连不上。
        /// </summary>
        public bool UseSystemProxy { get; set; } = false;

        /// <summary>单个文件传输失败后的重试次数。</summary>
        public int MaxRetries { get; set; } = 3;

        /// <summary>是否启用断点续传（保留 .part 并从中断处继续）。</summary>
        public bool ResumePartial { get; set; } = true;

        public VaultSettings Clone()
        {
            return new VaultSettings
            {
                WebDavUrl = this.WebDavUrl,
                Username = this.Username,
                Password = this.Password,
                LocalRoot = this.LocalRoot,
                TimeoutSeconds = this.TimeoutSeconds,
                CompressOnArchive = this.CompressOnArchive,
                PartSizeMB = this.PartSizeMB,
                PipelineExtract = this.PipelineExtract,
                Concurrency = this.Concurrency,
                UseSystemProxy = this.UseSystemProxy,
                MaxRetries = this.MaxRetries,
                ResumePartial = this.ResumePartial
            };
        }

        public bool IsConfigured
        {
            get { return !string.IsNullOrWhiteSpace(WebDavUrl); }
        }

        public static string DefaultLocalRoot()
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return System.IO.Path.Combine(profile, "VaultApps");
        }
    }
}
