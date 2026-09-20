using System;
using System.Collections.Generic;

namespace PlayniteVault.Models
{
    /// <summary>
    /// 主题同步（Playnite 的 Themes 目录 ⇄ WebDAV 仓库的同名镜像）。
    ///
    /// 与游戏归档（apps/ 下的内容寻址区块）**刻意用两套不同的存储模型**：
    ///   · 游戏是**大二进制**，切成 32MB 区块按内容寻址存，图的是去重和断点续传；
    ///   · 主题是**一堆小文本/图片**（xaml/yaml/png/ttf），而且 Playnite 本来就是
    ///     按目录直接读的。所以这里存成**普通文件镜像**：
    ///
    ///         themes/index.json                        全局索引（每个主题一行指纹）
    ///         themes/{主题Id}/manifest.json            该主题的文件清单（路径 → sha1）
    ///         themes/{主题Id}/<原始相对路径>            真实的文件，目录结构照搬
    ///
    /// 好处：NAS 上那棵树本身就是一份能用的主题目录，能直接浏览、能整目录拷回来；
    /// 增量同步只要比哈希，改一个文件不用重写一整块。
    /// </summary>
    public class ThemeFileEntry
    {
        /// <summary>相对主题根目录的路径，一律用正斜杠，方便跨平台比较。</summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>文件内容的 SHA-1（小写十六进制）。</summary>
        public string Sha1 { get; set; } = string.Empty;

        public long Bytes { get; set; }

        /// <summary>UTC 修改时间。**只在冲突时用来定胜负**（两边都改过 → 时间新的赢）。</summary>
        public DateTime Modified { get; set; }
    }

    public class ThemeManifest
    {
        /// <summary>主题 Id = Themes\Desktop|Fullscreen 下的那个目录名。</summary>
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        /// <summary>Desktop 或 Fullscreen。</summary>
        public string Mode { get; set; } = string.Empty;

        public string Version { get; set; } = string.Empty;

        public DateTime UpdatedAt { get; set; }

        /// <summary>整棵子树的指纹（由「路径:哈希」排序后算 SHA-1），用来快速判断「变没变」。</summary>
        public string Fingerprint { get; set; } = string.Empty;

        public long Bytes { get; set; }

        public List<ThemeFileEntry> Files { get; set; } = new List<ThemeFileEntry>();
    }

    public class ThemeIndexEntry
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Mode { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public int Files { get; set; }
        public long Bytes { get; set; }
        public string Fingerprint { get; set; } = string.Empty;
        public DateTime UpdatedAt { get; set; }
    }

    /// <summary>远端 themes/index.json 的结构。</summary>
    public class ThemeIndex
    {
        /// <summary>写死一个标记，防止把别人的 index.json 当成我们的（同名冲突时能看出来）。</summary>
        public string Kind { get; set; } = ThemeIndexKind;

        public int Schema { get; set; } = CurrentSchema;

        public DateTime UpdatedAt { get; set; }

        public List<ThemeIndexEntry> Themes { get; set; } = new List<ThemeIndexEntry>();

        public const string ThemeIndexKind = "playnite-vault-themes";
        public const int CurrentSchema = 1;
    }

    /// <summary>
    /// 本地记的「上次同步时远端长什么样」。三方比对的基准：
    ///   基准 + 本地现状 + 远端现状 才能分清「我改的」「他改的」「我们都改了」。
    /// 少了它就只能二选一，删掉一个主题会被当成「另一侧新增」又拉回来。
    /// </summary>
    public class ThemeSyncState
    {
        public string Kind { get; set; } = "playnite-vault-theme-sync-state";

        /// <summary>上次同步用的主题根目录（换目录相当于换了对象，要重来）。</summary>
        public string Root { get; set; } = string.Empty;

        public DateTime LastSyncAt { get; set; }

        /// <summary>主题 Id → 上次同步后**远端**的指纹。</summary>
        public Dictionary<string, string> RemoteFingerprints { get; set; }
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>主题 Id → 上次同步后**本地**的指纹（用来判断是不是「我改的」）。</summary>
        public Dictionary<string, string> LocalFingerprints { get; set; }
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public enum ThemeSyncMode
    {
        /// <summary>只上传（本地 → 远端）。备份场景。</summary>
        Upload = 0,

        /// <summary>只下载（远端 → 本地）。迁移/还原场景。</summary>
        Download = 1,

        /// <summary>双向（各自的新增与改动互相同步）。</summary>
        Both = 2
    }

    public enum ThemeAction
    {
        UploadTheme,
        DownloadTheme,
        UploadFile,
        DownloadFile,
        SaveConflict,
        Skip
    }

    public class ThemeSyncOptions
    {
        public ThemeSyncMode Mode { get; set; } = ThemeSyncMode.Both;

        /// <summary>只算计划不落盘，用来先给用户看「会发生什么」。</summary>
        public bool DryRun { get; set; } = false;

        /// <summary>强制整树上传（跳过指纹相同的快路径），用于修「指纹对但文件坏了」。</summary>
        public bool Force { get; set; } = false;
    }

    public class ThemeSyncCounters
    {
        public int ThemesUploaded { get; set; }
        public int ThemesDownloaded { get; set; }
        public int FilesUploaded { get; set; }
        public int FilesDownloaded { get; set; }
        public int FilesSkipped { get; set; }
        public int Conflicts { get; set; }
        public int ThemesUnchanged { get; set; }
        public long BytesUp { get; set; }
        public long BytesDown { get; set; }

        /// <summary>
        /// 远端有、本地没有，而且这次模式不让下载的主题（只提示，**不动它们**）。
        /// 主题同步刻意不做「本地删了就把远端也删了」——远端是只增不减的档案。
        /// </summary>
        public List<string> RemoteOnly { get; set; } = new List<string>();

        public string Describe()
        {
            return string.Format(
                "上传 {0} 个主题/{1} 个文件（{2}），下载 {3} 个主题/{4} 个文件（{5}），"
                + "冲突 {6}，跳过 {7}，未变 {8}",
                ThemesUploaded, FilesUploaded, SyncProgress.FormatSize(BytesUp),
                ThemesDownloaded, FilesDownloaded, SyncProgress.FormatSize(BytesDown),
                Conflicts, FilesSkipped, ThemesUnchanged);
        }
    }

    /// <summary>
    /// 同步过程的汇报口。刻意不依赖 Playnite 的进度 API ——
    /// 这样同一个引擎既能给插件界面用，也能给命令行工具和无头自检用。
    /// </summary>
    public interface IThemeSyncReporter
    {
        void Stage(string text);
        void Progress(SyncProgress progress);
        void Log(string line);
    }

    /// <summary>什么都不做的实现（自检/静默用）。</summary>
    public class NullThemeReporter : IThemeSyncReporter
    {
        public void Stage(string text) { }
        public void Progress(SyncProgress progress) { }
        public void Log(string line) { }
    }
}
