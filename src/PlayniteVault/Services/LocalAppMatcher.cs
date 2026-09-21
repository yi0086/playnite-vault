using System;
using System.Collections.Generic;
using System.Text;
using PlayniteVault.Models;

namespace PlayniteVault.Services
{
    /// <summary>
    /// 「本地 Playnite 游戏」与「仓库归档条目」的对账规则。
    ///
    /// <para><b>为什么要单独抽一个类</b>：判定「这东西传过没有」是卡片墙上唯一会让用户
    /// 下错结论的地方 —— 判错一次（明明是传过的却显示「未上传」）用户就会重传一遍，
    /// 白等几十分钟。抽成不依赖 Playnite 运行时的纯函数之后，自检能对着三种命中键
    /// 逐个断言，不需要真的起一个 Playnite。</para>
    ///
    /// <para><b>五级命中键</b>（顺序不能换）：</para>
    /// <list type="number">
    /// <item><c>GameId</c>：Playnite 给库记录的不变量 GUID（<c>Game.Id</c>），跨机器、跨改名都稳定。</item>
    /// <item><c>LibraryId</c>：<c>Game.GameId</c> —— 库内标识（Steam 就是 appid）。
    /// 游戏删掉重新导入时 GUID 会重新分配，而它不变，所以这一档专治「重导入」。</item>
    /// <item><c>slug</c>：归档时用 <see cref="VaultService.MakeAppId"/> 现推的 Id。
    /// 目录改名就会推不出同一个值 —— 只用来兜住 v1.8 之前归档、两把钥匙都没存的老条目。</item>
    /// <item><c>LibraryId 直对条目 Id</c>：本插件导入的游戏就是 <c>Game.GameId = app.Id</c>。
    /// 这一档专治「老条目 + 本地已卸载」—— 卸载后安装目录没了，上一档的 slug 就推不出来，
    /// 而两个键实际上指的是同一样东西。（Steam 那种数字 appid 不可能撞上 slug，不会误判。）</item>
    /// <item><c>名字</c>：把名字去掉空白与标点、统一小写后比。**最后一根稻草**，
    /// 专治「手工添加、没有库内标识、也没存游戏 ID」的条目。命中会明说依据是名字，
    /// 因为改名或重名时它确实会错。</item>
    /// </list>
    /// </summary>
    public static class LocalAppMatcher
    {
        /// <summary>命中依据的措辞，直接显示给用户（他需要知道「凭什么说传过了」）。</summary>
        public const string NoteByGameId = "按 Playnite 游戏 ID 对上（最可靠）";

        public const string NoteByLibraryId = "按库内 ID 对上（Steam appid 这类库标识）";

        public const string NoteBySlugOld = "按安装目录名推的 Id 对上（老条目，没有游戏 ID 可对）";

        public const string NoteBySlug = "按安装目录名推的 Id 对上（目录名与归档时一致）";

        /// <summary>库内标识与条目 Id 直接相等 —— 本插件导入的游戏就是这么存的。</summary>
        public const string NoteByIdentity = "库内标识就是这个条目的 Id（本插件导入的游戏）";

        /// <summary>靠名字对上的。要写清楚，因为改名/重名时它会错。</summary>
        public const string NoteByName = "按游戏名字对上（没有更可靠的键可用；改名后会对不上）";

        public const string NoteMiss = "仓库里找不到对应条目";

        /// <summary>索引不可用（没读到）时的说明 —— 和「确实没传过」是两回事，不能混。</summary>
        public const string NoteIndexUnavailable = "还没读到仓库索引，暂时无法判断。";

        // ================================================================ 建桶

        public static Dictionary<string, AppEntry> BucketByGameId(IEnumerable<AppEntry> apps)
        {
            var bucket = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
            if (apps == null)
            {
                return bucket;
            }

            foreach (var app in apps)
            {
                if (app == null || string.IsNullOrWhiteSpace(app.PlayniteGameId))
                {
                    continue;
                }

                // 重复键保留第一条：索引本身出问题时也不该抛异常把整页打掉
                if (!bucket.ContainsKey(app.PlayniteGameId))
                {
                    bucket[app.PlayniteGameId] = app;
                }
            }

            return bucket;
        }

        public static Dictionary<string, AppEntry> BucketByLibraryId(IEnumerable<AppEntry> apps)
        {
            var bucket = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
            if (apps == null)
            {
                return bucket;
            }

            foreach (var app in apps)
            {
                if (app == null || string.IsNullOrWhiteSpace(app.PlayniteLibraryId))
                {
                    continue;
                }

                if (!bucket.ContainsKey(app.PlayniteLibraryId))
                {
                    bucket[app.PlayniteLibraryId] = app;
                }
            }

            return bucket;
        }

        public static Dictionary<string, AppEntry> BucketBySlug(IEnumerable<AppEntry> apps)
        {
            var bucket = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
            if (apps == null)
            {
                return bucket;
            }

            foreach (var app in apps)
            {
                if (app == null || string.IsNullOrWhiteSpace(app.Id))
                {
                    continue;
                }

                if (!bucket.ContainsKey(app.Id))
                {
                    bucket[app.Id] = app;
                }
            }

            return bucket;
        }

