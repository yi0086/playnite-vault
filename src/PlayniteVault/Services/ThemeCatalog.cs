using System;
using System.Collections.Generic;
using System.IO;
using PlayniteVault.Models;
using PlayniteVault.Net;

namespace PlayniteVault.Services
{
    /// <summary>
    /// 「主题」页上的一张卡片：一个主题在两边的状态。
    ///
    /// <para>为什么不复用 <see cref="ThemeIndexEntry"/>：那个是**远端索引**的一行，
    /// 而卡片要同时表达「本地有没有 / 远端有没有 / 我这次勾没勾」。
    /// 合成一个视图对象之后，界面不用自己去 join 两份列表。</para>
    /// </summary>
    public class ThemeCatalogItem
    {
        /// <summary>Desktop / Fullscreen。</summary>
        public string Mode { get; set; } = string.Empty;

        /// <summary>主题目录名 —— 也就是主题 Id。</summary>
        public string Id { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string Version { get; set; } = string.Empty;

        /// <summary>本地那份的体积（本地没有则 0）。</summary>
        public long LocalBytes { get; set; }

        /// <summary>远端那份的体积（远端没有则 0）。</summary>
        public long RemoteBytes { get; set; }

        public int LocalFiles { get; set; }

        public int RemoteFiles { get; set; }

        public bool Local { get; set; }

        public bool Remote { get; set; }

        /// <summary>传给同步引擎的键，必须和引擎内部的 <c>Key()</c> 是同一个字符串。</summary>
        public string Key
        {
            get { return ThemeSyncOptions.ThemeSyncEngineKey(Mode, Id); }
        }

        public string DisplayName
        {
            get { return string.IsNullOrWhiteSpace(Name) ? Id : Name; }
        }

        /// <summary>卡片上那一行状态字。</summary>
        public string StateText
        {
            get
            {
                if (Local && Remote)
                {
                    return "两边都有";
                }

                if (Local)
                {
                    return "只在本机";
                }

                if (Remote)
                {
                    return "只在 NAS";
                }

                return "未知";
            }
        }

        /// <summary>体积：两边都有时优先显示本地那份（用户要看的是自己磁盘上的占用）。</summary>
        public long EffectiveBytes
        {
            get { return Local ? LocalBytes : RemoteBytes; }
        }

        /// <summary>卡片上的副标题：「桌面 · 12.3 MB · 只在本机」。</summary>
        public string Describe()
        {
            var mode = string.Equals(Mode, "Fullscreen", StringComparison.OrdinalIgnoreCase)
                ? "全屏"
                : "桌面";

            var parts = new List<string> { mode };
            if (EffectiveBytes > 0)
            {
                parts.Add(SyncProgress.FormatSize(EffectiveBytes));
            }

            if (!Local && Remote)
            {
                parts.Add("只在 NAS");
            }
            else if (Local && !Remote)
            {
                parts.Add("只在本机");
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>
    /// 把「本地主题目录」与「远端 themes/index.json」拼成一份卡片列表。
    ///
    /// <para><b>为什么只读索引、不去列远端目录</b>：远端 themes/ 下每个主题都有自己的
    /// manifest.json，列目录再逐个读就是 N+1 次请求。而 index.json 本来就是为了
    /// 「一次读全」才存在的 —— 同步引擎自己也是这么用的。</para>
    /// </summary>
    public static class ThemeCatalog
    {
        public static readonly string[] Modes = { "Desktop", "Fullscreen" };

        // ---------------------------------------------------------------- 本地

        /// <summary>
        /// 扫本地主题目录。**只数文件与大小，不算哈希** —— 哈希留给真正同步时去做，
        /// 列表页要的是「有什么」，几百兆的主题在这里不该被读一遍。
        /// </summary>
        public static List<ThemeCatalogItem> ListLocal(string themesRoot)
        {
            var items = new List<ThemeCatalogItem>();
            if (string.IsNullOrWhiteSpace(themesRoot) || !Directory.Exists(themesRoot))
            {
                return items;
            }

            foreach (var mode in Modes)
            {
                var dir = Path.Combine(themesRoot, mode);
                if (!Directory.Exists(dir))
                {
                    continue;
                }

                foreach (var themeDir in Directory.GetDirectories(dir))
                {
                    var id = Path.GetFileName(themeDir);
                    if (string.IsNullOrEmpty(id) || id.Contains(".conflict-"))
                    {
                        // 冲突留档不算一个主题（同步引擎扫描时也会跳过它们）
                        continue;
                    }

                    var item = new ThemeCatalogItem
                    {
                        Mode = mode,
                        Id = id,
                        Name = id,
                        Local = true
                    };

                    item.Name = ReadName(themeDir) ?? id;

                    // 版本号：Playnite 的主题清单在 extension.yaml 里，读一眼不为过（就是个文本文件）
                    item.Version = ReadVersion(themeDir);

                    long bytes;
                    int files;
                    Measure(themeDir, out bytes, out files);
                    item.LocalBytes = bytes;
                    item.LocalFiles = files;

                    items.Add(item);
                }
            }

            return items;
        }

        /// <summary>主题自述文件里的 display name（extension.yaml）。取不到就用目录名。</summary>
        private static string ReadName(string themeDir)
        {
            var yaml = Path.Combine(themeDir, "extension.yaml");
            if (!File.Exists(yaml))
            {
                return null;
            }

            try
            {
                foreach (var line in File.ReadAllLines(yaml))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("Name:", StringComparison.OrdinalIgnoreCase))
                    {
                        var value = trimmed.Substring(5).Trim().Trim('"', '\'');
                        if (value.Length > 0)
                        {
                            return value;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                VaultLog.Warn("读主题 " + themeDir + " 的 extension.yaml 失败：" + ex.Message);
            }

            return null;
        }

        private static string ReadVersion(string themeDir)
        {
            var yaml = Path.Combine(themeDir, "extension.yaml");
            if (!File.Exists(yaml))
            {
                return string.Empty;
            }

            try
            {
                foreach (var line in File.ReadAllLines(yaml))
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("Version:", StringComparison.OrdinalIgnoreCase))
                    {
                        return trimmed.Substring(8).Trim().Trim('"', '\'');
                    }
                }
            }
            catch (Exception)
            {
            }

            return string.Empty;
        }

        private static void Measure(string dir, out long bytes, out int files)
        {
            bytes = 0;
            files = 0;

            IEnumerable<string> all;
            try
            {
                all = Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                VaultLog.Warn("扫描主题目录失败（" + dir + "）：" + ex.Message);
                return;
            }

            foreach (var file in all)
            {
                try
                {
                    bytes += new FileInfo(file).Length;
                    files++;
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        // ---------------------------------------------------------------- 远端

        /// <summary>读远端 themes/index.json。远端还没有这个文件时返回空表（不是错误）。</summary>
        public static List<ThemeCatalogItem> ListRemote(WebDavClient client)
        {
            var items = new List<ThemeCatalogItem>();
            if (client == null)
            {
                return items;
            }

            string json;
            try
            {
                json = client.DownloadString("themes/index.json");
            }
            catch (Exception ex)
            {
                if (ThemeSyncEngine.IsNotFound(ex))
                {
                    return items;      // 还没同步过主题
                }

                throw;
            }

            if (string.IsNullOrWhiteSpace(json))
            {
                return items;
            }

            var index = Newtonsoft.Json.JsonConvert.DeserializeObject<ThemeIndex>(json);
            if (index == null || index.Themes == null)
            {
                return items;
            }

            foreach (var entry in index.Themes)
            {
                items.Add(new ThemeCatalogItem
                {
                    Mode = entry.Mode,
                    Id = entry.Id,
                    Name = string.IsNullOrWhiteSpace(entry.Name) ? entry.Id : entry.Name,
                    Version = entry.Version ?? string.Empty,
                    RemoteBytes = entry.Bytes,
                    RemoteFiles = entry.Files,
                    Remote = true
                });
            }

            return items;
        }

        // ---------------------------------------------------------------- 合并

        /// <summary>按「模式/Id」合并两份列表；本地为主，远端独有的补进来。</summary>
        public static List<ThemeCatalogItem> Merge(List<ThemeCatalogItem> local,
            List<ThemeCatalogItem> remote)
        {
            var byKey = new Dictionary<string, ThemeCatalogItem>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();

            if (local != null)
            {
                foreach (var item in local)
                {
                    if (!byKey.ContainsKey(item.Key))
                    {
                        byKey[item.Key] = item;
                        order.Add(item.Key);
                    }
                }
            }

            if (remote != null)
            {
                foreach (var item in remote)
                {
                    ThemeCatalogItem existing;
                    if (byKey.TryGetValue(item.Key, out existing))
                    {
                        existing.Remote = true;
                        existing.RemoteBytes = item.RemoteBytes;
                        existing.RemoteFiles = item.RemoteFiles;
                        if (string.IsNullOrWhiteSpace(existing.Name) || existing.Name == existing.Id)
                        {
                            existing.Name = item.Name;
                        }

                        if (string.IsNullOrWhiteSpace(existing.Version))
                        {
                            existing.Version = item.Version;
                        }

                        continue;
                    }

                    byKey[item.Key] = item;
                    order.Add(item.Key);
                }
            }

            var merged = new List<ThemeCatalogItem>(order.Count);
            foreach (var key in order)
            {
                merged.Add(byKey[key]);
            }

            return merged;
        }
    }
}
