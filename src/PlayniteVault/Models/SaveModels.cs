using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace PlayniteVault.Models
{
    /// <summary>
    /// 存档元素的类型。照抄 Playnite 的 <c>GameSaveType</c>：
    /// 它那边只有 File / Directory，注释写着「初步考虑有文件夹，文件，以及注册表」——
    /// 注册表那类官方没做，我们也不做（要改注册表就得提权，代价和风险都不对等）。
    /// </summary>
    public enum SaveElementType
    {
        File = 0,
        Directory = 1
    }

    /// <summary>快照是什么时候、被什么动作造出来的。</summary>
    public enum SaveSnapshotOrigin
    {
        /// <summary>用户手动点上传。</summary>
        Manual = 0,

        /// <summary>游戏退出后自动上传。</summary>
        AutoOnStop = 1,

        /// <summary>恢复前自动给当前本地状态留的底。</summary>
        BeforeRestore = 2,

        /// <summary>从别处下载回来的（远端快照落地到本地）。</summary>
        Imported = 3
    }

    /// <summary>
    /// 一条存档路径定义。
    ///
    /// <c>Title</c> 是**跨机器对齐用的唯一标识** —— 这不是我发明的，是 Playnite 自己的做法：
    /// <c>SavePath._title</c> 的源码注释写着「不同的电脑就靠这个来进行对齐，相当于 savepath 的唯一标识」。
    /// 所以两台机器上同一个存档位置即使盘符不同，只要 Title 一样就被认成同一条。
    /// </summary>
    public class SavePathSpec
    {
        /// <summary>跨机器对齐用的稳定标识。</summary>
        public string Title { get; set; } = string.Empty;

        public SaveElementType Type { get; set; } = SaveElementType.Directory;

        /// <summary>路径。自适应时是 token 形式（见 <see cref="Services.SavePathAdapter"/>）。</summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>是否按 token 展开（对应 Playnite 的 <c>SavePath.AutoAdaptive</c>）。</summary>
        public bool AutoAdaptive { get; set; }

        /// <summary>临时停用某条路径（不删定义）。</summary>
        public bool Enabled { get; set; } = true;

        /// <summary>这条来自哪边：playnite / plugin。</summary>
        public string Source { get; set; } = SaveSource.Plugin;

        public string Comment { get; set; }

        /// <summary>收集时跳过的通配符（相对路径，<c>*</c> 与 <c>?</c> 有效）。</summary>
        public List<string> Ignore { get; set; }

        public SavePathSpec GetCopy()
        {
            return new SavePathSpec
            {
                Title = Title,
                Type = Type,
                Path = Path,
                AutoAdaptive = AutoAdaptive,
                Enabled = Enabled,
                Source = Source,
                Comment = Comment,
                Ignore = Ignore == null ? null : new List<string>(Ignore)
            };
        }

        /// <summary>给人看的一行。</summary>
        public override string ToString()
        {
            return (Type == SaveElementType.Directory ? "[目录] " : "[文件] ") + Path
                   + (AutoAdaptive ? "（自适应）" : string.Empty);
        }
    }

    /// <summary>路径来源的取值。用常量而不是 enum：它要原样写进 JSON 里给人看。</summary>
    public static class SaveSource
    {
        public const string Playnite = "playnite";
        public const string Plugin = "plugin";
        public const string Sniffed = "sniffed";
    }

    /// <summary>存档里的一个文件。</summary>
    public class SaveFileEntry
    {
        /// <summary>相对该路径定义的根（目录型）或文件名（文件型）的路径，一律用 <c>/</c>。</summary>
        public string Path { get; set; } = string.Empty;

        public long Bytes { get; set; }

        /// <summary>内容 sha1（小写 hex）。对象存储的键就是它。</summary>
        public string Sha1 { get; set; } = string.Empty;

        public DateTime Modified { get; set; }

        /// <summary>这条是哪个路径定义收上来的（Title）。用来在还原时把文件放回正确的位置。</summary>
        public string Title { get; set; } = string.Empty;
    }

    /// <summary>
    /// 一份存档快照。
    ///
    /// 元数据字段刻意对齐 Playnite 云存档管理窗口绑的那五个
    /// （<c>ShowName</c> / <c>Machine</c> / <c>CreateTime</c> / <c>FileSize</c> / <c>Comment</c>），
    /// 这样两边的语义能对上，将来想互相导入也不用翻译。
    /// </summary>
    public class SaveSnapshot
    {
        /// <summary><c>yyyyMMdd-HHmmss-机器名</c>（UTC）。带机器名是为了同秒两台机器也不撞。</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>给人看的名字，默认等于时间。</summary>
        public string ShowName { get; set; } = string.Empty;

        /// <summary>哪台机器造出来的。</summary>
        public string Machine { get; set; } = string.Empty;

        public string Comment { get; set; }

        public string Branch { get; set; } = SaveBranch.Default;

        /// <summary>从哪份快照分叉出来的（可为空）。用来回溯「从哪来的」。</summary>
        public string Parent { get; set; }

        public SaveSnapshotOrigin Origin { get; set; } = SaveSnapshotOrigin.Manual;

        /// <summary>标星的快照永不被保留策略删掉。</summary>
        public bool Pinned { get; set; }

        public DateTime CreateTime { get; set; } = DateTime.UtcNow;

        public long FileSize { get; set; }

        public int FileCount { get; set; }

        /// <summary>Playnite 版本，出问题时便于对照。</summary>
        public string GameVersion { get; set; }

        /// <summary>采集时的路径定义（跨机器还原要靠它）。</summary>
        public List<SavePathSpec> Paths { get; set; } = new List<SavePathSpec>();

        /// <summary>文件清单。列表页只读元数据，所以这个字段在摘要里是空的。</summary>
        public List<SaveFileEntry> Files { get; set; } = new List<SaveFileEntry>();

        /// <summary>
        /// 内容指纹：**排除时间**，只看「是哪些路径、各是什么内容」。
        /// 「内容没变就不产生新快照」这条判断靠的就是它。
        /// </summary>
        public string Fingerprint { get; set; }

        public SaveSnapshot GetSummary()
        {
            return new SaveSnapshot
            {
                Id = Id,
                ShowName = ShowName,
                Machine = Machine,
                Comment = Comment,
                Branch = Branch,
                Parent = Parent,
                Origin = Origin,
                Pinned = Pinned,
                CreateTime = CreateTime,
                FileSize = FileSize,
                FileCount = FileCount,
                GameVersion = GameVersion,
                Paths = Paths == null ? null : Paths.Select(p => p.GetCopy()).ToList(),
                Files = new List<SaveFileEntry>(),
                Fingerprint = Fingerprint
            };
        }

        public string Describe()
        {
            var text = new StringBuilder();
            text.Append(string.IsNullOrEmpty(ShowName) ? Id : ShowName);
            text.Append("　").Append(CreateTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
            text.Append("　").Append(SyncProgress.FormatSize(FileSize));
            text.Append("　").Append(FileCount).Append(" 个文件");
            if (!string.IsNullOrEmpty(Machine))
            {
                text.Append("　@").Append(Machine);
            }

            if (Pinned)
            {
                text.Append("　★");
            }

            return text.ToString();
        }
    }

    /// <summary>一条存档分支（主线 / 二周目 / 打 mod …）。</summary>
    public class SaveBranch
    {
        /// <summary>默认分支名。设置里可以改。</summary>
        public const string Default = "main";

        public string Name { get; set; } = Default;

        /// <summary>从哪份快照分叉出来的（可为空 = 从零开始）。</summary>
        public string CreatedFrom { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public string Comment { get; set; }

        /// <summary>该分支当前的头（最新一份快照）。</summary>
        public string Head { get; set; }

        public int SnapshotCount { get; set; }

        public override string ToString()
        {
            return Name + "（" + SnapshotCount + " 份）";
        }
    }

    /// <summary>
    /// 一个游戏的存档清单。远端 <c>saves/{gameKey}/manifest.json</c> 就是它。
    /// </summary>
    public class SaveGameManifest
    {
        public const string ManifestKind = "playnite-vault-saves";
        public const int CurrentSchema = 1;

        public string Kind { get; set; } = ManifestKind;

        public int Schema { get; set; } = CurrentSchema;

        public string GameId { get; set; } = string.Empty;

        public string GameName { get; set; } = string.Empty;

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>这个游戏当前启用的路径定义（插件侧合并后的结果）。</summary>
        public List<SavePathSpec> Paths { get; set; } = new List<SavePathSpec>();

        public List<SaveBranch> Branches { get; set; } = new List<SaveBranch>();

        /// <summary>快照摘要列表，按时间倒序（新的在前）。</summary>
        public List<SaveSnapshot> Snapshots { get; set; } = new List<SaveSnapshot>();

        public static SaveGameManifest NewFor(string gameId, string gameName)
        {
            var manifest = new SaveGameManifest
            {
                GameId = gameId ?? string.Empty,
                GameName = gameName ?? string.Empty
            };
            manifest.Branches.Add(new SaveBranch { Name = SaveBranch.Default });
            return manifest;
        }

        /// <summary>取分支，没有就按需建。</summary>
        public SaveBranch EnsureBranch(string name, string createdFrom, bool create)
        {
            var branch = FindBranch(name);
            if (branch != null)
            {
                return branch;
            }

            if (!create)
            {
                return null;
            }

            branch = new SaveBranch
            {
                Name = string.IsNullOrWhiteSpace(name) ? SaveBranch.Default : name,
                CreatedFrom = createdFrom
            };
            Branches.Add(branch);
            return branch;
        }

        public SaveBranch FindBranch(string name)
        {
            var key = string.IsNullOrWhiteSpace(name) ? SaveBranch.Default : name;
            return Branches.FirstOrDefault(b =>
                string.Equals(b.Name, key, StringComparison.OrdinalIgnoreCase));
        }

        public SaveSnapshot FindSnapshot(string id)
        {
            return Snapshots.FirstOrDefault(s =>
                string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>重算每个分支的快照数与头指针（每次改动快照表后调用一次）。</summary>
        public void RebuildBranchCounters()
        {
            foreach (var branch in Branches)
            {
                var list = SnapshotsIn(branch.Name);
                branch.SnapshotCount = list.Count;
                branch.Head = list.Count == 0 ? null : list[0].Id;
            }

            Snapshots.Sort((a, b) => b.CreateTime.CompareTo(a.CreateTime));
        }

        public List<SaveSnapshot> SnapshotsIn(string branch)
        {
            var key = string.IsNullOrWhiteSpace(branch) ? SaveBranch.Default : branch;
            return Snapshots
                .Where(s => string.Equals(s.Branch, key, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(s => s.CreateTime)
                .ToList();
        }

        public long TotalBytes
        {
            get { return Snapshots.Sum(s => s.FileSize); }
        }
    }

    /// <summary>全局索引里的一行。列表页不需要把每个游戏的清单都拉下来。</summary>
    public class SaveIndexEntry
    {
        public string GameId { get; set; } = string.Empty;

        public string GameName { get; set; } = string.Empty;

        public int Snapshots { get; set; }

        public int Branches { get; set; }

        /// <summary>去重前的逻辑体积（各快照之和）。实际占用更小 —— 对象是共享的。</summary>
        public long Bytes { get; set; }

        public DateTime UpdatedAt { get; set; }
    }

    /// <summary>远端 <c>saves/index.json</c>。</summary>
    public class SaveIndex
    {
        public const string IndexKind = "playnite-vault-save-index";

        public string Kind { get; set; } = IndexKind;

        public int Schema { get; set; } = SaveGameManifest.CurrentSchema;

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        public List<SaveIndexEntry> Games { get; set; } = new List<SaveIndexEntry>();
    }

    /// <summary>触发策略。设置页里选，每一档都能关。</summary>
    public enum SaveTriggerMode
    {
        /// <summary>只有点按钮才动。</summary>
        Manual = 0,

        /// <summary>游戏退出后自动上传。</summary>
        UploadOnStop = 1,

        /// <summary>退出后自动上传 + 启动前问一句要不要拉远端（不静默覆盖）。</summary>
        UploadOnStopAskOnStart = 2
    }

    /// <summary>一次存档操作的方向。</summary>
    public enum SaveAction
    {
        UploadNew = 0,
        UploadSkip = 1,
        DownloadNew = 2,
        DownloadSkip = 3,
        DeleteRemote = 4,
        PruneLocal = 5,
        Conflict = 6
    }

    public class SaveSyncOptions
    {
        public bool DryRun { get; set; }

        /// <summary>只看这个分支（为空 = 默认分支）。</summary>
        public string Branch { get; set; }

        /// <summary>每个分支最多留几份；0 = 无限。</summary>
        public int KeepPerBranch { get; set; } = 10;

        /// <summary>内容没变也强行造一份新快照。</summary>
        public bool Force { get; set; }

        /// <summary>允许发 DELETE（保留策略裁剪 / 显式删除）。关掉时一次都不发。</summary>
        public bool AllowDelete { get; set; }

        /// <summary>
        /// 恢复前要不要先把当前本地存档留一份底（默认要）。
        /// 存档是玩家最不能丢的东西：这一条默认开着，想关得显式关。
        /// </summary>
        public bool SafetyBackup { get; set; } = true;

        /// <summary>
        /// 恢复前要不要**再往远端推一份「恢复前」快照**（默认要）。这是 <see cref="SafetyBackup"/>
        /// 的第二层：本地那份留底只在这台机器上（盘坏了就没了），远端那份才是真备份。
        /// 远端连不上时只报告、不阻断恢复 —— 那时候本地留底就是唯一的保险。
        /// </summary>
        public bool SnapshotBeforeRestore { get; set; } = true;

        /// <summary>本地「恢复前留底」最多留几份（每游戏），0 = 不限。</summary>
        public int KeepLocalBackups { get; set; } = 5;

        /// <summary>给快照标星（标星的不受保留策略影响）。</summary>
        public bool Pinned { get; set; }

        /// <summary>新快照的名字与注释。</summary>
        public string ShowName { get; set; }

        public string Comment { get; set; }

        public SaveSnapshotOrigin Origin { get; set; } = SaveSnapshotOrigin.Manual;

        /// <summary>指定要操作的快照 Id（下载 / 删除用）。</summary>
        public string SnapshotId { get; set; }
    }

    public class SaveSyncCounters
    {
        public int SnapshotsUploaded { get; set; }
        public int SnapshotsDownloaded { get; set; }
        public int SnapshotsDeleted { get; set; }
        public int FilesUploaded { get; set; }
        public int FilesDownloaded { get; set; }
        public int FilesSkipped { get; set; }
        public int ObjectsReused { get; set; }
        public int ObjectsOrphaned { get; set; }
        public int SnapshotsUnchanged { get; set; }

        /// <summary>本地被还原/覆盖前自动留的底（见设计文档「绝不静默丢东西」）。</summary>
        public int SafetyBackups { get; set; }

        public long BytesUp { get; set; }
        public long BytesDown { get; set; }

        /// <summary>远端有、本地没有的快照（只提示，不自动拉）。</summary>
        public List<string> RemoteOnly { get; set; } = new List<string>();

        /// <summary>归零。一次操作一份计数，绝不能把上一次的数字累到这一次上。</summary>
        public void Reset()
        {
            SnapshotsUploaded = 0;
            SnapshotsDownloaded = 0;
            SnapshotsDeleted = 0;
            FilesUploaded = 0;
            FilesDownloaded = 0;
            FilesSkipped = 0;
            ObjectsReused = 0;
            ObjectsOrphaned = 0;
            SnapshotsUnchanged = 0;
            SafetyBackups = 0;
            BytesUp = 0;
            BytesDown = 0;
            RemoteOnly.Clear();
        }

        public string Describe()
        {
            return string.Format(
                "上传 {0} 份/{1} 个文件（{2}），下载 {3} 份/{4} 个文件（{5}），"
                + "对象复用 {6}，跳过 {7}，未变 {8}，删除快照 {9}，回收对象 {10}",
                SnapshotsUploaded, FilesUploaded, SyncProgress.FormatSize(BytesUp),
                SnapshotsDownloaded, FilesDownloaded, SyncProgress.FormatSize(BytesDown),
                ObjectsReused, FilesSkipped, SnapshotsUnchanged, SnapshotsDeleted, ObjectsOrphaned);
        }
    }

    /// <summary>
    /// 一次存档操作的结果。理由和主题同步那边一样：
    /// 命令行 / 右键菜单 / 管理窗口三条入口共用同一条执行路径，但只有其中一条适合自己弹窗。
    /// </summary>
    public class SaveSyncOutcome
    {
        public bool Ok { get; set; }
        public bool Cancelled { get; set; }
        public string Failure { get; set; }
        public bool DryRun { get; set; }

        /// <summary>这次操作落地的快照（上传/下载成功时）。</summary>
        public string SnapshotId { get; set; }

        public SaveSyncCounters Counters { get; set; }
        public List<string> Plan { get; set; } = new List<string>();

        public static SaveSyncOutcome Fail(string reason)
        {
            return new SaveSyncOutcome { Ok = false, Failure = reason };
        }

        public string Describe()
        {
            if (!string.IsNullOrEmpty(Failure))
            {
                return "存档操作失败：" + Failure;
            }

            if (Cancelled)
            {
                return "已取消。远端与本地都没有被改动到一半。";
            }

            var counters = Counters ?? new SaveSyncCounters();
            var text = counters.Describe();

            if (DryRun)
            {
                text = "【预演，没有落盘】" + text;
            }

            if (counters.RemoteOnly != null && counters.RemoteOnly.Count > 0)
            {
                text += Environment.NewLine + Environment.NewLine
                        + "远端独有 " + counters.RemoteOnly.Count
                        + " 份快照（只提示，不会自动覆盖本地）：" + Environment.NewLine
                        + string.Join(Environment.NewLine, counters.RemoteOnly.ToArray());
            }

            return text;
        }
    }

    /// <summary>存档同步的汇报口。刻意不依赖 Playnite 的进度 API（同主题同步）。</summary>
    public interface ISaveSyncReporter
    {
        void Stage(string text);
        void Progress(SyncProgress progress);
        void Log(string line);
    }

    public class NullSaveReporter : ISaveSyncReporter
    {
        public void Stage(string text) { }
        public void Progress(SyncProgress progress) { }
        public void Log(string line) { }
    }
}
