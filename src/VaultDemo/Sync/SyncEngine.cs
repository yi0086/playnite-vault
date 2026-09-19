using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using VaultDemo.Models;
using VaultDemo.Net;
using VaultDemo.Services;

namespace VaultDemo.Sync
{
    /// <summary>
    /// 同步引擎：把远端内容落到本地，或把本地分片推到远端。
    /// 只做传输与比对，清单 / 索引的 JSON 读写由 VaultService 负责。
    ///
    /// 下载分片路径（v2）会「边下边解」：后台线程顺序下载分片，
    /// 主线程拿到一片立刻解包并删除临时片，峰值磁盘 ≈ 一片 + 已解出文件。
    /// </summary>
    public class SyncEngine
    {
        private readonly WebDavClient client;

        public SyncEngine(WebDavClient client)
        {
            this.client = client;
        }

        // ================= 分片上传（归档） =================

        /// <summary>
        /// 上传单个分片。归档编排是「打包一片 → 上传一片 → 删临时片」，
        /// 目的是让超大游戏的本地临时占用始终只有一个分片。
        /// </summary>
        public void UploadPart(string appId, PartEntry part, string localPartPath,
            Action<long, long> onBytes, CancellationToken cancelToken)
        {
            client.EnsureDirectoryRecursive("apps/" + appId + "/parts");
            client.UploadFile(localPartPath, "apps/" + appId + "/" + part.Path, onBytes, cancelToken);
        }

        /// <summary>
        /// 【v3】上传一个内容寻址区块。带重试，重试次数通过 onRetry 回报给上层统计。
        /// </summary>
        public void UploadChunk(string appId, ChunkEntry chunk, string localPath, int maxAttempts,
            Action<long, long> onBytes, CancellationToken cancelToken, Action<int, Exception> onRetry)
        {
            client.EnsureDirectoryRecursive("apps/" + appId + "/chunks");
            client.UploadFile(localPath, "apps/" + appId + "/" + chunk.Path, onBytes, cancelToken,
                maxAttempts, onRetry);
        }

        /// <summary>
        /// 【v3】远端是否已经有这个区块（内容完全一致）。
        ///
        /// 这是 v3 最关键的收益：文件名就是内容的 SHA-1，区块一旦写出就不可变，
        /// 所以 **HEAD 一下 Content-Length 就够了**，不需要任何额外元数据。
        /// 失败重传时已完成的区块会全部跳过，不会再从第 0 块重来。
        /// </summary>
        public bool ChunkOnRemote(string appId, ChunkEntry chunk)
        {
            if (string.IsNullOrEmpty(chunk.Id) || chunk.StoredBytes <= 0)
            {
                return false;
            }

            try
            {
                var size = client.GetFileSize("apps/" + appId + "/" + chunk.Path);
                return size == chunk.StoredBytes;
            }
            catch (Exception ex)
            {
                VaultLog.Warn("探测远端区块失败（当作不存在）：" + chunk.Path + "，" + ex.Message);
                return false;
            }
        }

        /// <summary>【v3】区块在本地的临时文件名。确定性，便于失败后复用已打好的块。</summary>
        public static string ChunkLocalPath(string tempDir, ChunkEntry chunk)
        {
            return Path.Combine(tempDir, "chunk-" + chunk.Index.ToString("0000") + ".bin");
        }

        /// <summary>远端是否已有完全相同的分片（增量归档时整片跳过）。</summary>
        public static bool PartAlreadyOnRemote(PartEntry part, AppManifest knownRemote)
        {
            if (knownRemote == null || knownRemote.Parts == null)
            {
                return false;
            }

            foreach (var known in knownRemote.Parts)
            {
                if (known.Index == part.Index
                    && string.Equals(known.Path, part.Path, StringComparison.OrdinalIgnoreCase)
                    && known.StoredBytes == part.StoredBytes)
                {
                    return true;
                }
            }
            return false;
        }

        // ================= 分片下载（安装 / 修复） =================

