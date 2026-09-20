using System;
using System.Collections.Generic;

namespace PlayniteVault.Models
{
    /// <summary>
    /// 同步行为开关。
    /// </summary>
    public class SyncOptions
    {
        /// <summary>单个文件 / 区块失败后的最大重试次数。</summary>
        public int MaxRetries { get; set; } = 3;

        /// <summary>是否允许从 .part 断点续传。</summary>
        public bool ResumePartial { get; set; } = true;

        /// <summary>忽略比对结果，强制重新传输。</summary>
        public bool ForceTransfer { get; set; } = false;

        /// <summary>安装前要求的最小剩余空间（字节）。0 表示不检查。</summary>
        public long RequiredFreeBytes { get; set; } = 0;

        /// <summary>【v2 兼容】目标分片大小（字节）。新代码请用 ChunkSize。</summary>
        public long PartSize { get; set; } = 0;

        /// <summary>【v3】目标区块大小（字节）。0 表示用默认值。</summary>
        public long ChunkSize { get; set; } = 0;

        /// <summary>打包时是否对文件做 Deflate 压缩。游戏资源多半已压缩，默认关闭。</summary>
        public bool Compress { get; set; } = false;

        /// <summary>下载区块时是否与解包并行（边下边解）。</summary>
        public bool PipelineExtract { get; set; } = true;

        /// <summary>
        /// 并发传输的分片数。1 = 串行。
        ///
        /// 单条 HTTPS 连接在 .NET Framework 下有 ~27 MB/s 的天花板：
        /// SslStream 在一根连接上只有一个线程做 TLS 解密，千兆网根本喂不满。
        /// 实测（千兆内网 + 17 个 32MB 分片，HTTPS/WebDAV）：
        ///   1 路 26.9 | 2 路 49.4 | 3 路 72.0 | 4 路 93.0
        ///   6 路 96.1 | 8 路 85.8 | 12 路 95.4  (MB/s)
        /// 即 4 路已到拐点，6 路到顶且更稳，再多只会互相抢带宽。
        /// 明文 HTTP 下单路就能跑满千兆，此时设 1~2 即可。
        /// </summary>
        public int Concurrency { get; set; } = 6;

        /// <summary>
        /// 上传并发。0 表示沿用 <see cref="Concurrency"/>。
        ///
        /// 单独留一个开关是因为**写入侧通常是 NAS 的瓶颈**：实测下载 88 MB/s 而
        /// 上传只有 20~40 MB/s，且并发越高 NAS 越容易停顿。上传默认比下载保守。
        /// </summary>
        public int UploadConcurrency { get; set; } = 0;

        // ---------- 超时（毫秒）----------
        // 不能用 HttpWebRequest.Timeout 一把梭：它只管 GetRequestStream()/GetResponse()，
        // 而 PUT 的 GetResponse() 要等服务端把整个 body 收完落盘才回包，
        // 大分片必然撞上它 —— 实测 30 秒超时导致 1.93GB 的分片必然失败。

        /// <summary>建连超时（TCP/TLS）。</summary>
        public int ConnectTimeoutMs { get; set; } = 15000;

        /// <summary>
        /// **停滞超时**：连续多少毫秒没有任何字节流动就主动断开重试。
        /// 这才是「速度逐渐到 0」这种真实故障对应的判据。
        /// </summary>
        public int StallTimeoutMs { get; set; } = 30000;

        /// <summary>应答超时：body 发完后等服务端回包的上限。大区块要留足。</summary>
        public int ResponseTimeoutMs { get; set; } = 180000;

        public long EffectiveChunkSize
        {
            // PartSize 是对旧调用方（VaultPack CLI 的 --part-size）的兼容回退
            get { return ChunkSize > 0 ? ChunkSize : (PartSize > 0 ? PartSize : DefaultChunkSize); }
        }

        public int EffectiveUploadConcurrency
        {
            get { return UploadConcurrency > 0 ? UploadConcurrency : Concurrency; }
        }

        public const long DefaultPartSize = 256L * 1024 * 1024;

        /// <summary>默认区块大小 32 MB。实测这一档（而不是 256MB）才能让 NAS 稳定收下。</summary>
        public const long DefaultChunkSize = 32L * 1024 * 1024;
    }

