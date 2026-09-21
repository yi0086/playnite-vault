using System;
using System.Collections.Generic;

namespace PlayniteVault.Models
{
    /// <summary>
    /// 当前清单 / 索引格式版本。
    /// 1 = 逐文件直传（apps/{id}/files/...）
    /// 2 = 按序号分片（apps/{id}/parts/part-XXXX.bin）
    /// 3 = 内容寻址区块（apps/{id}/chunks/&lt;sha1&gt;.bin），一个文件可跨多个区块
    /// 读取端三种都要能处理：由 AppManifest.Layout 按字段推断，不依赖 Schema 字段
    /// （旧清单里根本没有这个字段，靠默认值初始化会被误判成最新版）。
    /// </summary>
    public static class VaultSchema
    {
        public const int Current = 3;
    }

    /// <summary>清单的实际布局。读取端据此分派还原逻辑。</summary>
    public enum VaultLayout
    {
        /// <summary>v1：apps/{id}/files/&lt;原始相对路径&gt;</summary>
        Files = 1,

        /// <summary>v2：apps/{id}/parts/part-XXXX.bin，整文件不跨片</summary>
        Parts = 2,

        /// <summary>v3：apps/{id}/chunks/&lt;sha1&gt;.bin，内容寻址、文件可跨块</summary>
        Chunks = 3
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

        /// <summary>分片数量；0 表示未分包（v1 布局）。v2 用。</summary>
        public int PartCount { get; set; }

        /// <summary>区块数量；0 表示不是 v3 布局。</summary>
        public int ChunkCount { get; set; }

        public bool Packed { get; set; }

        /// <summary>随包元数据（名称/简介/开发商/类型/标签/评分/图片等）。</summary>
        public AppMetadata Metadata { get; set; }

        /// <summary>
        /// 【v1.8】归档时那条 Playnite 库记录的 GUID（<c>Game.Id</c>）。
        ///
        /// <para>为什么需要它：仓库里的 <see cref="Id"/> 是从**安装目录名**压出来的 slug
        /// （见 <c>VaultService.MakeAppId</c>），它只保证「同一台机器上同一次归档稳定」，
        /// 并不能用来回答「这条库里记录，仓库里到底有没有」——目录改名、手工改名、
        /// 或者显示名和目录名不一致，都会让推导出来的 slug 对不上。
        /// 而 <c>Game.Id</c> 是 Playnite 自己给的、跨机器不变量，才是可靠的钥匙。</para>
        ///
        /// <para>老条目没有这个字段（空字符串）→ 读取端必须退回按 slug 比对。</para>
        /// </summary>
        public string PlayniteGameId { get; set; }

        /// <summary>
        /// 【v1.8】归档时那条记录的 <c>Game.GameId</c> —— **库内标识**
        /// （Steam 游戏就是 appid，别的库插件给什么就是什么）。
        ///
        /// <para>为什么第二把钥匙取的是它而不是「数据库自增 id」：查过 SDK ——
        /// <c>Playnite.SDK.Models.Game</c> 上**根本没有 DatabaseId 这个成员**
        /// （<c>IGameDatabase</c> 也没有按 int 取游戏的入口），所以拿不到。
        /// <c>Game.GameId</c> 是能拿到的第二稳定键，而且它在「游戏删掉再重新导入」
        /// 这一档上比 <see cref="PlayniteGameId"/>（库记录 GUID，重导入会重新分配）更稳。</para>
        ///
        /// <para>老条目没有这个字段（空字符串）→ 读取端退回按 slug 比对。</para>
        /// </summary>
        public string PlayniteLibraryId { get; set; }
    }

    /// <summary>
    /// 单个应用的详细清单 apps/{id}/manifest.json。
    ///
    /// 一份清单同时能表达三种布局（靠哪个集合非空区分），
    /// 所以读取端用 <see cref="Layout"/> 分派即可，不必看 Schema 数值。
    /// </summary>
    public class AppManifest
    {
        /// <summary>
        /// 写出时是 <see cref="VaultSchema.Current"/>。
        /// 默认 **0** 而不是当前版本：旧清单里没有这个字段，若默认成 3
        /// 就会把 v2 的归档误判成 v3。0 表示「未标注，按字段推断」。
        /// </summary>
        public int Schema { get; set; }

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

