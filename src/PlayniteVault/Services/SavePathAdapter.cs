using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using PlayniteVault.Models;

namespace PlayniteVault.Services
{
    /// <summary>
    /// 存档路径的自适应展开 / 反向折叠。
    ///
    /// <para><b>为什么需要它</b>：存档路径里几乎一定带着机器相关的部分 ——
    /// <c>C:\Users\Administrator\AppData\LocalLow\Team Cherry\Hollow Knight</c>。
    /// 把这种绝对路径原样存进 NAS，换台机器（用户名不同、盘符不同）就直接失效，
    /// 「云存档」也就名存实亡。所以存进去的必须是 <b>token 形式</b>，
    /// 读出来的时候再按当前机器展开。</para>
    ///
    /// <para><b>为什么不直接用 Playnite 的 <c>AutoAdaptivePathHelper</c></b>：
    /// 那个类**是公开的**（<c>Playnite.SDK.AutoAdaptivePathHelper</c>，
    /// 有 <c>TransformPath1(path, autoAdaptive, installDirectory)</c> 与 <c>ReplaceStart</c>），
    /// 但它没有公开的 token 契约 —— 到底支持哪些占位符、怎么折叠，只能靠反编译猜。
    /// 我们把它当**参考**（读出结果供界面显示），写入一律用下面这张自己的表：
    /// 出一份自己展开得了的路径，比对齐一个猜来的语法重要得多。</para>
    ///
    /// <para>PCGamingWiki 的路径变量（<c>{{p|userprofile}}\AppData\LocalLow\...</c>）
    /// 正好能映射到这张表上，所以从它那儿抓来的路径可以直接落成 token 形式 ——
    /// 见 <see cref="FromPcgamingWiki"/>。</para>
    /// </summary>
    public static class SavePathAdapter
    {
        public const string UserProfile = "{UserProfile}";
        public const string WinAppData = "{WinAppData}";
        public const string LocalAppData = "{LocalAppData}";
        public const string LocalAppDataLow = "{LocalAppDataLow}";
        public const string Documents = "{Documents}";
        public const string SavedGames = "{SavedGames}";
        public const string ProgramData = "{ProgramData}";
        public const string GameDir = "{GameDir}";