    /// <summary>
    /// 同步进度快照。既给进度条用，也给「多行富文本」用。
    ///
    /// 设计约束（踩坑得来）：**同一个字段只允许一个写入方**。
    /// 并发下载 / 上传时有多条工作线程，如果它们和消费者线程一起写同一组
    /// 「当前片字节」，界面上的百分比就会在不同分片之间来回跳，看起来非常抖。
    ///
    /// 于是把进度分成三层，每层只有一个写入方：
    ///   1. BytesDone / BytesTotal —— 进度条的唯一真值，来自各分片完成量累加，
    ///      单调递增，既不倒退也不跳；
    ///   2. 分片计数（PartsDone / PartsInFlight）—— 只用原子计数维护；
    ///   3. SubStage* —— 与主进度并行发生、但不进进度条的那一项：
    ///      下载时是「解包」，归档时是「打包」。
    /// 至于 CurrentFile，只在「开始传输某一片」时写一次，而不是每个数据块都写，
    /// 否则文件名会在 6 条线程之间高频切换。
    /// </summary>
    public class SyncProgress
    {
        /// <summary>整体阶段名（下载 / 归档 / 元数据…）。整段流程里尽量保持不变。</summary>
        public string Phase { get; set; }

        // ---------- 总体：进度条的唯一真值来源 ----------

        /// <summary>当前正在传输的分片路径（只在开始传输某一片时更新）。</summary>
        public string CurrentFile { get; set; }

        public int FilesDone { get; set; }
        public int FilesTotal { get; set; }

        /// <summary>整体已处理字节（跨分片累计，单调递增）。</summary>
        public long BytesDone { get; set; }
        public long BytesTotal { get; set; }

        /// <summary>平滑后的瞬时速度（字节 / 秒）。</summary>
        public double BytesPerSecond { get; set; }

        // ---------- 分片计数 ----------

        /// <summary>已完成的分片数。</summary>
        public int PartsDone { get; set; }
        public int PartsTotal { get; set; }

        /// <summary>此刻正在传输（下载 / 上传）的分片数。</summary>
        public int PartsInFlight { get; set; }

        // ---------- 并行子阶段 ----------
        // 下载工作流：主进度 = 下载，子阶段 = 解包
        // 归档工作流：主进度 = 上传，子阶段 = 打包

        /// <summary>子阶段名称，例如「解包」「打包」。为空则不显示这一行。</summary>
        public string SubStageName { get; set; }

        public long SubStageBytesDone { get; set; }
        public long SubStageBytesTotal { get; set; }

        // ---------- 报告元信息 ----------

        /// <summary>
        /// 本次上报是否由「强制」触发（阶段切换 / 收尾）。
        /// 界面层据此决定要不要立刻重建文本，而不必干等下一个降频窗口。
        /// </summary>
        public bool IsForced { get; set; }

        /// <summary>整体完成度 0~1。数据不足时返回 -1。</summary>
        public double Fraction
        {
            get
            {
                if (BytesTotal <= 0)
                {
                    return -1;
                }
                var value = (double)BytesDone / BytesTotal;
                return value < 0 ? 0 : (value > 1 ? 1 : value);
            }
        }

        /// <summary>预计剩余时间；无法估算时返回 null。</summary>
        public TimeSpan? Remaining
        {
            get
            {
                if (BytesPerSecond <= 1 || BytesTotal <= BytesDone)
                {
                    return null;
                }
                return TimeSpan.FromSeconds((BytesTotal - BytesDone) / BytesPerSecond);
            }
        }

        /// <summary>拼给单行日志用的一行文字。</summary>
        public string Describe()
        {
            if (BytesTotal <= 0)
            {
                return Phase ?? "处理中...";
            }

            return string.Format("{0}  {1}/{2}  {3}  {4}",
                Phase ?? "传输",
                FilesDone,
                FilesTotal,
                FormatSize(BytesDone) + " / " + FormatSize(BytesTotal),
                FormatSpeed(BytesPerSecond));
        }