        /// <summary>v2：目标分片大小（字节）。</summary>
        public long PartSize { get; set; }

        /// <summary>v3：目标区块大小（字节）。</summary>
        public long ChunkSize { get; set; }

        /// <summary>v2 的分片表。</summary>
        public List<PartEntry> Parts { get; set; } = new List<PartEntry>();

        /// <summary>v3 的区块表（内容寻址）。</summary>
        public List<ChunkEntry> Chunks { get; set; } = new List<ChunkEntry>();

        public List<FileEntry> Files { get; set; } = new List<FileEntry>();

        public AppMetadata Metadata { get; set; }

        /// <summary>【v1.8】归档来源那条 Playnite 记录的 GUID，见 <see cref="AppEntry.PlayniteGameId"/>。</summary>
        public string PlayniteGameId { get; set; }

        /// <summary>【v1.8】归档来源那条记录的 <c>Game.GameId</c>（库内标识），见 <see cref="AppEntry.PlayniteLibraryId"/>。</summary>
        public string PlayniteLibraryId { get; set; }

        /// <summary>按字段推断实际布局。读取端一律用这个，不要看 Schema 数值。</summary>
        public VaultLayout Layout
        {
            get
            {
                if (Chunks != null && Chunks.Count > 0)
                {
                    return VaultLayout.Chunks;
                }
                if (Packed && Parts != null && Parts.Count > 0)
                {
                    return VaultLayout.Parts;
                }
                return VaultLayout.Files;
            }
        }

        /// <summary>网络上的总字节数（v3 用区块表算；其余走文件表）。</summary>
        public long StoredBytes
        {
            get
            {
                if (Layout == VaultLayout.Chunks)
                {
                    long sum = 0;
                    foreach (var chunk in Chunks)
                    {
                        sum += chunk.StoredBytes;
                    }
                    return sum;
                }
                if (Layout == VaultLayout.Parts)
                {
                    long sum = 0;
                    foreach (var part in Parts)
                    {
                        sum += part.StoredBytes;
                    }
                    return sum;
                }
                return TotalBytes;
            }
        }
    }

    /// <summary>
    /// v3 的一个区块。**文件名就是内容的 SHA-1**，因此区块一旦写出就不可变：
    /// 「远端是否已有这一块」可以只用一个 HEAD 判断，失败重传天然可续传。
    /// </summary>
    public class ChunkEntry
    {
        /// <summary>在 Chunks[] 里的下标（清单里用下标引用，比存 40 位哈希省空间）。</summary>
        public int Index { get; set; }

        /// <summary>内容 SHA-1，40 位小写 hex。写入前为空。</summary>
        public string Id { get; set; }

        /// <summary>相对 apps/{Id}/ 的路径，例如 chunks/9f2a….bin。由 Id 派生。</summary>
        public string Path { get; set; }

        /// <summary>区块文件在网络上的字节数。</summary>
        public long StoredBytes { get; set; }

        /// <summary>区块里所有片段还原后的字节数之和。</summary>
        public long RawBytes { get; set; }

        /// <summary>由哈希算出仓库内相对路径。区块不可变，所以路径只取决于内容。</summary>
        public static string PathFor(string id)
        {
            return "chunks/" + id + ".bin";
        }
    }

    /// <summary>
    /// v3：一个文件落在某个区块里的一段。
    ///
    /// 因为切块是「先把当前块填满才换块」，所以**同一个文件在同一个区块里最多只有一段**
    /// （一段写完时，要么块满了、要么文件没了）。读取端不必处理「同块同文件多段」。
    /// </summary>
    public class PieceEntry
    {
        /// <summary>所属区块在 Chunks[] 里的下标。</summary>
        public int Chunk { get; set; }

