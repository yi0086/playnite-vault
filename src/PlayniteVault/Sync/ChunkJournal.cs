using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using PlayniteVault.Models;
using PlayniteVault.Services;

namespace PlayniteVault.Sync
{
    /// <summary>
    /// 【v3】下载侧的「已落盘区块」台账。
    ///
    /// 为什么需要它：v3 是按区块把片段**随机访问**写进目标文件的，
    /// 所以中途被中断时，目标文件是「半成品」——长度可能已经等于最终值
    /// （比如最后一段先落盘），于是**不能再靠「文件长度对不对」判断是否需要重下**。
    ///
    /// 台账记的是「哪些区块已经解完落盘」。区块是内容寻址且不可变的，
    /// 所以「这块已经解过」是一条可靠的增量事实：只要清单没变，
    /// 剩下的区块继续解完即可，半成品文件会被后续区块补齐。
    ///
    /// 安装成功后台账立刻删除。清单变了（换了版本）也会作废重建。
    /// </summary>
    public class ChunkJournal
    {
        public string Stamp { get; set; }

        public List<string> Done { get; set; } = new List<string>();

        [JsonIgnore]
        private HashSet<string> doneSet;

        private HashSet<string> Set
        {
            get { return doneSet ?? (doneSet = new HashSet<string>(Done ?? new List<string>(), StringComparer.Ordinal)); }
        }

        public bool Contains(string chunkId)
        {
            return !string.IsNullOrEmpty(chunkId) && Set.Contains(chunkId);
        }

        public void Mark(string chunkId)
        {
            if (string.IsNullOrEmpty(chunkId))
            {
                return;
            }
            if (Set.Add(chunkId))
            {
                Done.Add(chunkId);
            }
        }

        public int Count
        {
            get { return Done == null ? 0 : Done.Count; }
        }

        /// <summary>
        /// 清单指纹。只要区块集合或大小变了（换了版本 / 重新归档过），
        /// 旧台账就作废 —— 否则会拿旧版本的「已完成」去跳过新版本的区块。
        /// </summary>
        public static string StampOf(AppManifest manifest)
        {
            var sb = new StringBuilder();
            sb.Append(manifest.Id).Append('|').Append(manifest.TotalBytes)
              .Append('|').Append(manifest.ChunkSize).Append('|');
            if (manifest.Chunks != null)
            {
                foreach (var chunk in manifest.Chunks)
                {
                    sb.Append(chunk.Id).Append(':').Append(chunk.StoredBytes).Append(';');
                }
            }

            using (var sha = SHA1.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                var text = new StringBuilder(hash.Length * 2);
                foreach (var b in hash)
                {
                    text.Append(b.ToString("x2"));
                }
                return text.ToString();
            }
        }

        public static string PathFor(string stateDir, string appId)
        {
            return Path.Combine(stateDir, "journals", appId + ".json");
        }

        /// <summary>读台账；不存在 / 指纹不符 / 损坏时返回 null（视为全新下载）。</summary>
        public static ChunkJournal Load(string stateDir, string appId, string stamp)
        {
            try
            {
                var path = PathFor(stateDir, appId);
                if (!File.Exists(path))
                {
                    return null;
                }

                var journal = JsonConvert.DeserializeObject<ChunkJournal>(
                    File.ReadAllText(path, Encoding.UTF8));

                if (journal == null || !string.Equals(journal.Stamp, stamp, StringComparison.Ordinal))
                {
                    VaultLog.Info("区块台账与当前清单不符，作废重建：" + appId);
                    return null;
                }

                if (journal.Done == null)
                {
                    journal.Done = new List<string>();
                }
                return journal;
            }
            catch (Exception ex)
            {
                VaultLog.Warn("读区块台账失败（当作全新下载）：" + appId + "，" + ex.Message);
                return null;
            }
        }

        public void Save(string stateDir, string appId)
        {
            var path = PathFor(stateDir, appId);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                // 先写临时文件再改名，避免写到一半被杀留下半截 JSON
                var temp = path + ".tmp";
                File.WriteAllText(temp,
                    JsonConvert.SerializeObject(this, Formatting.Indented), new UTF8Encoding(false));
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
                File.Move(temp, path);
            }
            catch (Exception ex)
            {
                VaultLog.Warn("写区块台账失败（只影响断点续传）：" + path + "，" + ex.Message);
            }
        }

        public static void Clear(string stateDir, string appId)
        {
            try
            {
                var path = PathFor(stateDir, appId);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                VaultLog.Warn("清除区块台账失败：" + ex.Message);
            }
        }
    }
}
