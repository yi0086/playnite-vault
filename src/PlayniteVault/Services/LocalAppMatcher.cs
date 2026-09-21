using System;
using System.Collections.Generic;
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
    /// <para><b>三级命中键</b>（顺序不能换）：</para>
    /// <list type="number">
    /// <item><c>GameId</c>：Playnite 给库记录的不变量 GUID（<c>Game.Id</c>），跨机器、跨改名都稳定。</item>
    /// <item><c>LibraryId</c>：<c>Game.GameId</c> —— 库内标识（Steam 就是 appid）。
    /// 游戏删掉重新导入时 GUID 会重新分配，而它不变，所以这一档专治「重导入」。</item>
    /// <item><c>slug</c>：归档时用 <see cref="VaultService.MakeAppId"/> 现推的 Id。
    /// 目录改名就会推不出同一个值 —— 只用来兜住 v1.8 之前归档、两把钥匙都没存的老条目。</item>
    /// </list>
    /// </summary>
    public static class LocalAppMatcher
    {
        /// <summary>命中依据的措辞，直接显示给用户（他需要知道「凭什么说传过了」）。</summary>
        public const string NoteByGameId = "按 Playnite 游戏 ID 对上（最可靠）";

        public const string NoteByLibraryId = "按库内 ID 对上（Steam appid 这类库标识）";

        public const string NoteBySlugOld = "按安装目录名推的 Id 对上（老条目，没有游戏 ID 可对）";

        public const string NoteBySlug = "按安装目录名推的 Id 对上（目录名与归档时一致）";

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

        // ================================================================ 命中

        /// <summary>
        /// 按 GameId → DatabaseId → slug 的顺序找对应条目。
        /// 命中时 <paramref name="note"/> 写依据，未命中写 <see cref="NoteMiss"/>。
        /// </summary>
        public static AppEntry Match(
            string gameId,
            string libraryId,
            string slug,
            IDictionary<string, AppEntry> byGameId,
            IDictionary<string, AppEntry> byLibraryId,
            IDictionary<string, AppEntry> bySlug,
            out string note)
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
                var old = string.IsNullOrWhiteSpace(hit.PlayniteGameId)
                          && string.IsNullOrWhiteSpace(hit.PlayniteLibraryId);
                note = old ? NoteBySlugOld : NoteBySlug;
                return hit;
            }

            note = NoteMiss;
            return null;
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
