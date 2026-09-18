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

                var localPath = Path.Combine(targetDir, ToLocal(file.Path));
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
            return !IsUpToDate(Path.Combine(targetDir, ToLocal(entry.Path)), entry);
        }

        public static string PartLocalPath(string tempDir, PartEntry part)
        {
            return Path.Combine(tempDir, Path.GetFileName(part.Path));
        }

        public static bool IsUpToDate(string localPath, FileEntry entry)
        {
            if (!File.Exists(localPath))
            {
                return false;
            }
            return new FileInfo(localPath).Length == entry.Size;
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
