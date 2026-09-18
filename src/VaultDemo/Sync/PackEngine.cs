using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using VaultDemo.Models;
using VaultDemo.Services;

namespace VaultDemo.Sync
{
    /// <summary>
    /// 分片打包引擎。
    ///
    /// 设计要点（对齐 Steam 的做法）：
    ///   · 一个应用被切成若干「分片」（part），每片是一个纯字节流文件；
    ///   · 每个文件在分片里的位置/长度写在清单里（FileEntry.Part/Offset/StoredSize），
    ///     因此分片文件本身不需要索引头，下载完一片就能立刻解包；
    ///   · 解包与下载可以并行（一片在写盘时，下一片正在下载），
    ///     峰值磁盘占用 ≈ 一个分片 + 已解出的文件，而不是「整包 + 结果」；
    ///   · 压缩是逐文件可选的（默认不压：游戏资源本身多为已压缩格式，压了几乎不省还费 CPU）。
    /// </summary>
    public class PackEngine
    {
        public const string StoreMode = "store";
        public const string DeflateMode = "deflate";

        private const int IoBuffer = 512 * 1024;

        // ---------- 规划 ----------

        /// <summary>
        /// 按原始大小贪心装箱：尽量让每片接近 partSize。
        /// 单个超过 partSize 的大文件会独占一片（该片会大于 partSize）。
        /// </summary>
        public static void PlanParts(AppManifest manifest, long partSize)
        {
            if (partSize <= 0)
            {
                partSize = SyncOptions.DefaultPartSize;
            }

            manifest.Packed = true;
            manifest.PartSize = partSize;
            manifest.Parts = new List<PartEntry>();

            var current = (PartEntry)null;
            long currentRaw = 0;

            foreach (var file in manifest.Files)
            {
                if (current == null || (currentRaw > 0 && currentRaw + file.Size > partSize))
                {
                    current = new PartEntry
                    {
                        Index = manifest.Parts.Count,
                        Path = PartPath(manifest.Parts.Count),
                        RawBytes = 0,
                        StoredBytes = 0
                    };
                    manifest.Parts.Add(current);
                    currentRaw = 0;
                }

                file.Part = current.Index;
                file.Offset = 0;
                file.StoredSize = file.Size;
                file.Compression = StoreMode;

                currentRaw += file.Size;
                current.RawBytes += file.Size;
            }

            // 一个文件都没有时也留一个空清单，读取端不会崩
            if (manifest.Parts.Count == 0)
            {
                manifest.Packed = false;
            }
        }

        public static string PartPath(int index)
        {
            return "parts/part-" + index.ToString("0000") + ".bin";
        }

        // ---------- 打包（源目录 → 分片文件） ----------