        /// <summary>不认识但长得像 token 的东西：<c>{Xxx}</c>。</summary>
        private static readonly Regex TokenShape = new Regex(@"\{[A-Za-z][A-Za-z0-9_]*\}",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// PCGamingWiki 里我们没映射到的模板：<c>{{p|hkcu}}</c>、<c>{{p|steam}}</c> 之类。
        /// <para>它**不是** <c>{Xxx}</c> 的形状，所以只查 <see cref="TokenShape"/> 会漏掉它 ——
        /// 漏掉的后果是把一个名叫 <c>{{p|hkcu}}</c> 的目录真建出来。必须一起拦。</para>
        /// </summary>
        private static readonly Regex UnresolvedShape = new Regex(@"\{\{[^{}]*\}\}",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>Token 表的展示顺序（也是界面里说明用的一行）。</summary>
        public static readonly string[] KnownTokens =
        {
            GameDir, LocalAppDataLow, WinAppData, LocalAppData,
            SavedGames, Documents, ProgramData, UserProfile
        };

        /// <summary>
        /// 当前机器上每个 token 对应的根目录；取不到的（比如没装 .NET 的 SavedGames）
        /// 返回 null，调用方要当「未解析」处理而不是当成空串。
        ///
        /// 顺序**按根目录长度从长到短**：折叠时先命中最长的那个，
        /// 否则 <c>{UserProfile}</c> 会把 <c>...\AppData\LocalLow</c> 也吞进去，
        /// 一份在 LocalLow 里的路径就被折叠成 <c>{UserProfile}\AppData\LocalLow\...</c>，
        /// 换台机器照样能用但看起来很不专业，也丢掉了「这是 LocalLow」这个信息。
        /// </summary>
        public static List<KeyValuePair<string, string>> Roots(string gameInstallDir)
        {
            var list = new List<KeyValuePair<string, string>>();

            Add(list, GameDir, gameInstallDir);
            Add(list, LocalAppDataLow, Combine(UserProfilePath(), "AppData", "LocalLow"));
            Add(list, WinAppData, SafeGet(Environment.SpecialFolder.ApplicationData));
            Add(list, LocalAppData, SafeGet(Environment.SpecialFolder.LocalApplicationData));
            Add(list, SavedGames, Combine(UserProfilePath(), "Saved Games"));
            Add(list, Documents, SafeGet(Environment.SpecialFolder.MyDocuments));
            Add(list, ProgramData, SafeGet(Environment.SpecialFolder.CommonApplicationData));
            Add(list, UserProfile, UserProfilePath());

            return list
                .Where(p => !string.IsNullOrWhiteSpace(p.Value))
                .OrderByDescending(p => Trim(p.Value).Length)
                .ToList();
        }

        private static void Add(List<KeyValuePair<string, string>> list, string token, string root)
        {
            if (!string.IsNullOrWhiteSpace(root))
            {
                list.Add(new KeyValuePair<string, string>(token, Trim(root)));
            }
        }

        private static string SafeGet(Environment.SpecialFolder folder)
        {
            try
            {
                var value = Environment.GetFolderPath(folder);
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
            catch
            {
                return null;
            }
        }

        private static string UserProfilePath()
        {
            var fromFolder = SafeGet(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(fromFolder))
            {
                return fromFolder;
            }

            var fromVar = Environment.GetEnvironmentVariable("USERPROFILE");
            if (!string.IsNullOrWhiteSpace(fromVar))
            {
                return fromVar;
            }

            return Environment.GetEnvironmentVariable("HOMEDRIVE") is string drive
                   && Environment.GetEnvironmentVariable("HOMEPATH") is string home
                ? drive + home
                : null;
        }

        private static string Combine(string root, params string[] parts)
        {
            return string.IsNullOrWhiteSpace(root) ? null : Path.Combine(root, Path.Combine(parts));
        }

        private static string Trim(string path)
        {
            return (path ?? string.Empty).TrimEnd('\\', '/');
        }

        /// <summary>
        /// 展开成当前机器上的绝对路径。
        /// <paramref name="unresolved"/> 收下**没展开成功**的 token（不认识的、或本机取不到根的）——
        /// 这种路径绝不能拿去读写文件，调用方必须先把它们挡掉。
        /// </summary>
        public static string Expand(string path, string gameInstallDir, out List<string> unresolved)
        {
            unresolved = new List<string>();
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            var result = path.Trim();
            var roots = Roots(gameInstallDir);
            var byToken = roots.ToDictionary(p => p.Key, p => p.Value,
                StringComparer.OrdinalIgnoreCase);

            foreach (var token in KnownTokens)
            {
                if (result.IndexOf(token, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                string root;
                if (!byToken.TryGetValue(token, out root) || string.IsNullOrWhiteSpace(root))
                {
                    // 本机取不到这个根（例如没有「保存的游戏」目录、或游戏没装所以没有 GameDir）
                    if (!unresolved.Contains(token, StringComparer.OrdinalIgnoreCase))
                    {
                        unresolved.Add(token);
                    }
                    continue;
                }

                result = ReplaceToken(result, token, root);
            }

            // 剩下的 {Xxx}：不认识的 token。不猜，标出来。
            CollectTokenShapes(result, unresolved);

            if (unresolved.Count > 0)
            {
                return string.Empty;
            }

            return NormalizeSeparators(result);
        }

        /// <summary>展开的重载：不关心未解析项时用。</summary>
        public static string Expand(string path, string gameInstallDir)
        {
            List<string> ignored;
            return Expand(path, gameInstallDir, out ignored);
        }

        /// <summary>
        /// 反向折叠：把绝对路径开头命中某个已知根的部分换成 token。
        /// 已经带 token 的原样返回（幂等）。折不动就原样返回 —— **不硬凑**。
        /// </summary>
        public static string Collapse(string absolutePath, string gameInstallDir)
        {
            if (string.IsNullOrWhiteSpace(absolutePath))
            {
                return string.Empty;
            }

            if (HasToken(absolutePath))
            {
                return absolutePath.Trim();
            }

            var full = absolutePath.Trim();
            var best = Roots(gameInstallDir)
                .Where(p => StartsWithRoot(full, p.Value))
                .OrderByDescending(p => p.Value.Length)
                .FirstOrDefault();

            if (best.Key == null)
            {
                return full;
            }

            var rest = full.Substring(Trim(best.Value).Length).TrimStart('\\', '/');
            return rest.Length == 0 ? best.Key : best.Key + "\\" + NormalizeSeparators(rest);
        }

        /// <summary>把一条路径定义折算成当前机器上的绝对路径。</summary>
        public static string Resolve(SavePathSpec spec, string gameInstallDir, out List<string> unresolved)
        {
            unresolved = new List<string>();
            if (spec == null || string.IsNullOrWhiteSpace(spec.Path))
            {
                return string.Empty;
            }

            if (!spec.AutoAdaptive)
            {
                var literal = NormalizeSeparators(spec.Path.Trim());
                if (HasToken(literal))
                {
                    // 明明写着 token 却没勾自适应：这多半是导入来的数据出了问题，
                    // 直接当路径用会把一个叫 "{LocalAppData}" 的目录建出来，必须拦下。
                    CollectTokenShapes(literal, unresolved);
                    return string.Empty;
                }

                return literal;
            }

            return Expand(spec.Path, gameInstallDir, out unresolved);
        }

        public static bool HasToken(string path)
        {
            return !string.IsNullOrEmpty(path)
                   && (TokenShape.IsMatch(path) || UnresolvedShape.IsMatch(path));
        }

        /// <summary>把一段文本里所有「形状像占位符」的片段收出来（含 PCGamingWiki 的 <c>{{p|..}}</c>）。</summary>
        private static void CollectTokenShapes(string text, List<string> into)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            foreach (Match m in TokenShape.Matches(text))
            {
                if (!into.Contains(m.Value, StringComparer.OrdinalIgnoreCase))
                {
                    into.Add(m.Value);
                }
            }

            foreach (Match m in UnresolvedShape.Matches(text))
            {
                if (!into.Contains(m.Value, StringComparer.OrdinalIgnoreCase))
                {
                    into.Add(m.Value);
                }
            }
        }

        /// <summary>列出一条路径里所有 token 形状的片段（含不认识的）。</summary>
        public static List<string> TokensIn(string path)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(path))
            {
                return list;
            }

            foreach (Match m in TokenShape.Matches(path))
            {
                if (!list.Contains(m.Value, StringComparer.OrdinalIgnoreCase))
                {
                    list.Add(m.Value);
                }
            }
            return list;
        }

        /// <summary>里面有没有本机展开不了的 token。</summary>
        public static bool HasUnresolved(string path, string gameInstallDir)
        {
            List<string> unresolved;
            Expand(path, gameInstallDir, out unresolved);
            return unresolved.Count > 0;
        }

        // ================================================================ PCGamingWiki

        /// <summary>PCGamingWiki 的 <c>{{p|xxx}}</c> 变量 → 我们的 token。</summary>
        private static readonly Dictionary<string, string> PcgwVariables =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "userprofile", UserProfile },
                { "appdata", WinAppData },
                { "localappdata", LocalAppData },
                { "localappdatalow", LocalAppDataLow },
                { "documents", Documents },
                { "savedgames", SavedGames },
                { "programdata", ProgramData },
                { "game", GameDir },
                { "p|game", GameDir }
            };