        /// <summary>在区块文件里的起始偏移。</summary>
        public long Offset { get; set; }

        /// <summary>在区块里实际占用的字节数（压缩后）。</summary>
        public long Length { get; set; }

        /// <summary>还原后的字节数。</summary>
        public long Size { get; set; }

        /// <summary>本文件内的起始偏移。相邻片段必须首尾相接。</summary>
        public long FileOffset { get; set; }

        /// <summary>存储方式：null / "store" / "deflate"。</summary>
        public string Compression { get; set; }

        public bool IsStored
        {
            get { return string.IsNullOrEmpty(Compression) || Compression == "store"; }
        }
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
    /// v3 用 <see cref="Pieces"/>；v2 用 Part/Offset/StoredSize；v1 只用 Path/Size。
    /// </summary>
    public class FileEntry
    {
        /// <summary>相对路径，统一使用 / 分隔。</summary>
        public string Path { get; set; }

        /// <summary>文件原始大小（解包后）。</summary>
        public long Size { get; set; }

        public string Hash { get; set; }

        /// <summary>【v2】所属分片序号；-1 表示 v1 的逐文件布局。</summary>
        public int Part { get; set; } = -1;

        /// <summary>【v2】在分片内的字节偏移。</summary>
        public long Offset { get; set; }

        /// <summary>【v2】在分片内实际占用的字节数（压缩后）。</summary>
        public long StoredSize { get; set; }

        /// <summary>【v2】存储方式：null / "store" / "deflate"。</summary>
        public string Compression { get; set; }

        /// <summary>【v3】这个文件被切成的片段，按 FileOffset 升序。</summary>
        public List<PieceEntry> Pieces { get; set; }

        public bool IsStored
        {
            get { return string.IsNullOrEmpty(Compression) || Compression == "store"; }
        }

        /// <summary>v3 的片段加起来是否正好等于 Size（清单损坏检测）。</summary>
        public bool PiecesAreContiguous()
        {
            if (Pieces == null || Pieces.Count == 0)
            {
                return Size == 0;
            }

            long cursor = 0;
            foreach (var piece in Pieces)
            {
                if (piece.FileOffset != cursor || piece.Size <= 0)
                {
                    return false;
                }
                cursor += piece.Size;
            }
            return cursor == Size;
        }
    }

    /// <summary>
    /// 仓库连通性的四档状态。侧边栏右上角那个小圆点就是它的可视化。
    ///
    /// 之所以要区分 <see cref="Timeout"/> 和 <see cref="Failed"/>：
    /// 「NAS 关机 / 网线掉了」和「密码错了」是两件完全不同的事 ——
    /// 前者等一会儿就好，后者必须去改设置。一个红点把这两种情况糊在一起，
    /// 用户只会反复重试同一个改不动的设置。
    /// </summary>
    public enum VaultHealthState
    {
        /// <summary>绿：探测通了。</summary>
        Ok = 0,

        /// <summary>黄：连不上，但是超时/不可达这一类「可能是暂时的」。</summary>
        Timeout = 1,

        /// <summary>红：连上了但被拒（401/403），或者地址、证书这类配置问题。</summary>
        Failed = 2,

        /// <summary>灰：还没配置 WebDAV 地址，谈不上连不连得上。</summary>
        NotConfigured = 3,

        /// <summary>正在探测。</summary>
        Checking = 4
    }

    /// <summary>一次连通性探测的完整结果：状态 + 能直接给用户看的说明。</summary>
    public class RepositoryHealth
    {
        public VaultHealthState State { get; set; } = VaultHealthState.Checking;

        /// <summary>一句话总结，直接显示在提示浮窗的标题位置。</summary>
        public string Summary { get; set; } = string.Empty;

        /// <summary>详细说明（多行），放浮窗正文。</summary>
        public string Detail { get; set; } = string.Empty;

