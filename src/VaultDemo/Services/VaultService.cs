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
using VaultDemo.Models;
using VaultDemo.Net;
using VaultDemo.Sync;

namespace VaultDemo.Services
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

        public VaultService(IPlayniteAPI api, string dataPath)
        {
            this.api = api;
            this.dataPath = dataPath;
            Directory.CreateDirectory(dataPath);
            LoadSettings();
        }

        public string DataPath
        {
            get { return dataPath; }
        }

        public VaultSettings Settings
        {
            get { return settings; }
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
                PartSize = Math.Max(1, settings.PartSizeMB) * 1024L * 1024L,
                Compress = settings.CompressOnArchive,
                PipelineExtract = settings.PipelineExtract,
                Concurrency = Math.Max(1, settings.Concurrency)
            };
        }

        // ---------- WebDAV ----------

        public WebDavClient CreateClient()
        {
            return CreateClient(settings.TimeoutSeconds);
        }

        public WebDavClient CreateClient(int timeoutSeconds)
        {
            if (!settings.IsConfigured)
            {
                throw new InvalidOperationException("尚未配置 WebDAV 地址，请先打开插件设置。");
            }
            return new WebDavClient(settings.WebDavUrl, settings.Username, settings.Password,
                timeoutSeconds, settings.UseSystemProxy);
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

        public RepositoryIndex LoadCachedIndex()
        {
            try
            {
                if (!File.Exists(CacheFile))
                {
                    return null;
                }
                return JsonConvert.DeserializeObject<RepositoryIndex>(
                    File.ReadAllText(CacheFile, Encoding.UTF8));
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

        public string GetInstallDir(string appId)
        {
            return Path.Combine(settings.LocalRoot, appId);
        }

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
        public static AppManifest BuildManifest(string appId, string appName, string localDir,
            string launchExe, string version, AppMetadata metadata)
        {
            var manifest = new AppManifest
            {
                Id = appId,
                Name = appName,
                Version = version,
                UpdatedAt = DateTime.UtcNow,
                LaunchExe = launchExe,
                Metadata = metadata
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
        /// 归档到 WebDAV：分片打包 → 逐片上传 → 写清单与索引。
        /// 逐片处理（打包一片、传一片、删一片）是为了让超大游戏的本地临时占用
        /// 始终只有一个分片，而不是整个游戏。
        /// </summary>
        public SyncResult ArchiveApp(string appId, string appName, string localDir, string launchExe,
            string version, AppMetadata metadata, SyncOptions options,
            Action<SyncProgress> onProgress, CancellationToken cancelToken)
        {
            var client = CreateClient();
            var opt = options ?? BuildSyncOptions();
            var result = new SyncResult();
            var watch = Stopwatch.StartNew();
            var reporter = new ThrottledReporter(onProgress);

            var manifest = BuildManifest(appId, appName, localDir, launchExe, version, metadata);

            AppManifest knownRemote = null;
            try
            {
                knownRemote = FetchManifest(appId);
            }
            catch (Exception ex)
            {
                VaultLog.Info("远端尚无该应用的清单，将全量归档：" + appId + "（" + ex.Message + "）");
            }

            PackEngine.PlanParts(manifest, opt.EffectivePartSize);

            var progress = new SyncProgress
            {
                Phase = "归档",
                FilesTotal = manifest.Files.Count,
                BytesTotal = manifest.TotalBytes,
                PartsTotal = manifest.Parts.Count,
                SubStageName = "打包"
            };

            VaultLog.Info(string.Format(
                "归档开始：{0}，{1} 个文件 / {2}，切成 {3} 个分片（每片 {4}，{5}）",
                appId, manifest.Files.Count, SyncProgress.FormatSize(manifest.TotalBytes),
                manifest.Parts.Count, SyncProgress.FormatSize(opt.EffectivePartSize),
                opt.Compress ? "Deflate 压缩" : "不压缩"));

            var engine = new SyncEngine(client);
            var tempDir = Path.Combine(dataPath, "pack-temp", appId);

            // 每片对总进度的「原始字节」贡献。用数组而不是累加变量，
            // 因为多路上传时分片完成顺序是乱的，累加会算错。
            var partRaw = new long[manifest.Parts.Count];

            // 上传同样是 TLS 单连接瓶颈（见 SyncEngine.DownloadPacked 的说明），
            // 所以这里也开多路：一个线程负责打包（读盘+拼片），
            // N 个线程负责上传，队列上限卡住磁盘占用，边打边传。
            var uploaders = Math.Max(1, Math.Min(opt.Concurrency, manifest.Parts.Count));

            VaultLog.Info(string.Format("归档并发：{0} 路上传（共 {1} 个分片）",
                uploaders, manifest.Parts.Count));

            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancelToken);
            Exception firstError = null;
            var errorLock = new object();

            var skippedParts = 0;
            var uploadedParts = 0;
            long uploadedBytes = 0;
            var doneParts = 0;
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
                using (var queue = new BlockingCollection<PartWork>(uploaders + 1))
                {
                    // 打包线程：按序把每个分片打成临时文件后入队。
                    var packer = new Thread(() =>
                    {
                        try
                        {
                            foreach (var part in manifest.Parts)
                            {
                                cts.Token.ThrowIfCancellationRequested();

                                // 不压缩时，分片字节数在规划阶段就已知，可以直接整片跳过
                                if (!opt.ForceTransfer && !opt.Compress
                                    && SyncEngine.PartAlreadyOnRemote(part, knownRemote))
                                {
                                    Advance(partRaw, part.Index, part.RawBytes);
                                    Interlocked.Increment(ref skippedParts);
                                    progress.PartsDone = Interlocked.Increment(ref doneParts);
                                    progress.BytesDone = SyncEngine.SumBytes(partRaw);
                                    reporter.Report(progress, true);
                                    VaultLog.Info("远端已有相同分片，跳过：" + part.Path);
                                    continue;
                                }

                                var localPart = Path.Combine(tempDir, Path.GetFileName(part.Path));
                                var slot = part.Index;

                                // 打包只推进子阶段。**绝不能**用打包进度去推进总进度条：
                                // 打包是纯本地磁盘操作（468MB 实测不到 1 秒），而上传要十几秒，
                                // 一旦让打包去推主进度，进度条会在 1 秒内冲到 100% 然后干等十几秒，
                                // 看起来就像「卡死了」。主进度只反映真正耗时的上传。
                                progress.SubStageBytesDone = 0;
                                progress.SubStageBytesTotal = part.RawBytes;
                                reporter.Report(progress, true);

                                try
                                {
                                    PackEngine.WritePart(manifest, part, localDir, localPart, opt.Compress,
                                        (file, inPart) =>
                                        {
                                            progress.SubStageBytesDone = inPart;
                                            reporter.Report(progress, false);
                                        },
                                        cts.Token);
                                }
                                catch
                                {
                                    TryDeleteFile(localPart);
                                    throw;
                                }

                                progress.SubStageBytesDone = part.RawBytes;
                                queue.Add(new PartWork { Part = part, LocalPath = localPart }, cts.Token);
                            }
                        }
                        catch (Exception ex)
                        {
                            fail(ex);
                        }
                        finally
                        {
                            // 打包是最快的阶段（468MB 实测不到 1 秒），而上传要十几秒。
                            // 子阶段信息不在这里清掉的话，后面整段上传都会顶着一行
                            // 陈旧的「打包 100%」，看起来像还有活没干。
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

                    // 上传线程：从队列取片上传，传完立刻删临时片，占用不累积。
                    var uploadThreads = new List<Thread>();
                    for (var w = 0; w < uploaders; w++)
                    {
                        var worker = new Thread(() =>
                        {
                            try
                            {
                                foreach (var work in queue.GetConsumingEnumerable(cts.Token))
                                {
                                    var part = work.Part;
                                    var slot = part.Index;

                                    // CurrentFile 只在这里写一次（开始传这一片时），
                                    // 放进下面的字节回调会被多条上传线程轮流覆盖
                                    progress.CurrentFile = part.Path;
                                    progress.PartsInFlight = Interlocked.Increment(ref inFlight);
                                    reporter.Report(progress, true);

                                    try
                                    {
                                        // 「已写进 socket」不等于「服务端已落盘」。
                                        // 客户端把 100MB 塞进 TCP 缓冲用不了多久，之后线程就
                                        // 阻塞在 GetResponse() 等服务端把积压写完——实测 5 片并发
                                        // 时这段要等 2.4 秒。若按 socket 字节计进度，进度条会先冲到
                                        // 100% 再干等两秒多，看起来就是「卡死」。
                                        // 所以每片留最后一成「尾款」，等服务端确认（UploadPart 返回）
                                        // 才补上：这段等待就落在 90%→100% 的爬升里，而不是钉在 100%。
                                        var streamCap = (long)(part.RawBytes * (1.0 - UploadConfirmReserve));

                                        engine.UploadPart(appId, part, work.LocalPath,
                                            (written, total) =>
                                            {
                                                // 只增不减：打包阶段这个槽已经涨到过 RawBytes，
                                                // 上传阶段从 0 重新计会当着用户的面把进度条往回拉
                                                var streamed = ScaleToRaw(written, part);
                                                if (streamed > streamCap)
                                                {
                                                    streamed = streamCap;
                                                }
                                                Advance(partRaw, slot, streamed);
                                                progress.BytesDone = SyncEngine.SumBytes(partRaw);
                                                reporter.Report(progress, false);
                                            },
                                            cts.Token);
                                    }
                                    finally
                                    {
                                        // 无论成功失败都删掉临时片，避免残留
                                        TryDeleteFile(work.LocalPath);
                                        progress.PartsInFlight = Interlocked.Decrement(ref inFlight);
                                    }

                                    Advance(partRaw, slot, part.RawBytes);
                                    Interlocked.Increment(ref uploadedParts);
                                    Interlocked.Add(ref uploadedBytes, part.StoredBytes);
                                    progress.PartsDone = Interlocked.Increment(ref doneParts);
                                    progress.BytesDone = SyncEngine.SumBytes(partRaw);
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

            result.Skipped = skippedParts;
            result.PartsTransferred = uploadedParts;
            result.BytesTransferred = uploadedBytes;
            result.Transferred = manifest.Files.Count - result.Skipped;
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
                PartCount = manifest.Parts.Count,
                Packed = manifest.Packed,
                Metadata = metadata
            });

            // 老版本是逐文件布局，升级成分片后把旧的 files/ 清掉，避免仓库里留双份
            if (knownRemote != null && !knownRemote.Packed && knownRemote.Files.Count > 0)
            {
                CleanUpLegacyLayout(client, appId, knownRemote);
            }

            progress.CurrentFile = "完成";
            reporter.Report(progress, true);

            VaultLog.Info(string.Format("归档结束：{0}，{1}，存储 {2}",
                appId, result.Describe(),
                SyncProgress.FormatSize(manifest.Parts.Sum(p => p.StoredBytes))));

            return result;
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

        private static long ScaleToRaw(long storedWritten, PartEntry part)
        {
            if (part.StoredBytes <= 0)
            {
                return 0;
            }
            var scaled = part.RawBytes * (double)storedWritten / part.StoredBytes;
            return scaled > part.RawBytes ? part.RawBytes : (long)scaled;
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
        /// 分片清单走「边下边解」，v1 清单走逐文件下载。
        /// </summary>
        public SyncResult InstallApp(string appId, string targetDir, SyncOptions options,
            Action<SyncProgress> onProgress, CancellationToken cancelToken)
        {
            var manifest = FetchManifest(appId);
            var opt = options ?? BuildSyncOptions();
            var client = CreateClient();
            var engine = new SyncEngine(client);

            SyncResult result;

            if (manifest.Packed && manifest.Parts.Count > 0)
            {
                var tempDir = Path.Combine(dataPath, "part-temp", appId);
                result = engine.DownloadPacked(appId, manifest, targetDir, tempDir, opt, onProgress, cancelToken);
            }
            else
            {
                if (opt.RequiredFreeBytes <= 0)
                {
                    // 预留 10% 余量，另加 64MB 给临时文件与日志
                    opt.RequiredFreeBytes = (long)(manifest.TotalBytes * 1.1) + 64L * 1024 * 1024;
                }
                result = engine.Download(appId, manifest, targetDir, opt, onProgress, cancelToken);
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

        /// <summary>把游戏名压成安全的目录名 / 应用 Id。</summary>
        public static string MakeSlug(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "app-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            }

            var sb = new StringBuilder();
            foreach (var ch in name.Trim().ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(ch))
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
                slug = "app-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            }
            return slug.Length > 48 ? slug.Substring(0, 48).Trim('-') : slug;
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

        /// <summary>打包线程交给上传线程的一个待传分片。</summary>
        private class PartWork
        {
            public PartEntry Part { get; set; }

            /// <summary>本地临时分片文件的完整路径。</summary>
            public string LocalPath { get; set; }
        }
    }
}