        private static readonly Regex PcgwTemplate =
            new Regex(@"\{\{\s*p\s*\|\s*([A-Za-z0-9_]+)\s*\}\}",
                RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// 把 PCGamingWiki 页面里的路径写法翻成我们的 token 形式。
        /// 认不出的变量保持原样（调用方会用 <see cref="TokensIn"/> 发现它没法展开）。
        /// </summary>
        public static string FromPcgamingWiki(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            var text = PcgwTemplate.Replace(raw, m =>
            {
                string token;
                return PcgwVariables.TryGetValue(m.Groups[1].Value, out token)
                    ? token
                    : m.Value;
            });

            // PCGW 的路径分隔符就是反斜杠，但网页里偶尔混正斜杠
            return NormalizeSeparators(text.Trim());
        }

        // ================================================================ 导入别人的写法

        /// <summary>
        /// Playnite 官方文档里的变量名 / Windows 的 <c>%VAR%</c> 写法 → 我们的 token。
        /// <para>实测（Playnite SDK 6.13）：<c>AutoAdaptivePathHelper</c> 的
        /// <c>GetRealPath</c> / <c>TransformPath1</c> / <c>IsAdaptivePath</c> 对
        /// <c>{WinLocalAppData}</c>、<c>%LOCALAPPDATA%</c> 等 20 多种写法**一律原样返回**，
        /// 也就是说它在这一版里是个恒等函数 —— 没有任何可对接的展开逻辑。
        /// 但用户在游戏编辑里可能真就照着文档这么写了，所以导入时把它们翻成我们的 token，
        /// 总比丢给用户一句「解析不了」强。</para>
        /// </summary>
        private static readonly Dictionary<string, string> ForeignVariables =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "userprofile", UserProfile },
                { "winuserprofile", UserProfile },
                { "appdata", WinAppData },
                { "winappdata", WinAppData },
                { "localappdata", LocalAppData },
                { "winlocalappdata", LocalAppData },
                { "localappdatalow", LocalAppDataLow },
                { "winlocalappdatalow", LocalAppDataLow },
                { "documents", Documents },
                { "windocuments", Documents },
                { "savedgames", SavedGames },
                { "winsavedgames", SavedGames },
                { "programdata", ProgramData },
                { "winprogramdata", ProgramData },
                { "gamedir", GameDir },
                { "installdir", GameDir }
            };