        public SyncResult DownloadPacked(string appId, AppManifest manifest, string targetDir,
            string tempDir, SyncOptions options, Action<SyncProgress> onProgress, CancellationToken cancelToken)
        {
            var opt = options ?? new SyncOptions();
            var watch = Stopwatch.StartNew();
            var reporter = new ThrottledReporter(onProgress);
            var result = new SyncResult();

            // 整片跳过：该片里所有文件都已就位就不必下载
            var neededParts = new List<PartEntry>();
            long outputNeed = 0;

            foreach (var part in manifest.Parts)
            {
                var entries = manifest.Files.Where(f => f.Part == part.Index).ToList();
                var pending = entries.Where(f => IsPending(targetDir, f, opt)).ToList();

                result.Skipped += entries.Count - pending.Count;

                if (pending.Count == 0)
                {
                    continue;
                }

                neededParts.Add(part);
                outputNeed += pending.Sum(f => f.Size);
            }

            var progress = new SyncProgress
            {
                Phase = "下载",
                FilesTotal = manifest.Files.Count,
                FilesDone = result.Skipped,
                BytesTotal = neededParts.Sum(p => p.StoredBytes),
                PartsTotal = neededParts.Count,
                SubStageName = "解包",

                // 解包子进度按「全部分片累计」记：每片归零重来的话，
                // 这一行会在 0%→100% 之间反复横跳（一片 100MB，跳好几轮），
                // 主条明明很平滑，下面那行却在闪。
                SubStageBytesTotal = neededParts.Sum(p => p.RawBytes)
            };

            if (neededParts.Count == 0)
            {
                VaultLog.Info("本地已是最新，无需下载：" + appId);
                result.Skipped = manifest.Files.Count;
                result.Elapsed = watch.Elapsed;
                progress.CurrentFile = "已是最新";
                progress.FilesDone = manifest.Files.Count;
                reporter.Report(progress, true);
                return result;
            }

            // 为什么是多个下载线程而不是一个：
            // .NET Framework 的 SslStream 在单条连接上只有一个线程做 TLS 解密，
            // 实测单路 HTTPS 天花板约 27 MB/s（≈215Mbps），千兆网根本喂不满；
            // 开 N 路后每个连接各占一个核，实测 4 路 93 MB/s、6 路 96 MB/s 到顶。
            // 走明文 HTTP 时单路就能跑满千兆，并发数设 1~2 即可。
            var concurrency = Math.Max(1, Math.Min(opt.Concurrency, neededParts.Count));

            // 磁盘预留要把「在途分片」算进去：任意时刻临时目录里可能同时躺着
            // 队列里排队待解包的 2 片 + 正在下载的 concurrency 片。
            // 少算会导致大包在快完成时因为空间不足而失败。
            var inFlightBytes = (long)(concurrency + 2) * neededParts.Max(p => p.StoredBytes);

            var required = opt.RequiredFreeBytes > 0
                ? opt.RequiredFreeBytes
                : outputNeed + inFlightBytes + 64L * 1024 * 1024;
            EnsureFreeSpace(targetDir, required);

            Directory.CreateDirectory(targetDir);
            Directory.CreateDirectory(tempDir);

            VaultLog.Info(string.Format("分片同步开始：{0}，{1} 个分片待下载，共 {2}{3}",
                appId,
                neededParts.Count,
                SyncProgress.FormatSize(progress.BytesTotal),
                opt.PipelineExtract ? "，下载与解包并行" : string.Empty));

            // 每片当前已接收的字节；进行中是部分值，完成后补成整片。
            // 这样总进度是「各片实时字节之和」，多线程下也不会来回跳。
            var partBytes = new long[neededParts.Count];

            Exception producerError = null;
            var errorLock = new object();

            VaultLog.Info(string.Format("并发下载：{0} 路（分片 {1} 个，峰值临时占用约 {2}）",
                concurrency, neededParts.Count, SyncProgress.FormatSize(inFlightBytes)));

            using (var heartbeat = new ProgressHeartbeat(reporter, progress))
            using (var queue = new BlockingCollection<PartEntry>(2))
            {
                var cursor = 0;
                var cursorLock = new object();
                var alive = concurrency;
                var inFlight = 0;
                var workers = new List<Thread>();

                for (var w = 0; w < concurrency; w++)
                {
                    var worker = new Thread(() =>
                    {
                        try
                        {
                            while (true)
                            {
                                PartEntry part;
                                int idx;

                                lock (cursorLock)
                                {
                                    if (cursor >= neededParts.Count)
                                    {
                                        return;
                                    }
                                    idx = cursor;
                                    part = neededParts[cursor];
                                    cursor++;
                                }

                                cancelToken.ThrowIfCancellationRequested();

                                var localPart = PartLocalPath(tempDir, part);
                                var slot = idx;

                                // CurrentFile 只在「开始下载这一片」时写一次。
                                // 如果放进下面的字节回调里，会被 6 条线程轮流覆盖，
                                // 文件名就会在不同分片之间高频抖动。
                                progress.CurrentFile = part.Path;
                                progress.PartsInFlight = Interlocked.Increment(ref inFlight);

                                try
                                {
                                    client.DownloadFile(
                                        "apps/" + appId + "/" + part.Path,
                                        localPart,
                                        (written, total) =>
                                        {
                                            Interlocked.Exchange(ref partBytes[slot], written);
                                            progress.BytesDone = SumBytes(partBytes);
                                            reporter.Report(progress, false);
                                        },
                                        cancelToken,
                                        opt);
                                }
                                finally
                                {
                                    progress.PartsInFlight = Interlocked.Decrement(ref inFlight);
                                }

                                Interlocked.Exchange(ref partBytes[slot], part.StoredBytes);
                                progress.BytesDone = SumBytes(partBytes);
                                queue.Add(part, cancelToken);
                            }
                        }
                        catch (Exception ex)
                        {
                            // 只记第一个错误；其它线程可能同时因为取消而抛
                            lock (errorLock)
                            {
                                if (producerError == null)
                                {
                                    producerError = ex;
                                }
                            }
                        }
                        finally
                        {
                            // 所有下载线程都退出后才关闭队列，
                            // 否则还在下载的线程会 Add 到一个已完成的集合上。
                            if (Interlocked.Decrement(ref alive) == 0)
                            {
                                queue.CompleteAdding();
                            }
                        }
                    })
                    {
                        IsBackground = true,
                        Name = "vault-part-downloader-" + w
                    };

                    workers.Add(worker);
                    worker.Start();
                }

                // 解包子阶段的累计基数（只有这条消费线程会写，不需要原子操作）
                long extractedDone = 0;

                foreach (var part in queue.GetConsumingEnumerable(cancelToken))
                {
                    try
                    {
                        var localPart = PartLocalPath(tempDir, part);
                        var entries = manifest.Files.Where(f => f.Part == part.Index).ToList();
                        var pendingInPart = entries.Where(f => IsPending(targetDir, f, opt)).ToList();

                        if (pendingInPart.Count > 0)
                        {
                            progress.PartsDone = result.PartsTransferred;

                            PackEngine.ExtractPart(manifest, part, localPart, targetDir,
                                p =>
                                {
                                    // 只写子阶段槽：BytesDone / CurrentFile 归下载线程，
                                    // 两边都写就会互相覆盖，界面上的数字就会来回跳。
                                    // 这里加上已完成分片的基数，让子进度是单调累计的。
                                    progress.SubStageBytesDone = extractedDone + p.BytesDone;
                                    reporter.Report(progress, false);
                                },
                                cancelToken,
                                f => IsPending(targetDir, f, opt));

                            progress.FilesDone = Math.Min(manifest.Files.Count,
                                progress.FilesDone + pendingInPart.Count);
                        }

                        // 无论这一片是否真的解了包（已被跳过也算处理完），基数都要往前走，
                        // 否则子进度会永远差一截到不了 100%。
                        extractedDone += part.RawBytes;
                        progress.SubStageBytesDone = extractedDone;

                        result.PartsTransferred++;
                        progress.PartsDone = result.PartsTransferred;
                        reporter.Report(progress, true);

                        VaultLog.Info(string.Format("分片 {0}/{1} 处理完成（{2} 个文件）",
                            part.Index + 1, manifest.Parts.Count, pendingInPart.Count));
                    }
                    finally
                    {
                        TryDelete(PartLocalPath(tempDir, part));
                    }
                }

                foreach (var worker in workers)
                {
                    try
                    {
                        worker.Join();
                    }
                    catch (Exception ex)
                    {
                        VaultLog.Error("等待下载线程结束失败", ex);
                    }
                }
            }

            if (producerError != null)
            {
                throw producerError;
            }

            result.Transferred = manifest.Files.Count - result.Skipped;
            result.BytesTransferred = progress.BytesTotal;
            result.Elapsed = watch.Elapsed;
            progress.PartsDone = neededParts.Count;
            progress.FilesDone = manifest.Files.Count;

            // 解包结束就把子阶段清掉，否则收尾帧会顶着一行陈旧的「解包 100%」，
            // 让人以为还在解包。
            progress.SubStageName = null;
            progress.SubStageBytesDone = 0;
            progress.SubStageBytesTotal = 0;

            progress.CurrentFile = "完成";
            reporter.Report(progress, true);

            VaultLog.Info("分片同步结束：" + appId + "，" + result.Describe());
            return result;
        }

