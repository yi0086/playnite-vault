using System;
using PlayniteVault.Models;

namespace PlayniteVault.Services
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

        /// <summary>
        /// 建连 / 等响应头的上限（秒）。
        /// </summary>
        public int TimeoutSeconds { get; set; } = 15;

        /// <summary>
        /// **停滞超时（秒）**：连续这么久没有任何字节流动就断开重试。
        ///
        /// 这才是「速度从 20MB/s 逐渐掉到 0」对应的判据。
        /// 旧版本把单一超时当成「传输总超时」用，30 秒会把任何大分片判死。
        /// </summary>
        public int StallTimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// 应答超时（秒）：body 发完之后等服务端落盘回包的上限。
        /// NAS 收完一个区块还要合并/落盘，大区块要留足。
        /// </summary>
        public int ResponseTimeoutSeconds { get; set; } = 180;

        /// <summary>
        /// 是否对归档内容做压缩。默认关闭：游戏资源（贴图/音频/视频）本身多为
        /// 已压缩格式，Deflate 基本省不下空间，反而吃 CPU、拖慢打包。
        /// </summary>
        public bool CompressOnArchive { get; set; } = false;

        /// <summary>
        /// 【v2 遗留】分片大小（MB）。v3 请用 ChunkSizeMB；这里只为兼容旧 settings.json。
        /// </summary>
        public int PartSizeMB { get; set; } = 0;

        /// <summary>
        /// 【v3】区块大小（MB）。一个文件会被切成若干这样大小的区块，**可以跨块**。
        ///
        /// 为什么从 256MB 降到 32MB（实测结论）：
        ///   · 256MB 时「单个大文件独占一片」，Dead Cells 的 res.pak（1.93GB）
        ///     变成一个 1.93GB 的分片，单个 PUT 在 NAS 上要 100 秒 → 必然超时；
        ///   · 32MB 正好是之前测速时跑出 96MB/s 的那一档，单块完成快、重试代价低。
        /// </summary>
        public int ChunkSizeMB { get; set; } = 32;

        /// <summary>下载时是否边下边解（下载区块与解包并行）。</summary>
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
        /// 注意：峰值临时磁盘占用 ≈ (并发数 + 2) × 区块大小。
        /// </summary>
        public int Concurrency
        {
            get { return concurrency; }
            // 钳位放在 setter 上：settings.json 里手改的越界值也会在反序列化时被纠正
            set { concurrency = value < 1 ? 1 : (value > 16 ? 16 : value); }
        }

        private int uploadConcurrency;

        /// <summary>
        /// 上传并发。0 表示沿用 <see cref="Concurrency"/>。
        ///
        /// 单独留一个开关，是因为**瓶颈通常在 NAS 的写入侧**：
        /// 实测下载 88 MB/s 而上传只有 20~40 MB/s，且并发越高越容易停顿。
        /// 所以上传默认比下载保守（4 路），下载仍用 6 路。
        /// </summary>
        public int UploadConcurrency
        {
            get { return uploadConcurrency > 0 ? uploadConcurrency : DefaultUploadConcurrency; }
            set { uploadConcurrency = value < 0 ? 0 : (value > 16 ? 16 : value); }
        }

        /// <summary>上传默认并发：实测 4 路是拐点，比下载的 6 路保守一档。</summary>
        public const int DefaultUploadConcurrency = 4;

        /// <summary>
        /// 是否让 WebDAV 请求走系统代理。访问内网 NAS 时必须关闭，
        /// 否则请求会被本机代理（Clash 等）截走而连不上。
        /// </summary>
        public bool UseSystemProxy { get; set; } = false;

        /// <summary>单个文件传输失败后的重试次数。</summary>
        public int MaxRetries { get; set; } = 3;

        /// <summary>是否启用断点续传（保留 .part 并从中断处继续）。</summary>
        public bool ResumePartial { get; set; } = true;

        /// <summary>
        /// 仓库管理口令的哈希（只存派生值，不存明文）。
        /// 空表示还没设置过 —— 此时任何写操作都应先提示去设置，而不是直接执行。
        /// 真正的校验在 VaultAdmin 里做，这里只是本地缓存避免每次都去 NAS 取。
        /// </summary>
        public string AdminHash { get; set; } = string.Empty;

        public string AdminSalt { get; set; } = string.Empty;

        public int AdminIterations { get; set; } = 0;

        // ---------- 自动更新 ----------

        /// <summary>启动时自动检查插件更新。</summary>
        public bool AutoUpdateEnabled { get; set; } = true;

        /// <summary>
        /// 更新前先问一声。默认**关**（按需求是「自动更新 + 自动重启」）；
        /// 打开后只会下载并提示，重启由用户点头。
        /// </summary>
        public bool AutoUpdatePrompt { get; set; } = false;

        /// <summary>下载镜像：auto / github / gitee。</summary>
        public string UpdateMirror { get; set; } = "auto";

        /// <summary>用户选择「跳过此版本」时记下的版本号。</summary>
        public string SkippedVersion { get; set; } = string.Empty;

        // ---------- 自动刷新远端库 ----------

        /// <summary>定时把远端索引同步进 Playnite 库。</summary>
        public bool AutoRefreshEnabled { get; set; } = false;

        /// <summary>自动刷新间隔（分钟）。</summary>
        public int AutoRefreshMinutes { get; set; } = 30;

        /// <summary>启动后先刷新一次（30 秒延迟，等 Playnite 自己折腾完）。</summary>
        public bool AutoRefreshOnStartup { get; set; } = true;

        // ---------- 云存档（v1.7.0） ----------

        /// <summary>
        /// 总开关。默认**开着**但触发方式是手动的 —— 功能看得见、菜单里有，
        /// 但不会自己动你的存档。想自动上传得显式改下面那一项。
        /// </summary>
        public bool SaveSyncEnabled { get; set; } = true;

        /// <summary>什么时候自动动存档。默认只在你点的时候动。</summary>
        public SaveTriggerMode SaveTrigger { get; set; } = SaveTriggerMode.Manual;

        /// <summary>每个分支最多留几份快照；<b>0 = 无限</b>。</summary>
        public int SaveKeepPerBranch { get; set; } = 10;

        /// <summary>默认分支名（新游戏的存档都往这条上推）。</summary>
        public string SaveDefaultBranch { get; set; } = "main";

        /// <summary>恢复前先把当前状态也推一份快照（第二层保险）。</summary>
        public bool SaveBackupBeforeRestore { get; set; } = true;

        /// <summary>本地「恢复前留底」最多留几份（每游戏），0 = 不限。</summary>
        public int SaveKeepLocalBackups { get; set; } = 5;

        /// <summary>游戏运行时录一份目录指纹，退出后做会话差分（给「还是不知道存档在哪」用）。</summary>
        public bool SaveSessionSniff { get; set; } = true;

        // ---------- 界面（v1.8.0） ----------

        /// <summary>
        /// 侧边栏页的明暗模式：auto / light / dark，见 <see cref="UiThemeLight"/> 等常量。
        ///
        /// 为什么要留手动档：自动档是靠读 Playnite 的窗口背景色**猜**明暗的。
        /// 主题一旦把窗口底色做成半透明、贴图、或者在控件级另画背景，
        /// 猜出来的结果就可能和实际观感正好相反（于是出现「深底深字」这种一眼破相）。
        /// 猜错的时候用户必须能自己掰回来 —— 这就是这三档存在的唯一理由。
        /// </summary>
        public string UiTheme { get; set; } = UiThemeAuto;

        public const string UiThemeAuto = "auto";
        public const string UiThemeLight = "light";
        public const string UiThemeDark = "dark";

        /// <summary>
        /// 把 UiTheme 归一化成三档之一。
        /// settings.json 是给人手改的，写错值（大小写、空格、拼错）都不该让界面炸掉。
        /// </summary>
        public static string NormalizeUiTheme(string raw)
        {
            var value = (raw ?? string.Empty).Trim().ToLowerInvariant();
            return value == UiThemeLight || value == UiThemeDark ? value : UiThemeAuto;
        }

        /// <summary>界面上显示的名字，按档位取。</summary>
        public static string DescribeUiTheme(string raw)
        {
            switch (NormalizeUiTheme(raw))
            {
                case UiThemeLight:
                    return "浅色";
                case UiThemeDark:
                    return "深色";
                default:
                    return "跟随 Playnite";
            }
        }

        public VaultSettings Clone()
        {
            return new VaultSettings
            {
                WebDavUrl = this.WebDavUrl,
                Username = this.Username,
                Password = this.Password,
                LocalRoot = this.LocalRoot,
                TimeoutSeconds = this.TimeoutSeconds,
                StallTimeoutSeconds = this.StallTimeoutSeconds,
                ResponseTimeoutSeconds = this.ResponseTimeoutSeconds,
                CompressOnArchive = this.CompressOnArchive,
                PartSizeMB = this.PartSizeMB,
                ChunkSizeMB = this.ChunkSizeMB,
                PipelineExtract = this.PipelineExtract,
                Concurrency = this.Concurrency,
                UploadConcurrency = this.UploadConcurrency,
                UseSystemProxy = this.UseSystemProxy,
                MaxRetries = this.MaxRetries,
                ResumePartial = this.ResumePartial,
                AdminHash = this.AdminHash,
                AdminSalt = this.AdminSalt,
                AdminIterations = this.AdminIterations,
                AutoUpdateEnabled = this.AutoUpdateEnabled,
                AutoUpdatePrompt = this.AutoUpdatePrompt,
                UpdateMirror = this.UpdateMirror,
                SkippedVersion = this.SkippedVersion,
                AutoRefreshEnabled = this.AutoRefreshEnabled,
                AutoRefreshMinutes = this.AutoRefreshMinutes,
                AutoRefreshOnStartup = this.AutoRefreshOnStartup,
                SaveSyncEnabled = this.SaveSyncEnabled,
                SaveTrigger = this.SaveTrigger,
                SaveKeepPerBranch = this.SaveKeepPerBranch,
                SaveDefaultBranch = this.SaveDefaultBranch,
                SaveBackupBeforeRestore = this.SaveBackupBeforeRestore,
                SaveKeepLocalBackups = this.SaveKeepLocalBackups,
                SaveSessionSniff = this.SaveSessionSniff,
                UiTheme = this.UiTheme
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
