using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using PlayniteVault.Models;

namespace PlayniteVault.Services
{
    /// <summary>
    /// 嗅探出来的一个候选存档位置。
    ///
    /// <para><b>候选永远只是候选</b>：嗅探负责「把可能性摆到桌面上并说明为什么」，
    /// 写成正式的路径定义必须由用户勾选确认。理由很直白 —— 猜错的后果是把
    /// 一整个 <c>Cache</c> 目录打包上传，或者更糟：恢复的时候覆盖掉真正的存档。</para>
    /// </summary>
    public class SaveSniffCandidate
    {
        /// <summary>本机绝对路径（用来跑分数、算体积、去重）。</summary>
        public string AbsolutePath { get; set; } = string.Empty;

        /// <summary>折叠成 token 的形式。折不动就为空 —— 为空的一律建议用户确认后再存。</summary>
        public string TokenPath { get; set; } = string.Empty;

        public SaveElementType Type { get; set; } = SaveElementType.Directory;

        /// <summary>建议的 Title（跨机器对齐键）。默认取目录/文件名。</summary>
        public string Title { get; set; } = string.Empty;

        /// <summary>哪一档嗅出来的：<see cref="SaveSniffSource"/>。</summary>
        public string Source { get; set; } = SaveSniffSource.Heuristic;

        /// <summary>分数越高越可能是存档。只用来排序，不代表「确认」。</summary>
        public int Score { get; set; }

        /// <summary>给出这个分数的理由，界面上逐条显示。**不能只有分数没有理由。**</summary>
        public List<string> Reasons { get; set; } = new List<string>();

        public long Bytes { get; set; }

        public int FileCount { get; set; }

        public DateTime LastWriteUtc { get; set; }

        /// <summary>能不能折叠成 token（能的话换台机器照样可用）。</summary>
        public bool Adaptable
        {
            get { return !string.IsNullOrEmpty(TokenPath) && !SavePathAdapter.HasToken(AbsolutePath); }
        }

        /// <summary>
        /// 本机展开不了的占位符清单（空 = 全都能展开）。
        /// <para>**不能**用「里面有没有 <c>{Xxx}</c> 的形状」来判 —— <c>{GameDir}</c>
        /// 明明是能展开的，拿形状去判会把所有正常候选都标成「有问题」。</para>
        /// </summary>
        public List<string> UnresolvedTokens { get; set; } = new List<string>();

        /// <summary>路径里有没有本机展开不了的东西（含 <c>{{p|hkcu}}</c> 这类没映射到的模板）。</summary>
        public bool HasUnresolved
        {
            get { return UnresolvedTokens.Count > 0; }
        }

        /// <summary>落成路径定义时该写的那个字符串：优先 token 形式。</summary>
        public string EffectivePath
        {
            get { return string.IsNullOrEmpty(TokenPath) ? AbsolutePath : TokenPath; }
        }

        public SavePathSpec ToSpec()
        {
            return new SavePathSpec
            {
                Title = string.IsNullOrWhiteSpace(Title) ? FallbackTitle() : Title,
                Type = Type,
                Path = EffectivePath,
                AutoAdaptive = Adaptable,
                Enabled = true,
                Source = SaveSource.Sniffed,
                Comment = string.Join("；", Reasons.ToArray())
            };
        }

        private string FallbackTitle()
        {
            var path = EffectivePath.Replace('/', '\\').TrimEnd('\\');
            var slash = path.LastIndexOf('\\');
            return slash >= 0 ? path.Substring(slash + 1) : path;
        }

        public string Describe()
        {
            var sb = new StringBuilder();
            sb.Append(Score.ToString(CultureInfo.InvariantCulture).PadLeft(4)).Append("  ");
            sb.Append(EffectivePath);
            sb.Append("　[").Append(Source).Append(']');
            if (FileCount > 0 || Bytes > 0)
            {
                sb.Append("　").Append(FileCount).Append(" 文件 / ")
                  .Append(SyncProgress.FormatSize(Bytes));
            }

            if (HasUnresolved)
            {
                sb.Append("　⚠ 有本机解析不了的占位符");
            }
            else if (!Adaptable)
            {
                sb.Append("　⚠ 折不成 token，换机器会失效");
            }

            if (Reasons.Count > 0)
            {
                sb.Append(Environment.NewLine).Append("      ").Append(string.Join("；", Reasons.ToArray()));
            }

            return sb.ToString();
        }
    }

    /// <summary>嗅探的来源标签（比 <see cref="SaveSource"/> 细一档，只用于显示与排序）。</summary>
    public static class SaveSniffSource
    {
        public const string Heuristic = "常见位置";
        public const string Session = "会话差分";
        public const string PcgamingWiki = "PCGamingWiki";
    }

    /// <summary>嗅探的输入。</summary>
    public class SaveSniffContext
    {
        public string GameName { get; set; } = string.Empty;

        public string GameInstallDir { get; set; } = string.Empty;

        /// <summary>可执行文件名（去扩展名），有时比游戏名更贴近目录名。</summary>
        public List<string> ExecutableNames { get; set; } = new List<string>();

        /// <summary>已经配过的路径（绝对或 token 形式都行），用来把候选去重。</summary>
        public List<string> KnownPaths { get; set; } = new List<string>();

        /// <summary>整轮嗅探的时间预算（启发式要扫很多目录，必须有上限）。</summary>
        public TimeSpan Budget { get; set; } = TimeSpan.FromSeconds(6);

