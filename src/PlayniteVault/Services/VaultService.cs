using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Playnite.SDK;
using PlayniteVault.Models;
using PlayniteVault.Net;
using PlayniteVault.Sync;

namespace PlayniteVault.Services
{
    /// <summary>
    /// 业务层：串起 WebDAV 客户端、远端索引、本地索引、设置与打包引擎。
    /// </summary>
    public class VaultService
    {
        /// <summary>
        /// 上传进度的「尾款」比例：每片最后这一成留到服务端确认（PUT 返回）之后再计入总进度。
        /// 不这么留的话，进度条会按 socket 写入量提前冲到 100%，然后干等服务端落盘。
        /// </summary>
        private const double UploadConfirmReserve = 0.10;

        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
            NullValueHandling = NullValueHandling.Ignore
        };

        private readonly IPlayniteAPI api;
        private readonly string dataPath;
        private VaultSettings settings;
        private VaultRuntimeState state;

        public VaultService(IPlayniteAPI api, string dataPath)
        {
            this.api = api;
            this.dataPath = dataPath;
            Directory.CreateDirectory(dataPath);
            LoadSettings();
            state = VaultStateStore.Load(dataPath);
        }

        public string DataPath
        {
            get { return dataPath; }
        }

        public VaultSettings Settings
        {
            get { return settings; }
        }

        /// <summary>
        /// 运行时状态（已应用的索引指纹、上次检查时间、镜像记录…）。
        /// 刻意与 settings 分开存：设置页是「克隆 → 改 → 整份落盘」的模型，
        /// 后台线程往里写状态会被用户点一次「保存」覆盖掉。
        /// </summary>
        public VaultRuntimeState State
        {
            get { return state; }
        }

        /// <summary>把 <see cref="State"/> 落到 state.json。</summary>
        public void SaveState()
        {
            VaultStateStore.Save(dataPath, state);
        }

        private string SettingsFile { get { return Path.Combine(dataPath, "settings.json"); } }
        private string CacheFile { get { return Path.Combine(dataPath, "cache-index.json"); } }
        private string LocalIndexFile { get { return Path.Combine(dataPath, "local-index.json"); } }

        // ---------- 设置 ----------