        // ================= 区块下载（v3：内容寻址） =================

        /// <summary>
        /// 【v3】按区块下载并即时落盘。
        ///
        /// 与 v2 的三点区别：
        ///   1. 区块里的每一段按 FileOffset **随机访问**写进各自文件，
        ///      所以超大文件也能「取一块 → 写一块 → 删一块」，v2 必须凑出整个文件；
        ///   2. 峰值临时占用 ≈ 并发数 × ChunkSize，不再是 (并发+2) × PartSize
        ///      （默认参数下 2 GB → 192 MB）；
        ///   3. 中途断掉靠区块台账续传 —— 区块内容寻址、不可变，
        ///      「这块已经解过」是可靠的增量事实。
        /// </summary>
        public SyncResult DownloadChunked(string appId, AppManifest manifest, string targetDir,
            string tempDir, string stateDir, SyncOptions options, Action<SyncProgress> onProgress,
            CancellationToken cancelToken)
        {
            var opt = options ?? new SyncOptions();
            var watch = Stopwatch.StartNew();
            var reporter = new ThrottledReporter(onProgress);
            var result = new SyncResult();

            // 清单自洽性必须先过。片段不连续的清单会写出「长度看着对、内容缺一段」的文件，
            // 那比直接失败糟糕得多。
            PackEngine.ValidateChunkManifest(manifest);

            var stamp = ChunkJournal.StampOf(manifest);
            var journal = ChunkJournal.Load(stateDir, appId, stamp);
            var resuming = journal != null;
            if (journal == null)
            {
                journal = new ChunkJournal { Stamp = stamp };
            }

            // 零字节文件不会被任何区块承载，单独建出来
            foreach (var file in manifest.Files)
            {
                if (file.Size != 0)
                {
                    continue;
                }
                var full = PackEngine.SafeTarget(targetDir, file.Path);
                var dir = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                if (!File.Exists(full))
                {
                    File.WriteAllBytes(full, new byte[0]);
                }
            }

            var needed = new List<ChunkEntry>();
            long pendingRaw = 0;
            HashSet<string> pendingPaths = null;

            if (resuming)
            {
                // 上次是中途断的 → 目标文件可能是半成品，长度不可信，
                // 一律只按台账跳过「已经解完落盘」的区块。
                foreach (var chunk in manifest.Chunks)
                {
                    if (journal.Contains(chunk.Id))
                    {
                        continue;
                    }
                    needed.Add(chunk);
                    pendingRaw += chunk.RawBytes;
                }

                if (journal.Count > 0)
                {
                    VaultLog.Info(string.Format("检测到未完成的安装，续传：{0} 已有 {1}/{2} 个区块",
                        appId, journal.Count, manifest.Chunks.Count));
                }
            }
            else
            {
                // 全新状态 → 「文件是否存在且长度正确」是可信的判据
                pendingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in manifest.Files)
                {
                    if (file.Size == 0)
                    {
                        continue;
                    }
                    if (file.Pieces == null || file.Pieces.Count == 0)
                    {
                        throw new InvalidDataException(
                            "清单损坏：" + file.Path + " 有大小但没有片段");
                    }

                    var full = PackEngine.SafeTarget(targetDir, file.Path);
                    if (!IsUpToDate(full, file.Size))
                    {
                        pendingPaths.Add(file.Path);
                    }
                }

                foreach (var chunk in manifest.Chunks)
                {
                    var entries = PackEngine.FilesInChunk(manifest, chunk.Index);
                    var hit = false;
                    foreach (var entry in entries)
                    {
                        if (pendingPaths.Contains(entry.Path))
                        {
                            hit = true;
                            break;
                        }
                    }
                    if (hit)
                    {
                        needed.Add(chunk);
                        pendingRaw += chunk.RawBytes;
                    }
                }
            }

