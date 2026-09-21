using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using PlayniteVault.Models;
using PlayniteVault.Net;

namespace PlayniteVault.Services
{
    /// <summary>引擎内部用的本地状态：记住「我上次把哪份快照推上去了」，用来判断远端有没有别人更新。</summary>
    public class SaveSyncState
    {
        public const string StateKind = "playnite-vault-save-sync-state";

        public string Kind { get; set; } = StateKind;

        /// <summary>gameKey → (branch → 快照 Id)。</summary>
        public Dictionary<string, Dictionary<string, string>> LastUploaded { get; set; }
            = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>gameKey → (branch → 用户已经说「不用拉」的远端快照 Id)。免得每次启动都问一遍。</summary>
        public Dictionary<string, Dictionary<string, string>> SnoozedRemote { get; set; }
            = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 存档同步引擎。
    ///
    /// <para>和主题同步一样，这个类**不依赖 Playnite 的任何东西**（只吃一个 <see cref="WebDavClient"/>
    /// 和一份「路径定义 + 游戏目录」），所以命令行工具、插件界面、无头自检共用同一条生产代码路径。
    /// 这一点是有代价的（要多传几个参数），但换来的是「界面里能用的东西，命令行也能跑，
    /// 而且自检能把同样的场景真跑一遍」。</para>
    ///
    /// <para>存储布局与红线见 <c>docs/cloud-save.md</c>：
    /// 文件按内容寻址（<c>objects/xx/&lt;sha1&gt;</c>）所以天然去重；
    /// 快照清单独立成文件；确认删除之外一次 DELETE 都不发。</para>
    /// </summary>
    public class SaveSyncEngine
    {
        /// <summary>远端仓库里的存档根目录。</summary>
        public const string RemoteRoot = "saves";

        private const string IndexFile = RemoteRoot + "/index.json";
        private const string ManifestName = "manifest.json";
        private const string SnapshotsDir = "snapshots";
        private const string ObjectsDir = "objects";

        /// <summary>单个存档最多收这么多文件。超过就停下报错 —— 那多半是路径指向了整个盘。</summary>
        private const int MaxFilesPerSnapshot = 200000;

        private static readonly JsonSerializerSettings Json = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
            NullValueHandling = NullValueHandling.Ignore
        };

        private readonly WebDavClient client;
        private readonly string dataPath;
        private readonly SyncOptions transport;
        private readonly ISaveSyncReporter reporter;
        private readonly CancellationToken cancelToken;
        private readonly string machine;

        /// <summary>远端快照清单的缓存（key = 「gameKey/分支/快照Id」）。同一轮里反复用得到。</summary>
        private readonly Dictionary<string, SaveSnapshot> snapshotCache =
            new Dictionary<string, SaveSnapshot>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 本轮已经建过的远端目录。
        /// <para><b>为什么必须有这个</b>：<c>WebDavClient</c> 的上传**不会自动建父目录** ——
        /// 往一个不存在的 <c>objects/ab/</c> 里 PUT 会直接 409。所以每个分片目录
        /// 得先 MKCOL 一次；而不缓存的话就是「每个文件一次 MKCOL」，几千个文件白跑几千个来回。</para>
        /// </summary>
        private readonly HashSet<string> ensuredDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private readonly SaveSyncCounters counters = new SaveSyncCounters();
        private readonly List<string> planLines = new List<string>();
        private SyncProgress progress;

        public SaveSyncEngine(WebDavClient client, string dataPath, SyncOptions transport,
            ISaveSyncReporter reporter, CancellationToken cancelToken, string machineName = null)
        {
            if (client == null)
            {
                throw new ArgumentNullException("client");
            }

            this.client = client;
            this.dataPath = string.IsNullOrWhiteSpace(dataPath)
                ? Path.Combine(Path.GetTempPath(), "playnite-vault-saves")
                : dataPath;
            this.transport = transport ?? new SyncOptions();
            this.reporter = reporter ?? new NullSaveReporter();
            this.cancelToken = cancelToken;
            this.machine = string.IsNullOrWhiteSpace(machineName)
                ? SafeMachineName(Environment.MachineName)
                : SafeMachineName(machineName);
        }

        public string MachineName
        {
            get { return machine; }
        }

        /// <summary>幂等地建一个远端目录（同一轮里同一个路径只发一次 MKCOL）。</summary>
        private void EnsureDir(string relative)
        {
            if (string.IsNullOrWhiteSpace(relative))
            {
                return;
            }

            var key = relative.TrimEnd('/');
            if (!ensuredDirs.Add(key))
            {
                return;
            }

            client.EnsureDirectoryRecursive(key);
        }

        /// <summary>一个远端路径的父目录。</summary>
        private static string ParentOf(string relative)
        {
            var slash = (relative ?? string.Empty).LastIndexOf('/');
            return slash <= 0 ? string.Empty : relative.Substring(0, slash);
        }

        // ==================================================================
        //  远端路径
        // ==================================================================