        /// <summary>只保留分数 ≥ 这个值的候选。0 = 全留（供调试）。</summary>
        public int MinScore { get; set; } = 30;

        /// <summary>每档最多返回几条。</summary>
        public int MaxPerTier { get; set; } = 20;
    }

    /// <summary>会话差分用的指纹条：只记大小与修改时间，不读内容。</summary>
    public class FileStamp
    {
        public long Size { get; set; }
        public long Ticks { get; set; }

        public bool SameAs(FileStamp other)
        {
            return other != null && Size == other.Size && Ticks == other.Ticks;
        }
    }

    /// <summary>
    /// 一组目录的轻量指纹：<c>绝对路径 → (大小, 修改时间)</c>。
    /// 会话差分就靠它 —— 不动驱动、不挂句柄，代价可控。
    /// </summary>
    public class DirectoryFingerprint
    {
        public string Root { get; set; } = string.Empty;
        public Dictionary<string, FileStamp> Entries { get; set; }
            = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);

        /// <summary>因为深度/数量/时间上限被打断过 —— 这一轮的差分结果要打折看待。</summary>
        public bool Truncated { get; set; }

        public int Count { get { return Entries.Count; } }
    }

    /// <summary>指纹采集的上限。刻意都设成小值：嗅探是「顺手看一眼」，不能变成一次全盘扫描。</summary>
    public class SaveSniffLimits
    {
        public int MaxDepth { get; set; } = 3;
        public int MaxEntries { get; set; } = 40000;
        public int MaxMilliseconds { get; set; } = 4000;

        public static SaveSniffLimits Default { get { return new SaveSniffLimits(); } }
    }

    /// <summary>
    /// 存档路径嗅探，三档（对应 <c>docs/cloud-save.md</c> §7）：
    ///
    /// <list type="number">
    /// <item><b>常见位置启发式</b> —— 按游戏名去几个标准位置找同名目录并打分。最便宜。</item>
    /// <item><b>会话差分</b> —— 游戏启动前记一份 mtime/size 指纹，退出后比对，
    ///   把「整体都变了」的那个目录当成候选。复刻 Playnite 嗅探器的工程化版本。</item>
    /// <item><b>PCGamingWiki</b> —— 抓页面上的 <c>Game data/saves</c> 行，把 <c>{{p|...}}</c>
    ///   翻成我们的 token。抓不到就降级，只用前两档。</item>
    /// </list>
    ///
    /// <para>整个类**不碰网络、不碰 Playnite**（PCGamingWiki 的取文本由
    /// <see cref="PcgamingWikiClient"/> 负责），所以三段逻辑都能在自检里直接喂样本跑。</para>
    /// </summary>
    public static class SaveSniffer
    {
        /// <summary>看起来像存档/配置的扩展名。用来判断「一个目录里装的是不是存档」。</summary>
        private static readonly HashSet<string> SaveishExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".sav", ".sav2", ".sav3", ".save", ".savx", ".sl2", ".ess", ".fos", ".wld",
                ".dat", ".bin", ".json", ".xml", ".ini", ".cfg", ".config", ".profile",
                ".db", ".sqlite", ".sqlite3", ".slot", ".msv", ".rvdata2", ".rpgsave",
                ".unity3d", ".pak", ".opt", ".settings", ".keybinds", ".sfv", ".usp"
            };

        /// <summary>目录名里出现这些词基本就不是存档（缓存/日志/崩溃转储）。</summary>
        private static readonly string[] DenyWords =
        {
            "cache", "caches", "cacheddata", "logs", "log", "crashdumps", "crashpad",
            "shadercache", "gpucache", "code cache", "webcache", "dxcache", "tmp", "temp",
            "backup", "backups", "crash", "dumps", "telemetry", "updates", "installer",
            "shader", "browser", "cef", "gpu", "blob_storage", "indexeddb", "local storage",
            "session storage", "service worker", "cachestorage", "miniupdater"
        };

        /// <summary>目录名里出现这些词就强烈暗示是存档。</summary>
        private static readonly string[] SaveWords =
        {
            "save", "saves", "savegame", "savegames", "savedgame", "savedgames", "saved",
            "profile", "profiles", "userdata", "user data", "playerdata", "player data",
            "存档", "存档数据", "用户数据", "セーブ"
        };

        /// <summary>游戏名里的噪声词：参与「像不像」比较时要丢掉。</summary>
        private static readonly HashSet<string> NameNoise =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "the", "of", "a", "an", "and", "or", "to", "in", "on", "for", "with",
                "edition", "definitive", "ultimate", "deluxe", "complete", "remastered",
                "remaster", "goty", "hd", "gold", "enhanced", "special", "game", "games",
                "demo", "trial", "beta", "windows", "win64", "win32", "x64", "x86",
                "steam", "gog", "epic", "retail", "rip", "repack", "portable"
            };

        // ================================================================ 档位 1：常见位置启发式

        /// <summary>
        /// 「标准位置 + 同名目录」启发式。
        ///
        /// <para>扫的根刻意都限了深度：<c>%LOCALAPPDATA%</c> 这种目录下面可能有几万个文件夹，
        /// 不限深度就是一次全盘遍历。深度 2 正好对应 <c>&lt;根&gt;\&lt;厂商&gt;\&lt;游戏&gt;</c>
        /// 这个最常见的布局。</para>
        /// </summary>
        public static List<SaveSniffCandidate> Heuristic(SaveSniffContext ctx)
        {
            ctx = ctx ?? new SaveSniffContext();
            var result = new List<SaveSniffCandidate>();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var tokens = GameNameTokens(ctx);

            foreach (var probe in ProbeRoots(ctx.GameInstallDir))
            {
                if (watch.Elapsed > ctx.Budget)
                {
                    break;
                }

                foreach (var dir in EnumerateDirectories(probe.Key, probe.Value, watch, ctx.Budget))
                {
                    if (watch.Elapsed > ctx.Budget)
                    {
                        break;
                    }

                    var candidate = Score(ctx, dir, tokens);
                    if (candidate != null && candidate.Score >= ctx.MinScore)
                    {
                        result.Add(candidate);
                        foreach (var file in SingleSaveFiles(dir))
                        {
                            result.Add(file);
                        }
                    }
                }
            }

            return Dedupe(result, ctx).Take(ctx.MaxPerTier).ToList();
        }

        /// <summary>要扫的根 + 每个根的深度上限。顺序无所谓，后面靠分数排。</summary>
        private static List<KeyValuePair<string, int>> ProbeRoots(string gameInstallDir)
        {
            var roots = new List<KeyValuePair<string, int>>();
            var profile = SavePathAdapter.Roots(null).ToDictionary(p => p.Key, p => p.Value,
                StringComparer.OrdinalIgnoreCase);

            AddRoot(roots, profile, SavePathAdapter.SavedGames, 1);
            AddRoot(roots, profile, SavePathAdapter.LocalAppDataLow, 2);
            AddRoot(roots, profile, SavePathAdapter.LocalAppData, 2);
            AddRoot(roots, profile, SavePathAdapter.WinAppData, 2);
            AddRoot(roots, profile, SavePathAdapter.ProgramData, 2);

            string docs;
            if (profile.TryGetValue(SavePathAdapter.Documents, out docs) && !string.IsNullOrEmpty(docs))
            {
                // 「文档」下的布局千奇百怪：Documents\<游戏>、Documents\My Games\<游戏>、
                // Documents\<厂商>\<游戏>。深度 2 能覆盖前两种。
                roots.Add(new KeyValuePair<string, int>(docs, 2));
            }

            if (!string.IsNullOrWhiteSpace(gameInstallDir))
            {
                // 游戏自己的目录里放存档的老游戏（depth 3 够到 <游戏>\Saves\<角色>）
                roots.Add(new KeyValuePair<string, int>(gameInstallDir.TrimEnd('\\', '/'), 3));
            }

            return roots
                .Where(p => !string.IsNullOrWhiteSpace(p.Key))
                .GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(x => x.Value).First())
                .ToList();
        }

        private static void AddRoot(List<KeyValuePair<string, int>> into,
            Dictionary<string, string> profile, string token, int depth)
        {
            string root;
            if (profile.TryGetValue(token, out root) && !string.IsNullOrWhiteSpace(root))
            {
                into.Add(new KeyValuePair<string, int>(root, depth));
            }
        }

        /// <summary>广度优先列子目录，带上深度与时间预算。超出上限就停（不抛）。</summary>
        private static IEnumerable<string> EnumerateDirectories(string root, int maxDepth,
            System.Diagnostics.Stopwatch watch, TimeSpan budget)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                yield break;
            }

            var queue = new Queue<KeyValuePair<string, int>>();
            queue.Enqueue(new KeyValuePair<string, int>(root, 0));

            while (queue.Count > 0)
            {
                if (watch.Elapsed > budget)
                {
                    yield break;
                }

                var current = queue.Dequeue();
                string[] subs;
                try
                {
                    subs = Directory.GetDirectories(current.Key);
                }
                catch
                {
                    continue;
                }

                foreach (var sub in subs)
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

                    yield return sub;

                    if (current.Value + 1 < maxDepth)
                    {
                        queue.Enqueue(new KeyValuePair<string, int>(sub, current.Value + 1));
                    }
                }
            }
        }

        /// <summary>给一个目录打分。分不够（或命中黑名单）就返回 null。</summary>
        private static SaveSniffCandidate Score(SaveSniffContext ctx, string dir, List<string> tokens)
        {
            var name = Path.GetFileName(dir.TrimEnd('\\', '/'));
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var lower = name.ToLowerInvariant();
            var reasons = new List<string>();
            var score = 0;

            foreach (var deny in DenyWords)
            {
                if (lower.IndexOf(deny, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    // 直接出局：把一个 Cache 目录当存档上传比漏掉存档更糟
                    return null;
                }
            }

            var normalized = Normalize(name);
            int nameScore;
            if (tokens.Count > 0 && normalized.Length > 0 && tokens.Contains(normalized))
            {
                nameScore = 40;
                reasons.Add("目录名与游戏名一致");
            }
            else
            {
                var hit = tokens.FirstOrDefault(t => t.Length >= 4
                    && (normalized.Contains(t) || t.Contains(normalized) && normalized.Length >= 4));
                if (hit != null)
                {
                    nameScore = 22;
                    reasons.Add("目录名与游戏名相关（" + hit + "）");
                }
                else
                {
                    nameScore = 0;
                }
            }

            score += nameScore;

            var saveish = SaveWords.Any(w => lower.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0);
            if (saveish)
            {
                score += 18;
                reasons.Add("目录名含存档关键字");
            }

            if (nameScore == 0 && !saveish)
            {
                // 既不沾游戏名、又不含存档词：分数永远上不去，早点退出省一次枚举
                return null;
            }

            long bytes;
            int files;
            DateTime lastWrite;
            if (!Stat(dir, out files, out bytes, out lastWrite))
            {
                return null;
            }

            if (files == 0)
            {
                return null;
            }

            var age = DateTime.UtcNow - lastWrite;
            if (age <= TimeSpan.FromDays(7))
            {
                score += 22;
                reasons.Add("最近 7 天内有改动");
            }
            else if (age <= TimeSpan.FromDays(30))
            {
                score += 12;
                reasons.Add("最近 30 天内有改动");
            }
            else if (age <= TimeSpan.FromDays(365))
            {
                score += 4;
            }

            if (files <= 5000)
            {
                score += 6;
            }
            else if (files > 50000)
            {
                score -= 25;
                reasons.Add("文件数异常多（" + files + "），可能不是存档");
            }
            else
            {
                score -= 6;
            }

            if (HasSaveishFile(dir))
            {
                score += 10;
                reasons.Add("里面有存档/配置类文件");
            }

            if (!string.IsNullOrWhiteSpace(ctx.GameInstallDir)
                && SavePathAdapter.StartsWithRoot(dir, ctx.GameInstallDir))
            {
                score += saveish ? 10 : -8;
                reasons.Add(saveish ? "就在游戏目录下的存档子目录" : "在游戏目录下但不像存档");
            }

            // Documents 与 Saved Games 是最正统的两个位置，同样条件下优先
            if (IsUnder(dir, SavePathAdapter.SavedGames, ctx.GameInstallDir)
                || IsUnder(dir, SavePathAdapter.Documents, ctx.GameInstallDir))
            {
                score += 8;
                reasons.Add("位于「保存的游戏」或「文档」");
            }

            var token = SavePathAdapter.Collapse(dir, ctx.GameInstallDir);
            if (SavePathAdapter.HasToken(token))
            {
                reasons.Add("可折叠成自适应路径：" + token);
            }
            else
            {
                reasons.Add("折不成 token，换机器会失效");
            }

            return new SaveSniffCandidate
            {
                AbsolutePath = dir,
                TokenPath = token,
                Type = SaveElementType.Directory,
                Title = name,
                Source = SaveSniffSource.Heuristic,
                Score = score,
                Reasons = reasons,
                Bytes = bytes,
                FileCount = files,
                LastWriteUtc = lastWrite
            };
        }

        /// <summary>目录里只有孤零零一个存档文件时，顺便给一条 File 型候选。</summary>
        private static IEnumerable<SaveSniffCandidate> SingleSaveFiles(string dir)
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(dir);
            }
            catch
            {
                yield break;
            }

            if (files.Length != 1 || !SaveishExtensions.Contains(Path.GetExtension(files[0])))
            {
                yield break;
            }

            var info = new FileInfo(files[0]);
            var collapsed = SavePathAdapter.Collapse(files[0], null);

            yield return new SaveSniffCandidate
            {
                AbsolutePath = files[0],
                TokenPath = collapsed,
                Type = SaveElementType.File,
                Title = Path.GetFileNameWithoutExtension(files[0]),
                Source = SaveSniffSource.Heuristic,
                Score = 34,
                Reasons = new List<string> { "目录里只有一个存档文件，按单文件存档给出" },
                Bytes = info.Length,
                FileCount = 1,
                LastWriteUtc = info.LastWriteTimeUtc
            };
        }

        private static bool IsUnder(string dir, string token, string gameInstallDir)
        {
            var roots = SavePathAdapter.Roots(gameInstallDir);
            for (var i = 0; i < roots.Count; i++)
            {
                if (string.Equals(roots[i].Key, token, StringComparison.OrdinalIgnoreCase))
                {
                    return SavePathAdapter.StartsWithRoot(dir, roots[i].Value);
                }
            }

            return false;
        }

        /// <summary>数文件、累计体积、取最近的修改时间。上限内看一眼就够。</summary>
        private static bool Stat(string dir, out int files, out long bytes, out DateTime lastWrite)
        {
            files = 0;
            bytes = 0;
            lastWrite = DateTime.MinValue;

            var stack = new Stack<string>();
            stack.Push(dir);

            try
            {
                while (stack.Count > 0)
                {
                    var current = stack.Pop();
                    foreach (var file in Directory.GetFiles(current))
                    {
                        try
                        {
                            var info = new FileInfo(file);
                            files++;
                            bytes += info.Length;

                            // 「目录的最后活动」取所有文件里最新的那个，比目录自身的 LastWriteTime 准
                            if (info.LastWriteTimeUtc > lastWrite)
                            {
                                lastWrite = info.LastWriteTimeUtc;
                            }
                        }
                        catch
                        {
                            // 单个文件读不到就跳过，不影响整体判断
                        }

                        if (files > 200000)
                        {
                            return true;
                        }
                    }

                    foreach (var sub in Directory.GetDirectories(current))
                    {
                        try
                        {
                            if ((new DirectoryInfo(sub).Attributes & FileAttributes.ReparsePoint) == 0)
                            {
                                stack.Push(sub);
                            }
                        }
                        catch
                        {
                            // 读属性的权限都没有，直接跳过
                        }
                    }
                }
            }
            catch
            {
                return files > 0;
            }

            return true;
        }

        private static bool HasSaveishFile(string dir)
        {
            try
            {
                // 只看前两层：再看下去成本就上去了，收益很低
                var count = 0;
                var stack = new Stack<KeyValuePair<string, int>>();
                stack.Push(new KeyValuePair<string, int>(dir, 0));

                while (stack.Count > 0 && count < 200)
                {
                    var current = stack.Pop();
                    foreach (var file in Directory.GetFiles(current.Key))
                    {
                        count++;
                        if (SaveishExtensions.Contains(Path.GetExtension(file)))
                        {
                            return true;
                        }

                        if (count >= 200)
                        {
                            break;
                        }
                    }

                    if (current.Value >= 1)
                    {
                        continue;
                    }

                    foreach (var sub in Directory.GetDirectories(current.Key))
                    {
                        stack.Push(new KeyValuePair<string, int>(sub, current.Value + 1));
                    }
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        // ================================================================ 档位 2：会话差分

        /// <summary>游戏启动前记一份指纹。</summary>
        public static DirectoryFingerprint Capture(string root, SaveSniffLimits limits)
        {
            limits = limits ?? SaveSniffLimits.Default;
            var fingerprint = new DirectoryFingerprint { Root = root ?? string.Empty };
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                return fingerprint;
            }

            var watch = System.Diagnostics.Stopwatch.StartNew();
            var stack = new Stack<KeyValuePair<string, int>>();
            stack.Push(new KeyValuePair<string, int>(root, 0));

            while (stack.Count > 0)
            {
                if (watch.ElapsedMilliseconds > limits.MaxMilliseconds
                    || fingerprint.Count > limits.MaxEntries)
                {
                    fingerprint.Truncated = true;
                    break;
                }

                var current = stack.Pop();
                string[] files;
                string[] subs;

                try
                {
                    files = Directory.GetFiles(current.Key);
                    subs = Directory.GetDirectories(current.Key);
                }
                catch
                {
                    continue;
                }

                foreach (var file in files)
                {
                    try
                    {
                        var info = new FileInfo(file);
                        fingerprint.Entries[info.FullName] = new FileStamp
                        {
                            Size = info.Length,
                            Ticks = info.LastWriteTimeUtc.Ticks
                        };
                    }
                    catch
                    {
                        // 读不到就当作没这个文件
                    }
                }

                if (current.Value >= limits.MaxDepth)
                {
                    fingerprint.Truncated = true;
                    continue;
                }

                foreach (var sub in subs)
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

                    stack.Push(new KeyValuePair<string, int>(sub, current.Value + 1));
                }
            }

            return fingerprint;
        }

        /// <summary>给一组根各记一份指纹（键 = 根路径）。</summary>
        public static Dictionary<string, DirectoryFingerprint> CaptureAll(
            IEnumerable<string> roots, SaveSniffLimits limits)
        {
            var map = new Dictionary<string, DirectoryFingerprint>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(root) || map.ContainsKey(root))
                {
                    continue;
                }

                map[root] = Capture(root, limits);
            }

            return map;
        }

        /// <summary>会话差分建议监听哪些根。刻意挑「存档常落的地方」，不监听整个盘。</summary>
        public static List<string> WatchRoots(string gameInstallDir)
        {
            var roots = new List<string>();
            foreach (var probe in ProbeRoots(gameInstallDir))
            {
                roots.Add(probe.Key);
            }

            return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>
        /// 比对两份指纹，把「整块都变了」的目录挑出来当候选。
        ///
        /// <para>核心是一个**向上爬**的动作：从一个变更文件出发，只要它的父目录里
        /// 所有文件都在本轮变过，就继续往上爬 —— 爬到的顶点就是这次改动的作用域。
        /// 这正好对应「存档写在某个目录里，这轮写了几个文件」的实际情况，
        /// 不需要猜目录名，也不会有「按名字匹配」那种漏网。</para>
        /// </summary>
        public static List<SaveSniffCandidate> Diff(DirectoryFingerprint before,
            DirectoryFingerprint after, SaveSniffContext ctx)
        {
            ctx = ctx ?? new SaveSniffContext();
            var result = new List<SaveSniffCandidate>();
            if (after == null)
            {
                return result;
            }

            before = before ?? new DirectoryFingerprint();

            // 新增或改动过的文件
            var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in after.Entries)
            {
                FileStamp old;
                if (!before.Entries.TryGetValue(pair.Key, out old) || !pair.Value.SameAs(old))
                {
                    changed.Add(pair.Key);
                }
            }

            // 被删掉的文件也算「这个目录被动过」（有时游戏靠删文件推进状态）
            foreach (var pair in before.Entries)
            {
                if (!after.Entries.ContainsKey(pair.Key))
                {
                    changed.Add(pair.Key);
                }
            }

            if (changed.Count == 0)
            {
                return result;
            }

            // 每个目录下都有哪些文件（以「之后」的指纹为准，删掉的文件单独记）
            var filesByDir = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in after.Entries)
            {
                var dir = Path.GetDirectoryName(pair.Key);
                if (dir == null)
                {
                    continue;
                }

                HashSet<string> bag;
                if (!filesByDir.TryGetValue(dir, out bag))
                {
                    bag = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    filesByDir[dir] = bag;
                }

                bag.Add(pair.Key);
            }

            var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in changed)
            {
                var dir = Path.GetDirectoryName(file);
                if (string.IsNullOrEmpty(dir))
                {
                    continue;
                }

                // 往上爬：父目录里如果混着没变过的文件，说明父目录不是这次的作用域
                while (true)
                {
                    var parent = Path.GetDirectoryName(dir);
                    if (string.IsNullOrEmpty(parent) || !IsInside(parent, after.Root))
                    {
                        break;
                    }

                    HashSet<string> bag;
                    if (!filesByDir.TryGetValue(parent, out bag) || bag.Count == 0)
                    {
                        break;
                    }

                    var allChanged = bag.All(f => changed.Contains(f));
                    if (!allChanged)
                    {
                        break;
                    }

                    dir = parent;
                }

                List<string> list;
                if (!groups.TryGetValue(dir, out list))
                {
                    list = new List<string>();
                    groups[dir] = list;
                }

                list.Add(file);
            }

            var ordered = groups
                .OrderByDescending(g => g.Value.Count)
                .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Take(ctx.MaxPerTier <= 0 ? 20 : ctx.MaxPerTier)
                .ToList();

            foreach (var group in ordered)
            {
                long bytes;
                int files;
                DateTime lastWrite;
                if (!Stat(group.Key, out files, out bytes, out lastWrite) || files == 0)
                {
                    continue;
                }

                // 变更文件数占整个目录文件数的比例：越高越确定「这个目录整体就是这次的作用域」
                var ratio = files == 0 ? 0d : (double)group.Value.Count / files;
                var score = 60 + (int)Math.Round(Math.Min(ratio, 1d) * 20);
                if (group.Value.Count >= 3)
                {
                    score += 5;
                }

                var reasons = new List<string>
                {
                    "本轮有 " + group.Value.Count + " 个文件变动（占该目录 " + files + " 个文件的 "
                    + Math.Round(ratio * 100).ToString(CultureInfo.InvariantCulture) + "%）"
                };

                if (after.Truncated)
                {
                    score -= 15;
                    reasons.Add("指纹采集被上限截断过，结论要打折");
                }

                if (SavePathAdapter.StartsWithRoot(group.Key,
                        SafeRootOf(after.Root)))
                {
                    reasons.Add("位于监听根 " + SafeRootOf(after.Root) + " 下");
                }

                var token = SavePathAdapter.Collapse(group.Key, ctx.GameInstallDir);
                reasons.Add(SavePathAdapter.HasToken(token)
                    ? "可折叠成自适应路径：" + token
                    : "折不成 token，换机器会失效");

                result.Add(new SaveSniffCandidate
                {
                    AbsolutePath = group.Key,
                    TokenPath = token,
                    Type = SaveElementType.Directory,
                    Title = Path.GetFileName(group.Key.TrimEnd('\\', '/')),
                    Source = SaveSniffSource.Session,
                    Score = score,
                    Reasons = reasons,
                    Bytes = bytes,
                    FileCount = files,
                    LastWriteUtc = lastWrite
                });
            }

            return Dedupe(result, ctx);
        }

        private static string SafeRootOf(string root)
        {
            return string.IsNullOrWhiteSpace(root) ? "\\" : root;
        }

        private static bool IsInside(string path, string root)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                return true;
            }

            return SavePathAdapter.StartsWithRoot(path, root)
                   || SavePathAdapter.StartsWithRoot(root, path);
        }

        // ================================================================ 档位 3：PCGamingWiki

        /// <summary>
        /// 从 PCGamingWiki 的页面源码里挖出存档/配置路径。
        ///
        /// <para>页面上长这样（这是模板展开前的源码）：</para>
        /// <code>
        /// {{Game data|
        /// {{Game data/saves|Windows|{{p|userprofile}}\Documents\My Games\Game}}
        /// {{Game data/config|Windows|{{p|userprofile}}\AppData\Local\Game}}
        /// }}
        /// </code>
        /// <para>所以做法是：找到模板名 → 用花括号配对找出模板边界 → 按顶层 <c>|</c> 切参数。
        /// <b>不能</b>用简单的正则切 <c>|</c>，因为路径里的 <c>{{p|userprofile}}</c> 自带竖线。</para>
        /// </summary>
        public static List<SaveSniffCandidate> FromPcgamingWiki(string wikitext, SaveSniffContext ctx)
        {
            ctx = ctx ?? new SaveSniffContext();
            var result = new List<SaveSniffCandidate>();

            foreach (var row in EnumerateGameDataRows(wikitext))
            {
                var os = row.Os ?? string.Empty;
                if (!IsSupportedOs(os))
                {
                    continue;
                }

                var raw = CleanWikiValue(row.RawPath);
                if (raw.Length == 0)
                {
                    continue;
                }

                // 注册表位置：Playnite 那边也没做，我们也不假装能做
                if (raw.StartsWith("HKEY", StringComparison.OrdinalIgnoreCase)
                    || raw.IndexOf("hkcu", StringComparison.OrdinalIgnoreCase) >= 0
                    || raw.IndexOf("hklm", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    continue;
                }

                var converted = SavePathAdapter.FromPcgamingWiki(raw).TrimEnd('\\', '/');
                if (converted.Length == 0)
                {
                    continue;
                }

                // 展开一次，把「本机到底能不能用」问清楚 —— 不能靠猜形状
                List<string> unresolvedTokens;
                var absolute = SavePathAdapter.Expand(converted, ctx.GameInstallDir, out unresolvedTokens);

                var isSaves = row.Kind.IndexOf("saves", StringComparison.OrdinalIgnoreCase) >= 0;
                var reasons = new List<string>
                {
                    "PCGamingWiki 的 " + row.Kind + " 行（" + (os.Length == 0 ? "未标平台" : os) + "）"
                };

                if (unresolvedTokens.Count > 0)
                {
                    reasons.Add("含本机解析不了的写法（" + string.Join("、", unresolvedTokens.ToArray())
                                + "），落成定义前要先确认");
                }
                else
                {
                    reasons.Add("已翻成自适应路径");
                }

                long bytes = 0;
                int files = 0;
                var lastWrite = DateTime.MinValue;

                var exists = !string.IsNullOrEmpty(absolute)
                             && (Directory.Exists(absolute) || File.Exists(absolute));
                if (exists)
                {
                    if (Directory.Exists(absolute))
                    {
                        Stat(absolute, out files, out bytes, out lastWrite);
                    }
                    else
                    {
                        files = 1;
                        try
                        {
                            var info = new FileInfo(absolute);
                            bytes = info.Length;
                            lastWrite = info.LastWriteTimeUtc;
                        }
                        catch
                        {
                            // 读不到属性而已，不影响这条候选的路径价值
                        }
                    }

                    reasons.Add("本机存在（" + files + " 个文件）");
                }
                else
                {
                    reasons.Add("本机还没有这个目录（游戏可能还没运行过）");
                }

                // 抓来的东西是「官方标注」级别的线索，但不能因为抓到了就当确认：
                // 分数高、排在前面，仍然要用户过一眼。
                var score = isSaves ? 70 : 50;
                if (exists)
                {
                    score += 8;
                }

                if (unresolvedTokens.Count > 0)
                {
                    // 用不了的写法排后面：能让它排前面，但要让用户先看到「这条要确认」
                    score -= 25;
                }

                result.Add(new SaveSniffCandidate
                {
                    AbsolutePath = absolute ?? string.Empty,
                    TokenPath = converted,
                    Type = SaveElementType.Directory,
                    Title = Path.GetFileName(converted.Replace('/', '\\').TrimEnd('\\')),
                    Source = SaveSniffSource.PcgamingWiki,
                    Score = score,
                    Reasons = reasons,
                    UnresolvedTokens = unresolvedTokens,
                    Bytes = bytes,
                    FileCount = files,
                    LastWriteUtc = lastWrite
                });
            }

            return Dedupe(result, ctx).Take(ctx.MaxPerTier).ToList();
        }

        /// <summary>PCGamingWiki 的一行 <c>Game data/xxx</c>。</summary>
        public class GameDataRow
        {
            public string Kind { get; set; } = string.Empty;
            public string Os { get; set; } = string.Empty;
            public string RawPath { get; set; } = string.Empty;
        }

        /// <summary>哪些平台字段算「Windows 上能用的」。Linux/macOS 的路径在原机器上才有意义。</summary>
        private static bool IsSupportedOs(string os)
        {
            if (string.IsNullOrWhiteSpace(os))
            {
                return true;
            }

            var lower = os.Trim().ToLowerInvariant();
            if (lower.Contains("linux") || lower.Contains("mac") || lower.Contains("os x")
                || lower.Contains("osx") || lower.Contains("dos") || lower.Contains("wine"))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// 找出所有 <c>Game data/&lt;kind&gt;</c> 模板并切成参数。
        /// 用花括号配对定边界，按顶层竖线切参数 —— 路径里的 <c>{{p|...}}</c> 才不会把参数切错。
        /// </summary>
        public static IEnumerable<GameDataRow> EnumerateGameDataRows(string wikitext)
        {
            if (string.IsNullOrEmpty(wikitext))
            {
                yield break;
            }

            var index = 0;
            const string Marker = "Game data/";

            while (index < wikitext.Length)
            {
                var hit = wikitext.IndexOf(Marker, index, StringComparison.OrdinalIgnoreCase);
                if (hit < 0)
                {
                    yield break;
                }

                index = hit + Marker.Length;

                // 模板名一直读到分隔符为止
                var nameEnd = index;
                while (nameEnd < wikitext.Length && wikitext[nameEnd] != '|' && wikitext[nameEnd] != '}')
                {
                    nameEnd++;
                }

                var kind = wikitext.Substring(hit, nameEnd - hit).Trim();

                // 只认 saves / config 这两类；Game data/row 之类是别的用途
                if (kind.IndexOf("saves", StringComparison.OrdinalIgnoreCase) < 0
                    && kind.IndexOf("config", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                var args = SplitTemplateArgs(wikitext, hit);
                if (args.Count < 3)
                {
                    continue;
                }

                yield return new GameDataRow
                {
                    Kind = kind,
                    Os = args[1].Trim(),
                    RawPath = args[2].Trim()
                };
            }
        }

        /// <summary>
        /// 从 <paramref name="start"/>（指向 <c>Game data/...</c>）往前找到所在模板的
        /// <c>{{</c>，然后按花括号配对走到 <c>}}</c>，再按顶层竖线切参数。
        /// </summary>
        private static List<string> SplitTemplateArgs(string text, int start)
        {
            var open = text.LastIndexOf("{{", start, StringComparison.Ordinal);
            if (open < 0)
            {
                return new List<string>();
            }

            var depth = 0;
            var end = -1;
            for (var i = open; i < text.Length - 1; i++)
            {
                if (text[i] == '{' && text[i + 1] == '{')
                {
                    depth++;
                    i++;
                    continue;
                }

                if (text[i] == '}' && text[i + 1] == '}')
                {
                    depth--;
                    i++;
                    if (depth == 0)
                    {
                        end = i;
                        break;
                    }
                }
            }

            if (end < 0)
            {
                // 模板没闭合（页面被截断）。退回「读到行尾」，聊胜于无。
                end = text.IndexOf('\n', open);
                if (end < 0)
                {
                    end = text.Length;
                }
            }

            var body = text.Substring(open + 2, Math.Max(0, end - open - 3));
            var args = new List<string>();
            var current = new StringBuilder();
            depth = 0;

            for (var i = 0; i < body.Length; i++)
            {
                var c = body[i];
                if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    depth = Math.Max(0, depth - 1);
                }

                if (c == '|' && depth == 0)
                {
                    args.Add(current.ToString());
                    current.Length = 0;
                    continue;
                }

                current.Append(c);
            }

            args.Add(current.ToString());
            return args;
        }

        /// <summary>清掉 wiki 标记：注释、<c>&lt;ref&gt;</c>、换行、以及行尾的说明文字。</summary>
        private static string CleanWikiValue(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            var text = Regex.Replace(raw, @"<!--.*?-->", " ", RegexOptions.Singleline);
            text = Regex.Replace(text, @"<ref[^>]*>.*?</ref>", " ", RegexOptions.Singleline);
            text = Regex.Replace(text, @"<ref[^>]*/>", " ");
            text = Regex.Replace(text, @"<[^>]+>", " ");

            // 只取第一行：多平台时后面还会跟别的写法
            var line = text.Split('\n')[0].Trim();

            // 去掉可能跟着的说明（例如 "（存档）" 之后的注释）
            var cut = line.IndexOf("//", StringComparison.Ordinal);
            if (cut >= 0)
            {
                line = line.Substring(0, cut).Trim();
            }

            return line;
        }

        // ================================================================ 收尾

        /// <summary>去掉与已配路径重复的、以及被别的候选包含的（留范围大的那个）。</summary>
        private static List<SaveSniffCandidate> Dedupe(List<SaveSniffCandidate> candidates,
            SaveSniffContext ctx)
        {
            var known = new HashSet<string>(
                (ctx.KnownPaths ?? new List<string>())
                    .Where(p => !string.IsNullOrWhiteSpace(p))
                    .Select(NormalizeForCompare),
                StringComparer.OrdinalIgnoreCase);

            var kept = new List<SaveSniffCandidate>();

            foreach (var candidate in candidates
                .OrderByDescending(c => c.Score)
                .ThenBy(c => c.EffectivePath, StringComparer.OrdinalIgnoreCase))
            {
                var key = NormalizeForCompare(candidate.EffectivePath);
                if (key.Length == 0 || known.Contains(key))
                {
                    continue;
                }

                // 已经被留下的候选覆盖了（同一条路径、或它是别人的子目录）就丢掉
                var covered = kept.Any(k =>
                    string.Equals(NormalizeForCompare(k.EffectivePath), key, StringComparison.OrdinalIgnoreCase)
                    || (k.Type == SaveElementType.Directory && candidate.Type == SaveElementType.Directory
                        && !string.IsNullOrEmpty(candidate.AbsolutePath)
                        && SavePathAdapter.StartsWithRoot(candidate.AbsolutePath, k.AbsolutePath)));

                if (covered)
                {
                    continue;
                }

                known.Add(key);
                kept.Add(candidate);
            }

            return kept;
        }

        private static string NormalizeForCompare(string path)
        {
            return (path ?? string.Empty).Trim().Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
        }

        /// <summary>把游戏名切成参与比较的词（全小写、去噪声词、去版本号）。</summary>
        public static List<string> GameNameTokens(SaveSniffContext ctx)
        {
            var tokens = new List<string>();
            var sources = new List<string> { ctx == null ? null : ctx.GameName };
            if (ctx != null && ctx.ExecutableNames != null)
            {
                sources.AddRange(ctx.ExecutableNames);
            }

            if (ctx != null && !string.IsNullOrWhiteSpace(ctx.GameInstallDir))
            {
                try
                {
                    sources.Add(Path.GetFileName(ctx.GameInstallDir.TrimEnd('\\', '/')));
                }
                catch
                {
                    // 目录名读不出来就算了，不差这一个词源
                }
            }

            foreach (var source in sources)
            {
                if (string.IsNullOrWhiteSpace(source))
                {
                    continue;
                }

                foreach (var piece in Regex.Split(source, @"[^A-Za-z0-9\u4e00-\u9fff]+"))
                {
                    if (piece.Length == 0)
                    {
                        continue;
                    }

                    var token = Normalize(piece);
                    if (token.Length == 0 || NameNoise.Contains(token) || tokens.Contains(token))
                    {
                        continue;
                    }

                    // 纯数字（版本号/年份）对得上也说明不了什么
                    if (token.All(char.IsDigit))
                    {
                        continue;
                    }

                    tokens.Add(token);
                }
            }

            return tokens;
        }

        private static string Normalize(string text)
        {
            return (text ?? string.Empty).Trim().ToLowerInvariant()
                .Replace(" ", string.Empty).Replace("_", string.Empty).Replace("-", string.Empty)
                .Replace(".", string.Empty).Replace("'", string.Empty).Replace("&", string.Empty);
        }
    }
}