        /// <summary>
        /// 按归一化后的名字建桶。
        ///
        /// <para>归一化只做两件事：去掉空白与标点、统一小写。**保留中文** ——
        /// 这一档存在的全部意义就是给「老条目 + 已卸载 + 中文名」兜底。</para>
        /// </summary>
        public static Dictionary<string, AppEntry> BucketByName(IEnumerable<AppEntry> apps)
        {
            var bucket = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
            if (apps == null)
            {
                return bucket;
            }

            foreach (var app in apps)
            {
                var key = NormalizeName(app == null ? null : app.Name);
                if (key.Length == 0)
                {
                    continue;
                }

                if (!bucket.ContainsKey(key))
                {
                    bucket[key] = app;
                }
            }

            return bucket;
        }

        /// <summary>名字归一化：去掉空白与非字母数字字符，统一小写。中文会保留。</summary>
        public static string NormalizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            var sb = new StringBuilder(name.Length);
            foreach (var ch in name)
            {
                if (char.IsWhiteSpace(ch))
                {
                    continue;
                }

                if (char.IsLetterOrDigit(ch))
                {
                    sb.Append(char.ToLowerInvariant(ch));
                }
            }

            return sb.ToString();
        }

        // ================================================================ 命中

        /// <summary>
        /// 按 GameId → LibraryId → slug → LibraryId 直对 Id → 名字 的顺序找对应条目。
        /// 命中时 <paramref name="note"/> 写依据，未命中写 <see cref="NoteMiss"/>。
        ///
        /// <para>后两档是 v1.9 加的，专治「同一游戏在卡片墙上出现两次」：老条目没存
        /// Playnite 游戏 ID，而本地那份已经卸载（安装目录没了 → slug 推不出来），
        /// 于是卡片显示「未上传」、同时又在孤儿列表里出现一次。两档都是只往后加、
        /// 不改变前三级的行为。</para>
        /// </summary>
        public static AppEntry Match(
            string gameId,
            string libraryId,
            string slug,
            IDictionary<string, AppEntry> byGameId,
            IDictionary<string, AppEntry> byLibraryId,
            IDictionary<string, AppEntry> bySlug,
            out string note,
            IDictionary<string, AppEntry> byName = null,
            string nameKey = null)
        {
            AppEntry hit;

            if (!string.IsNullOrWhiteSpace(gameId)
                && byGameId != null && byGameId.TryGetValue(gameId, out hit))
            {
                note = NoteByGameId;
                return hit;
            }

            // 库内标识（Steam appid 那一种）。空串是常态（手动添加的游戏可能没有），
            // 所以这里必须判空 —— 否则 null 键会把所有「没库标识」的游戏互相认成同一个。
            if (!string.IsNullOrWhiteSpace(libraryId)
                && byLibraryId != null && byLibraryId.TryGetValue(libraryId, out hit))
            {
                note = NoteByLibraryId;
                return hit;
            }

            if (!string.IsNullOrWhiteSpace(slug)
                && bySlug != null && bySlug.TryGetValue(slug, out hit))
            {
                // 「老条目」= 两把钥匙一个都没存（v1.8 之前归档的就是这样）。
                // 这个区别必须写出来，否则用户会以为「它也是按游戏 ID 认出来的」，
                // 下次一改名就对不上，而界面上什么提示都没有。
                note = IsOldEntry(hit) ? NoteBySlugOld : NoteBySlug;
                return hit;
            }

            // 库内标识直接等于条目 Id：本插件导入的游戏就是 Game.GameId = app.Id。
            // 归到这一步而不是并进上一档，是因为「存的库标识」与「条目 Id」是两个字段，
            // 依据不同、出错时的排查方向也不同，界面上要说清楚。
            if (!string.IsNullOrWhiteSpace(libraryId)
                && bySlug != null && bySlug.TryGetValue(libraryId, out hit))
            {
                note = NoteByIdentity;
                return hit;
            }

            if (!string.IsNullOrWhiteSpace(nameKey)
                && byName != null && byName.TryGetValue(nameKey, out hit))
            {
                note = NoteByName;
                return hit;
            }

            note = NoteMiss;
            return null;
        }

        /// <summary>v1.8 之前归档的条目：两把钥匙一个都没存。</summary>
        public static bool IsOldEntry(AppEntry app)
        {
            return app != null
                   && string.IsNullOrWhiteSpace(app.PlayniteGameId)
                   && string.IsNullOrWhiteSpace(app.PlayniteLibraryId);
        }

        // ================================================================ 孤儿

        /// <summary>
        /// 仓库里存在、但本地 Playnite 库里已经没有对应游戏的那些条目。
        ///
        /// <para>这些条目必须仍然能在界面上删掉 —— 游戏从库里删了/换了安装位置，
        /// 归档不会自己消失。少了这条路，用户就只能去开那个（本该被内联掉的）仓库管理窗口。</para>
        /// </summary>
        public static List<AppEntry> Orphans(IEnumerable<AppEntry> apps, IEnumerable<LocalAppCard> cards)
        {
            var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (cards != null)
            {
                foreach (var card in cards)
                {
                    if (card != null && card.InRepository && !string.IsNullOrWhiteSpace(card.RepoAppId))
                    {
                        matched.Add(card.RepoAppId);
                    }
                }
            }

            var result = new List<AppEntry>();
            if (apps == null)
            {
                return result;
            }

            foreach (var app in apps)
            {
                if (app == null || string.IsNullOrWhiteSpace(app.Id))
                {
                    continue;
                }

                if (!matched.Contains(app.Id))
                {
                    result.Add(app);
                }
            }

            return result;
        }
    }
}