            result.PartsSkipped = manifest.Chunks.Count - needed.Count;
            result.Skipped = manifest.Files.Count - (pendingPaths == null
                ? manifest.Files.Count
                : manifest.Files.Count - pendingPaths.Count);

            var progress = new SyncProgress
            {
                Phase = "下载",
                FilesTotal = manifest.Files.Count,
                BytesTotal = needed.Sum(c => c.StoredBytes),
                PartsTotal = needed.Count,
                SubStageName = "解包",
                SubStageBytesTotal = pendingRaw
            };

            if (needed.Count == 0)
            {
                VaultLog.Info("本地已是最新，无需下载：" + appId);
                ChunkJournal.Clear(stateDir, appId);
                result.Skipped = manifest.Files.Count;
                result.Elapsed = watch.Elapsed;
                progress.CurrentFile = "已是最新";
                progress.FilesDone = manifest.Files.Count;
                reporter.Report(progress, true);
                return result;
            }

            var concurrency = Math.Max(1, Math.Min(opt.Concurrency, needed.Count));

            // 预留空间：要落盘的原始字节 + 在途区块 + 余量
            var inFlightBytes = (long)(concurrency + 2) * needed.Max(c => c.StoredBytes);
            var required = opt.RequiredFreeBytes > 0
                ? opt.RequiredFreeBytes
                : pendingRaw + inFlightBytes + 64L * 1024 * 1024;
            EnsureFreeSpace(targetDir, required);