        /// <summary>
        /// 只写一个分片。调用方应当在写完后立刻上传并删除它，
        /// 这样打包超大游戏时本地临时占用始终只有一个分片，而不是整个游戏。
        /// 写完后会把该片内每个文件的 Offset / StoredSize / Compression 填回 manifest。
        /// </summary>
        public static void WritePart(AppManifest manifest, PartEntry part, string sourceDir,
            string partFilePath, bool compress, Action<FileEntry, long> onFileProgress,
            CancellationToken cancelToken)
        {
            var dir = Path.GetDirectoryName(partFilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var entries = manifest.Files.Where(f => f.Part == part.Index).ToList();

            using (var output = new FileStream(partFilePath, FileMode.Create, FileAccess.Write,
                FileShare.None, IoBuffer))
            {
                long offset = 0;
                long rawWritten = 0;

                foreach (var file in entries)
                {
                    cancelToken.ThrowIfCancellationRequested();

                    var localPath = Path.Combine(sourceDir,
                        file.Path.Replace('/', Path.DirectorySeparatorChar));

                    if (!File.Exists(localPath))
                    {
                        VaultLog.Warn("源文件已消失，写入空占位：" + localPath);
                        file.Size = 0;
                        file.Offset = offset;
                        file.StoredSize = 0;
                        file.Compression = StoreMode;
                        continue;
                    }

                    file.Offset = offset;
                    file.Compression = compress ? DeflateMode : StoreMode;

                    var written = AppendFile(output, localPath, compress, cancelToken);

                    file.StoredSize = written;
                    offset += written;
                    rawWritten += file.Size;

                    if (onFileProgress != null)
                    {
                        onFileProgress(file, rawWritten);
                    }
                }

                part.StoredBytes = offset;
                part.RawBytes = rawWritten;
            }
        }

        /// <summary>把一个文件追加到分片流，返回实际写入字节数。</summary>
        private static long AppendFile(FileStream output, string localPath, bool compress,
            CancellationToken cancelToken)
        {
            var start = output.Position;

            using (var input = new FileStream(localPath, FileMode.Open, FileAccess.Read,
                FileShare.Read, IoBuffer))
            {
                if (compress)
                {
                    // leaveOpen: true —— 关掉 DeflateStream 时不要把分片文件也关掉
                    using (var deflate = new DeflateStream(output, CompressionMode.Compress, true))
                    {
                        Copy(input, deflate, null, cancelToken);
                        deflate.Flush();
                    }
                }
                else
                {
                    Copy(input, output, null, cancelToken);
                }
            }

            return output.Position - start;
        }

        // ---------- 解包（分片文件 → 目标目录） ----------

        /// <summary>
        /// 把一片解包到目标目录。一片解完后调用方就可以把它删掉，从而做到「边下边解」。
        /// <paramref name="filter"/> 返回 false 的文件会被跳过（增量修复时用）。
        /// </summary>
        public static void ExtractPart(AppManifest manifest, PartEntry part, string partFilePath,
            string targetDir, Action<SyncProgress> onProgress, CancellationToken cancelToken,
            Func<FileEntry, bool> filter)
        {
            // 这里刻意不自己节流：调用方（SyncEngine）本来就有一个 ThrottledReporter，
            // 两层节流叠加会让上报节拍变得很不规则，界面反而更抖。
            // 这一层的职责只是「把当前这一片的解包进度如实写进 SyncProgress」，
            // 所以 BytesTotal 也必须填本片的原始字节数，
            // 而不是整个应用的字节数 —— 否则百分比就没意义了。
            var progress = new SyncProgress
            {
                Phase = "解包",
                BytesTotal = part.RawBytes,
                FilesTotal = manifest.Files.Count
            };

            var all = manifest.Files.Where(f => f.Part == part.Index).ToList();
            var entries = filter == null ? all : all.Where(filter).ToList();
            if (entries.Count == 0)
            {
                return;
            }

            Directory.CreateDirectory(targetDir);

            using (var source = new FileStream(partFilePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, IoBuffer))
            {
                long done = 0;

                foreach (var entry in entries)
                {
                    cancelToken.ThrowIfCancellationRequested();

                    progress.CurrentFile = entry.Path;
                    var targetPath = Path.Combine(targetDir,
                        entry.Path.Replace('/', Path.DirectorySeparatorChar));

                    var dir = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    source.Seek(entry.Offset, SeekOrigin.Begin);

                    using (var bounded = new BoundedStream(source, entry.StoredSize))
                    using (var target = new FileStream(targetPath, FileMode.Create, FileAccess.Write,
                        FileShare.None, IoBuffer))
                    {
                        if (entry.IsStored)
                        {
                            Copy(bounded, target, null, cancelToken);
                        }
                        else
                        {
                            using (var inflate = new DeflateStream(bounded, CompressionMode.Decompress))
                            {
                                Copy(inflate, target, null, cancelToken);
                            }
                        }
                    }

                    done += entry.Size;
                    progress.BytesDone = done;
                    if (onProgress != null)
                    {
                        onProgress(progress);
                    }
                }
            }

            progress.BytesDone = part.RawBytes;
            if (onProgress != null)
            {
                onProgress(progress);
            }
        }

        // ---------- 辅助 ----------

        private static void Copy(Stream input, Stream output, Action<long> onBytes, CancellationToken cancelToken)
        {
            var buffer = new byte[IoBuffer];
            long total = 0;
            int read;

            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancelToken.ThrowIfCancellationRequested();
                output.Write(buffer, 0, read);
                total += read;
                if (onBytes != null)
                {
                    onBytes(total);
                }
            }
        }
    }

    /// <summary>
    /// 只允许读取指定字节数的只读包装流，用于在分片里切出单个文件的区间。
    /// </summary>
    public class BoundedStream : Stream
    {
        private readonly Stream inner;
        private long remaining;

        public BoundedStream(Stream inner, long length)
        {
            this.inner = inner;
            this.remaining = length;
        }

        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return false; } }
        public override long Length { get { return remaining; } }

        public override long Position
        {
            get { return inner.Position; }
            set { throw new NotSupportedException(); }
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (remaining <= 0)
            {
                return 0;
            }

            var toRead = (int)Math.Min(count, remaining);
            var read = inner.Read(buffer, offset, toRead);
            remaining -= read;
            return read;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
    }
}
