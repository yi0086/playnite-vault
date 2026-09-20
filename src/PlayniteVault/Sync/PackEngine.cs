using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using PlayniteVault.Models;
using PlayniteVault.Services;

namespace PlayniteVault.Sync
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

        /// <summary>
        /// 小于这个字节数的片段一律强制 store：deflate 的流头尾开销会让小片段反而变大，
        /// 而游戏里绝大多数文件都是小文件。
        /// </summary>
        private const int MinCompressSize = 512;

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

        // ==================== v3：内容寻址区块 ====================

        /// <summary>
        /// 【v3】规划区块：按原始大小贪心装填，**一个文件可以跨多个区块**。
        ///
        /// 这就是修掉「1.93 GB 的 res.pak 独占一个分片、单个 PUT 必然超时」的地方。
        /// 确定性是「跳过已传区块」的前提，所以规则只依赖文件自身的字节与一个全局参数：
        /// 顺序取 Path 升序的文件表，逐文件按 chunkSize 切段，装满一块就封块换下一块。
        ///
        /// 因为「一段写完时，要么块满了、要么文件没了」，所以
        /// **同一个文件在同一个区块里最多只有一段**，读取端不必处理多段。
        /// </summary>
        public static void PlanChunks(AppManifest manifest, long chunkSize)
        {
            if (chunkSize <= 0)
            {
                chunkSize = SyncOptions.DefaultChunkSize;
            }

            manifest.Schema = VaultSchema.Current;
            manifest.Packed = true;
            manifest.ChunkSize = chunkSize;
            manifest.Chunks = new List<ChunkEntry>();
            manifest.Parts = new List<PartEntry>();

            ChunkEntry current = null;
            long buffered = 0;

            foreach (var file in manifest.Files)
            {
                file.Pieces = new List<PieceEntry>();

                var remaining = file.Size;
                var fileOffset = 0L;

                while (remaining > 0)
                {
                    if (current == null)
                    {
                        current = new ChunkEntry { Index = manifest.Chunks.Count };
                        manifest.Chunks.Add(current);
                        buffered = 0;
                    }

                    var take = Math.Min(chunkSize - buffered, remaining);

                    file.Pieces.Add(new PieceEntry
                    {
                        Chunk = current.Index,
                        Offset = buffered,
                        // 压缩开启时 Length 要等写完才知道，这里先按不压缩估
                        Length = take,
                        Size = take,
                        FileOffset = fileOffset,
                        Compression = StoreMode
                    });

                    buffered += take;
                    current.RawBytes += take;
                    remaining -= take;
                    fileOffset += take;

                    if (buffered >= chunkSize)
                    {
                        current = null;
                    }
                }
            }

            // 一个文件都没有时也留一个空清单，读取端不会崩
            if (manifest.Chunks.Count == 0)
            {
                manifest.Packed = false;
            }
        }

        /// <summary>
        /// 这个区块里含有的文件（按文件表顺序，确定性）。
        /// 一个文件的片段只可能落在它自己的若干个区块里，扫一遍即可。
        /// </summary>
        public static List<FileEntry> FilesInChunk(AppManifest manifest, int chunkIndex)
        {
            var result = new List<FileEntry>();
            foreach (var file in manifest.Files)
            {
                if (file.Pieces == null)
                {
                    continue;
                }
                foreach (var piece in file.Pieces)
                {
                    if (piece.Chunk == chunkIndex)
                    {
                        result.Add(file);
                        break;   // 同块同文件最多一段
                    }
                }
            }
            return result;
        }

        /// <summary>该文件在这个区块里的那一段；没有就返回 null。</summary>
        public static PieceEntry PieceInChunk(FileEntry file, int chunkIndex)
        {
            if (file.Pieces == null)
            {
                return null;
            }
            foreach (var piece in file.Pieces)
            {
                if (piece.Chunk == chunkIndex)
                {
                    return piece;
                }
            }
            return null;
        }

        /// <summary>
        /// 【v3】写一个区块到临时文件，**同时增量计算 SHA-1**，返回 40 位小写 hex。
        ///
        /// 边写边算（TransformBlock）而不是写完再读一遍，省掉一倍的 I/O。
        /// 调用方拿到哈希后应当按它命名上传，然后立刻删掉临时文件。
        /// 顺便把每段的 Offset / Length / Compression 回填进清单。
        /// </summary>
        public static string WriteChunk(AppManifest manifest, ChunkEntry chunk, string sourceDir,
            string partFilePath, bool compress, Action<long, long> onProgress, CancellationToken cancelToken)
        {
            var dir = Path.GetDirectoryName(partFilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var entries = FilesInChunk(manifest, chunk.Index);
            long written = 0;
            long rawDone = 0;
            var shrunk = false;
            byte[] hash;

            using (var sha = SHA1.Create())
            using (var output = new FileStream(partFilePath, FileMode.Create, FileAccess.Write,
                FileShare.None, IoBuffer))
            {
                foreach (var file in entries)
                {
                    cancelToken.ThrowIfCancellationRequested();

                    var piece = PieceInChunk(file, chunk.Index);
                    if (piece == null)
                    {
                        continue;
                    }

                    var localPath = Path.Combine(sourceDir,
                        file.Path.Replace('/', Path.DirectorySeparatorChar));

                    if (!File.Exists(localPath))
                    {
                        // 归档过程中源文件消失：写成零长片段，让清单仍然自洽
                        VaultLog.Warn("源文件已消失，写入空片段：" + localPath);
                        piece.Offset = written;
                        piece.Length = 0;
                        piece.Size = 0;
                        piece.Compression = StoreMode;
                        shrunk = true;
                        continue;
                    }

                    piece.Offset = written;
                    piece.Compression = compress ? DeflateMode : StoreMode;

                    var stored = AppendSlice(output, sha, localPath, piece.FileOffset, piece.Size,
                        compress, cancelToken);

                    piece.Length = stored;
                    written += stored;
                    rawDone += piece.Size;

                    if (onProgress != null)
                    {
                        onProgress(rawDone, chunk.RawBytes);
                    }
                }

                hash = sha.ComputeHash(new byte[0]);
            }

            chunk.StoredBytes = written;

            // 有文件缩水时把清单的合计值同步过来，否则校验会判定「清单损坏」
            if (shrunk)
            {
                foreach (var file in manifest.Files)
                {
                    if (file.Pieces != null)
                    {
                        file.Size = file.Pieces.Sum(p => p.Size);
                    }
                }
                manifest.TotalBytes = manifest.Files.Sum(f => f.Size);
            }

            var id = ToHex(hash);
            chunk.Id = id;
            chunk.Path = ChunkEntry.PathFor(id);
            return id;
        }

        /// <summary>
        /// 把某个文件的 [fileOffset, fileOffset+length) 追加到区块流，返回实际写入字节数。
        /// 哈希按**写入的（压缩后）字节**累计 —— 区块的哈希就是它文件内容的哈希，
        /// 这样「远端是否已有这块」的判据才和实际文件对应得上。
        /// </summary>
        private static long AppendSlice(FileStream output, HashAlgorithm sha, string localPath,
            long fileOffset, long length, bool compress, CancellationToken cancelToken)
        {
            if (length <= 0)
            {
                return 0;
            }

            var start = output.Position;

            using (var input = new FileStream(localPath, FileMode.Open, FileAccess.Read,
                FileShare.Read, IoBuffer))
            {
                if (fileOffset > 0)
                {
                    input.Seek(fileOffset, SeekOrigin.Begin);
                }

                var bounded = new BoundedStream(input, length);

                if (compress && length >= MinCompressSize)
                {
                    // leaveOpen: true —— 关掉 DeflateStream 时不要把区块文件也关掉。
                    // 这里要往 sha 里喂的是压缩后的字节，所以先用内存缓冲接住再写。
                    using (var memory = new MemoryStream())
                    {
                        using (var deflate = new DeflateStream(memory, CompressionMode.Compress, true))
                        {
                            Copy(bounded, deflate, null, cancelToken);
                            deflate.Flush();
                        }
                        var blob = memory.ToArray();
                        output.Write(blob, 0, blob.Length);
                        sha.TransformBlock(blob, 0, blob.Length, null, 0);
                    }
                }
                else
                {
                    CopyHashed(bounded, output, sha, cancelToken);
                }
            }

            return output.Position - start;
        }

        /// <summary>边写边喂哈希的拷贝。缓冲区较大，TransformBlock 的调用次数很少。</summary>
        private static void CopyHashed(Stream input, Stream output, HashAlgorithm sha,
            CancellationToken cancelToken)
        {
            var buffer = new byte[IoBuffer];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancelToken.ThrowIfCancellationRequested();
                output.Write(buffer, 0, read);
                sha.TransformBlock(buffer, 0, read, null, 0);
            }
        }

        private static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (var b in bytes)
            {
                sb.Append(b.ToString("x2"));
            }
            return sb.ToString();
        }

        /// <summary>
        /// 【v3】把一个区块解到目标目录：块里每一段按 FileOffset **随机访问**写入各自文件。
        ///
        /// 与 v2 的关键区别：v2 必须凑出完整文件才能写，v3 可以按块增量地拼出大文件，
        /// 所以「取一块 → 立刻落盘 → 删块」对超大文件同样成立。
        /// </summary>
        public static void ExtractChunk(AppManifest manifest, ChunkEntry chunk, string chunkFilePath,
            string targetDir, Action<SyncProgress> onProgress, CancellationToken cancelToken,
            Func<FileEntry, bool> filter)
        {
            var progress = new SyncProgress
            {
                Phase = "解包",
                BytesTotal = chunk.RawBytes,
                FilesTotal = manifest.Files.Count
            };

            var entries = FilesInChunk(manifest, chunk.Index);
            if (filter != null)
            {
                entries = entries.Where(filter).ToList();
            }
            if (entries.Count == 0)
            {
                return;
            }

            Directory.CreateDirectory(targetDir);

            using (var source = new FileStream(chunkFilePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, IoBuffer))
            {
                long done = 0;

                foreach (var entry in entries)
                {
                    cancelToken.ThrowIfCancellationRequested();

                    var piece = PieceInChunk(entry, chunk.Index);
                    if (piece == null || piece.Size <= 0)
                    {
                        continue;
                    }

                    progress.CurrentFile = entry.Path;
                    var targetPath = SafeTarget(targetDir, entry.Path);

                    var dir = Path.GetDirectoryName(targetPath);
                    if (!string.IsNullOrEmpty(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    source.Seek(piece.Offset, SeekOrigin.Begin);

                    using (var bounded = new BoundedStream(source, piece.Length))
                    using (var target = new FileStream(targetPath, FileMode.OpenOrCreate,
                        FileAccess.Write, FileShare.None, IoBuffer))
                    {
                        // 随机访问：直接落到本文件内的正确偏移
                        target.Seek(piece.FileOffset, SeekOrigin.Begin);

                        if (piece.IsStored)
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

                    done += piece.Size;
                    progress.BytesDone = done;
                    if (onProgress != null)
                    {
                        onProgress(progress);
                    }
                }
            }

            progress.BytesDone = chunk.RawBytes;
            if (onProgress != null)
            {
                onProgress(progress);
            }
        }

        /// <summary>
        /// 校验清单自洽：片段必须首尾相接、下标必须在范围内、偏移不能越出区块。
        /// **损坏的清单必须直接报错停下**，否则会写出静默损坏的文件（缺一段但长度看着对）。
        /// </summary>
        public static void ValidateChunkManifest(AppManifest manifest)
        {
            if (manifest.Chunks == null || manifest.Chunks.Count == 0)
            {
                throw new InvalidDataException("清单里没有区块表");
            }

            long total = 0;
            foreach (var file in manifest.Files)
            {
                if (!file.PiecesAreContiguous())
                {
                    throw new InvalidDataException(string.Format(
                        "清单损坏：{0} 的片段不连续（期望 {1} 字节）", file.Path, file.Size));
                }

                total += file.Size;

                if (file.Pieces == null)
                {
                    continue;
                }

                foreach (var piece in file.Pieces)
                {
                    if (piece.Chunk < 0 || piece.Chunk >= manifest.Chunks.Count)
                    {
                        throw new InvalidDataException(string.Format(
                            "清单损坏：{0} 引用了不存在的区块 {1}", file.Path, piece.Chunk));
                    }
                    if (piece.Offset < 0 || piece.Offset + piece.Length > manifest.Chunks[piece.Chunk].StoredBytes)
                    {
                        throw new InvalidDataException(string.Format(
                            "清单损坏：{0} 在区块 {1} 里越界", file.Path, piece.Chunk));
                    }
                }
            }

            if (total != manifest.TotalBytes)
            {
                throw new InvalidDataException(string.Format(
                    "清单损坏：文件大小合计 {0} 与 TotalBytes {1} 不一致", total, manifest.TotalBytes));
            }
        }

        /// <summary>
        /// 把清单里的相对路径安全地拼到目标目录下。
        /// 路径来自远端 JSON，属于不可信输入：`..\..\Windows\System32\x` 这种
        /// 一旦直接 Path.Combine 就会写到目标目录之外。
        /// </summary>
        public static string SafeTarget(string targetDir, string relative)
        {
            var root = Path.GetFullPath(targetDir);
            if (!root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            {
                root += Path.DirectorySeparatorChar;
            }

            var cleaned = (relative ?? string.Empty).Replace('/', Path.DirectorySeparatorChar);
            var full = Path.GetFullPath(Path.Combine(root, cleaned));

            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("清单里的路径越出了目标目录：" + relative);
            }
            return full;
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
                    var targetPath = SafeTarget(targetDir, entry.Path);

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