            Directory.CreateDirectory(targetDir);
            Directory.CreateDirectory(tempDir);

            VaultLog.Info(string.Format(
                "区块同步开始：{0}，{1}/{2} 个区块待下载，共 {3}，峰值临时占用约 {4}",
                appId, needed.Count, manifest.Chunks.Count,
                SyncProgress.FormatSize(progress.BytesTotal),
                SyncProgress.FormatSize(inFlightBytes)));

            var chunkBytes = new long[needed.Count];

            Exception producerError = null;
            var errorLock = new object();

            using (var heartbeat = new ProgressHeartbeat(reporter, progress))
            using (var queue = new BlockingCollection<ChunkEntry>(2))
            {
                var cursor = 0;
                var cursorLock = new object();
                var alive = concurrency;
                var inFlight = 0;
                var workers = new List<Thread>();

                for (var w = 0; w < concurrency; w++)
                {
                    var worker = new Thread(() =>
                    {
                        try
                        {
                            while (true)
                            {
                                ChunkEntry chunk;
                                int idx;

                                lock (cursorLock)
                                {
                                    if (cursor >= needed.Count)
                                    {
                                        return;
                                    }
                                    idx = cursor;
                                    chunk = needed[cursor];
                                    cursor++;
                                }

                                cancelToken.ThrowIfCancellationRequested();

                                var localPath = ChunkLocalPath(tempDir, chunk);
                                var slot = idx;

                                // CurrentFile 只在开始下载这一块时写一次，
                                // 放进字节回调会被多条线程轮流覆盖、名字高频抖动。
                                progress.CurrentFile = chunk.Path;
                                progress.PartsInFlight = Interlocked.Increment(ref inFlight);

                                try
                                {
                                    client.DownloadFile(
                                        "apps/" + appId + "/" + chunk.Path,
                                        localPath,
                                        (written, total) =>
                                        {
                                            Interlocked.Exchange(ref chunkBytes[slot], written);
                                            progress.BytesDone = SumBytes(chunkBytes);
                                            reporter.Report(progress, false);
                                        },
                                        cancelToken,
                                        opt);
                                }
                                finally
                                {
                                    progress.PartsInFlight = Interlocked.Decrement(ref inFlight);
                                }

                                Interlocked.Exchange(ref chunkBytes[slot], chunk.StoredBytes);
                                progress.BytesDone = SumBytes(chunkBytes);
                                queue.Add(chunk, cancelToken);
                            }
                        }
                        catch (Exception ex)
                        {
                            lock (errorLock)
                            {
                                if (producerError == null)
                                {
                                    producerError = ex;
                                }
                            }
                        }
                        finally
                        {
                            // 所有下载线程退出后才关闭队列，
                            // 否则还在下载的线程会 Add 到一个已完成的集合上。
                            if (Interlocked.Decrement(ref alive) == 0)
                            {
                                queue.CompleteAdding();
                            }
                        }
                    })
                    {
                        IsBackground = true,
                        Name = "vault-chunk-downloader-" + w
                    };

                    workers.Add(worker);
                    worker.Start();
                }

                long extractedDone = 0;

                foreach (var chunk in queue.GetConsumingEnumerable(cancelToken))
                {
                    try
                    {
                        var localPath = ChunkLocalPath(tempDir, chunk);

                        PackEngine.ExtractChunk(manifest, chunk, localPath, targetDir,
                            p =>
                            {
                                progress.SubStageBytesDone = extractedDone + p.BytesDone;
                                reporter.Report(progress, false);
                            },
                            cancelToken,
                            pendingPaths == null ? null : (Func<FileEntry, bool>)(f => pendingPaths.Contains(f.Path)));

                        extractedDone += chunk.RawBytes;
                        progress.SubStageBytesDone = extractedDone;

                        result.PartsTransferred++;
                        progress.PartsDone = result.PartsTransferred;

                        // 台账在解包成功之后立刻记一笔 —— 这是断点续传的全部依据
                        journal.Mark(chunk.Id);
                        journal.Save(stateDir, appId);

                        result.Transferred += PackEngine.FilesInChunk(manifest, chunk.Index).Count;
                        reporter.Report(progress, true);

                        VaultLog.Info(string.Format("区块 {0}/{1} 已落盘并删除（{2}）",
                            result.PartsTransferred, needed.Count,
                            SyncProgress.FormatSize(chunk.StoredBytes)));
                    }
                    finally
                    {
                        TryDelete(ChunkLocalPath(tempDir, chunk));
                    }
                }

                foreach (var worker in workers)
                {
                    try
                    {
                        worker.Join();
                    }
                    catch (Exception ex)
                    {
                        VaultLog.Error("等待下载线程结束失败", ex);
                    }
                }
            }

