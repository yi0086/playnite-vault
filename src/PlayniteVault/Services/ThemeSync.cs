using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// <summary>一次主题同步的结果快照。</summary>
    public class ThemeSyncResult
    {
        public ThemeSyncCounters Counters { get; set; } = new ThemeSyncCounters();

        /// <summary>人类可读的计划/动作清单，--dry-run 时就是全部产出。</summary>
        public List<string> Plan { get; set; } = new List<string>();

        public TimeSpan Elapsed { get; set; }

        public bool DryRun { get; set; }
    }

    /// <summary>
    /// 主题同步引擎：Playnite 的 <c>Themes</c> 目录 ⇄ WebDAV 上的 <c>themes/</c> 目录。
    ///
    /// <para><b>远端布局刻意与本地同构</b>：</para>
    /// <code>
    ///   themes/index.json                       全局索引（每个主题一行指纹）
    ///   themes/Desktop/{Id}/manifest.json       该主题的文件清单（路径 → sha1）
    ///   themes/Desktop/{Id}/&lt;原始相对路径&gt;      真实文件，目录结构照搬
    ///   themes/Fullscreen/{Id}/...
    /// </code>
    /// <para>
    /// 为什么不套用游戏的「内容寻址区块」？因为两者形状完全不同：游戏是几个 GB 的大二进制，
    /// 分块是为了去重和断点续传；主题是一堆 xaml/yaml/png/ttf 小文件，而且 Playnite 本来就
    /// 按目录直接读。存成普通文件镜像之后，NAS 上那棵树本身就是一份能用的主题库 ——
    /// 可以直接浏览，也可以整个 <c>Desktop\*</c> 拷回本地。
    /// </para>
    ///
    /// <para><b>冲突策略（三方比较）</b>：以「上次同步后双方的指纹」为基准，</para>
    /// <list type="bullet">
    /// <item>只有一边变了 → 变的那边覆盖另一边；</item>
    /// <item>两边都变了 → <b>修改时间新的赢</b>，输的那份原地留成
    /// <c>{Id}.conflict-local-&lt;时间戳&gt;</c>（本地）或
    /// <c>{Id}.conflict-remote-&lt;时间戳&gt;</c>（远端），一个字节都不丢；</item>
    /// <item>没有基准可依（第一次同步、或换了目录）→ 按时间定胜负。</item>
    /// </list>
    ///
    /// <para><b>远端只增不减</b>：本地删掉一个主题不会同步删掉远端那份 ——
    /// 这里是「备份档案」而不是「双向镜像」。所以有 <see cref="ThemeSyncCounters.RemoteOnly"/>
    /// 这套只提示、不动手的记录。</para>
    /// </summary>
    public class ThemeSyncEngine
    {
        /// <summary>远端仓库里的主题根目录。</summary>
        public const string RemoteRoot = "themes";

        private const string IndexFile = RemoteRoot + "/index.json";
        private const string ManifestName = "manifest.json";

        /// <summary>冲突副本的标记。出现在目录名里，扫描时会被跳过。</summary>
        private const string ConflictTag = ".conflict-";

        /// <summary>Playnite 的主题就是按这两个子目录分的。</summary>
        private static readonly string[] Modes = { "Desktop", "Fullscreen" };

        private static readonly JsonSerializerSettings Json = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
            NullValueHandling = NullValueHandling.Ignore
        };

        private readonly WebDavClient client;
        private readonly string themesRoot;
        private readonly string stateFile;
        private readonly SyncOptions transport;
        private readonly IThemeSyncReporter reporter;
        private readonly CancellationToken cancelToken;

        // 进度：传输是串行的（主题都是小文件，NAS 的写入侧本来就是瓶颈，
        // 并发只会互相抢），所以这里可以放心用单写方的简单累加。
        private readonly ThemeSyncCounters counters = new ThemeSyncCounters();
        private SyncProgress progress;
        private long progressBase;
        private readonly Stopwatch reportWatch = Stopwatch.StartNew();
        private readonly List<string> planLines = new List<string>();

        /// <summary>
        /// 刚下载完的主题清单（按「模式/Id」）。删完远端那份之后本地目录里的文件
        /// 就是清单本身，没必要为了算指纹再全量读一遍磁盘。
        /// 刻意做成实例字段而不是静态缓存：同一个进程里跑第二次同步时，
        /// 静态缓存会把上一轮的陈旧清单交出去。
        /// </summary>
        private readonly Dictionary<string, ThemeManifest> downloadedManifests =
            new Dictionary<string, ThemeManifest>(StringComparer.OrdinalIgnoreCase);

        public ThemeSyncEngine(WebDavClient client, string themesRoot, string stateFile,
            SyncOptions transport, IThemeSyncReporter reporter, CancellationToken cancelToken)
        {
            if (client == null)
            {
                throw new ArgumentNullException("client");
            }
            if (string.IsNullOrWhiteSpace(themesRoot))
            {
                throw new ArgumentException("主题目录不能为空", "themesRoot");
            }

            this.client = client;
            this.themesRoot = Path.GetFullPath(themesRoot);
            this.stateFile = stateFile;
            this.transport = transport ?? new SyncOptions();
            this.reporter = reporter ?? new NullThemeReporter();
            this.cancelToken = cancelToken;
        }

        public string ThemesRoot
        {
            get { return themesRoot; }
        }

        // ==================================================================
        //  主流程
        // ==================================================================

        public ThemeSyncResult Run(ThemeSyncOptions options)
        {
            var opt = options ?? new ThemeSyncOptions();
            var started = Stopwatch.StartNew();
            var result = new ThemeSyncResult { DryRun = opt.DryRun, Plan = planLines };

            Report("扫描本地主题：" + themesRoot);
            var local = ScanLocal();
            Report(string.Format("  本地 {0} 个主题，{1}", local.Count, SyncProgress.FormatSize(Sum(local))));

            Report("读取远端索引：" + IndexFile);
            var remoteIndex = LoadRemoteIndex();
            Report(string.Format("  远端 {0} 个主题", remoteIndex.Themes.Count));

            var state = LoadState();
            if (!string.IsNullOrEmpty(state.Root)
                && !string.Equals(NormalizeRoot(state.Root), NormalizeRoot(themesRoot), StringComparison.OrdinalIgnoreCase))
            {
                // 换了主题目录 = 换了同步对象，旧的基准不再适用
                Report("上次同步用的是另一个主题目录（" + state.Root + "），基准已重置");
                state.RemoteFingerprints.Clear();
                state.LocalFingerprints.Clear();
            }
            state.Root = themesRoot;

            var plan = BuildPlan(local, remoteIndex, state, opt);
            LogPlan(plan, opt);

            if (opt.DryRun)
            {
                result.Counters = counters;
                result.Elapsed = started.Elapsed;
                return result;
            }

            PrepareProgress(plan);
            var index = remoteIndex.Themes.ToDictionary(EntryKey, e => e, StringComparer.OrdinalIgnoreCase);
            var applied = new List<KeyValuePair<string, string>>();

            foreach (var item in plan)
            {
                cancelToken.ThrowIfCancellationRequested();
                switch (item.Action)
                {
                    case ThemeAction.UploadTheme:
                        UploadTheme(item, opt.Force);
                        index[Key(item)] = ToIndexEntry(item.Local);
                        applied.Add(new KeyValuePair<string, string>(Key(item), item.Local.Fingerprint));
                        counters.ThemesUploaded++;
                        break;

                    case ThemeAction.DownloadTheme:
                        DownloadTheme(item, opt.Force);
                        index[Key(item)] = ToIndexEntry(item.Remote);
                        applied.Add(new KeyValuePair<string, string>(Key(item), item.Remote.Fingerprint));
                        counters.ThemesDownloaded++;
                        break;

                    case ThemeAction.SaveConflict:
                        ResolveConflict(item, opt, index, applied);
                        break;

                    default:
                        break;
                }
            }

            // 索引与基准最后一起落盘：中途失败时宁可保持「上次同步」的旧状态，
            // 也不要写成一个半真半假的基准 —— 那会让下一次同步把两边都判成「都改过」。
            if (applied.Count > 0 || counters.RemoteOnly.Count > 0)
            {
                WriteIndex(index);
                foreach (var pair in applied)
                {
                    state.RemoteFingerprints[pair.Key] = pair.Value;
                    state.LocalFingerprints[pair.Key] = pair.Value;
                }
                state.LastSyncAt = DateTime.UtcNow;
                SaveState(state);
            }

            FinishProgress();
            result.Counters = counters;
            result.Elapsed = started.Elapsed;
            return result;
        }

        // ==================================================================
        //  计划
        // ==================================================================

        private class PlanItem
        {
            public string Mode;
            public string Id;
            public string LocalDir;
            public ThemeManifest Local;
            public ThemeManifest Remote;
            public ThemeAction Action;
            public string Reason = string.Empty;
            public int FilesToMove;
            public long BytesToMove;
        }

        private List<PlanItem> BuildPlan(List<ThemeManifest> local, ThemeIndex remoteIndex,
            ThemeSyncState state, ThemeSyncOptions opt)
        {
            var plan = new List<PlanItem>();

            var remoteEntries = new Dictionary<string, ThemeIndexEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in remoteIndex.Themes)
            {
                remoteEntries[Key(entry.Mode, entry.Id)] = entry;
            }

            var localKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var manifest in local)
            {
                var key = Key(manifest.Mode, manifest.Id);
                localKeys.Add(key);

                // 没勾选的主题直接忽略：它既不上传也不下载，
                // 但仍然记进 localKeys（「本地是有的」，只是这次不参与）。
                if (!opt.Wants(manifest.Mode, manifest.Id))
                {
                    continue;
                }

                var item = new PlanItem
                {
                    Mode = manifest.Mode,
                    Id = manifest.Id,
                    Local = manifest,
                    LocalDir = Path.Combine(themesRoot, manifest.Mode, manifest.Id)
                };

                ThemeIndexEntry entry;
                if (!remoteEntries.TryGetValue(key, out entry))
                {
                    if (opt.Mode == ThemeSyncMode.Download)
                    {
                        item.Action = ThemeAction.Skip;
                        item.Reason = "远端没有，本次是「只下载」";
                    }
                    else
                    {
                        item.Action = ThemeAction.UploadTheme;
                        item.Reason = "远端没有（首次上传）";
                        CountUpload(item, null, opt.Force);
                    }
                    plan.Add(item);
                    continue;
                }

                item.Remote = LoadRemoteManifest(manifest.Mode, manifest.Id);
                var remoteFp = entry.Fingerprint;

                if (!opt.Force && string.Equals(remoteFp, manifest.Fingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    item.Action = ThemeAction.Skip;
                    item.Reason = "两边指纹一致";
                    counters.ThemesUnchanged++;
                    plan.Add(item);
                    continue;
                }

                string baseFp;
                state.RemoteFingerprints.TryGetValue(key, out baseFp);

                if (baseFp == null)
                {
                    // 没有共同基准：可能是第一次同步这个主题，也可能中途换过目录。
                    // 分不清「谁改的」就只能按时间定胜负。
                    item.Action = ThemeAction.SaveConflict;
                    item.Reason = "没有共同基准，按修改时间定胜负";
                }
                else
                {
                    var localChanged = !string.Equals(baseFp, manifest.Fingerprint, StringComparison.OrdinalIgnoreCase);
                    var remoteChanged = !string.Equals(baseFp, remoteFp, StringComparison.OrdinalIgnoreCase);

                    if (localChanged && remoteChanged)
                    {
                        item.Action = ThemeAction.SaveConflict;
                        item.Reason = "两边都改过，按修改时间定胜负";
                    }
                    else if (localChanged)
                    {
                        item.Action = ThemeAction.UploadTheme;
                        item.Reason = "本地有改动";
                        CountUpload(item, item.Remote, opt.Force);
                    }
                    else if (remoteChanged)
                    {
                        item.Action = ThemeAction.DownloadTheme;
                        item.Reason = "远端有改动";
                        CountDownload(item, opt.Force);
                    }
                    else if (opt.Force)
                    {
                        // 两边一致本来该跳过，但 --force 就是用来对付
                        // 「指纹对得上、文件其实坏了」这种情况的，所以照样全传一遍
                        item.Action = ThemeAction.UploadTheme;
                        item.Reason = "强制整树重传";
                        CountUpload(item, item.Remote, true);
                    }
                    else
                    {
                        item.Action = ThemeAction.Skip;
                        item.Reason = "指纹一致";
                        counters.ThemesUnchanged++;
                    }
                }

                // 单向模式下的越权动作一律降级为跳过：上传模式绝不悄悄拉远端覆盖本地，
                // 下载模式也绝不悄悄推本地覆盖远端。
                if (opt.Mode == ThemeSyncMode.Upload && item.Action == ThemeAction.DownloadTheme)
                {
                    item.Action = ThemeAction.Skip;
                    item.Reason = "远端有改动，但本次只上传（本地保留现状）";
                }
                else if (opt.Mode == ThemeSyncMode.Download && item.Action == ThemeAction.UploadTheme)
                {
                    item.Action = ThemeAction.Skip;
                    item.Reason = "本地有改动，但本次只下载";
                }

                plan.Add(item);
            }

            // 远端有、本地没有
            foreach (var pair in remoteEntries)
            {
                if (localKeys.Contains(pair.Key))
                {
                    continue;
                }

                // 勾选过滤在这里同样生效：没勾的主题不会被悄悄拉下来。
                if (!opt.Wants(pair.Value.Mode, pair.Value.Id))
                {
                    continue;
                }

                var entry = pair.Value;
                var item = new PlanItem
                {
                    Mode = entry.Mode,
                    Id = entry.Id,
                    Remote = LoadRemoteManifest(entry.Mode, entry.Id),
                    LocalDir = Path.Combine(themesRoot, entry.Mode, entry.Id)
                };

                string baseFp;
                state.RemoteFingerprints.TryGetValue(pair.Key, out baseFp);
                var remoteChanged = baseFp == null
                    || !string.Equals(baseFp, entry.Fingerprint, StringComparison.OrdinalIgnoreCase);

                if (!remoteChanged)
                {
                    item.Action = ThemeAction.Skip;
                    item.Reason = "本地已删除（远端保留，不做镜像删除）";
                }
                else if (opt.Mode == ThemeSyncMode.Upload)
                {
                    item.Action = ThemeAction.Skip;
                    item.Reason = "远端独有，本次只上传";
                }
                else
                {
                    item.Action = ThemeAction.DownloadTheme;
                    item.Reason = baseFp == null ? "远端独有，拉到本地" : "远端更新过，本地没有，拉回来";
                    CountDownload(item, opt.Force);
                }

                counters.RemoteOnly.Add(Key(entry.Mode, entry.Id));
                plan.Add(item);
            }

            return plan;
        }

        /// <summary>算「这次到底要搬几个文件、多少字节」——只有远端清单在手时才能跳过没变的文件。</summary>
        private void CountUpload(PlanItem item, ThemeManifest remote, bool force)
        {
            var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (remote != null && remote.Files != null)
            {
                foreach (var f in remote.Files)
                {
                    known[f.Path] = f.Sha1;
                }
            }

            foreach (var f in item.Local.Files)
            {
                string sha;
                if (!force && known.TryGetValue(f.Path, out sha)
                    && string.Equals(sha, f.Sha1, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                item.FilesToMove++;
                item.BytesToMove += f.Bytes;
            }
        }

        private void CountDownload(PlanItem item, bool force)
        {
            var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (item.Local != null)
            {
                foreach (var f in item.Local.Files)
                {
                    known[f.Path] = f.Sha1;
                }
            }

            foreach (var f in item.Remote.Files)
            {
                string sha;
                if (!force && known.TryGetValue(f.Path, out sha)
                    && string.Equals(sha, f.Sha1, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                item.FilesToMove++;
                item.BytesToMove += f.Bytes;
            }
        }

        private void LogPlan(List<PlanItem> plan, ThemeSyncOptions opt)
        {
            Report(string.Format("---- 计划（模式 {0}{1}）----", opt.Mode,
                opt.Force ? "，强制整树重传" : string.Empty));

            foreach (var item in plan)
            {
                string mark;
                switch (item.Action)
                {
                    case ThemeAction.UploadTheme: mark = "↑ 上传"; break;
                    case ThemeAction.DownloadTheme: mark = "↓ 下载"; break;
                    case ThemeAction.SaveConflict: mark = "! 冲突"; break;
                    default: mark = "- 跳过"; break;
                }

                var line = string.Format("{0}  {1}/{2}   {3}", mark, item.Mode, item.Id, item.Reason);
                if (item.FilesToMove > 0)
                {
                    line += string.Format("（{0} 个文件，{1}）", item.FilesToMove, SyncProgress.FormatSize(item.BytesToMove));
                }
                Report(line);
            }

            if (counters.RemoteOnly.Count > 0)
            {
                Report(string.Format("远端独有 {0} 个（只提示，不会删除）：{1}",
                    counters.RemoteOnly.Count, string.Join("、", counters.RemoteOnly.ToArray())));
            }
        }

        // ==================================================================
        //  执行
        // ==================================================================

        private void UploadTheme(PlanItem item, bool force)
        {
            var remoteDir = RemoteDir(item.Mode, item.Id);
            Report("↑ " + item.Mode + "/" + item.Id);

            var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (item.Remote != null && item.Remote.Files != null)
            {
                foreach (var f in item.Remote.Files)
                {
                    known[f.Path] = f.Sha1;
                }
            }

            // 先把要用的目录建出来（去重），免得每个文件都 MKCOL 一轮
            var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in item.Local.Files)
            {
                var idx = f.Path.LastIndexOf('/');
                if (idx > 0)
                {
                    dirs.Add(remoteDir + "/" + f.Path.Substring(0, idx));
                }
            }
            client.EnsureDirectoryRecursive(remoteDir);
            foreach (var dir in dirs.OrderBy(d => d.Length))
            {
                client.EnsureDirectoryRecursive(dir);
            }

            foreach (var f in item.Local.Files)
            {
                cancelToken.ThrowIfCancellationRequested();

                string sha;
                if (!force && known.TryGetValue(f.Path, out sha)
                    && string.Equals(sha, f.Sha1, StringComparison.OrdinalIgnoreCase))
                {
                    counters.FilesSkipped++;
                    continue;
                }

                var remoteRel = remoteDir + "/" + f.Path;
                var localPath = CombineUnder(item.LocalDir, f.Path);

                BeginFile(string.Format("上传 {0}/{1}/{2}", item.Mode, item.Id, f.Path));
                client.UploadFile(localPath, remoteRel, OnFileProgress, cancelToken,
                    Math.Max(1, transport.MaxRetries), OnRetry);

                counters.FilesUploaded++;
                counters.BytesUp += f.Bytes;
                progressBase += f.Bytes;
            }

            client.UploadString(JsonConvert.SerializeObject(item.Local, Json), remoteDir + "/" + ManifestName);
        }

        private void DownloadTheme(PlanItem item, bool force)
        {
            Report("↓ " + item.Mode + "/" + item.Id);

            var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (item.Local != null)
            {
                foreach (var f in item.Local.Files)
                {
                    known[f.Path] = f.Sha1;
                }
            }

            var localDir = Path.Combine(themesRoot, item.Mode, item.Id);
            Directory.CreateDirectory(localDir);

            foreach (var f in item.Remote.Files)
            {
                cancelToken.ThrowIfCancellationRequested();

                string sha;
                if (!force && known.TryGetValue(f.Path, out sha)
                    && string.Equals(sha, f.Sha1, StringComparison.OrdinalIgnoreCase))
                {
                    counters.FilesSkipped++;
                    continue;
                }

                // 远端清单是外部数据，路径必须过一遍清洗再拼到本地磁盘上
                var rel = SafeRelative(f.Path);
                var target = CombineUnder(localDir, rel);
                var remoteRel = RemoteDir(item.Mode, item.Id) + "/" + rel;
                Directory.CreateDirectory(Path.GetDirectoryName(target));

                BeginFile(string.Format("下载 {0}/{1}/{2}", item.Mode, item.Id, rel));
                client.DownloadFile(remoteRel, target, OnFileProgress, cancelToken, transport);

                counters.FilesDownloaded++;
                counters.BytesDown += f.Bytes;
                progressBase += f.Bytes;
            }

            // 本地清单要重算：光看远端清单不够，本地可能本来就有远端没有的文件
            downloadedManifests[Key(item.Mode, item.Id)] =
                BuildManifest(item.Mode, item.Id, localDir, null);
        }

        /// <summary>两边都改过 → 时间新的赢，输的那份留一个 .conflict-&lt;时间戳&gt; 副本。</summary>
        private void ResolveConflict(PlanItem item, ThemeSyncOptions opt,
            Dictionary<string, ThemeIndexEntry> index, List<KeyValuePair<string, string>> applied)
        {
            counters.Conflicts++;

            if (item.Remote == null)
            {
                // 索引里有、清单读不到：远端状态不可信，宁可不动
                Report("! " + item.Mode + "/" + item.Id + " 远端清单缺失，跳过以免误判");
                return;
            }

            var localNewest = item.Local.UpdatedAt;
            var remoteNewest = item.Remote.UpdatedAt;
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var localWins = localNewest >= remoteNewest;

            Report(string.Format("! {0}/{1} 本地 {2} / 远端 {3} → {4} 胜出",
                item.Mode, item.Id,
                localNewest.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                remoteNewest.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                localWins ? "本地" : "远端"));

            if (localWins)
            {
                if (opt.Mode == ThemeSyncMode.Download)
                {
                    Report("  （本次只下载，不覆盖远端；留待下次）");
                    return;
                }

                PreserveRemote(item, stamp);
                UploadTheme(item, false);
                index[Key(item)] = ToIndexEntry(item.Local);
                applied.Add(new KeyValuePair<string, string>(Key(item), item.Local.Fingerprint));
                counters.ThemesUploaded++;
            }
            else
            {
                if (opt.Mode == ThemeSyncMode.Upload)
                {
                    Report("  （本次只上传，不覆盖本地；留待下次）");
                    return;
                }

                PreserveLocal(item, stamp);
                DownloadTheme(item, false);

                var rebuilt = downloadedManifests[Key(item.Mode, item.Id)];
                index[Key(item)] = ToIndexEntry(rebuilt);
                applied.Add(new KeyValuePair<string, string>(Key(item), rebuilt.Fingerprint));
                counters.ThemesDownloaded++;
            }
        }

        /// <summary>本地要输了：把本地那份整体改名留档，再拉远端下来。</summary>
        private void PreserveLocal(PlanItem item, string stamp)
        {
            var target = item.LocalDir + ConflictTag + "local-" + stamp;
            if (Directory.Exists(target))
            {
                return;
            }
            Directory.Move(item.LocalDir, target);
            Report("  本地那份已留档：" + Path.GetFileName(target));
        }

        /// <summary>
        /// 远端要输了：先把远端那份整个复制到 <c>themes/{Mode}/{Id}.conflict-remote-&lt;时间戳&gt;/</c>，
        /// 再让本地上传覆盖。WebDAV 没有可靠的「服务端拷贝」（各家的 COPY 支持参差），
        /// 所以老老实实下载一遍再传回去 —— 冲突本来就少见，这点代价换「一个字节都不丢」值得。
        /// </summary>
        private void PreserveRemote(PlanItem item, string stamp)
        {
            var conflictDir = RemoteDir(item.Mode, item.Id) + ConflictTag + "remote-" + stamp;
            var temp = Path.Combine(Path.GetTempPath(), "vault-theme-conflict",
                Guid.NewGuid().ToString("N"));

            try
            {
                Directory.CreateDirectory(temp);
                client.EnsureDirectoryRecursive(conflictDir);

                foreach (var f in item.Remote.Files)
                {
                    cancelToken.ThrowIfCancellationRequested();
                    var rel = SafeRelative(f.Path);
                    var local = Path.Combine(temp, rel.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(local));

                    client.DownloadFile(RemoteDir(item.Mode, item.Id) + "/" + rel, local, null, cancelToken, transport);

                    var idx = rel.LastIndexOf('/');
                    if (idx > 0)
                    {
                        client.EnsureDirectoryRecursive(conflictDir + "/" + rel.Substring(0, idx));
                    }
                    client.UploadFile(local, conflictDir + "/" + rel, null, cancelToken,
                        Math.Max(1, transport.MaxRetries), null);
                }

                client.UploadString(JsonConvert.SerializeObject(item.Remote, Json),
                    conflictDir + "/" + ManifestName);

                Report("  远端那份已留档：themes/" + item.Mode + "/" + item.Id + ConflictTag + "remote-" + stamp);
            }
            catch (Exception ex)
            {
                // 留档失败不能吞掉：继续上传就会真的丢掉远端那份
                throw new InvalidOperationException(
                    string.Format("留档远端冲突副本失败，已中止以免丢数据：{0}", ex.Message), ex);
            }
            finally
            {
                try
                {
                    if (Directory.Exists(temp))
                    {
                        Directory.Delete(temp, true);
                    }
                }
                catch
                {
                    // 临时目录清不掉无所谓，系统会回收
                }
            }
        }

        // ==================================================================
        //  本地扫描
        // ==================================================================

        private List<ThemeManifest> ScanLocal()
        {
            var list = new List<ThemeManifest>();

            foreach (var mode in Modes)
            {
                var modeDir = Path.Combine(themesRoot, mode);
                if (!Directory.Exists(modeDir))
                {
                    continue;
                }

                foreach (var dir in Directory.GetDirectories(modeDir))
                {
                    var id = Path.GetFileName(dir);
                    if (string.IsNullOrEmpty(id) || id.StartsWith(".") || id.Contains(ConflictTag))
                    {
                        continue;
                    }
                    if (!File.Exists(Path.Combine(dir, "theme.yaml")))
                    {
                        // 没有 theme.yaml 的目录 Playnite 也不认，别把它当成主题传上去
                        VaultLog.Warn("跳过不含 theme.yaml 的目录：" + dir);
                        continue;
                    }

                    list.Add(BuildManifest(mode, id, dir, null));
                }
            }

            return list;
        }

        /// <summary>
        /// 给一个主题目录做清单。传 <paramref name="previous"/> 时会把之前算过的哈希当缓存用 ——
        /// 只对「大小和 mtime 都没变」的文件省一次全文件读取。
        /// </summary>
        private ThemeManifest BuildManifest(string mode, string id, string dir, ThemeManifest previous)
        {
            var cached = new Dictionary<string, ThemeFileEntry>(StringComparer.OrdinalIgnoreCase);
            if (previous != null && previous.Files != null)
            {
                foreach (var f in previous.Files)
                {
                    cached[f.Path] = f;
                }
            }

            var manifest = new ThemeManifest { Id = id, Mode = mode, Name = id };
            var files = new List<ThemeFileEntry>();
            long bytes = 0;
            var newest = DateTime.MinValue;

            foreach (var path in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                cancelToken.ThrowIfCancellationRequested();

                var rel = Relative(dir, path).Replace('\\', '/');
                if (rel.EndsWith(".part", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(rel, ManifestName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var info = new FileInfo(path);
                string sha1;

                ThemeFileEntry hit;
                if (cached.TryGetValue(rel, out hit)
                    && hit.Bytes == info.Length
                    && hit.Modified == info.LastWriteTimeUtc)
                {
                    sha1 = hit.Sha1;
                }
                else
                {
                    sha1 = Sha1File(path);
                }

                files.Add(new ThemeFileEntry
                {
                    Path = rel,
                    Sha1 = sha1,
                    Bytes = info.Length,
                    Modified = info.LastWriteTimeUtc
                });

                bytes += info.Length;
                if (info.LastWriteTimeUtc > newest)
                {
                    newest = info.LastWriteTimeUtc;
                }
            }

            files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));

            manifest.Files = files;
            manifest.Bytes = bytes;
            manifest.UpdatedAt = newest == DateTime.MinValue ? DateTime.UtcNow : newest;
            manifest.Fingerprint = Fingerprint(files);
            ReadThemeYaml(dir, manifest);

            return manifest;
        }

        /// <summary>
        /// 从 <c>theme.yaml</c> 里抠 Name / Version / Mode。
        /// 只做逐行正则，不引 YAML 库 —— 这三个字段是固定的一级标量，不值得为它背一个依赖。
        /// </summary>
        private static void ReadThemeYaml(string dir, ThemeManifest manifest)
        {
            try
            {
                var path = Path.Combine(dir, "theme.yaml");
                if (!File.Exists(path))
                {
                    return;
                }

                foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var idx = line.IndexOf(':');
                    if (idx <= 0)
                    {
                        continue;
                    }

                    var key = line.Substring(0, idx).Trim().ToLowerInvariant();
                    var value = line.Substring(idx + 1).Trim().Trim('"', '\'');
                    if (value.Length == 0)
                    {
                        continue;
                    }

                    switch (key)
                    {
                        case "name": manifest.Name = value; break;
                        case "version": manifest.Version = value; break;
                        case "mode": manifest.Mode = value; break;
                    }
                }
            }
            catch (Exception ex)
            {
                VaultLog.Warn("读取 theme.yaml 失败（不影响同步）：" + ex.Message);
            }
        }

        // ==================================================================
        //  远端读写
        // ==================================================================

        private ThemeIndex LoadRemoteIndex()
        {
            string json;
            try
            {
                json = client.DownloadString(IndexFile);
            }
            catch (Exception ex)
            {
                if (IsNotFound(ex))
                {
                    return new ThemeIndex();
                }
                throw;
            }

            ThemeIndex index;
            try
            {
                index = JsonConvert.DeserializeObject<ThemeIndex>(json);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "远端 " + IndexFile + " 不是合法的 JSON，已停止（不会覆盖它）：" + ex.Message, ex);
            }

            if (index == null)
            {
                return new ThemeIndex();
            }

            // Kind 对不上说明这个 themes/ 目录不是本插件建的。绝不能往上写：
            // 覆盖别人的索引比同步失败严重得多。
            if (!string.Equals(index.Kind, ThemeIndex.ThemeIndexKind, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "远端 " + IndexFile + " 的 Kind 是「" + index.Kind + "」，不是本插件的主题索引，已停止以免覆盖。");
            }

            if (index.Themes == null)
            {
                index.Themes = new List<ThemeIndexEntry>();
            }
            return index;
        }

        private ThemeManifest LoadRemoteManifest(string mode, string id)
        {
            try
            {
                var json = client.DownloadString(RemoteDir(mode, id) + "/" + ManifestName);
                var manifest = JsonConvert.DeserializeObject<ThemeManifest>(json);
                if (manifest == null || manifest.Files == null)
                {
                    return null;
                }
                manifest.Id = string.IsNullOrEmpty(manifest.Id) ? id : manifest.Id;
                manifest.Mode = string.IsNullOrEmpty(manifest.Mode) ? mode : manifest.Mode;
                return manifest;
            }
            catch (Exception ex)
            {
                if (IsNotFound(ex))
                {
                    return null;
                }
                VaultLog.Warn(string.Format("读取 {0}/{1} 的远端清单失败：{2}", mode, id, ex.Message));
                return null;
            }
        }

        private void WriteIndex(Dictionary<string, ThemeIndexEntry> entries)
        {
            var index = new ThemeIndex { UpdatedAt = DateTime.UtcNow };
            index.Themes.AddRange(entries.Values
                .OrderBy(e => e.Mode, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Id, StringComparer.OrdinalIgnoreCase));
            client.UploadString(JsonConvert.SerializeObject(index, Json), IndexFile);
            Report("索引已更新：" + IndexFile + "（" + index.Themes.Count + " 条）");
        }

        // ==================================================================
        //  状态（三方比对的基准）
        // ==================================================================

        private ThemeSyncState LoadState()
        {
            try
            {
                if (!string.IsNullOrEmpty(stateFile) && File.Exists(stateFile))
                {
                    var state = JsonConvert.DeserializeObject<ThemeSyncState>(
                        File.ReadAllText(stateFile, Encoding.UTF8));
                    if (state != null)
                    {
                        if (state.RemoteFingerprints == null)
                        {
                            state.RemoteFingerprints = NewMap();
                        }
                        if (state.LocalFingerprints == null)
                        {
                            state.LocalFingerprints = NewMap();
                        }
                        return state;
                    }
                }
            }
            catch (Exception ex)
            {
                VaultLog.Warn("读取主题同步状态失败，按首次同步处理：" + ex.Message);
            }

            return new ThemeSyncState();
        }

        private void SaveState(ThemeSyncState state)
        {
            if (string.IsNullOrEmpty(stateFile))
            {
                return;
            }

            try
            {
                var dir = Path.GetDirectoryName(stateFile);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                File.WriteAllText(stateFile, JsonConvert.SerializeObject(state, Json),
                    new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                VaultLog.Warn("保存主题同步状态失败（下次会按首次同步处理）：" + ex.Message);
            }
        }

        private static Dictionary<string, string> NewMap()
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        // ==================================================================
        //  进度
        // ==================================================================

        private void PrepareProgress(List<PlanItem> plan)
        {
            long bytes = 0;
            var files = 0;
            foreach (var item in plan)
            {
                bytes += item.BytesToMove;
                files += item.FilesToMove;
            }

            progress = new SyncProgress
            {
                Phase = "主题同步",
                FilesTotal = files,
                BytesTotal = bytes,
                IsForced = true
            };
            progressBase = 0;
            reporter.Progress(progress);
        }

        private void BeginFile(string what)
        {
            if (progress == null)
            {
                return;
            }
            progress.CurrentFile = what;
            progress.BytesDone = progressBase;
            progress.FilesDone = counters.FilesUploaded + counters.FilesDownloaded + counters.FilesSkipped;
            reporter.Progress(progress);
        }

        private void OnFileProgress(long done, long total)
        {
            if (progress == null)
            {
                return;
            }

            // 小文件多，全量上报会把界面刷爆；按时间降频，收尾那一下必报
            if (done < total && reportWatch.ElapsedMilliseconds < 100)
            {
                return;
            }
            reportWatch.Restart();

            progress.BytesDone = progressBase + done;
            reporter.Progress(progress);
        }

        private void FinishProgress()
        {
            if (progress == null)
            {
                return;
            }
            progress.FilesDone = progress.FilesTotal;
            progress.BytesDone = progress.BytesTotal;
            progress.CurrentFile = null;
            progress.IsForced = true;
            reporter.Progress(progress);
            Report(counters.Describe());
        }

        private void Report(string line)
        {
            planLines.Add(line);
            reporter.Log(line);
        }

        private void OnRetry(int attempt, Exception ex)
        {
            Report(string.Format("  第 {0} 次重试：{1}", attempt, ex.Message));
        }

        // ==================================================================
        //  小工具
        // ==================================================================

        private static string Fingerprint(List<ThemeFileEntry> files)
        {
            var sb = new StringBuilder();
            foreach (var f in files)
            {
                sb.Append(f.Path).Append('\0').Append(f.Sha1).Append('\n');
            }
            return Sha1Text(sb.ToString());
        }

        private static string Sha1File(string path)
        {
            using (var sha = SHA1.Create())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite, 64 * 1024))
            {
                return ToHex(sha.ComputeHash(stream));
            }
        }

        private static string Sha1Text(string text)
        {
            using (var sha = SHA1.Create())
            {
                return ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(text)));
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

        private static string Relative(string root, string path)
        {
            var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(path);
            if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("文件不在主题目录内：" + path);
            }
            return full.Substring(rootFull.Length);
        }

        /// <summary>把远端清单里的相对路径清洗成能安全拼到本地磁盘上的样子。</summary>
        private static string SafeRelative(string path)
        {
            var value = (path ?? string.Empty).Replace('\\', '/').Trim();
            while (value.StartsWith("/", StringComparison.Ordinal))
            {
                value = value.Substring(1);
            }
            if (value.Length == 0)
            {
                throw new InvalidDataException("远端清单里有空路径");
            }

            foreach (var segment in value.Split('/'))
            {
                if (segment.Length == 0 || segment == "." || segment == ".."
                    || segment.IndexOf(':') >= 0)
                {
                    throw new InvalidDataException("远端清单里的路径不安全，已拒绝：" + path);
                }
            }

            return value;
        }

        private static string CombineUnder(string root, string relative)
        {
            var full = Path.GetFullPath(Path.Combine(root,
                relative.Replace('/', Path.DirectorySeparatorChar)));
            var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("路径越出目标目录，已拒绝：" + relative);
            }
            return full;
        }

        /// <summary>404/410 都算「远端还没有这份东西」——不是错误，是空。</summary>
        internal static bool IsNotFound(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                var response = (e as WebException) == null ? null : ((WebException)e).Response as HttpWebResponse;
                if (response != null)
                {
                    return response.StatusCode == HttpStatusCode.NotFound
                        || response.StatusCode == HttpStatusCode.Gone;
                }
            }
            return false;
        }

        private static string RemoteDir(string mode, string id)
        {
            return RemoteRoot + "/" + mode + "/" + id;
        }

        private static string Key(PlanItem item)
        {
            return Key(item.Mode, item.Id);
        }

        private static string Key(string mode, string id)
        {
            return (mode ?? string.Empty) + "/" + (id ?? string.Empty);
        }

        private static string EntryKey(ThemeIndexEntry entry)
        {
            return Key(entry.Mode, entry.Id);
        }

        private static string NormalizeRoot(string root)
        {
            try
            {
                return Path.GetFullPath(root).TrimEnd('\\', '/');
            }
            catch
            {
                return (root ?? string.Empty).TrimEnd('\\', '/');
            }
        }

        private static ThemeIndexEntry ToIndexEntry(ThemeManifest manifest)
        {
            return new ThemeIndexEntry
            {
                Id = manifest.Id,
                Name = manifest.Name,
                Mode = manifest.Mode,
                Version = manifest.Version,
                Files = manifest.Files == null ? 0 : manifest.Files.Count,
                Bytes = manifest.Bytes,
                Fingerprint = manifest.Fingerprint,
                UpdatedAt = manifest.UpdatedAt
            };
        }

        private static long Sum(List<ThemeManifest> list)
        {
            long total = 0;
            foreach (var m in list)
            {
                total += m.Bytes;
            }
            return total;
        }
    }
}