        /// <summary>
        /// 多行富文本，用于 Playnite 的全局进度窗口。
        /// 每一行的数值都只有一个写入方，所以整段文字不会来回抖。
        /// </summary>
        public string DescribeRich()
        {
            var lines = new List<string>();
            var fraction = Fraction;

            // 1) 整体百分比与总量
            lines.Add(string.Format("{0}   {1}   {2} / {3}",
                Phase ?? "传输",
                fraction >= 0 ? (fraction * 100).ToString("0.0") + "%" : "--",
                FormatSize(BytesDone),
                FormatSize(BytesTotal)));

            // 2) 速度与剩余时间
            lines.Add(string.Format("速度 {0}{1}",
                FormatSpeed(BytesPerSecond),
                FormatRemaining(Remaining)));

            // 3) 区块 / 文件计数（有区块时优先展示区块）
            if (PartsTotal > 1)
            {
                var text = string.Format("区块 {0}/{1} 完成", PartsDone, PartsTotal);
                if (PartsInFlight > 0)
                {
                    text += string.Format(" · {0} 块进行中", PartsInFlight);
                }
                lines.Add(text);
            }
            else if (FilesTotal > 1)
            {
                lines.Add(string.Format("文件 {0}/{1}", FilesDone, FilesTotal));
            }

            // 4) 并行子阶段（解包 / 打包）的进度
            var sub = DescribeStage(SubStageName, SubStageBytesDone, SubStageBytesTotal);
            if (sub != null)
            {
                lines.Add(sub);
            }

            // 5) 当前正在传输的分片
            if (!string.IsNullOrEmpty(CurrentFile))
            {
                lines.Add(Shorten(CurrentFile, 78));
            }

            return string.Join(Environment.NewLine, lines.ToArray());
        }

        /// <summary>子阶段拼成一行，例如「解包 88%  88.1 MB/100.0 MB」。</summary>
        private static string DescribeStage(string name, long done, long total)
        {
            if (string.IsNullOrEmpty(name) || total <= 0)
            {
                return null;
            }

            if (done < 0) done = 0;
            if (done > total) done = total;

            return string.Format("{0} {1}%   {2}/{3}",
                name,
                done * 100 / total,
                FormatSize(done),
                FormatSize(total));
        }

        private static string Shorten(string value, int max)
        {
            if (value == null)
            {
                return string.Empty;
            }
            return value.Length <= max ? value : "..." + value.Substring(value.Length - max + 3);
        }

        public static string FormatSpeed(double bytesPerSecond)
        {
            if (bytesPerSecond <= 0)
            {
                return "-- /s";
            }
            return FormatSize((long)bytesPerSecond) + "/s";
        }

        public static string FormatRemaining(TimeSpan? remaining)
        {
            if (remaining == null)
            {
                return string.Empty;
            }

            var span = remaining.Value;
            if (span.TotalHours >= 1)
            {
                return "  剩余 " + span.ToString(@"h\:mm\:ss");
            }
            return "  剩余 " + span.ToString(@"m\:ss");
        }

        public static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024L * 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024).ToString("0.0") + " MB";
            return (bytes / 1024.0 / 1024 / 1024).ToString("0.00") + " GB";
        }
    }

    /// <summary>
    /// 同步结果统计。
    /// </summary>
    public class SyncResult
    {
        public int Transferred { get; set; }
        public int Skipped { get; set; }
        public int Failed { get; set; }
        public long BytesTransferred { get; set; }

        /// <summary>实际传输过的区块数（v2 时是分片数）。</summary>
        public int PartsTransferred { get; set; }

        /// <summary>因为远端已有（v3 按内容寻址判断）而跳过的区块数。</summary>
        public int PartsSkipped { get; set; }

        /// <summary>传输过程中发生的重试次数（含停滞重连）。</summary>
        public int Retries { get; set; }

        /// <summary>随包上传的图片张数（封面 / 背景 / 图标）。</summary>
        public int ImagesUploaded { get; set; }

        public TimeSpan Elapsed { get; set; }

        public string Describe()
        {
            var chunks = PartsTransferred > 0 || PartsSkipped > 0
                ? string.Format("，区块 {0} 个（跳过 {1}）", PartsTransferred, PartsSkipped)
                : string.Empty;
            var images = ImagesUploaded > 0 ? string.Format("，随包图片 {0} 张", ImagesUploaded) : string.Empty;
            var retries = Retries > 0 ? string.Format("，重试 {0} 次", Retries) : string.Empty;
            return string.Format("传输 {0} 个文件（{1}）{2}{3}{4}，跳过 {5} 个，用时 {6:0.0} 秒",
                Transferred, SyncProgress.FormatSize(BytesTransferred), chunks, images, retries,
                Skipped, Elapsed.TotalSeconds);
        }
    }
}