            if (producerError != null)
            {
                throw producerError;
            }

            // 全部区块都到位了，台账使命结束
            ChunkJournal.Clear(stateDir, appId);

            result.BytesTransferred = progress.BytesTotal;
            result.Elapsed = watch.Elapsed;
            result.Skipped = manifest.Files.Count - result.Transferred;
            progress.PartsDone = needed.Count;
            progress.FilesDone = manifest.Files.Count;

            progress.SubStageName = null;
            progress.SubStageBytesDone = 0;
            progress.SubStageBytesTotal = 0;
            progress.CurrentFile = "完成";
            reporter.Report(progress, true);

            VaultLog.Info("区块同步结束：" + appId + "，" + result.Describe());
            return result;
        }

        // ================= 逐文件路径（v1 仓库兼容） =================

        public SyncResult Download(string appId, AppManifest manifest, string targetDir, SyncOptions options,
            Action<SyncProgress> onProgress, CancellationToken cancelToken)
        {
            var opt = options ?? new SyncOptions();
            var watch = Stopwatch.StartNew();
            var reporter = new ThrottledReporter(onProgress);
            var result = new SyncResult();
            var progress = new SyncProgress
            {
                Phase = "下载",
                FilesTotal = manifest.Files.Count,
                SubStageName = "当前文件"
            };

            if (opt.RequiredFreeBytes > 0)
            {
                EnsureFreeSpace(targetDir, opt.RequiredFreeBytes);
            }

            long pending = 0;
            foreach (var file in manifest.Files)
            {
                if (!IsPending(targetDir, file, opt))
                {
                    continue;
                }
                pending += file.Size;
            }
            progress.BytesTotal = pending;

            if (pending == 0)
            {
                result.Skipped = manifest.Files.Count;
                result.Elapsed = watch.Elapsed;
                progress.FilesDone = manifest.Files.Count;
                progress.CurrentFile = "已是最新";
                reporter.Report(progress, true);
                return result;
            }

            Directory.CreateDirectory(targetDir);

            long bytesDone = 0;
            foreach (var file in manifest.Files)
            {
                cancelToken.ThrowIfCancellationRequested();

                var localPath = PackEngine.SafeTarget(targetDir, file.Path);
                progress.CurrentFile = file.Path;

                if (!IsPending(targetDir, file, opt))
                {
                    result.Skipped++;
                    progress.FilesDone++;
                    reporter.Report(progress, false);
                    continue;
                }

                var captured = bytesDone;
                client.DownloadFile(
                    "apps/" + appId + "/files/" + file.Path,
                    localPath,
                    (written, total) =>
                    {
                        progress.BytesDone = captured + written;
                        // v1 是逐文件串行，所以「当前文件进度」直接占子阶段那一行
                        progress.SubStageBytesDone = written;
                        progress.SubStageBytesTotal = file.Size;
                        reporter.Report(progress, false);
                    },
                    cancelToken,
                    opt);

                bytesDone += file.Size;
                result.Transferred++;
                result.BytesTransferred += file.Size;
                progress.FilesDone++;
                progress.BytesDone = bytesDone;
                reporter.Report(progress, false);
            }

            result.Elapsed = watch.Elapsed;
            progress.CurrentFile = "完成";
            reporter.Report(progress, true);
            return result;
        }