        private static readonly Regex ForeignBrace = new Regex(@"\{\s*([A-Za-z][A-Za-z0-9_]*)\s*\}",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex ForeignPercent = new Regex(@"%\s*([A-Za-z][A-Za-z0-9_ ]*?)\s*%",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        /// <summary>
        /// 把「别人家的」自适应写法翻成我们的 token（Playnite 文档里的 <c>{WinLocalAppData}</c>、
        /// 以及 <c>%LOCALAPPDATA%</c> 这类环境变量写法）。我们自己的 token 原样通过（幂等）。
        /// 翻不动的（例如 <c>{PlayniteDir}</c>）保持原样，由 <see cref="TokensIn"/> 报出来。
        /// </summary>
        public static string FromPlaynite(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return string.Empty;
            }

            var text = ForeignBrace.Replace(raw, m =>
            {
                string token;
                return ForeignVariables.TryGetValue(m.Groups[1].Value, out token) ? token : m.Value;
            });

            text = ForeignPercent.Replace(text, m =>
            {
                string token;
                return ForeignVariables.TryGetValue(m.Groups[1].Value, out token) ? token : m.Value;
            });

            // 「保存的游戏」在 %VAR% 世界里常常写成 %USERPROFILE%\Saved Games 两段
            var savedGames = Combine(UserProfilePath(), "Saved Games");
            if (!string.IsNullOrEmpty(savedGames))
            {
                text = ReplaceIgnoreCase(text, savedGames, SavedGames);
            }

            return NormalizeSeparators(text.Trim());
        }

        /// <summary>net462 没有 <c>string.Replace(…, StringComparison)</c>，自己来一个够用的。</summary>
        private static string ReplaceIgnoreCase(string text, string from, string to)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(from))
            {
                return text;
            }

            var result = new StringBuilder();
            var index = 0;

            while (true)
            {
                var hit = text.IndexOf(from, index, StringComparison.OrdinalIgnoreCase);
                if (hit < 0)
                {
                    result.Append(text, index, text.Length - index);
                    break;
                }

                result.Append(text, index, hit - index);
                result.Append(to);
                index = hit + from.Length;
            }

            return result.ToString();
        }

        // ================================================================ 杂项

        /// <summary>统一成当前系统的分隔符。</summary>
        public static string NormalizeSeparators(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            return path.Replace('\\', Path.DirectorySeparatorChar)
                .Replace('/', Path.DirectorySeparatorChar);
        }

        /// <summary>
        /// <paramref name="path"/> 是不是在 <paramref name="root"/> 底下（或就是它）。
        /// 边界要卡死：<c>C:\Users\Abc</c> 不能匹配 <c>C:\Users\Ab</c>。
        /// </summary>
        public static bool StartsWithRoot(string path, string root)
        {
            var p = Trim(path);
            var r = Trim(root);
            if (r.Length == 0 || p.Length < r.Length)
            {
                return false;
            }

            if (!p.StartsWith(r, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return p.Length == r.Length
                   || p[r.Length] == '\\' || p[r.Length] == '/';
        }

        private static string ReplaceToken(string text, string token, string root)
        {
            var result = new StringBuilder();
            var index = 0;

            while (true)
            {
                var hit = text.IndexOf(token, index, StringComparison.OrdinalIgnoreCase);
                if (hit < 0)
                {
                    result.Append(text, index, text.Length - index);
                    break;
                }

                result.Append(text, index, hit - index);
                result.Append(Trim(root));
                index = hit + token.Length;
            }

            return result.ToString();
        }

        /// <summary>token 表说明，给设置页 / 关于页用。</summary>
        public static string Describe()
        {
            var sb = new StringBuilder();
            sb.Append("路径里的这些写法会按当前机器展开（勾了「自适应」才生效）：");
            foreach (var token in KnownTokens)
            {
                sb.Append(Environment.NewLine).Append("  ").Append(token).Append("　");
                switch (token)
                {
                    case GameDir: sb.Append("游戏安装目录"); break;
                    case WinAppData: sb.Append("Roaming AppData"); break;
                    case LocalAppData: sb.Append("Local AppData"); break;
                    case LocalAppDataLow: sb.Append("LocalLow AppData"); break;
                    case Documents: sb.Append("「文档」"); break;
                    case SavedGames: sb.Append("「保存的游戏」"); break;
                    case ProgramData: sb.Append("ProgramData"); break;
                    case UserProfile: sb.Append("用户主目录"); break;
                }
            }
            return sb.ToString();
        }
    }
}