        /// <summary>
        /// 游戏 Id → 远端目录名。Playnite 的 Id 是 GUID，直接用；
        /// 万一别处传进来奇怪的字符，退回 sha1 前缀，绝不让它拼出路径穿越。
        /// </summary>
        public static string SafeGameKey(string gameId)
        {
            var raw = (gameId ?? string.Empty).Trim();
            if (raw.Length == 0)
            {
                throw new ArgumentException("游戏 Id 不能为空", "gameId");
            }

            var ok = raw.All(c => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                                  || (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.');
            if (ok && raw != "." && raw != "..")
            {
                return raw;
            }

            return "x-" + Sha1Text(raw).Substring(0, 16);
        }

        private static string GameDir(string gameKey)
        {
            return RemoteRoot + "/" + gameKey;
        }

        private static string ManifestRel(string gameKey)
        {
            return GameDir(gameKey) + "/" + ManifestName;
        }

        private static string SnapshotRel(string gameKey, string branch, string snapshotId)
        {
            return GameDir(gameKey) + "/" + SnapshotsDir + "/" + SafeSegment(branch) + "/"
                   + SafeSegment(snapshotId) + ".json";
        }

        private static string SnapshotDirRel(string gameKey, string branch)
        {
            return GameDir(gameKey) + "/" + SnapshotsDir + "/" + SafeSegment(branch);
        }

        private static string SnapshotPrefix(string gameKey, string branch)
        {
            return SnapshotDirRel(gameKey, branch) + "/";
        }

        private static string ObjectRel(string gameKey, string sha1)
        {
            var hash = (sha1 ?? string.Empty).ToLowerInvariant();
            return GameDir(gameKey) + "/" + ObjectsDir + "/" + hash.Substring(0, 2) + "/" + hash;
        }

        /// <summary>分支名 / 快照 Id 里绝不允许出现路径分隔符。</summary>
        private static string SafeSegment(string value)
        {
            var raw = (value ?? string.Empty).Trim();
            if (raw.Length == 0)
            {
                throw new ArgumentException("名字不能为空");
            }

            if (raw.IndexOfAny(new[] { '/', '\\', ':', '*' , '?', '"', '<', '>', '|' }) >= 0
                || raw == "." || raw == "..")
            {
                throw new ArgumentException("名字里有不能用于路径的字符：" + value);
            }

            return raw;
        }

        // ==================================================================
        //  清单与索引
        // ==================================================================

        /// <summary>读远端某游戏的清单。远端还没有就返回 null（不建空的）。</summary>
        public SaveGameManifest LoadManifest(string gameKey)
        {
            try
            {
                var text = client.DownloadString(ManifestRel(gameKey));
                var manifest = JsonConvert.DeserializeObject<SaveGameManifest>(text, Json);
                if (manifest == null)
                {
                    return null;
                }

                if (!string.Equals(manifest.Kind, SaveGameManifest.ManifestKind, StringComparison.Ordinal))
                {
                    // 别人的目录：宁可停下，也不要把别人的东西当存档仓库写坏
                    throw new InvalidDataException(
                        "远端 saves/" + gameKey + "/manifest.json 不是本插件的存档清单（Kind="
                        + (manifest.Kind ?? "空") + "），已停止");
                }

                if (manifest.Branches == null)
                {
                    manifest.Branches = new List<SaveBranch>();
                }

                if (manifest.Snapshots == null)
                {
                    manifest.Snapshots = new List<SaveSnapshot>();
                }

                if (manifest.Paths == null)
                {
                    manifest.Paths = new List<SavePathSpec>();
                }

                if (manifest.FindBranch(SaveBranch.Default) == null && manifest.Branches.Count == 0)
                {
                    manifest.Branches.Add(new SaveBranch { Name = SaveBranch.Default });
                }

                manifest.RebuildBranchCounters();
                return manifest;
            }
            catch (Exception ex)
            {
                if (IsNotFound(ex))
                {
                    return null;
                }

                throw;
            }
        }

        public void SaveManifest(string gameKey, SaveGameManifest manifest)
        {
            manifest.UpdatedAt = DateTime.UtcNow;
            manifest.RebuildBranchCounters();
            EnsureDir(GameDir(gameKey));
            client.UploadString(JsonConvert.SerializeObject(manifest, Json), ManifestRel(gameKey));
        }

        public SaveIndex LoadSaveIndex()
        {
            try
            {
                var text = client.DownloadString(IndexFile);
                var index = JsonConvert.DeserializeObject<SaveIndex>(text, Json);
                if (index == null)
                {
                    return new SaveIndex();
                }

                if (!string.Equals(index.Kind, SaveIndex.IndexKind, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "远端 saves/index.json 不是本插件的存档索引（Kind="
                        + (index.Kind ?? "空") + "），已停止");
                }

                if (index.Games == null)
                {
                    index.Games = new List<SaveIndexEntry>();
                }

                return index;
            }
            catch (Exception ex)
            {
                if (IsNotFound(ex))
                {
                    return new SaveIndex();
                }

                throw;
            }
        }

        public void WriteSaveIndex(SaveIndex index)
        {
            index.UpdatedAt = DateTime.UtcNow;
            index.Games = index.Games.OrderBy(g => g.GameName, StringComparer.CurrentCulture).ToList();
            EnsureDir(RemoteRoot);
            client.UploadString(JsonConvert.SerializeObject(index, Json), IndexFile);
        }

        /// <summary>把一个游戏的摘要写回索引（索引里没有就加一行）。</summary>
        public void UpsertIndex(SaveGameManifest manifest)
        {
            var index = LoadSaveIndex();
            var entry = index.Games.FirstOrDefault(g =>
                string.Equals(g.GameId, manifest.GameId, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
            {
                entry = new SaveIndexEntry { GameId = manifest.GameId };
                index.Games.Add(entry);
            }

            entry.GameName = manifest.GameName;
            entry.Snapshots = manifest.Snapshots.Count;
            entry.Branches = manifest.Branches.Count;
            entry.Bytes = manifest.TotalBytes;
            entry.UpdatedAt = manifest.UpdatedAt;
            WriteSaveIndex(index);
        }

        // ==================================================================
        //  本地状态
        // ==================================================================

        private string StateFile
        {
            get { return Path.Combine(dataPath, "save-sync-state.json"); }
        }

        public SaveSyncState LoadState()
        {
            try
            {
                if (!File.Exists(StateFile))
                {
                    return new SaveSyncState();
                }

                var state = JsonConvert.DeserializeObject<SaveSyncState>(
                    File.ReadAllText(StateFile, Encoding.UTF8), Json);
                if (state == null)
                {
                    return new SaveSyncState();
                }

                if (state.LastUploaded == null)
                {
                    state.LastUploaded = new Dictionary<string, Dictionary<string, string>>(
                        StringComparer.OrdinalIgnoreCase);
                }

                if (state.SnoozedRemote == null)
                {
                    state.SnoozedRemote = new Dictionary<string, Dictionary<string, string>>(
                        StringComparer.OrdinalIgnoreCase);
                }

                return state;
            }
            catch (Exception ex)
            {
                VaultLog.Warn("读存档同步状态失败（当作全新处理）：" + ex.Message);
                return new SaveSyncState();
            }
        }

        public void SaveState(SaveSyncState state)
        {
            try
            {
                Directory.CreateDirectory(dataPath);
                File.WriteAllText(StateFile, JsonConvert.SerializeObject(state, Json),
                    new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                VaultLog.Warn("写存档同步状态失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 每次公开操作开头调一次：计数、计划、快照缓存归零。
        ///
        /// <para>这三样都是字段，不归零的话「上一次操作的数字」会累到这一次上 ——
        /// 界面会报出「上传 2 份」而实际上这次一份都没传。缓存同理：留着就可能
        /// 拿另一个操作读到的旧清单当当前状态。</para>
        /// </summary>
        private void BeginOperation()
        {
            counters.Reset();
            planLines.Clear();
            snapshotCache.Clear();
            progress = null;
        }

        private static void Remember(Dictionary<string, Dictionary<string, string>> map,
            string gameKey, string branch, string value)
        {
            Dictionary<string, string> perBranch;
            if (!map.TryGetValue(gameKey, out perBranch))
            {
                perBranch = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                map[gameKey] = perBranch;
            }

            perBranch[string.IsNullOrWhiteSpace(branch) ? SaveBranch.Default : branch] = value;
        }

        private static string Recall(Dictionary<string, Dictionary<string, string>> map,
            string gameKey, string branch)
        {
            Dictionary<string, string> perBranch;
            if (map == null || !map.TryGetValue(gameKey, out perBranch) || perBranch == null)
            {
                return null;
            }

            string value;
            return perBranch.TryGetValue(
                string.IsNullOrWhiteSpace(branch) ? SaveBranch.Default : branch, out value)
                ? value
                : null;
        }

        /// <summary>
        /// 远端有没有「我没推过的、更新的」快照。启动前问要不要拉，判断的就是它。
        /// 明确被用户忽略过的那份不再重复问。
        /// </summary>
        public SaveSnapshot FindRemoteUpdate(string gameKey, string branch, SaveGameManifest manifest)
        {
            var state = LoadState();
            var lastMine = Recall(state.LastUploaded, gameKey, branch);
            var snoozed = Recall(state.SnoozedRemote, gameKey, branch);

            return manifest.SnapshotsIn(branch)
                .FirstOrDefault(s => !string.Equals(s.Id, lastMine, StringComparison.OrdinalIgnoreCase)
                                     && !string.Equals(s.Id, snoozed, StringComparison.OrdinalIgnoreCase)
                                     && !string.Equals(s.Machine, machine, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>记下「这份远端更新用户不想要」。</summary>
        public void SnoozeRemoteUpdate(string gameKey, string branch, string snapshotId)
        {
            var state = LoadState();
            Remember(state.SnoozedRemote, gameKey, branch, snapshotId);
            SaveState(state);
        }

        // ==================================================================
        //  采集
        // ==================================================================

        /// <summary>
        /// 把本地存档收成一份快照（还没上传）。
        /// <paramref name="warnings"/> 收下「路径没解析出来」「路径不存在」这类要告诉用户的事 ——
        /// 这些一律**不静默跳过**：存档少收一个目录，用户是看不出来的。
        /// </summary>
        public SaveSnapshot Collect(IEnumerable<SavePathSpec> specs, string gameInstallDir,
            out List<string> warnings)
        {
            warnings = new List<string>();
            var files = new List<SaveFileEntry>();
            collectedLocalPaths.Clear();

            foreach (var spec in specs ?? Enumerable.Empty<SavePathSpec>())
            {
                cancelToken.ThrowIfCancellationRequested();

                if (spec == null || !spec.Enabled || string.IsNullOrWhiteSpace(spec.Path))
                {
                    continue;
                }

                var title = string.IsNullOrWhiteSpace(spec.Title)
                    ? spec.Path
                    : spec.Title.Trim();

                List<string> unresolved;
                var resolved = SavePathAdapter.Resolve(spec, gameInstallDir, out unresolved);
                if (unresolved.Count > 0)
                {
                    warnings.Add("「" + title + "」里的 " + string.Join("、", unresolved.ToArray())
                                 + " 在当前机器上展开不了，已跳过这条");
                    continue;
                }

                if (spec.Type == SaveElementType.Directory)
                {
                    if (!Directory.Exists(resolved))
                    {
                        warnings.Add("「" + title + "」的目录不存在，已跳过：" + resolved);
                        continue;
                    }

                    CollectDirectory(title, resolved, spec.Ignore, files);
                }
                else
                {
                    if (!File.Exists(resolved))
                    {
                        warnings.Add("「" + title + "」的文件不存在，已跳过：" + resolved);
                        continue;
                    }

                    files.Add(MakeEntry(title, Path.GetFileName(resolved), resolved));
                }
            }

            files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));

            // 快照里的路径定义要能跨机器用，Title 就是跨机器的对齐键（和 Playnite 的 SavePath.Title 一个意思）。
            // 没写 Title 的在这里补一个稳定的 —— 用路径本身 —— 免得两边机器按 Title 对不上。
            var savedPaths = (specs ?? Enumerable.Empty<SavePathSpec>())
                .Where(s => s != null && s.Enabled)
                .Select(s =>
                {
                    var copy = s.GetCopy();
                    if (string.IsNullOrWhiteSpace(copy.Title))
                    {
                        copy.Title = (copy.Path ?? string.Empty).Trim();
                    }

                    return copy;
                })
                .ToList();

            var snapshot = new SaveSnapshot
            {
                Paths = savedPaths,
                Files = files,
                FileCount = files.Count,
                FileSize = files.Sum(f => f.Bytes),
                Fingerprint = Fingerprint(files)
            };

            return snapshot;
        }

        private void CollectDirectory(string title, string root, List<string> ignore,
            List<SaveFileEntry> into)
        {
            // 手工走栈而不是 SearchOption.AllDirectories：一来要跳过 reparse point
            // （junction / 符号链接会让递归绕圈），二来要在超限时能干净地停下来。
            var pending = new Stack<string>();
            pending.Push(root);

            while (pending.Count > 0)
            {
                cancelToken.ThrowIfCancellationRequested();

                var dir = pending.Pop();
                string[] subDirs;
                string[] filesInDir;

                try
                {
                    subDirs = Directory.GetDirectories(dir);
                    filesInDir = Directory.GetFiles(dir);
                }
                catch (UnauthorizedAccessException ex)
                {
                    VaultLog.Warn("存档目录读不了，已跳过：" + dir + "：" + ex.Message);
                    continue;
                }
                catch (DirectoryNotFoundException)
                {
                    continue;
                }

                foreach (var sub in subDirs)
                {
                    try
                    {
                        var info = new DirectoryInfo(sub);
                        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            continue;
                        }
                    }
                    catch
                    {
                        continue;
                    }

                    pending.Push(sub);
                }

                foreach (var file in filesInDir)
                {
                    var rel = SaveSyncEngine.RelativeWithin(root, file);
                    if (IsIgnored(rel, ignore))
                    {
                        continue;
                    }

                    into.Add(MakeEntry(title, rel, file));

                    if (into.Count > MaxFilesPerSnapshot)
                    {
                        throw new InvalidDataException(
                            "单个存档收到的文件超过 " + MaxFilesPerSnapshot
                            + " 个，多半是路径定义指到了整个盘。已停下，请检查路径。");
                    }
                }
            }
        }

        /// <summary>
        /// 造一条文件记录，顺手把「快照里的相对路径 → 本机绝对路径」记下来。
        /// 快照本身不能写本机绝对路径（要能跨机器用），所以上传阶段找文件靠的是这张表。
        /// </summary>
        private SaveFileEntry MakeEntry(string title, string relative, string fullPath)
        {
            var info = new FileInfo(fullPath);
            var key = (title ?? string.Empty).Replace('\\', '/').TrimEnd('/') + "/"
                      + relative.Replace('\\', '/');

            collectedLocalPaths[key] = fullPath;

            return new SaveFileEntry
            {
                Path = key,
                Title = title,
                Bytes = info.Length,
                Modified = info.LastWriteTimeUtc,
                Sha1 = Sha1File(fullPath)
            };
        }

        /// <summary>
        /// 内容指纹：只看「是哪些文件、各是什么内容」，**排除时间与顺序之外的一切**。
        /// 用来判「内容没变 → 不再造新快照」。
        /// </summary>
        private static string Fingerprint(List<SaveFileEntry> files)
        {
            if (files == null || files.Count == 0)
            {
                return string.Empty;
            }

            var sb = new StringBuilder();
            foreach (var f in files.OrderBy(f => f.Path, StringComparer.Ordinal))
            {
                sb.Append(f.Path).Append('\u0001').Append(f.Sha1).Append('\n');
            }

            return Sha1Text(sb.ToString());
        }

        // ==================================================================
        //  上传
        // ==================================================================

        /// <summary>
        /// 采一份新快照推到远端。
        /// 「内容与最新快照一模一样」时**不造新快照**（除非 <c>Force</c>）——
        /// 否则每退一次游戏就多一条没区别的历史。
        /// </summary>
        public SaveSyncOutcome Upload(string gameKey, string gameName, string gameInstallDir,
            SaveGameManifest manifest, SaveSyncOptions options)
        {
            var opt = options ?? new SaveSyncOptions();
            var branchName = string.IsNullOrWhiteSpace(opt.Branch) ? SaveBranch.Default : opt.Branch.Trim();

            BeginOperation();

            List<string> warnings;
            Report("扫描本地存档…");
            var snapshot = Collect(manifest.Paths, gameInstallDir, out warnings);
            foreach (var warning in warnings)
            {
                Report("  ! " + warning);
            }

            if (snapshot.FileCount == 0)
            {
                return SaveSyncOutcome.Fail(
                    warnings.Count > 0
                        ? "本地没有收到任何文件：" + warnings[0]
                        : "本地没有收到任何文件。检查一下这个游戏的存档路径是否填对了。");
            }

            var branch = manifest.EnsureBranch(branchName, null, true);
            var head = manifest.SnapshotsIn(branch.Name).FirstOrDefault();

            if (head != null && !opt.Force
                && string.Equals(head.Fingerprint, snapshot.Fingerprint, StringComparison.Ordinal))
            {
                counters.SnapshotsUnchanged++;
                Report("内容与最新快照「" + head.Id + "」完全一致，不造新快照");

                // 记一笔：本地内容 == 这份远端快照，下次启动就不必再问「远端有更新要不要拉」
                var unchangedState = LoadState();
                Remember(unchangedState.LastUploaded, gameKey, branch.Name, head.Id);
                SaveState(unchangedState);

                return Finish(true, head.Id, false);
            }

            snapshot.Id = MakeSnapshotId(DateTime.UtcNow);

            // 同一秒里连推两次（自动 + 手动碰上了）会撞 Id，加个后缀就好
            var suffix = 0;
            while (manifest.FindSnapshot(snapshot.Id) != null)
            {
                suffix++;
                snapshot.Id = MakeSnapshotId(DateTime.UtcNow) + "-"
                              + suffix.ToString(CultureInfo.InvariantCulture);
            }

            snapshot.Branch = branch.Name;
            snapshot.Machine = machine;
            snapshot.Parent = head == null ? branch.CreatedFrom : head.Id;
            snapshot.Origin = opt.Origin;
            snapshot.Pinned = opt.Pinned;
            snapshot.Comment = opt.Comment;
            snapshot.ShowName = string.IsNullOrWhiteSpace(opt.ShowName)
                ? snapshot.CreateTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")
                : opt.ShowName.Trim();

            planLines.Add("上传快照 " + snapshot.Id + "（" + snapshot.FileCount + " 个文件，"
                          + SyncProgress.FormatSize(snapshot.FileSize) + "）");

            if (opt.DryRun)
            {
                return Finish(true, snapshot.Id, true);
            }

            Report("准备远端目录…");
            EnsureDir(GameDir(gameKey));
            EnsureDir(SnapshotDirRel(gameKey, branch.Name));

            // 先把远端已知的对象集合攒出来，能省掉绝大多数 HEAD
            var known = KnownObjects(gameKey, manifest);
            Report("已有对象 " + known.Count + " 个可复用");

            PrepareProgress(snapshot);

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var uploaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var failed = new List<string>();

            foreach (var file in snapshot.Files)
            {
                cancelToken.ThrowIfCancellationRequested();

                // 同一份内容在快照里出现多次（多个目录里有同一个文件）时只传一次
                if (seen.Add(file.Sha1))
                {
                    if (known.Contains(file.Sha1))
                    {
                        counters.ObjectsReused++;
                    }
                    else if (UploadObject(gameKey, file))
                    {
                        known.Add(file.Sha1);
                        uploaded.Add(file.Sha1);
                    }
                    else
                    {
                        failed.Add(file.Path);
                    }
                }

                Advance(file.Bytes, file.Path);
            }

            counters.FilesUploaded = uploaded.Count;
            foreach (var sha1 in uploaded)
            {
                var one = snapshot.Files.FirstOrDefault(f =>
                    string.Equals(f.Sha1, sha1, StringComparison.OrdinalIgnoreCase));
                if (one != null)
                {
                    counters.BytesUp += one.Bytes;
                }
            }

            if (failed.Count > 0)
            {
                // 采完之后文件被删了或被独占住了。宁可这一份不发，
                // 也不能发出一份「引用着不存在的内容」的快照 —— 那要到恢复的时候才炸。
                counters.FilesSkipped += failed.Count;
                var bad = SaveSyncOutcome.Fail(
                    "有 " + failed.Count + " 个文件的内容没传上去（多半是采集之后被删了或被别的程序占着），"
                    + "这次的快照没有发布，远端保持原样。受影响："
                    + string.Join("；", failed.Take(5).ToArray())
                    + (failed.Count > 5 ? " 等 " + failed.Count + " 个" : ""));
                bad.SnapshotId = snapshot.Id;
                bad.Counters = counters;
                bad.Plan = planLines;
                return bad;
            }

            // 快照清单（含文件列表）最后写。
            var full = new SaveSnapshot
            {
                Id = snapshot.Id,
                ShowName = snapshot.ShowName,
                Machine = snapshot.Machine,
                Comment = snapshot.Comment,
                Branch = snapshot.Branch,
                Parent = snapshot.Parent,
                Origin = snapshot.Origin,
                Pinned = snapshot.Pinned,
                CreateTime = snapshot.CreateTime,
                FileSize = snapshot.FileSize,
                FileCount = snapshot.FileCount,
                GameVersion = snapshot.GameVersion,
                Paths = snapshot.Paths,
                Files = snapshot.Files,
                Fingerprint = snapshot.Fingerprint
            };

            client.UploadString(JsonConvert.SerializeObject(full, Json),
                SnapshotRel(gameKey, branch.Name, snapshot.Id));
            snapshotCache[CacheKey(gameKey, branch.Name, snapshot.Id)] = full;

            manifest.Snapshots.Add(snapshot.GetSummary());

            // 保留策略 + 对象回收
            var pruned = PruneInternal(gameKey, manifest, opt);

            SaveManifest(gameKey, manifest);
            UpsertIndex(manifest);

            var state = LoadState();
            Remember(state.LastUploaded, gameKey, branch.Name, snapshot.Id);
            Remember(state.SnoozedRemote, gameKey, branch.Name, null);
            SaveState(state);

            counters.SnapshotsUploaded++;
            FinishProgress();

            if (pruned.Count > 0)
            {
                Report("保留策略裁掉 " + pruned.Count + " 份旧快照（每个分支最多 "
                       + (opt.KeepPerBranch <= 0 ? "不限" : opt.KeepPerBranch.ToString()) + " 份）");
            }

            return Finish(true, snapshot.Id, false);
        }

        /// <summary>把一个对象的内容传上去。返回「现在远端确实有这份内容了」。</summary>
        private bool UploadObject(string gameKey, SaveFileEntry file)
        {
            var remote = ObjectRel(gameKey, file.Sha1);

            // 内容寻址的好处：同一个内容不重复传。已经在了就不动它。
            try
            {
                if (client.Exists(remote))
                {
                    counters.ObjectsReused++;
                    return true;
                }
            }
            catch (Exception ex)
            {
                VaultLog.Warn("探测对象是否存在失败，改为直接上传：" + ex.Message);
            }

            var local = LocalPathFor(file);
            if (local == null)
            {
                Report("  ! 采集之后文件不在了，传不了：" + file.Path);
                return false;
            }

            try
            {
                // 分片目录得先建出来（WebDAV 的 PUT 不会替你建父目录）
                EnsureDir(ParentOf(remote));
                client.UploadFile(local, remote, null, cancelToken, transport.MaxRetries);
                return true;
            }
            catch (Exception ex)
            {
                VaultLog.Warn("上传对象失败：" + file.Path + "：" + ex.Message);
                Report("  ! 上传失败：" + file.Path + "（" + ex.Message + "）");
                return false;
            }
        }

        /// <summary>
        /// 采集阶段不把绝对路径写进快照（快照要能跨机器用，写本机路径没意义），
        /// 所以由 <see cref="MakeEntry"/> 在采集的同时把映射记在这张表里，上传时用它找文件。
        /// </summary>
        private readonly Dictionary<string, string> collectedLocalPaths =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private string LocalPathFor(SaveFileEntry file)
        {
            string path;
            if (collectedLocalPaths.TryGetValue(file.Path, out path) && File.Exists(path))
            {
                return path;
            }

            return null;
        }

        /// <summary>
        /// 远端已知的对象集合：把该游戏所有快照清单里的 sha1 并起来。
        /// 清单很小（一行一个文件），比每个文件发一次 HEAD 划算得多。
        /// </summary>
        private HashSet<string> KnownObjects(string gameKey, SaveGameManifest manifest)
        {
            var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var summary in manifest.Snapshots)
            {
                var full = LoadSnapshot(gameKey, summary.Branch, summary.Id, false);
                if (full == null || full.Files == null)
                {
                    continue;
                }

                foreach (var f in full.Files)
                {
                    if (!string.IsNullOrEmpty(f.Sha1))
                    {
                        known.Add(f.Sha1);
                    }
                }
            }

            return known;
        }

        /// <summary>读远端某份快照的完整清单（带文件列表）。<paramref name="refresh"/> 为真时忽略缓存。</summary>
        public SaveSnapshot LoadSnapshot(string gameKey, string branch, string snapshotId, bool refresh)
        {
            var key = CacheKey(gameKey, branch, snapshotId);
            SaveSnapshot cached;
            if (!refresh && snapshotCache.TryGetValue(key, out cached))
            {
                return cached;
            }

            try
            {
                var text = client.DownloadString(SnapshotRel(gameKey, branch, snapshotId));
                var snapshot = JsonConvert.DeserializeObject<SaveSnapshot>(text, Json);
                if (snapshot == null)
                {
                    return null;
                }

                if (snapshot.Files == null)
                {
                    snapshot.Files = new List<SaveFileEntry>();
                }

                if (snapshot.Paths == null)
                {
                    snapshot.Paths = new List<SavePathSpec>();
                }

                snapshotCache[key] = snapshot;
                return snapshot;
            }
            catch (Exception ex)
            {
                if (IsNotFound(ex))
                {
                    return null;
                }

                throw;
            }
        }

        // ==================================================================
        //  下载 / 恢复
        // ==================================================================

        /// <summary>
        /// 把远端一份快照恢复到本地。
        ///
        /// <para><b>两条不可退让的规矩</b>：
        /// ① 动手之前先把当前本地状态留一份底（本地副本 + 可选地上传一份快照），
        /// ② 只覆盖快照里有的文件，**不删**本地多出来的文件 —— 恢复只做加法。
        /// 存档是玩家最不能丢的东西，宁可留一堆没用的旧文件。</para>
        /// </summary>
        public SaveSyncOutcome Download(string gameKey, string gameName, string gameInstallDir,
            SaveGameManifest manifest, SaveSyncOptions options)
        {
            var opt = options ?? new SaveSyncOptions();
            var branchName = string.IsNullOrWhiteSpace(opt.Branch) ? SaveBranch.Default : opt.Branch.Trim();
            var snapshotId = opt.SnapshotId;

            BeginOperation();

            if (string.IsNullOrWhiteSpace(snapshotId))
            {
                var newest = manifest.SnapshotsIn(branchName).FirstOrDefault();
                if (newest == null)
                {
                    return SaveSyncOutcome.Fail("分支「" + branchName + "」上没有任何快照");
                }

                snapshotId = newest.Id;
            }

            var snapshot = LoadSnapshot(gameKey, branchName, snapshotId, true);
            if (snapshot == null)
            {
                return SaveSyncOutcome.Fail("远端找不到快照 " + snapshotId);
            }

            planLines.Add("恢复快照 " + snapshot.Id + "（" + snapshot.FileCount + " 个文件，"
                          + SyncProgress.FormatSize(snapshot.FileSize) + "）");

            if (opt.DryRun)
            {
                return Finish(true, snapshot.Id, true);
            }

            // ---------- ① 先留底 ----------
            // 两层：本地一份（NAS 连不上时也拿得到），远端一份（盘坏了也拿得到）。
            // 顺序是先本地后远端：远端那份要么成功、要么只是一条警告，绝不让它挡住恢复。
            if (opt.SafetyBackup)
            {
                var backupDir = Path.Combine(dataPath, "save-backup", gameKey,
                    DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
                var copied = BackupCurrent(snapshot, gameInstallDir, backupDir);
                if (copied > 0)
                {
                    counters.SafetyBackups++;
                    Report("恢复前已把当前本地存档留底：" + copied + " 个文件 → " + backupDir);
                    PruneLocalBackups(gameKey, opt.KeepLocalBackups);
                }
            }

            if (opt.SnapshotBeforeRestore)
            {
                try
                {
                    Report("恢复前先把当前状态推一份快照到远端…");
                    var pre = new SaveSyncOptions
                    {
                        Branch = branchName,
                        Origin = SaveSnapshotOrigin.BeforeRestore,
                        Comment = "恢复快照 " + snapshot.Id + " 之前自动备份",
                        ShowName = "恢复前自动备份（" + DateTime.Now.ToString("MM-dd HH:mm") + "）",
                        KeepPerBranch = opt.KeepPerBranch,
                        AllowDelete = false,   // 留底这一步绝不删任何东西
                        SafetyBackup = false
                    };

                    // 用一个独立引擎实例：这样「留底上传」的计数不会混进「恢复」的计数里，
                    // 但两边共用同一个 WebDavClient 与同一份本地状态文件。
                    var preEngine = new SaveSyncEngine(client, dataPath, transport, reporter,
                        cancelToken, machine);
                    var preOutcome = preEngine.Upload(gameKey, gameName, gameInstallDir, manifest, pre);
                    if (!preOutcome.Ok)
                    {
                        Report("  ! 恢复前快照没推上去（" + preOutcome.Failure + "），继续恢复"
                               + "（本地留底还在）");
                    }
                    else if (preOutcome.Counters.SnapshotsUploaded == 0)
                    {
                        Report("  · 远端已有内容一致的快照，不必多推一份");
                    }
                }
                catch (Exception ex)
                {
                    // 网络问题不该让用户拿不回自己的存档：报告一声，继续。
                    Report("  ! 恢复前快照失败（" + ex.Message + "），继续恢复（本地留底还在）");
                }
            }

            // ---------- ② 按清单落文件 ----------
            PrepareProgress(snapshot);
            var placed = 0;

            foreach (var file in snapshot.Files)
            {
                cancelToken.ThrowIfCancellationRequested();

                var spec = FindSpec(snapshot.Paths, file.Title);
                if (spec == null)
                {
                    Report("  ! 快照里的「" + file.Title + "」在本地没有对应路径定义，已跳过");
                    counters.FilesSkipped++;
                    continue;
                }

                List<string> unresolved;
                var root = SavePathAdapter.Resolve(spec, gameInstallDir, out unresolved);
                if (unresolved.Count > 0)
                {
                    Report("  ! 「" + file.Title + "」在本机展开不了（" + string.Join("、", unresolved.ToArray())
                           + "），已跳过");
                    counters.FilesSkipped++;
                    continue;
                }

                var target = TargetPathSafe(spec, root, file, out var targetError);
                if (target == null)
                {
                    counters.FilesSkipped++;
                    Report("  ! 快照里这条记录用不了，已跳过：" + file.Path + "（" + targetError + "）");
                    continue;
                }

                if (string.IsNullOrEmpty(file.Sha1) || file.Sha1.Length < 4)
                {
                    counters.FilesSkipped++;
                    Report("  ! 快照里这条记录缺少内容指纹，已跳过：" + file.Path);
                    continue;
                }

                bool downloaded;
                var cache = CacheObject(gameKey, file, out downloaded);
                if (cache == null)
                {
                    counters.FilesSkipped++;
                    Report("  ! 对象 " + Short(file.Sha1) + " 取不下来，已跳过：" + file.Path);
                    continue;
                }

                var dir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.Copy(cache, target, true);
                File.SetLastWriteTimeUtc(target, file.Modified);
                placed++;

                // 命本地对象缓存的不算流量
                counters.FilesDownloaded++;
                if (downloaded)
                {
                    counters.BytesDown += file.Bytes;
                }

                Advance(file.Bytes, file.Path);
            }

            var extra = CountExtras(snapshot, gameInstallDir);
            FinishProgress();

            if (extra > 0)
            {
                Report("本地多出 " + extra + " 个不在快照里的文件，**没有删**（恢复只做加法）");
            }

            if (placed == 0 && snapshot.Files.Count > 0)
            {
                return SaveSyncOutcome.Fail("一个文件都没落下来。检查路径定义在这个游戏上是否可用。");
            }

            counters.SnapshotsDownloaded++;
            return Finish(true, snapshot.Id, false);
        }

        /// <summary>
        /// <see cref="TargetPath"/> 的安全版：清单是从 NAS 读回来的，可能是手改过的、
        /// 也可能是别的版本写的。一条坏记录只能废掉它自己，**不能让整次恢复倒下** ——
        /// 恢复到一半崩掉，比少恢复一个文件糟糕得多。
        /// </summary>
        private static string TargetPathSafe(SavePathSpec spec, string resolvedRoot, SaveFileEntry file,
            out string error)
        {
            error = null;
            try
            {
                return TargetPath(spec, resolvedRoot, file);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        /// <summary>快照里每个文件该落到哪：目录型拼相对路径，文件型就是那个文件本身。</summary>
        private static string TargetPath(SavePathSpec spec, string resolvedRoot, SaveFileEntry file)
        {
            var rel = file.Path.Replace('\\', '/');
            var slash = rel.IndexOf('/');
            if (slash >= 0)
            {
                rel = rel.Substring(slash + 1);
            }

            if (spec.Type == SaveElementType.File)
            {
                return resolvedRoot;
            }

            return CombineUnder(resolvedRoot, rel);
        }

        private static SavePathSpec FindSpec(List<SavePathSpec> specs, string title)
        {
            if (specs == null)
            {
                return null;
            }

            var hit = specs.FirstOrDefault(s =>
                string.Equals(s.Title, title, StringComparison.OrdinalIgnoreCase));
            if (hit != null)
            {
                return hit;
            }

            // 兼容：老快照可能没记 Title，只有一条路径时按它处理
            return specs.Count == 1 ? specs[0] : null;
        }

        /// <summary>把当前本地存档拷到 <paramref name="backupDir"/>（只拷快照覆盖得到的那些文件）。</summary>
        private int BackupCurrent(SaveSnapshot snapshot, string gameInstallDir, string backupDir)
        {
            var count = 0;
            foreach (var file in snapshot.Files)
            {
                var spec = FindSpec(snapshot.Paths, file.Title);
                if (spec == null)
                {
                    continue;
                }

                List<string> unresolved;
                var root = SavePathAdapter.Resolve(spec, gameInstallDir, out unresolved);
                if (unresolved.Count > 0)
                {
                    continue;
                }

                var source = TargetPath(spec, root, file);
                if (!File.Exists(source))
                {
                    continue;
                }

                var dest = Path.Combine(backupDir, file.Path.Replace('/', Path.DirectorySeparatorChar));
                var dir = Path.GetDirectoryName(dest);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                try
                {
                    File.Copy(source, dest, true);
                    count++;
                }
                catch (Exception ex)
                {
                    VaultLog.Warn("留底时拷贝失败（继续）：" + source + "：" + ex.Message);
                }
            }

            return count;
        }

        /// <summary>
        /// 本地留底只留最近几份，免得越攒越多把磁盘吃掉。
        /// <paramref name="keep"/> ≤ 0 表示不限。远端那层留底归保留策略管，这里只管本地这份。
        /// </summary>
        private void PruneLocalBackups(string gameKey, int keep)
        {
            if (keep <= 0)
            {
                return;
            }

            try
            {
                var root = Path.Combine(dataPath, "save-backup", gameKey);
                if (!Directory.Exists(root))
                {
                    return;
                }

                // 目录名就是 yyyyMMdd-HHmmss，字符串降序 == 时间降序
                var dirs = Directory.GetDirectories(root)
                    .OrderByDescending(d => d, StringComparer.Ordinal)
                    .ToList();

                foreach (var stale in dirs.Skip(keep))
                {
                    try
                    {
                        Directory.Delete(stale, true);
                        VaultLog.Info("清理旧的本地留底：" + stale);
                    }
                    catch (Exception ex)
                    {
                        VaultLog.Warn("清理本地留底失败（跳过）：" + stale + "：" + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                VaultLog.Warn("清理本地留底时出错（继续）：" + ex.Message);
            }
        }

        /// <summary>数一下本地有多少文件不在快照里（只报告，不删）。</summary>
        private int CountExtras(SaveSnapshot snapshot, string gameInstallDir)
        {
            var inSnapshot = new HashSet<string>(
                snapshot.Files.Select(f => f.Path.Replace('\\', '/')),
                StringComparer.OrdinalIgnoreCase);

            var extras = 0;
            foreach (var spec in snapshot.Paths.Where(s => s.Enabled))
            {
                List<string> unresolved;
                var root = SavePathAdapter.Resolve(spec, gameInstallDir, out unresolved);
                if (unresolved.Count > 0)
                {
                    continue;
                }

                if (spec.Type == SaveElementType.File)
                {
                    continue;
                }

                if (!Directory.Exists(root))
                {
                    continue;
                }

                var prefix = TitleOf(spec).Replace('\\', '/').TrimEnd('/') + "/";
                foreach (var file in EnumerateFilesSafe(root, spec.Ignore))
                {
                    if (!inSnapshot.Contains(prefix + file))
                    {
                        extras++;
                    }
                }
            }

            return extras;
        }

        /// <summary>
        /// 从本地对象缓存里拿到内容，没有就下下来。缓存是为了「同一个对象恢复多次只下一次」。
        /// 下完**校验 sha1**：NAS 上的对象被改坏了要在这里就发现，而不是等文件已经覆盖了存档。
        /// </summary>
        private string CacheObject(string gameKey, SaveFileEntry file, out bool downloaded)
        {
            downloaded = false;
            var cached = Path.Combine(dataPath, "save-objects", file.Sha1.Substring(0, 2), file.Sha1);
            if (File.Exists(cached))
            {
                try
                {
                    if (new FileInfo(cached).Length == file.Bytes
                        && string.Equals(Sha1File(cached), file.Sha1, StringComparison.OrdinalIgnoreCase))
                    {
                        counters.ObjectsReused++;
                        return cached;
                    }
                }
                catch
                {
                    // 读不了就当没有，重新下
                }
            }

            var dir = Path.GetDirectoryName(cached);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            try
            {
                client.DownloadFile(ObjectRel(gameKey, file.Sha1), cached, null, cancelToken, transport);
            }
            catch (Exception ex)
            {
                VaultLog.Warn("下载对象失败：" + file.Sha1 + "：" + ex.Message);
                return null;
            }

            var actual = Sha1File(cached);
            if (!string.Equals(actual, file.Sha1, StringComparison.OrdinalIgnoreCase))
            {
                // 坏对象绝不能拿去覆盖存档：删掉它并放弃这个文件
                try { File.Delete(cached); } catch { }
                VaultLog.Error("对象校验失败（远端内容与 sha1 不符）：期望 " + file.Sha1 + "，实际 " + actual,
                    null);
                return null;
            }

            downloaded = true;
            return cached;
        }

        /// <summary>快照/路径定义里用的显示名：没写 Title 时退回路径本身。两边口径必须一致。</summary>
        private static string TitleOf(SavePathSpec spec)
        {
            return string.IsNullOrWhiteSpace(spec.Title) ? (spec.Path ?? string.Empty).Trim() : spec.Title.Trim();
        }

        // ==================================================================
        //  删除与保留
        // ==================================================================

        /// <summary>删掉远端一份快照（连同它独占的对象）。</summary>
        public SaveSyncOutcome DeleteSnapshot(string gameKey, SaveGameManifest manifest,
            string branch, string snapshotId, SaveSyncOptions options)
        {
            var opt = options ?? new SaveSyncOptions();
            BeginOperation();

            var summary = manifest.FindSnapshot(snapshotId);
            if (summary == null)
            {
                return SaveSyncOutcome.Fail("清单里没有快照 " + snapshotId);
            }

            planLines.Add("删除远端快照 " + summary.Id);
            if (opt.DryRun)
            {
                return Finish(true, summary.Id, true);
            }

            if (!opt.AllowDelete)
            {
                return SaveSyncOutcome.Fail("没有开启删除许可，已停下（远端的东西不默认删）");
            }

            client.Delete(SnapshotRel(gameKey, summary.Branch, summary.Id));
            snapshotCache.Remove(CacheKey(gameKey, summary.Branch, summary.Id));
            manifest.Snapshots.RemoveAll(s =>
                string.Equals(s.Id, summary.Id, StringComparison.OrdinalIgnoreCase));

            counters.SnapshotsDeleted++;

            var pruned = PruneInternal(gameKey, manifest, opt);
            SaveManifest(gameKey, manifest);
            UpsertIndex(manifest);
            return Finish(true, summary.Id, false);
        }

        /// <summary>按保留策略裁剪，并回收没人引用的对象。<paramref name="options.AllowDelete"/> 为假时只看不删。</summary>
        public SaveSyncOutcome Prune(string gameKey, SaveGameManifest manifest, SaveSyncOptions options)
        {
            var opt = options ?? new SaveSyncOptions();
            BeginOperation();

            var removed = PruneInternal(gameKey, manifest, opt);

            if (opt.DryRun || removed.Count == 0)
            {
                return Finish(true, null, opt.DryRun);
            }

            SaveManifest(gameKey, manifest);
            UpsertIndex(manifest);
            return Finish(true, null, false);
        }

        /// <summary>
        /// 保留策略：**按分支分别算**（否则多分支游戏会被别的分支挤掉历史）。
        /// 标星的不删、最新的必留；允许删除时才真发 DELETE。
        /// </summary>
        private List<SaveSnapshot> PruneInternal(string gameKey, SaveGameManifest manifest,
            SaveSyncOptions opt)
        {
            var removed = new List<SaveSnapshot>();
            var keep = opt.KeepPerBranch;

            foreach (var branch in manifest.Branches.ToList())
            {
                var list = manifest.SnapshotsIn(branch.Name);
                if (list.Count == 0)
                {
                    continue;
                }

                var nonPinned = list.Where(s => !s.Pinned).ToList();
                var survivors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // 标星的永远留
                foreach (var s in list.Where(s => s.Pinned))
                {
                    survivors.Add(s.Id);
                }

                // 最新的必留（哪怕它没标星、哪怕 keep = 0 表示不限）
                survivors.Add(list[0].Id);

                if (keep > 0)
                {
                    foreach (var s in nonPinned.Take(keep))
                    {
                        survivors.Add(s.Id);
                    }
                }
                else
                {
                    foreach (var s in nonPinned)
                    {
                        survivors.Add(s.Id);
                    }
                }

                foreach (var s in list.Where(s => !survivors.Contains(s.Id)))
                {
                    removed.Add(s);
                    planLines.Add("（保留策略）删除 " + branch.Name + "/" + s.Id);
                }
            }

            if (removed.Count == 0)
            {
                return removed;
            }

            if (!opt.AllowDelete || opt.DryRun)
            {
                // 不让删就先只报告：下一次带着许可来再真删
                foreach (var s in removed)
                {
                    planLines.Add("（未开启删除许可，暂不执行）" + s.Branch + "/" + s.Id);
                }
                return removed;
            }

            // 收下被删快照引用过的对象，稍后判断还有没有人用
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in removed)
            {
                var full = LoadSnapshot(gameKey, s.Branch, s.Id, false);
                if (full != null && full.Files != null)
                {
                    foreach (var f in full.Files)
                    {
                        if (!string.IsNullOrEmpty(f.Sha1))
                        {
                            candidates.Add(f.Sha1);
                        }
                    }
                }

                try
                {
                    client.Delete(SnapshotRel(gameKey, s.Branch, s.Id));
                }
                catch (Exception ex)
                {
                    VaultLog.Warn("删快照失败（继续）：" + s.Id + "：" + ex.Message);
                    continue;
                }

                snapshotCache.Remove(CacheKey(gameKey, s.Branch, s.Id));
                manifest.Snapshots.RemoveAll(x =>
                    string.Equals(x.Id, s.Id, StringComparison.OrdinalIgnoreCase));
                counters.SnapshotsDeleted++;
            }

            // 剩下的快照还引用着什么
            var stillReferenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in manifest.Snapshots)
            {
                var full = LoadSnapshot(gameKey, s.Branch, s.Id, false);
                if (full == null || full.Files == null)
                {
                    // 清单读不到就**不回收**：宁可多留对象，也不能删掉正在被引用的内容
                    return removed;
                }

                foreach (var f in full.Files)
                {
                    if (!string.IsNullOrEmpty(f.Sha1))
                    {
                        stillReferenced.Add(f.Sha1);
                    }
                }
            }

            foreach (var sha1 in candidates)
            {
                if (stillReferenced.Contains(sha1))
                {
                    continue;
                }

                try
                {
                    client.Delete(ObjectRel(gameKey, sha1));
                    counters.ObjectsOrphaned++;
                }
                catch (Exception ex)
                {
                    if (!IsNotFound(ex))
                    {
                        VaultLog.Warn("回收对象失败（继续）：" + sha1 + "：" + ex.Message);
                    }
                }
            }

            return removed;
        }

        // ==================================================================
        //  快照列表 / 分支
        // ==================================================================

        /// <summary>列出远端某个分支上的快照（新 → 旧）。</summary>
        public List<SaveSnapshot> ListRemote(string gameKey, string branch)
        {
            var manifest = LoadManifest(gameKey);
            if (manifest == null)
            {
                return new List<SaveSnapshot>();
            }

            return manifest.SnapshotsIn(branch);
        }

        /// <summary>新建一条分支，可以从某份快照分叉。</summary>
        public SaveGameManifest CreateBranch(string gameKey, SaveGameManifest manifest,
            string name, string fromSnapshotId, string comment)
        {
            var branch = manifest.FindBranch(name);
            if (branch != null)
            {
                throw new InvalidOperationException("分支「" + name + "」已经存在");
            }

            branch = new SaveBranch
            {
                Name = SafeSegment(name),
                CreatedFrom = fromSnapshotId,
                Comment = comment
            };
            manifest.Branches.Add(branch);
            SaveManifest(gameKey, manifest);
            return manifest;
        }

        // ==================================================================
        //  进度与收尾
        // ==================================================================

        private void PrepareProgress(SaveSnapshot snapshot)
        {
            progress = new SyncProgress
            {
                Phase = "存档",
                FilesTotal = snapshot.FileCount,
                BytesTotal = snapshot.FileSize
            };
            reporter.Progress(progress);
        }

        private void Advance(long bytes, string file)
        {
            if (progress == null)
            {
                return;
            }

            progress.FilesDone++;
            progress.BytesDone += bytes;
            progress.CurrentFile = file;
            reporter.Progress(progress);
        }

        private void FinishProgress()
        {
            if (progress != null)
            {
                progress.FilesDone = progress.FilesTotal;
                progress.BytesDone = progress.BytesTotal;
                reporter.Progress(progress);
            }
        }

        private void Report(string text)
        {
            reporter.Stage(text);
            VaultLog.Info("存档同步：" + text);
        }

        private SaveSyncOutcome Finish(bool ok, string snapshotId, bool dryRun)
        {
            return new SaveSyncOutcome
            {
                Ok = ok,
                SnapshotId = snapshotId,
                DryRun = dryRun,
                Counters = counters,
                Plan = planLines
            };
        }

        // ==================================================================
        //  静态工具
        // ==================================================================

        private static string CacheKey(string gameKey, string branch, string snapshotId)
        {
            return gameKey + "/" + branch + "/" + snapshotId;
        }

        private static string MakeSnapshotId(DateTime utc)
        {
            return utc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + SafeMachineName(Environment.MachineName);
        }

        private static string SafeMachineName(string name)
        {
            var raw = (name ?? string.Empty).Trim();
            if (raw.Length == 0)
            {
                return "unknown";
            }

            var sb = new StringBuilder(raw.Length);
            foreach (var c in raw)
            {
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            }

            return sb.ToString();
        }

        private static string Short(string sha1)
        {
            return string.IsNullOrEmpty(sha1) ? "(空)" : sha1.Substring(0, Math.Min(8, sha1.Length));
        }

        /// <summary>相对 <paramref name="root"/> 的路径（一律用 <c>/</c>）。</summary>
        private static string RelativeWithin(string root, string path)
        {
            var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)
                           + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(path);
            if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("文件不在存档目录内：" + path);
            }

            return full.Substring(rootFull.Length).Replace('\\', '/');
        }

        private static string CombineUnder(string root, string relative)
        {
            var full = Path.GetFullPath(Path.Combine(root,
                (relative ?? string.Empty).Replace('/', Path.DirectorySeparatorChar)));
            var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)
                           + Path.DirectorySeparatorChar;
            if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("路径越出目标目录，已拒绝：" + relative);
            }

            return full;
        }

        /// <summary>列文件（相对路径，用 <c>/</c>），跳过 reparse point 与忽略项。与 Collect 的口径必须一致。</summary>
        private static List<string> EnumerateFilesSafe(string root, List<string> ignore)
        {
            var result = new List<string>();
            var pending = new Stack<string>();
            pending.Push(root);

            while (pending.Count > 0)
            {
                var dir = pending.Pop();
                string[] subDirs;
                string[] files;

                try
                {
                    subDirs = Directory.GetDirectories(dir);
                    files = Directory.GetFiles(dir);
                }
                catch
                {
                    continue;
                }

                foreach (var sub in subDirs)
                {
                    try
                    {
                        if ((new DirectoryInfo(sub).Attributes & FileAttributes.ReparsePoint) != 0)
                        {
                            continue;
                        }
                    }
                    catch
                    {
                        continue;
                    }

                    pending.Push(sub);
                }

                foreach (var file in files)
                {
                    string rel;
                    try
                    {
                        rel = RelativeWithin(root, file);
                    }
                    catch
                    {
                        continue;
                    }

                    if (!IsIgnored(rel, ignore))
                    {
                        result.Add(rel);
                    }
                }
            }

            result.Sort(StringComparer.Ordinal);
            return result;
        }

        /// <summary>
        /// 极简通配：<c>*</c> 任意串、<c>?</c> 一个字符，忽略大小写。
        /// 同时拿整条相对路径和「文件名」各试一次 —— 用户写 <c>*.log</c> 时想的是文件名。
        /// </summary>
        public static bool IsIgnored(string relativePath, List<string> patterns)
        {
            if (patterns == null || patterns.Count == 0 || string.IsNullOrEmpty(relativePath))
            {
                return false;
            }

            var rel = relativePath.Replace('\\', '/');
            var name = rel;
            var slash = rel.LastIndexOf('/');
            if (slash >= 0)
            {
                name = rel.Substring(slash + 1);
            }

            foreach (var pattern in patterns)
            {
                if (string.IsNullOrWhiteSpace(pattern))
                {
                    continue;
                }

                var p = pattern.Trim().Replace('\\', '/');
                if (GlobMatch(p, rel) || GlobMatch(p, name))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool GlobMatch(string pattern, string text)
        {
            // 经典的两行回溯写法，够用且不会栈溢出
            var p = 0;
            var t = 0;
            var star = -1;
            var mark = 0;

            while (t < text.Length)
            {
                if (p < pattern.Length && (pattern[p] == '?' || char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(text[t])))
                {
                    p++;
                    t++;
                }
                else if (p < pattern.Length && pattern[p] == '*')
                {
                    star = p++;
                    mark = t;
                }
                else if (star >= 0)
                {
                    p = star + 1;
                    t = ++mark;
                }
                else
                {
                    return false;
                }
            }

            while (p < pattern.Length && pattern[p] == '*')
            {
                p++;
            }

            return p == pattern.Length;
        }

        public static string Sha1File(string path)
        {
            using (var sha = SHA1.Create())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024))
            {
                return ToHex(sha.ComputeHash(stream));
            }
        }

        public static string Sha1Text(string text)
        {
            using (var sha = SHA1.Create())
            {
                return ToHex(sha.ComputeHash(new UTF8Encoding(false).GetBytes(text ?? string.Empty)));
            }
        }

        private static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes)
            {
                sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        private static bool IsNotFound(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                var web = e as WebException;
                var response = web == null ? null : web.Response as HttpWebResponse;
                if (response != null)
                {
                    return response.StatusCode == HttpStatusCode.NotFound
                           || response.StatusCode == HttpStatusCode.Gone;
                }
            }

            return false;
        }
    }
}