        // ================= 辅助 =================

        /// <summary>该文件是否还需要写盘。</summary>
        private static bool IsPending(string targetDir, FileEntry entry, SyncOptions opt)
        {
            if (opt.ForceTransfer)
            {
                return true;
            }
            return !IsUpToDate(PackEngine.SafeTarget(targetDir, entry.Path), entry);
        }

        public static string PartLocalPath(string tempDir, PartEntry part)
        {
            return Path.Combine(tempDir, Path.GetFileName(part.Path));
        }

        public static bool IsUpToDate(string localPath, FileEntry entry)
        {
            return IsUpToDate(localPath, entry == null ? 0 : entry.Size);
        }

        /// <summary>长度一致即认为已就位。v3 用它判断「这个文件还要不要下」。</summary>
        public static bool IsUpToDate(string localPath, long size)
        {
            if (!File.Exists(localPath))
            {
                return false;
            }
            return new FileInfo(localPath).Length == size;
        }

        public static void EnsureFreeSpace(string targetDir, long requiredBytes)
        {
            var full = Path.GetFullPath(targetDir);
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root))
            {
                return;
            }

            var drive = new DriveInfo(root);
            if (!drive.IsReady)
            {
                return;
            }

            if (drive.AvailableFreeSpace < requiredBytes)
            {
                throw new IOException(string.Format(
                    "磁盘空间不足：需要 {0}，{1} 当前可用 {2}",
                    SyncProgress.FormatSize(requiredBytes),
                    drive.Name,
                    SyncProgress.FormatSize(drive.AvailableFreeSpace)));
            }
        }

        public static string ToLocal(string remotePath)
        {
            return (remotePath ?? string.Empty).Replace('/', Path.DirectorySeparatorChar);
        }

        // 汇总各分片的实时字节数。写入端用 Interlocked 保证单槽原子，
        // 这里读取时可能出现「读到半旧值」，仅用于进度显示，允许轻微误差。
        // 上传侧（VaultService.ArchiveApp）的多路并发也复用这个方法。
        public static long SumBytes(long[] values)
        {
            long sum = 0;
            for (var i = 0; i < values.Length; i++)
            {
                sum += Interlocked.Read(ref values[i]);
            }
            return sum;
        }

        private static void TryDelete(string path)
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
    }
}
