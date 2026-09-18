using System;
using System.Collections.Generic;

namespace VaultDemo.Models
{
    /// <summary>
    /// 当前清单 / 索引格式版本。
    /// 1 = 逐文件直传（apps/{id}/files/...）
    /// 2 = 分包打包（apps/{id}/parts/part-XXXX.bin）+ 随包元数据
    /// 读取端两种都要能处理：分片字段缺失（FileEntry.Part &lt; 0）即视为 v1。
    /// </summary>
    public static class VaultSchema
    {
        public const int Current = 2;
    }

    /// <summary>
    /// 远端仓库总索引 index.json。
    /// </summary>
    public class RepositoryIndex
    {
        public int Schema { get; set; } = VaultSchema.Current;
        public string Name { get; set; }
        public DateTime UpdatedAt { get; set; }
        public List<AppEntry> Apps { get; set; } = new List<AppEntry>();
    }

    /// <summary>
    /// 索引中的单条应用记录。
    /// 这里也带上元数据：库导入（GetGames）只读 index.json，
    /// 逐个下载 manifest 太慢，所以展示所需的信息必须在索引里。
    /// </summary>
    public class AppEntry
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Version { get; set; }
        public long TotalBytes { get; set; }
        public int FileCount { get; set; }

        /// <summary>启动程序相对路径，相对于应用目录。</summary>
        public string LaunchExe { get; set; }

        /// <summary>分片数量；0 表示未分包（v1 布局）。</summary>
        public int PartCount { get; set; }

        public bool Packed { get; set; }

        /// <summary>随包元数据（名称/简介/开发商/类型/标签/评分/图片等）。</summary>
        public AppMetadata Metadata { get; set; }
    }

    /// <summary>
    /// 单个应用的详细清单 apps/{id}/manifest.json。
    /// </summary>
    public class AppManifest
    {
        public int Schema { get; set; } = VaultSchema.Current;
        public string Id { get; set; }
        public string Name { get; set; }
        public string Version { get; set; }
        public DateTime UpdatedAt { get; set; }
        public string LaunchExe { get; set; }
        public string Arguments { get; set; }
        public string WorkingDir { get; set; }

        /// <summary>安装后的原始总字节数（所有 FileEntry.Size 之和）。</summary>
        public long TotalBytes { get; set; }

        public bool Packed { get; set; }

        /// <summary>目标分片大小（字节）。单个超过它的大文件会独占一个分片。</summary>
        public long PartSize { get; set; }

        public List<PartEntry> Parts { get; set; } = new List<PartEntry>();

        public List<FileEntry> Files { get; set; } = new List<FileEntry>();

        public AppMetadata Metadata { get; set; }
    }

    /// <summary>
    /// 一个分片（part）。分片文件本身只是字节流的拼接，
    /// 每个文件的位置由 FileEntry 的 Part/Offset/StoredSize 描述，
    /// 因此下载完一个分片就能立刻解包（不必等整个应用传完）。
    /// </summary>
    public class PartEntry
    {
        public int Index { get; set; }

        /// <summary>相对 apps/{id}/ 的路径，例如 parts/part-0000.bin。</summary>
        public string Path { get; set; }

        /// <summary>分片文件在网络上的字节数。</summary>
        public long StoredBytes { get; set; }

        /// <summary>分片解包后的字节数。</summary>
        public long RawBytes { get; set; }
    }

    /// <summary>
    /// 清单中的单文件记录。
    /// </summary>
    public class FileEntry
    {
        /// <summary>相对路径，统一使用 / 分隔。</summary>
        public string Path { get; set; }

        /// <summary>文件原始大小（解包后）。</summary>
        public long Size { get; set; }

        public string Hash { get; set; }

        /// <summary>所属分片序号；-1 表示 v1 的逐文件布局。</summary>
        public int Part { get; set; } = -1;

        /// <summary>在分片内的字节偏移。</summary>
        public long Offset { get; set; }

        /// <summary>在分片内实际占用的字节数（压缩后）。</summary>
        public long StoredSize { get; set; }

        /// <summary>存储方式：null / "store" / "deflate"。</summary>
        public string Compression { get; set; }

        public bool IsStored
        {
            get { return string.IsNullOrEmpty(Compression) || Compression == "store"; }
        }
    }

    /// <summary>
    /// 本地已安装记录。
    /// </summary>
    public class LocalEntry
    {
        public string AppId { get; set; }
        public string InstallDir { get; set; }
        public string Version { get; set; }
        public string LaunchExe { get; set; }
        public DateTime InstalledAt { get; set; }
    }

    /// <summary>
    /// 插件私有数据目录下的本地索引 local-index.json。
    /// </summary>
    public class LocalIndex
    {
        public List<LocalEntry> Apps { get; set; } = new List<LocalEntry>();

        public LocalEntry Find(string appId)
        {
            if (Apps == null) return null;
            foreach (var entry in Apps)
            {
                if (string.Equals(entry.AppId, appId, StringComparison.OrdinalIgnoreCase))
                {
                    return entry;
                }
            }
            return null;
        }

        public void Upsert(LocalEntry entry)
        {
            if (Apps == null) Apps = new List<LocalEntry>();
            Apps.RemoveAll(x => string.Equals(x.AppId, entry.AppId, StringComparison.OrdinalIgnoreCase));
            Apps.Add(entry);
        }

        public void Remove(string appId)
        {
            if (Apps == null) return;
            Apps.RemoveAll(x => string.Equals(x.AppId, appId, StringComparison.OrdinalIgnoreCase));
        }
    }
}