        public void LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsFile))
                {
                    settings = JsonConvert.DeserializeObject<VaultSettings>(
                        File.ReadAllText(SettingsFile, Encoding.UTF8));
                }
            }
            catch (Exception ex)
            {
                VaultLog.Error("读取设置失败，使用默认值", ex);
            }

            if (settings == null)
            {
                settings = new VaultSettings();
            }

            if (string.IsNullOrWhiteSpace(settings.LocalRoot))
            {
                settings.LocalRoot = VaultSettings.DefaultLocalRoot();
            }
        }

        public void SaveSettings(VaultSettings value)
        {
            settings = value;
            try
            {
                File.WriteAllText(SettingsFile,
                    JsonConvert.SerializeObject(settings, JsonSettings), new UTF8Encoding(false));
                VaultLog.Info("设置已保存，WebDAV=" + settings.WebDavUrl);
            }
            catch (Exception ex)
            {
                VaultLog.Error("保存设置失败", ex);
                throw;
            }
        }

        /// <summary>把设置翻译成一次传输要用的开关。</summary>
        public SyncOptions BuildSyncOptions(bool force = false)
        {
            return new SyncOptions
            {
                MaxRetries = Math.Max(1, settings.MaxRetries),
                ResumePartial = settings.ResumePartial,
                ForceTransfer = force,
                ChunkSize = Math.Max(1, settings.ChunkSizeMB) * 1024L * 1024L,
                Compress = settings.CompressOnArchive,
                PipelineExtract = settings.PipelineExtract,
                Concurrency = Math.Max(1, settings.Concurrency),
                UploadConcurrency = settings.UploadConcurrency,
                ConnectTimeoutMs = Math.Max(5, settings.TimeoutSeconds) * 1000,
                StallTimeoutMs = Math.Max(5, settings.StallTimeoutSeconds) * 1000,
                ResponseTimeoutMs = Math.Max(30, settings.ResponseTimeoutSeconds) * 1000
            };
        }

        // ---------- WebDAV ----------

        public WebDavClient CreateClient()
        {
            return CreateClient(TimeoutsFromSettings());
        }

        public WebDavTimeouts TimeoutsFromSettings()
        {
            return new WebDavTimeouts
            {
                ConnectMs = Math.Max(5, settings.TimeoutSeconds) * 1000,
                StallMs = Math.Max(5, settings.StallTimeoutSeconds) * 1000,
                ResponseMs = Math.Max(30, settings.ResponseTimeoutSeconds) * 1000
            };
        }

        public WebDavClient CreateClient(WebDavTimeouts timeouts)
        {
            if (!settings.IsConfigured)
            {
                throw new InvalidOperationException("尚未配置 WebDAV 地址，请先打开插件设置。");
            }
            return new WebDavClient(settings.WebDavUrl, settings.Username, settings.Password,
                timeouts, settings.UseSystemProxy);
        }

        /// <summary>按「单一超时秒数」建客户端（索引拉取、图片下载这类小请求用）。</summary>
        public WebDavClient CreateClient(int timeoutSeconds)
        {
            return CreateClient(WebDavTimeouts.FromSeconds(timeoutSeconds));
        }

        // ---------- 远端索引 ----------

        /// <summary>
        /// 三级降级取索引：远端 → 本地缓存 → 本地索引（仅已安装项）。
        /// NAS 不可达时绝不能返回空列表，否则 Playnite 会把整库条目判为失效。
        /// </summary>
        public RepositoryIndex GetIndex(out string source, out string error)
        {
            error = null;
            source = "remote";

            try
            {
                // 库更新是同步阻塞的，索引拉取必须快，拿不到就走缓存
                var client = CreateClient(Math.Min(settings.TimeoutSeconds, 10));
                var json = client.DownloadString("index.json");
                var index = JsonConvert.DeserializeObject<RepositoryIndex>(json);
                if (index == null)
                {
                    throw new InvalidOperationException("index.json 内容无法解析");
                }
                if (index.Apps == null)
                {
                    index.Apps = new List<AppEntry>();
                }

                SaveCachedIndex(index);
                VaultLog.Info("远端索引拉取成功，条目数=" + index.Apps.Count);
                return MergeWithLocal(index);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                VaultLog.Error("拉取远端索引失败", ex);
            }

            var cached = LoadCachedIndex();
            if (cached != null)
            {
                source = "cache";
                return MergeWithLocal(cached);
            }

            source = "local-only";
            var fallback = new RepositoryIndex { Name = "本地索引（NAS 不可达）" };
            foreach (var entry in GetLocalIndex().Apps)
            {
                fallback.Apps.Add(new AppEntry
                {
                    Id = entry.AppId,
                    Name = entry.AppId,
                    Version = entry.Version,
                    LaunchExe = entry.LaunchExe
                });
            }
            return fallback;
        }

        /// <summary>把本地已安装但远端索引缺失的条目补回来。</summary>
        private RepositoryIndex MergeWithLocal(RepositoryIndex remote)
        {
            var local = GetLocalIndex();
            foreach (var entry in local.Apps)
            {
                var exists = remote.Apps.Any(a =>
                    string.Equals(a.Id, entry.AppId, StringComparison.OrdinalIgnoreCase));
                if (!exists)
                {
                    remote.Apps.Add(new AppEntry
                    {
                        Id = entry.AppId,
                        Name = entry.AppId,
                        Version = entry.Version,
                        LaunchExe = entry.LaunchExe
                    });
                }
            }
            return remote;
        }

        private void SaveCachedIndex(RepositoryIndex index)
        {
            memoIndex = index;
            memoIndexAt = DateTime.UtcNow;
            try
            {
                File.WriteAllText(CacheFile,
                    JsonConvert.SerializeObject(index, JsonSettings), new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                VaultLog.Error("写缓存索引失败", ex);
            }
        }

        // 内存备忘录：库导入会对每个应用查一次 InstallDirName，
        // 每次都去读一遍 cache-index.json 就成了 O(n) 次文件读取。
        private RepositoryIndex memoIndex;
        private DateTime memoIndexAt = DateTime.MinValue;

        public RepositoryIndex LoadCachedIndex()
        {
            var fresh = (DateTime.UtcNow - memoIndexAt).TotalSeconds < 30;
            if (memoIndex != null && fresh)
            {
                return memoIndex;
            }

            try
            {
                if (!File.Exists(CacheFile))
                {
                    return null;
                }
                var index = JsonConvert.DeserializeObject<RepositoryIndex>(
                    File.ReadAllText(CacheFile, Encoding.UTF8));
                memoIndex = index;
                memoIndexAt = DateTime.UtcNow;
                return index;
            }
            catch (Exception ex)
            {
                VaultLog.Error("读缓存索引失败", ex);
                return null;
            }
        }

        public AppManifest FetchManifest(string appId)
        {
            var client = CreateClient();
            var json = client.DownloadString("apps/" + appId + "/manifest.json");
            var manifest = JsonConvert.DeserializeObject<AppManifest>(json);
            if (manifest == null)
            {
                throw new InvalidOperationException("manifest.json 内容无法解析：" + appId);
            }
            if (manifest.Files == null)
            {
                manifest.Files = new List<FileEntry>();
            }
            if (manifest.Parts == null)
            {
                manifest.Parts = new List<PartEntry>();
            }
            return manifest;
        }

        // ---------- 本地索引 ----------

        public LocalIndex GetLocalIndex()
        {
            try
            {
                if (File.Exists(LocalIndexFile))
                {
                    var index = JsonConvert.DeserializeObject<LocalIndex>(
                        File.ReadAllText(LocalIndexFile, Encoding.UTF8));
                    if (index != null)
                    {
                        if (index.Apps == null) index.Apps = new List<LocalEntry>();
                        return index;
                    }
                }
            }
            catch (Exception ex)
            {
                VaultLog.Error("读取本地索引失败", ex);
            }
            return new LocalIndex();
        }

        public void SaveLocalIndex(LocalIndex index)
        {
            File.WriteAllText(LocalIndexFile,
                JsonConvert.SerializeObject(index, JsonSettings), new UTF8Encoding(false));
        }

        // ---------- 路径 ----------

        /// <summary>
        /// 本地安装目录。**Id 与文件夹名是两件事**：
        /// 文件夹名优先用随包元数据里的 InstallDirName（归档前原始安装目录的最后一段），
        /// 这样「解出来放哪儿」和「上传前放哪儿」一致，也不会变成中文目录。
        /// 取不到才退回 Id。
        /// </summary>
        public string GetInstallDir(string appId, string installDirName)
        {
            return Path.Combine(settings.LocalRoot, FolderNameFor(appId, installDirName));
        }

        /// <summary>只给 Id 时：先从索引里找 InstallDirName，找不到再退回 Id。</summary>
        public string GetInstallDir(string appId)
        {
            return Path.Combine(settings.LocalRoot, FolderNameFor(appId, FindInstallDirName(appId)));
        }

        /// <summary>确定落盘的子目录名（做过路径清洗与 Windows 保留名过滤）。</summary>
        public static string FolderNameFor(string appId, string installDirName)
        {
            return SanitizeFolderName(installDirName)
                ?? SanitizeFolderName(appId)
                ?? ("app-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        }

        /// <summary>
        /// 从缓存索引（必要时退回远端清单）里取该应用的 InstallDirName。
        /// 缓存索引是本地文件，所以这是「零网络开销」的常规路径。
        /// </summary>
        public string FindInstallDirName(string appId)
        {
            try
            {
                var cached = LoadCachedIndex();
                if (cached != null && cached.Apps != null)
                {
                    var app = cached.Apps.FirstOrDefault(a =>
                        string.Equals(a.Id, appId, StringComparison.OrdinalIgnoreCase));
                    if (app != null && app.Metadata != null
                        && !string.IsNullOrWhiteSpace(app.Metadata.InstallDirName))
                    {
                        return app.Metadata.InstallDirName;
                    }
                }
            }
            catch (Exception ex)
            {
                VaultLog.Warn("从缓存索引取 InstallDirName 失败：" + ex.Message);
            }

            try
            {
                var manifest = FetchManifest(appId);
                return manifest.Metadata == null ? null : manifest.Metadata.InstallDirName;
            }
            catch (Exception ex)
            {
                VaultLog.Warn("从远端清单取 InstallDirName 失败：" + appId + "，" + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 本地子目录名的清洗。与 Python 端 <c>core.sanitize_folder_name()</c> 规则一致。
        ///
        /// 这个名字来自远端 JSON，属于**不可信输入**：直接拼路径会被
        /// `..\..\Windows` 这种名字穿越出去。拒绝分隔符、保留设备名和全是点的名字。
        /// </summary>
        public static string SanitizeFolderName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var s = name.Trim();

            // Windows 上结尾的点与空格会被静默吃掉，导致实际目录名和记录不一致
            s = s.TrimEnd('.', ' ');
            if (s.Length == 0 || s == "." || s == "..")
            {
                return null;
            }

            foreach (var ch in s)
            {
                if (ch < 32 || "<>:\"/\\|?*".IndexOf(ch) >= 0)
                {
                    return null;
                }
            }

            var head = s.Split('.')[0].ToUpperInvariant();
            if (ReservedDeviceNames.Contains(head))
            {
                return null;
            }

            if (s.Length > 128)
            {
                return null;
            }

            return s;
        }

        private static readonly HashSet<string> ReservedDeviceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };

        /// <summary>判断某个应用当前是否真的装在本地。</summary>
        public LocalEntry GetInstalledEntry(string appId)
        {
            var entry = GetLocalIndex().Find(appId);
            if (entry == null)
            {
                return null;
            }
            if (!Directory.Exists(entry.InstallDir))
            {
                return null;
            }
            return entry;
        }

        // ---------- 随包图片 ----------

        /// <summary>
        /// 把随包图片（封面 / 背景 / 图标）抓到本地缓存，返回本地路径。
        /// 库导入时 Playnite 需要本地文件，因此这里做一层缓存，第二次导入不再下载。
        /// </summary>
        public string EnsureLocalImage(string appId, string key, AppMetadata metadata)
        {
            if (metadata == null || metadata.Images == null)
            {
                return null;
            }

            string remotePath;
            if (!metadata.Images.TryGetValue(key, out remotePath) || string.IsNullOrWhiteSpace(remotePath))
            {
                return null;
            }

            var localDir = Path.Combine(dataPath, "meta-cache", appId);
            var localPath = Path.Combine(localDir,
                key + (Path.GetExtension(remotePath) ?? string.Empty));

            try
            {
                if (File.Exists(localPath) && new FileInfo(localPath).Length > 0)
                {
                    return localPath;
                }

                Directory.CreateDirectory(localDir);
                var client = CreateClient(Math.Min(settings.TimeoutSeconds, 30));
                var options = new SyncOptions { MaxRetries = 2, ResumePartial = false };
                client.DownloadFile("apps/" + appId + "/" + remotePath, localPath, null,
                    CancellationToken.None, options);

                return File.Exists(localPath) ? localPath : null;
            }
            catch (Exception ex)
            {
                VaultLog.Warn(string.Format("拉取随包图片失败：{0}/{1}，{2}", appId, key, ex.Message));
                return null;
            }
        }

        // ---------- 归档 ----------

        /// <summary>扫描本地目录生成清单（按路径排序，保证分片划分稳定）。</summary>
        /// <param name="playniteGameId">
        /// 【v1.8】来源那条 Playnite 库记录的 GUID。命令行打包（VaultPack）手里没有 Playnite
        /// 数据库，所以它是可选的 —— 只有插件侧的归档才填得上。
        /// </param>
        /// <param name="playniteLibraryId">
        /// 【v1.8】来源那条记录的 <c>Game.GameId</c>（库内标识：Steam 就是 appid）。
        /// 同样只有插件侧填得上。
        /// </param>
        public static AppManifest BuildManifest(string appId, string appName, string localDir,
            string launchExe, string version, AppMetadata metadata,
            string playniteGameId = null, string playniteLibraryId = null)
        {
            var manifest = new AppManifest
            {
                Id = appId,
                Name = appName,
                Version = version,
                UpdatedAt = DateTime.UtcNow,
                LaunchExe = launchExe,
                Metadata = metadata,
                PlayniteGameId = playniteGameId,
                PlayniteLibraryId = playniteLibraryId
            };

            if (metadata != null)
            {
                if (string.IsNullOrWhiteSpace(metadata.Version))
                {
                    metadata.Version = version;
                }
                manifest.Arguments = metadata.LaunchArguments;
                manifest.WorkingDir = metadata.LaunchWorkingDir;
            }

            foreach (var file in Directory.GetFiles(localDir, "*", SearchOption.AllDirectories))
            {
                var info = new FileInfo(file);
                manifest.Files.Add(new FileEntry
                {
                    Path = MakeRelative(localDir, file).Replace('\\', '/'),
                    Size = info.Length
                });
                manifest.TotalBytes += info.Length;
            }

            manifest.Files = manifest.Files
                .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return manifest;
        }

        /// <summary>
        /// 归档到 WebDAV：切区块 → 逐块上传 → 写清单与索引。
        ///
        /// 编排是「打好一块 → 传一块 → 删一块」，所以超大游戏的本地临时占用
        /// 始终只有「在途区块」那么多，而不是整个游戏。
        ///
        /// 与旧版的三个关键差别：
        ///   1. **一个文件可以跨多个区块**（旧版让 1.93GB 的 res.pak 独占一个分片，
        ///      单个 PUT 必然超时）；
        ///   2. 区块按**内容哈希**命名，所以可以先用 HEAD 问「远端有没有这块」，
        ///      失败重传时已完成的块全部跳过，不会再从第 0 块重来；
        ///   3. 每块都有**独立的失败重试与退避**，一块卡住不再拖垮整个归档。
        /// </summary>
        public SyncResult ArchiveApp(string appId, string appName, string localDir, string launchExe,
            string version, AppMetadata metadata, SyncOptions options,
            Action<SyncProgress> onProgress, CancellationToken cancelToken,
            string playniteGameId = null, string playniteLibraryId = null)
        {
            var opt = options ?? BuildSyncOptions();
            var client = CreateClient(TimeoutsFrom(opt));
            var result = new SyncResult();
            var watch = Stopwatch.StartNew();
            var reporter = new ThrottledReporter(onProgress);

            var manifest = BuildManifest(appId, appName, localDir, launchExe, version, metadata,
                playniteGameId, playniteLibraryId);

            AppManifest knownRemote = null;
            try
            {
                knownRemote = FetchManifest(appId);
            }
            catch (Exception ex)
            {
                VaultLog.Info("远端尚无该应用的清单，将全量归档：" + appId + "（" + ex.Message + "）");
            }

            PackEngine.PlanChunks(manifest, opt.EffectiveChunkSize);

            var progress = new SyncProgress
            {
                Phase = "归档",
                FilesTotal = manifest.Files.Count,
                BytesTotal = manifest.TotalBytes,
                PartsTotal = manifest.Chunks.Count,
                SubStageName = "打包"
            };

            VaultLog.Info(string.Format(
                "归档开始：{0}，{1} 个文件 / {2}，切成 {3} 个区块（每块 {4}，{5}）",
                appId, manifest.Files.Count, SyncProgress.FormatSize(manifest.TotalBytes),
                manifest.Chunks.Count, SyncProgress.FormatSize(opt.EffectiveChunkSize),
                opt.Compress ? "Deflate 压缩" : "不压缩"));

            var engine = new SyncEngine(client);
            var tempDir = Path.Combine(dataPath, "pack-temp", appId);

            // 每块对总进度的「原始字节」贡献。用数组而不是累加变量，
            // 因为多路上传时区块完成顺序是乱的，累加会算错。
            var chunkRaw = new long[manifest.Chunks.Count];

            var uploaders = Math.Max(1, Math.Min(opt.EffectiveUploadConcurrency, manifest.Chunks.Count));

            VaultLog.Info(string.Format("归档并发：{0} 路上传（共 {1} 个区块）",
                uploaders, manifest.Chunks.Count));

            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancelToken);
            Exception firstError = null;
            var errorLock = new object();

            var skippedChunks = 0;
            var uploadedChunks = 0;
            var retries = 0;
            long uploadedBytes = 0;
            var doneChunks = 0;
            var inFlight = 0;

            // 任一线程出错就取消整条流水线，并把第一个异常留给主线程抛出。
            Action<Exception> fail = ex =>
            {
                lock (errorLock)
                {
                    if (firstError == null)
                    {
                        firstError = ex;
                    }
                }
                try
                {
                    cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // 已经收尾，忽略
                }
            };

            try
            {
                using (var heartbeat = new ProgressHeartbeat(reporter, progress))
                using (var queue = new BlockingCollection<ChunkWork>(uploaders + 1))
                {
                    // 打包线程：按序把每块打成临时文件后入队。
                    var packer = new Thread(() =>
                    {
                        try
                        {
                            foreach (var chunk in manifest.Chunks)
                            {
                                cts.Token.ThrowIfCancellationRequested();

                                var localChunk = SyncEngine.ChunkLocalPath(tempDir, chunk);
                                var slot = chunk.Index;

                                // 打包只推进子阶段。**绝不能**用打包进度去推进总进度条：
                                // 打包是纯本地磁盘操作，而上传要慢一个量级，
                                // 一旦让打包去推主进度，进度条会先冲到 100% 然后干等，
                                // 看起来就像「卡死了」。
                                progress.SubStageBytesDone = 0;
                                progress.SubStageBytesTotal = chunk.RawBytes;
                                reporter.Report(progress, true);

                                try
                                {
                                    PackEngine.WriteChunk(manifest, chunk, localDir, localChunk,
                                        opt.Compress,
                                        (inChunk, total) =>
                                        {
                                            progress.SubStageBytesDone = inChunk;
                                            reporter.Report(progress, false);
                                        },
                                        cts.Token);
                                }
                                catch
                                {
                                    TryDeleteFile(localChunk);
                                    throw;
                                }

                                progress.SubStageBytesDone = chunk.RawBytes;

                                // 内容已定，哈希已知 → 直接问远端有没有这块。
                                // 这是「失败重传不从头开始」的全部依据。
                                if (!opt.ForceTransfer && engine.ChunkOnRemote(appId, chunk))
                                {
                                    TryDeleteFile(localChunk);
                                    Advance(chunkRaw, slot, chunk.RawBytes);
                                    Interlocked.Increment(ref skippedChunks);
                                    progress.PartsDone = Interlocked.Increment(ref doneChunks);
                                    progress.BytesDone = SyncEngine.SumBytes(chunkRaw);
                                    reporter.Report(progress, true);
                                    VaultLog.Info("远端已有相同区块，跳过：" + chunk.Path);
                                    continue;
                                }

                                queue.Add(new ChunkWork { Chunk = chunk, LocalPath = localChunk },
                                    cts.Token);
                            }
                        }
                        catch (Exception ex)
                        {
                            fail(ex);
                        }
                        finally
                        {
                            // 子阶段信息不在这里清掉的话，后面整段上传都会顶着
                            // 一行陈旧的「打包 100%」，看起来像还有活没干。
                            progress.SubStageName = null;
                            progress.SubStageBytesDone = 0;
                            progress.SubStageBytesTotal = 0;
                            queue.CompleteAdding();
                        }
                    })
                    {
                        IsBackground = true,
                        Name = "vault-pack"
                    };

                    packer.Start();

                    // 上传线程：从队列取块上传，传完立刻删临时文件，占用不累积。
                    var uploadThreads = new List<Thread>();
                    for (var w = 0; w < uploaders; w++)
                    {
                        var worker = new Thread(() =>
                        {
                            try
                            {
                                foreach (var work in queue.GetConsumingEnumerable(cts.Token))
                                {
                                    var chunk = work.Chunk;
                                    var slot = chunk.Index;

                                    // CurrentFile 只在这里写一次（开始传这块时），
                                    // 放进字节回调会被多条上传线程轮流覆盖
                                    progress.CurrentFile = chunk.Path;
                                    progress.PartsInFlight = Interlocked.Increment(ref inFlight);
                                    reporter.Report(progress, true);

                                    try
                                    {
                                        // 「已写进 socket」不等于「服务端已落盘」。
                                        // 每块留最后一成「尾款」，等服务端确认才补上，
                                        // 这样等待就落在 90%→100% 的爬升里而不是钉在 100%。
                                        var streamCap = (long)(chunk.RawBytes * (1.0 - UploadConfirmReserve));

                                        engine.UploadChunk(appId, chunk, work.LocalPath,
                                            Math.Max(1, opt.MaxRetries),
                                            (written, total) =>
                                            {
                                                var streamed = total <= 0
                                                    ? streamCap
                                                    : (long)(chunk.RawBytes * (double)written / total);
                                                if (streamed > streamCap)
                                                {
                                                    streamed = streamCap;
                                                }
                                                Advance(chunkRaw, slot, streamed);
                                                progress.BytesDone = SyncEngine.SumBytes(chunkRaw);
                                                reporter.Report(progress, false);
                                            },
                                            cts.Token,
                                            (attempt, ex) => Interlocked.Increment(ref retries));
                                    }
                                    finally
                                    {
                                        // 无论成功失败都删掉临时块，避免残留
                                        TryDeleteFile(work.LocalPath);
                                        progress.PartsInFlight = Interlocked.Decrement(ref inFlight);
                                    }

                                    Advance(chunkRaw, slot, chunk.RawBytes);
                                    Interlocked.Increment(ref uploadedChunks);
                                    Interlocked.Add(ref uploadedBytes, chunk.StoredBytes);
                                    progress.PartsDone = Interlocked.Increment(ref doneChunks);
                                    progress.BytesDone = SyncEngine.SumBytes(chunkRaw);
                                    reporter.Report(progress, true);
                                }
                            }
                            catch (Exception ex)
                            {
                                fail(ex);
                            }
                        })
                        {
                            IsBackground = true,
                            Name = "vault-upload-" + w
                        };

                        uploadThreads.Add(worker);
                        worker.Start();
                    }

                    packer.Join();

                    foreach (var worker in uploadThreads)
                    {
                        worker.Join();
                    }
                }
            }
            finally
            {
                TryDeleteDirectory(tempDir);
            }

            if (firstError != null)
            {
                throw firstError;
            }

            result.Skipped = 0;
            result.PartsTransferred = uploadedChunks;
            result.PartsSkipped = skippedChunks;
            result.Retries = retries;
            result.BytesTransferred = uploadedBytes;
            result.Transferred = manifest.Files.Count;
            result.Elapsed = watch.Elapsed;

            // 封面 / 背景 / 图标：上传到 apps/{id}/meta/ 并把路径改写成远端相对路径，
            // 这样下次从别的机器安装时，库导入阶段不用再刮削就能拿到图。
            progress.Phase = "元数据";
            progress.CurrentFile = "随包图片";
            progress.SubStageName = null;
            progress.SubStageBytesDone = 0;
            progress.SubStageBytesTotal = 0;
            progress.PartsInFlight = 0;
            reporter.Report(progress, true);
            result.ImagesUploaded = UploadMetadataImages(client, appId, metadata, cancelToken);

            // 写清单与索引（两个小文件，直接覆盖）
            client.UploadString(JsonConvert.SerializeObject(manifest, JsonSettings),
                "apps/" + appId + "/manifest.json");

            UpdateRemoteIndex(client, new AppEntry
            {
                Id = appId,
                Name = appName,
                Version = version,
                LaunchExe = launchExe,
                FileCount = manifest.Files.Count,
                TotalBytes = manifest.TotalBytes,
                PartCount = 0,
                ChunkCount = manifest.Chunks.Count,
                Packed = manifest.Packed,
                Metadata = metadata,

                // 卡片墙要用它来判「这条库里记录仓库里到底有没有」，
                // 而卡片墙只读 index.json —— 所以这两个键必须进索引，不能只进 manifest。
                PlayniteGameId = playniteGameId,
                PlayniteLibraryId = playniteLibraryId
            });

            // 旧格式升级上来之后把旧目录清掉，并把过期区块回收掉，避免仓库里留垃圾
            CleanUpOldLayouts(client, appId, knownRemote, manifest);

            progress.CurrentFile = "完成";
            reporter.Report(progress, true);

            VaultLog.Info(string.Format("归档结束：{0}，{1}，存储 {2}",
                appId, result.Describe(),
                SyncProgress.FormatSize(manifest.Chunks.Sum(p => p.StoredBytes))));

            return result;
        }

        /// <summary>
        /// 归档收尾：把仓库里**这次不再引用**的旧数据清掉。
        ///
        /// 三类垃圾：
        ///   1. v2 的 <c>parts/</c> —— 整目录删；
        ///   2. v1 的 <c>files/</c> —— 整目录删；
        ///   3. v3 里内容已经不在新清单里的区块 —— 区块不可变且内容寻址
        ///      （文件名就是内容哈希），所以换过区块大小、或源目录里删改过文件之后，
        ///      旧区块就变成没人引用的垃圾。不清的话「游戏更新一次就重新归档一次」
        ///      会让仓库越滚越大。
        ///
        /// **必须在写新清单之后调用**：新清单只引用该留下的那些区块，
        /// 所以从新清单落盘那一刻起，其余的都是纯垃圾，删它不影响任何人。
        /// </summary>
        private void CleanUpOldLayouts(WebDavClient client, string appId,
            AppManifest knownRemote, AppManifest manifest)
        {
            if (knownRemote != null)
            {
                if (knownRemote.Layout == VaultLayout.Parts)
                {
                    DeleteRemoteTree(client, "apps/" + appId + "/parts/");
                }
                else if (knownRemote.Layout == VaultLayout.Files && knownRemote.Files != null
                         && knownRemote.Files.Count > 0)
                {
                    CleanUpLegacyLayout(client, appId, knownRemote);
                }
            }

            // 这一步不看 knownRemote：上一次归档如果半路崩了，也可能留下没引用的区块
            PurgeStaleChunks(client, appId, manifest);
        }

        /// <summary>
        /// 删掉 <c>apps/{id}/chunks/</c> 里不被当前清单引用的区块文件。
        ///
        /// 之所以能这么干：区块是内容寻址 + 不可变的，**同一个应用目录内**
        /// 不存在「两个区块内容相同却是两个文件」的情况，也就不存在共享引用，
        /// 不需要引用计数。区块只放自己应用的目录里，所以也不会误删别人的。
        /// </summary>
        private static void PurgeStaleChunks(WebDavClient client, string appId, AppManifest manifest)
        {
            if (manifest == null || manifest.Layout != VaultLayout.Chunks)
            {
                return;
            }

            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var chunk in manifest.Chunks)
            {
                if (!string.IsNullOrWhiteSpace(chunk.Id))
                {
                    keep.Add(chunk.Id + ".bin");
                }
            }

            List<WebDavEntry> entries;
            try
            {
                entries = client.List("apps/" + appId + "/chunks");
            }
            catch (Exception ex)
            {
                // 列举失败不算归档失败：这次没清掉，下次归档还会再清一次
                VaultLog.Warn("列举区块目录失败：" + appId + "，" + ex.Message);
                return;
            }

            var removed = 0;
            foreach (var entry in entries)
            {
                if (entry.IsCollection || keep.Contains(entry.Name))
                {
                    continue;
                }

                try
                {
                    client.Delete("apps/" + appId + "/chunks/" + entry.Name);
                    removed++;
                }
                catch (Exception ex)
                {
                    VaultLog.Warn("删除过期区块失败：" + entry.Name + "，" + ex.Message);
                }
            }

            if (removed > 0)
            {
                VaultLog.Info(string.Format("清理过期区块 {0} 个（{1}）", removed, appId));
            }
        }

        /// <summary>递归删掉远端一棵子树（best-effort，不抛异常）。</summary>
        public static int DeleteRemoteTree(WebDavClient client, string relativeDir)
        {
            var prefix = relativeDir.EndsWith("/", StringComparison.Ordinal) ? relativeDir : relativeDir + "/";
            var files = new List<string>();
            var dirs = new List<string> { prefix };

            try
            {
                CollectTree(client, prefix, files, dirs);
            }
            catch (Exception ex)
            {
                VaultLog.Warn("列举待删目录失败：" + prefix + "，" + ex.Message);
                return 0;
            }

            var removed = 0;
            foreach (var file in files)
            {
                try
                {
                    client.Delete(file);
                    removed++;
                }
                catch (Exception ex)
                {
                    VaultLog.Warn("删除远端文件失败：" + file + "，" + ex.Message);
                }
            }

            foreach (var dir in dirs.OrderByDescending(d => d.Length))
            {
                try
                {
                    client.Delete(dir);
                }
                catch
                {
                    // 目录非空或服务器不允许删集合，忽略
                }
            }

            return removed;
        }

        private static WebDavTimeouts TimeoutsFrom(SyncOptions opt)
        {
            return new WebDavTimeouts
            {
                ConnectMs = opt.ConnectTimeoutMs > 0 ? opt.ConnectTimeoutMs : 15000,
                StallMs = opt.StallTimeoutMs > 0 ? opt.StallTimeoutMs : 30000,
                ResponseMs = opt.ResponseTimeoutMs > 0 ? opt.ResponseTimeoutMs : 180000
            };
        }

        /// <summary>打包线程交给上传线程的一个待传区块。</summary>
        private class ChunkWork
        {
            public ChunkEntry Chunk { get; set; }

            /// <summary>本地临时区块文件的完整路径。</summary>
            public string LocalPath { get; set; }
        }

        /// <summary>
        /// 把元数据里的本地图片上传到 apps/{id}/meta/，并把 Images 的值就地改写成
        /// 远端相对路径（如 meta/cover.png）。返回实际上传张数。
        ///
        /// 值本身已经是相对路径（重打包场景）或文件不存在时跳过/剔除，不让归档整体失败。
        /// </summary>
        private int UploadMetadataImages(WebDavClient client, string appId, AppMetadata metadata,
            CancellationToken cancelToken)
        {
            if (metadata == null || metadata.Images == null || metadata.Images.Count == 0)
            {
                return 0;
            }

            var uploaded = 0;
            var remoteDir = "apps/" + appId + "/meta";
            var prepared = false;

            foreach (var key in metadata.Images.Keys.ToList())
            {
                cancelToken.ThrowIfCancellationRequested();

                var value = metadata.Images[key];
                if (string.IsNullOrWhiteSpace(value))
                {
                    metadata.Images.Remove(key);
                    continue;
                }

                // 已经是远端相对路径（没有盘符、也不是已存在的本地文件）→ 原样保留
                var looksLocal = value.Length > 1 && value[1] == ':';
                if (!looksLocal && !File.Exists(value))
                {
                    continue;
                }

                if (!File.Exists(value))
                {
                    VaultLog.Warn(string.Format("随包图片不存在，已剔除：{0} → {1}", key, value));
                    metadata.Images.Remove(key);
                    continue;
                }

                try
                {
                    if (!prepared)
                    {
                        client.EnsureDirectoryRecursive(remoteDir);
                        prepared = true;
                    }

                    var ext = Path.GetExtension(value);
                    if (string.IsNullOrEmpty(ext))
                    {
                        ext = ".png";
                    }

                    var remoteRelative = "meta/" + key + ext;
                    client.UploadFile(value, "apps/" + appId + "/" + remoteRelative, null, cancelToken);

                    metadata.Images[key] = remoteRelative;
                    uploaded++;
                    VaultLog.Info(string.Format("随包图片已上传：{0} → {1}", key, remoteRelative));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // 图片是锦上添花，传不上去不该让整个归档前功尽弃
                    VaultLog.Warn(string.Format("随包图片上传失败（忽略）：{0}，{1}", key, ex.Message));
                    metadata.Images.Remove(key);
                }
            }

            return uploaded;
        }

        /// <summary>
        /// 从远端仓库彻底删除一个应用：递归删掉 apps/{id}/ 下的所有文件与目录
        /// （包含 apps/{id}/ 本身），再从 index.json 摘掉对应条目，
        /// 并清掉本地安装记录（否则 ls 会把本地记录当成幽灵条目补回来）。
        /// 本地已下载的游戏文件不动。返回实际删掉的文件数。
        /// </summary>
        public int RemoveApp(string appId, Action<string> onLog = null)
        {
            var client = CreateClient();
            var prefix = "apps/" + appId + "/";

            var files = new List<string>();
            var dirs = new List<string> { prefix };
            CollectTree(client, prefix, files, dirs);

            var removed = 0;
            foreach (var file in files)
            {
                try
                {
                    client.Delete(file);
                    removed++;
                    if (onLog != null)
                    {
                        onLog(file);
                    }
                }
                catch (Exception ex)
                {
                    VaultLog.Warn("删除远端文件失败：" + file + "，" + ex.Message);
                }
            }

            // 目录由深到浅删，父目录在子目录清空后才删得掉
            foreach (var dir in dirs.OrderByDescending(d => d.Length))
            {
                try
                {
                    client.Delete(dir);
                }
                catch
                {
                    // 目录非空或服务器不允许删集合，忽略
                }
            }

            RemoveFromRemoteIndex(client, appId);

            var localIndex = GetLocalIndex();
            localIndex.Remove(appId);
            SaveLocalIndex(localIndex);

            VaultLog.Info(string.Format("已从仓库删除应用：{0}，共 {1} 个文件", appId, removed));
            return removed;
        }

        // ---------- 仓库管理口令（破坏性操作的闸门） ----------

        /// <summary>
        /// 读仓库里的管理口令记录。**一律以远端为准** —— settings.json 里那份只是本地缓存，
        /// 拿它当准入依据的话，改一下本地文件就能绕过闸门。读到手顺手刷新缓存。
        /// 返回 null 表示还没设置过。
        /// </summary>
        public VaultAdmin FetchAdmin()
        {
            var admin = VaultAdmin.Load(CreateClient());
            CacheAdmin(admin);
            return admin;
        }

        /// <summary>设置 / 修改管理口令：写回仓库根目录，并同步本地缓存。</summary>
        public void SetAdminPassword(string password)
        {
            var admin = VaultAdmin.Create(password);
            VaultAdmin.Save(CreateClient(), admin);
            CacheAdmin(admin);
            VaultLog.Info("仓库管理口令已更新（" + VaultAdmin.FileName + "）");
        }

        /// <summary>
        /// 校验管理口令。失败时用 <paramref name="isSet"/> 区分两种情况：
        /// false = 口令错，需要重试；true 里再返回 false 表示口令错，
        /// 而 isSet=false 表示「压根没设置」—— 提示语完全不同（要引导去设置）。
        /// </summary>
        public bool VerifyAdminPassword(string password, out bool isSet)
        {
            WebDavClient client;
            VaultAdmin admin;
            try
            {
                client = CreateClient();
                admin = VaultAdmin.Load(client);
            }
            catch (Exception ex)
            {
                // 网络不通时不能当作「口令正确」，也不能当作「没设置」：
                // 只能报连不上，让调用方提示用户。
                VaultLog.Warn("校验管理口令时读取失败：" + ex.Message);
                isSet = true;
                return false;
            }

            isSet = admin != null && admin.IsSet;
            if (!isSet)
            {
                return false;
            }

            var ok = admin.Verify(password);
            if (ok)
            {
                CacheAdmin(admin);
            }

            return ok;
        }

        /// <summary>把远端记录镜像进本地设置（只为界面显示「已设置」，不做准入判断）。</summary>
        private void CacheAdmin(VaultAdmin admin)
        {
            var hash = admin == null ? string.Empty : (admin.Hash ?? string.Empty);
            var salt = admin == null ? string.Empty : (admin.Salt ?? string.Empty);
            var iterations = admin == null ? 0 : admin.Iterations;

            if (hash == settings.AdminHash && salt == settings.AdminSalt
                && iterations == settings.AdminIterations)
            {
                return;
            }

            var copy = settings.Clone();
            copy.AdminHash = hash;
            copy.AdminSalt = salt;
            copy.AdminIterations = iterations;
            SaveSettings(copy);
        }

        /// <summary>本地缓存看起来「设置过口令」。仅供界面措辞，不能当准入依据。</summary>
        public bool AdminProbablySet
        {
            get { return !string.IsNullOrWhiteSpace(settings.AdminHash); }
        }

        /// <summary>递归收集目录下的全部文件与子目录（相对仓库根的路径）。</summary>
        private static void CollectTree(WebDavClient client, string relative, List<string> files, List<string> dirs)
        {
            List<WebDavEntry> entries;
            try
            {
                entries = client.List(relative);
            }
            catch (Exception ex)
            {
                VaultLog.Warn("列举远端目录失败：" + relative + "，" + ex.Message);
                return;
            }

            var basePath = relative.TrimEnd('/') + "/";

            foreach (var entry in entries)
            {
                var child = basePath + entry.Name;

                if (entry.IsCollection)
                {
                    var childDir = child + "/";
                    dirs.Add(childDir);
                    CollectTree(client, childDir, files, dirs);
                }
                else
                {
                    files.Add(child);
                }
            }
        }

        /// <summary>把某个应用从远端 index.json 里摘掉（不动磁盘上的 apps/{id}/）。</summary>
        private static void RemoveFromRemoteIndex(WebDavClient client, string appId)
        {
            RepositoryIndex index;
            try
            {
                index = JsonConvert.DeserializeObject<RepositoryIndex>(client.DownloadString("index.json"));
            }
            catch (Exception ex)
            {
                VaultLog.Warn("读取远端 index.json 失败，跳过索引清理：" + ex.Message);
                return;
            }

            if (index == null || index.Apps == null)
            {
                return;
            }

            var removed = index.Apps.RemoveAll(a =>
                string.Equals(a.Id, appId, StringComparison.OrdinalIgnoreCase));

            if (removed == 0)
            {
                return;
            }

            index.UpdatedAt = DateTime.UtcNow;
            client.UploadString(JsonConvert.SerializeObject(index, JsonSettings), "index.json");
        }

        private void UpdateRemoteIndex(WebDavClient client, AppEntry entry)
        {
            RepositoryIndex index = null;
            try
            {
                index = JsonConvert.DeserializeObject<RepositoryIndex>(client.DownloadString("index.json"));
            }
            catch (Exception ex)
            {
                VaultLog.Warn("读取远端 index.json 失败，将新建：" + ex.Message);
            }

            if (index == null)
            {
                index = new RepositoryIndex { Name = "Vault" };
            }
            if (index.Apps == null)
            {
                index.Apps = new List<AppEntry>();
            }

            index.Apps.RemoveAll(a => string.Equals(a.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
            index.Apps.Add(entry);
            index.UpdatedAt = DateTime.UtcNow;
            index.Schema = VaultSchema.Current;

            client.UploadString(JsonConvert.SerializeObject(index, JsonSettings), "index.json");
        }

        /// <summary>把 v1 的 apps/{id}/files/ 逐个删掉（best-effort）。</summary>
        private void CleanUpLegacyLayout(WebDavClient client, string appId, AppManifest legacy)
        {
            var prefix = "apps/" + appId + "/files/";
            var removed = 0;

            VaultLog.Info(string.Format("检测到旧的逐文件布局，正在清理 {0} 个文件...", legacy.Files.Count));

            foreach (var file in legacy.Files)
            {
                try
                {
                    client.Delete(prefix + file.Path);
                    removed++;
                }
                catch (Exception ex)
                {
                    VaultLog.Warn("删除旧文件失败：" + file.Path + "，" + ex.Message);
                }
            }

            // 目录由深到浅删
            var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in legacy.Files)
            {
                var dir = Path.GetDirectoryName(file.Path.Replace('\\', '/'));
                while (!string.IsNullOrEmpty(dir))
                {
                    dirs.Add(dir);
                    dir = Path.GetDirectoryName(dir.Replace('\\', '/'));
                }
            }

            foreach (var dir in dirs.OrderByDescending(d => d.Length))
            {
                try
                {
                    client.Delete(prefix + dir + "/");
                }
                catch
                {
                    // 目录非空或服务器不允许删集合，忽略
                }
            }

            try
            {
                client.Delete(prefix);
            }
            catch
            {
                // 忽略
            }

            VaultLog.Info(string.Format("旧布局清理完成：删除 {0}/{1} 个文件", removed, legacy.Files.Count));
        }

        // ---------- 安装 / 卸载 ----------

        /// <summary>
        /// 从远端安装（或修复）到本地。
        /// v3 清单走「区块随机访问落盘」，v2 走「边下边解」，v1 走逐文件下载。
        /// 三种布局由 <see cref="AppManifest.Layout"/> 按字段推断，不看 Schema 数值。
        /// </summary>
        public SyncResult InstallApp(string appId, string targetDir, SyncOptions options,
            Action<SyncProgress> onProgress, CancellationToken cancelToken)
        {
            var manifest = FetchManifest(appId);
            var opt = options ?? BuildSyncOptions();
            var client = CreateClient(TimeoutsFrom(opt));
            var engine = new SyncEngine(client);

            SyncResult result;

            switch (manifest.Layout)
            {
                case VaultLayout.Chunks:
                {
                    // v3：区块可以乱序、并行取；取一块就写一块、删一块
                    var tempDir = Path.Combine(dataPath, "chunk-temp", appId);
                    result = engine.DownloadChunked(appId, manifest, targetDir, tempDir, dataPath,
                        opt, onProgress, cancelToken);
                    break;
                }

                case VaultLayout.Parts:
                {
                    var tempDir = Path.Combine(dataPath, "part-temp", appId);
                    result = engine.DownloadPacked(appId, manifest, targetDir, tempDir, opt,
                        onProgress, cancelToken);
                    break;
                }

                default:
                {
                    if (opt.RequiredFreeBytes <= 0)
                    {
                        // 预留 10% 余量，另加 64MB 给临时文件与日志
                        opt.RequiredFreeBytes = (long)(manifest.TotalBytes * 1.1) + 64L * 1024 * 1024;
                    }
                    result = engine.Download(appId, manifest, targetDir, opt, onProgress, cancelToken);
                    break;
                }
            }

            var localIndex = GetLocalIndex();
            localIndex.Upsert(new LocalEntry
            {
                AppId = manifest.Id,
                InstallDir = targetDir,
                Version = manifest.Version,
                LaunchExe = manifest.LaunchExe,
                InstalledAt = DateTime.UtcNow
            });
            SaveLocalIndex(localIndex);

            return result;
        }

        /// <summary>把应用从本地移除（只删文件与索引，NAS 归档不动）。</summary>
        public void UninstallApp(string appId, string targetDir)
        {
            if (!string.IsNullOrEmpty(targetDir) && Directory.Exists(targetDir))
            {
                Directory.Delete(targetDir, true);
            }

            var localIndex = GetLocalIndex();
            localIndex.Remove(appId);
            SaveLocalIndex(localIndex);
        }

        // ---------- 路径工具 ----------

        public static string MakeRelative(string baseDir, string fullPath)
        {
            var baseUri = new Uri(AppendSlash(Path.GetFullPath(baseDir)));
            var fileUri = new Uri(Path.GetFullPath(fullPath));
            return Uri.UnescapeDataString(baseUri.MakeRelativeUri(fileUri).ToString());
        }

        private static string AppendSlash(string path)
        {
            return path.EndsWith("\\", StringComparison.Ordinal)
                || path.EndsWith("/", StringComparison.Ordinal)
                ? path
                : path + Path.DirectorySeparatorChar;
        }

        /// <summary>
        /// 把名字压成仓库用的 ASCII slug（`[a-z0-9-]`，最长 48 字符）。
        ///
        /// **中文一律不保留**：旧实现用 `char.IsLetterOrDigit`，而它对 CJK 返回 true，
        /// 于是 `植物大战僵尸融合版` 原样变成 appId，仓库里就出现了中文目录。
        /// slug 是给 URL 与跨平台文件名用的，非 ASCII 只会带来编码问题。
        /// 结果为空（名字全是符号）时退回 `app-&lt;8 位随机&gt;`。
        /// </summary>
        public static string MakeSlug(string name)
        {
            return SlugCore(name) ?? RandomSlug();
        }

        /// <summary>
        /// 归档时该用哪个 Id：**优先由「上传前安装目录的最后一段」推出来**，
        /// 取不到安装目录才退回应用展示名。
        ///
        /// 这样 `D:\Game\Lib\Plants Vs Zombies RH` 对应的 Id 是
        /// `plants-vs-zombies-rh`，而不是中文名压出来的东西。
        ///
        /// 两处都压不出 ASCII（例如目录名与显示名全中文）时，用名字的 SHA-1 前 8 位
        /// 生成 `app-xxxxxxxx`。**这里必须是确定性的** —— 早先版本退回随机串，
        /// 结果每次重新归档都会生成新 Id，仓库里攒下一串重复条目。
        /// </summary>
        public static string MakeAppId(string installDirName, string displayName)
        {
            return SlugCore(installDirName)
                ?? SlugCore(displayName)
                ?? StableSlug(installDirName)
                ?? StableSlug(displayName)
                ?? "app-unknown";
        }

        /// <summary>非 ASCII 名称的确定性兜底 Id（同一名字永远得到同一个 Id）。</summary>
        private static string StableSlug(string seed)
        {
            if (string.IsNullOrWhiteSpace(seed))
            {
                return null;
            }

            using (var sha = System.Security.Cryptography.SHA1.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(seed.Trim()));
                var sb = new StringBuilder("app-");
                for (var i = 0; i < 4; i++)
                {
                    sb.Append(bytes[i].ToString("x2"));
                }

                return sb.ToString();
            }
        }

        /// <summary>
        /// 判断一个 Id 是不是纯 ASCII slug（`[a-z0-9-]`）。
        /// 用来识别历史遗留的中文 Id：只有这类 Id 才需要迁移成新的 ASCII Id。
        /// </summary>
        public static bool IsAsciiSlug(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return false;
            }

            foreach (var ch in id)
            {
                var ok = (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '-';
                if (!ok)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>压不出任何 ASCII 字符时返回 null，交给调用方决定兜底。</summary>
        private static string SlugCore(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var sb = new StringBuilder();
            foreach (var ch in name.Trim().ToLowerInvariant())
            {
                if ((ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9'))
                {
                    sb.Append(ch);
                }
                else if (sb.Length > 0 && sb[sb.Length - 1] != '-')
                {
                    sb.Append('-');
                }
            }

            var slug = sb.ToString().Trim('-');
            if (slug.Length == 0)
            {
                return null;
            }
            return slug.Length > 48 ? slug.Substring(0, 48).Trim('-') : slug;
        }

        private static string RandomSlug()
        {
            return "app-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        // ---------- 启动程序推断 ----------

        /// <summary>
        /// 已知的非游戏可执行文件关键字（Unity 崩溃处理器、安装器、卸载器等）。
        /// 注意不能按「体积最大」推断：ADOFAI 的 UnityCrashHandler64.exe 就比游戏本体大。
        /// </summary>
        private static readonly string[] HelperExeKeywords =
        {
            "unitycrashhandler", "crashhandler", "crashreport", "unins", "uninstall",
            "vcredist", "dotnetfx", "dxsetup", "dxwebsetup", "notificationhelper",
            "activatedeactivate", "helper"
        };

        /// <summary>
        /// 从目录里推断启动程序（只返回文件名）。
        /// 顺序：与目录名同名 → 与应用名同名 → 名字互相包含 → 体积最大的非辅助 exe。
        /// </summary>
        public static string GuessLaunchExe(string directory, string appName)
        {
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return "app.exe";
            }

            var candidates = new List<string>();
            try
            {
                foreach (var file in Directory.GetFiles(directory, "*.exe", SearchOption.TopDirectoryOnly))
                {
                    if (IsHelperExe(Path.GetFileNameWithoutExtension(file)))
                    {
                        continue;
                    }
                    candidates.Add(file);
                }
            }
            catch (Exception ex)
            {
                VaultLog.Error("枚举启动程序失败：" + directory, ex);
            }

            if (candidates.Count == 0)
            {
                return "app.exe";
            }

            var dirToken = NormalizeToken(Path.GetFileName((directory ?? string.Empty).TrimEnd('\\', '/')));
            var appToken = NormalizeToken(appName);
            string best = null;

            if (dirToken.Length > 0)
            {
                best = candidates.FirstOrDefault(
                    f => NormalizeToken(Path.GetFileNameWithoutExtension(f)) == dirToken);
            }

            if (best == null && appToken.Length > 0)
            {
                best = candidates.FirstOrDefault(
                    f => NormalizeToken(Path.GetFileNameWithoutExtension(f)) == appToken);
            }

            if (best == null)
            {
                best = candidates.FirstOrDefault(f =>
                {
                    var token = NormalizeToken(Path.GetFileNameWithoutExtension(f));
                    if (token.Length <= 3)
                    {
                        return false;
                    }
                    if (dirToken.Length > 3 && (token.Contains(dirToken) || dirToken.Contains(token)))
                    {
                        return true;
                    }
                    return appToken.Length > 3 && (token.Contains(appToken) || appToken.Contains(token));
                });
            }

            if (best == null)
            {
                best = candidates.OrderByDescending(FileSizeOf).First();
            }

            return Path.GetFileName(best);
        }

        private static long FileSizeOf(string path)
        {
            try
            {
                return new FileInfo(path).Length;
            }
            catch
            {
                return 0L;
            }
        }

        private static bool IsHelperExe(string nameWithoutExtension)
        {
            var token = NormalizeToken(nameWithoutExtension);
            foreach (var keyword in HelperExeKeywords)
            {
                if (token.Contains(keyword))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>只保留字母与数字并转小写，用于名字比对。</summary>
        public static string NormalizeToken(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var sb = new StringBuilder(value.Length);
            foreach (var ch in value)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    sb.Append(char.ToLowerInvariant(ch));
                }
            }
            return sb.ToString();
        }

        // ---------- 清理 ----------

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                VaultLog.Warn("删除临时分片失败：" + path + "，" + ex.Message);
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch (Exception ex)
            {
                VaultLog.Warn("删除临时目录失败：" + path + "，" + ex.Message);
            }
        }

        /// <summary>
        /// 「只增不减」地推进某个分片的进度槽。
        ///
        /// 归档时同一个槽会被两个阶段先后写：先由打包线程按文件累加，再由上传线程
        /// 按已传字节折算。如果直接用 Exchange 覆盖，上传一开始就会把该槽从
        /// RawBytes 推回 0，进度条会当着用户的面往回跳一大截。
        /// 这里用 CAS 做单调取大，保证总和永远只前进。
        /// </summary>
        private static void Advance(long[] slots, int slot, long value)
        {
            while (true)
            {
                var current = Interlocked.Read(ref slots[slot]);
                if (value <= current)
                {
                    return;
                }
                if (Interlocked.CompareExchange(ref slots[slot], value, current) == current)
                {
                    return;
                }
            }
        }

    }
}