        /// <summary>探测完成的时间（本地时间）。</summary>
        public DateTime CheckedAt { get; set; } = DateTime.Now;

        /// <summary>探测用的仓库地址，便于用户在浮窗里核对。</summary>
        public string Url { get; set; } = string.Empty;

        public static RepositoryHealth NotConfigured(string url)
        {
            return new RepositoryHealth
            {
                State = VaultHealthState.NotConfigured,
                Summary = "还没配置仓库地址",
                Detail = "去「设置」里填上 WebDAV 地址和账号，这里就会开始探测。",
                Url = url
            };
        }
    }

    /// <summary>
    /// 「仓库」页卡片墙上的一张卡：本地 Playnite 里的一条应用，
    /// 外加「它在仓库里有没有」这个判定结果。
    /// </summary>
    public class LocalAppCard
    {
        /// <summary>Playnite 库记录 GUID（<c>Game.Id</c>）。卡片的主键。</summary>
        public string GameId { get; set; }

        /// <summary><c>Game.GameId</c> —— 库内标识（Steam 就是 appid）；手动添加的游戏可能是空串。</summary>
        public string LibraryId { get; set; }

        public string Name { get; set; }

        /// <summary>本机安装目录；空表示没装（只在库里）。</summary>
        public string InstallDir { get; set; }

        /// <summary>本机确实装了（安装目录存在）。</summary>
        public bool IsInstalled { get; set; }

        /// <summary>仓库里已经有一条对应的归档。</summary>
        public bool InRepository { get; set; }

        /// <summary>命中的仓库条目 Id（用于显示与跳转）。</summary>
        public string RepoAppId { get; set; }

        /// <summary>仓库里那份的体积。</summary>
        public long RepoBytes { get; set; }

        /// <summary>
        /// 本机那份的体积。0 = 还没量出来（本机没装，或者后台扫描还没回来）。
        ///
        /// <para>刻意**不在建卡片时算** —— 一个 100GB 的游戏要遍历几万个文件，
        /// 放在 UI 线程上就是一秒起步的卡顿。界面拿到卡片后在后台量，量完只改一个文本块。</para>
        /// </summary>
        public long LocalBytes { get; set; }

        /// <summary>本机这份是从仓库拉下来的（本地索引里有记录）→ 可以「卸载」。</summary>
        public bool Downloaded { get; set; }

        /// <summary>从仓库拉下来时装到的目录（卸载要用；空则回退到默认目录）。</summary>
        public string DownloadedDir { get; set; }

        /// <summary>封面图的绝对路径；解析不出来就是空，卡片会退化成一个占位块。</summary>
        public string CoverPath { get; set; }

        /// <summary>
        /// 判定依据，直接显示在卡片提示里。
        /// 用户问「凭什么说它传过了」时，能一眼看到是拿哪个键对上的。
        /// </summary>
        public string MatchNote { get; set; }
    }

    /// <summary>
    /// 「删掉仓库里一个应用」的结果。
    ///
    /// <para>为什么单独要一个类型而不是返回 bool：删除失败的原因对用户来说是**三种完全不同的下一步** ——
    /// 口令打错（重输）、仓库压根没设口令（先去设置）、网络/服务端出错（去查连接）。
    /// 一个 bool 会把它们糊成一句「删除失败」，那用户就只能瞎试。</para>
    /// </summary>
    public class RepositoryDeleteResult
    {
        /// <summary>口令验证通过且删除完成。</summary>
        public bool Ok { get; set; }

        /// <summary>口令不对。</summary>
        public bool PasswordWrong { get; set; }

        /// <summary>仓库还没设置过管理口令。</summary>
        public bool PasswordNotSet { get; set; }

        /// <summary>远端被清理掉的文件数。</summary>
        public int RemovedFiles { get; set; }

        /// <summary>出错说明（网络/服务端）。</summary>
        public string Error { get; set; }

        public static RepositoryDeleteResult Fail(string error)
        {
            return new RepositoryDeleteResult { Error = error };
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
