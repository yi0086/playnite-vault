using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using Newtonsoft.Json;
using PlayniteVault.Models;

namespace PlayniteVault.Services
{
    /// <summary>
    /// PCGamingWiki 的取文本端。
    ///
    /// <para>单独一个文件、单独一个类，是为了让 <see cref="SaveSniffer"/> 保持「纯函数」——
    /// 解析逻辑能在自检里喂死样本跑，而不需要真去联网。这里只负责把 wikitext 拿回来。</para>
    ///
    /// <para><b>失败一律降级</b>：抓不到就返回 false 并给出原因，由调用方决定
    /// 「只用前两档」。嗅探少了这档，功能照用 —— 为了一个可选线索去抛异常说不过去。</para>
    ///
    /// <para>代理沿用仓库工具链的约定：<c>VAULT_HTTPS_PROXY</c>（例如
    /// <c>http://192.168.31.55:7890</c>）。本机直连 GitHub 常被中断，PCGamingWiki 同理。</para>
    /// </summary>
    public static class PcgamingWikiClient
    {
        private const string Api = "https://www.pcgamingwiki.com/w/api.php";

        /// <summary>默认超时。嗅探是「顺手看一眼」，不能让界面卡住。</summary>
        public static int TimeoutMs { get; set; } = 8000;

        /// <summary>最近一次失败的原因（给界面显示用）。</summary>
        public static string LastError { get; private set; }

        /// <summary>走代理的话从环境变量取。</summary>
        private static IWebProxy Proxy()
        {
            var raw = Environment.GetEnvironmentVariable("VAULT_HTTPS_PROXY");
            if (string.IsNullOrWhiteSpace(raw))
            {
                return WebRequest.DefaultWebProxy;
            }

            try
            {
                return new WebProxy(raw.Trim());
            }
            catch
            {
                // 环境变量写得不对就当没设，不要因为这个报错
                return WebRequest.DefaultWebProxy;
            }
        }

        private static HttpWebRequest Create(string url)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "GET";
            request.Timeout = TimeoutMs;
            request.ReadWriteTimeout = TimeoutMs;
            request.UserAgent = "PlayniteVault/" + (Version() ?? "1.x")
                                + " (save-path sniffer; +https://github.com/yi0086/playnite-vault)";
            request.Proxy = Proxy();
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            return request;
        }

        private static string Version()
        {
            try
            {
                return typeof(PcgamingWikiClient).Assembly.GetName().Version.ToString();
            }
            catch
            {
                return null;
            }
        }

        private static string Get(string url, out string error)
        {
            error = null;
            try
            {
                using (var response = (HttpWebResponse)Create(url).GetResponse())
                using (var stream = response.GetResponseStream())
                {
                    if (stream == null)
                    {
                        error = "响应没有内容";
                        return null;
                    }

                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        return reader.ReadToEnd();
                    }
                }
            }
            catch (WebException ex)
            {
                error = Describe(ex);
                return null;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        private static string Describe(WebException ex)
        {
            if (ex == null)
            {
                return "网络错误";
            }

            var response = ex.Response as HttpWebResponse;
            if (response != null)
            {
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return "页面不存在（404）";
                }

                return "HTTP " + (int)response.StatusCode + " " + response.StatusDescription;
            }

            switch (ex.Status)
            {
                case WebExceptionStatus.Timeout:
                    return "超时（" + TimeoutMs + "ms），可能需要代理（设 VAULT_HTTPS_PROXY）";
                case WebExceptionStatus.NameResolutionFailure:
                    return "域名解析失败（检查网络/代理）";
                case WebExceptionStatus.ConnectFailure:
                    return "连不上（检查网络/代理）";
                default:
                    return ex.Status + "：" + ex.Message;
            }
        }

        /// <summary>
        /// 用游戏名搜一个页面标题。搜不到返回 null。
        /// </summary>
        public static string FindPageTitle(string gameName, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(gameName))
            {
                error = "游戏名为空";
                return null;
            }

            var url = Api + "?action=query&format=json&list=search&srlimit=5&srsearch="
                      + Uri.EscapeDataString(gameName.Trim());

            var body = Get(url, out error);
            if (body == null)
            {
                return null;
            }

            try
            {
                var json = JsonConvert.DeserializeObject<Dictionary<string, object>>(body);
                object queryToken;
                if (json == null || !json.TryGetValue("query", out queryToken))
                {
                    error = "搜索结果里没有 query 字段";
                    return null;
                }

                var query = queryToken as Dictionary<string, object>;
                object searchToken;
                if (query == null || !query.TryGetValue("search", out searchToken))
                {
                    error = "搜索没有结果";
                    return null;
                }

                var list = searchToken as List<object>;
                if (list == null || list.Count == 0)
                {
                    error = "搜索没有结果";
                    return null;
                }

                foreach (var item in list)
                {
                    var row = item as Dictionary<string, object>;
                    if (row == null)
                    {
                        continue;
                    }

                    object title;
                    if (row.TryGetValue("title", out title) && title != null)
                    {
                        var name = title.ToString();
                        // 命名空间页面（Category:/Template: 之类）不是游戏页
                        if (name.IndexOf(':') < 0)
                        {
                            return name;
                        }
                    }
                }

                error = "搜索结果里没有看起来像游戏页的条目";
                return null;
            }
            catch (Exception ex)
            {
                error = "解析搜索结果失败：" + ex.Message;
                return null;
            }
        }

        /// <summary>取页面源码（wikitext）。</summary>
        public static string FetchWikitext(string title, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(title))
            {
                error = "页面标题为空";
                return null;
            }

            var url = Api + "?action=parse&format=json&prop=wikitext&page="
                      + Uri.EscapeDataString(title.Trim());

            var body = Get(url, out error);
            if (body == null)
            {
                return null;
            }

            try
            {
                var json = JsonConvert.DeserializeObject<Dictionary<string, object>>(body);
                object parseToken;
                if (json == null || !json.TryGetValue("parse", out parseToken))
                {
                    object errorToken;
                    if (json != null && json.TryGetValue("error", out errorToken))
                    {
                        var errObj = errorToken as Dictionary<string, object>;
                        error = errObj != null && errObj.ContainsKey("info")
                            ? "wiki 返回错误：" + errObj["info"]
                            : "wiki 返回错误";
                        return null;
                    }

                    error = "返回里没有 parse 字段";
                    return null;
                }

                var parse = parseToken as Dictionary<string, object>;
                object wikitextToken;
                if (parse == null || !parse.TryGetValue("wikitext", out wikitextToken))
                {
                    error = "返回里没有 wikitext";
                    return null;
                }

                var wikitext = wikitextToken as Dictionary<string, object>;
                object star;
                if (wikitext == null || !wikitext.TryGetValue("*", out star) || star == null)
                {
                    error = "wikitext 是空的";
                    return null;
                }

                return star.ToString();
            }
            catch (Exception ex)
            {
                error = "解析页面源码失败：" + ex.Message;
                return null;
            }
        }

        /// <summary>
        /// 一条龙：搜页面 → 取源码 → 解析出候选。
        /// 任何一步失败都只是「这一档没有了」，返回空列表 + 原因。
        /// </summary>
        public static List<SaveSniffCandidate> TrySniff(string gameName, SaveSniffContext ctx,
            out string error)
        {
            error = null;
            LastError = null;

            var title = FindPageTitle(gameName, out error);
            if (title == null)
            {
                LastError = error;
                return new List<SaveSniffCandidate>();
            }

            var wikitext = FetchWikitext(title, out error);
            if (wikitext == null)
            {
                LastError = error;
                return new List<SaveSniffCandidate>();
            }

            var candidates = SaveSniffer.FromPcgamingWiki(wikitext, ctx);
            if (candidates.Count == 0)
            {
                error = "页面「" + title + "」里没有可用的存档路径行";
                LastError = error;
            }

            return candidates;
        }

        /// <summary>
        /// 合并三档结果，按分数排序、去掉重复。界面上「一个列表带来源标签」就是它。
        /// </summary>
        public static List<SaveSniffCandidate> Merge(params IEnumerable<SaveSniffCandidate>[] tiers)
        {
            var all = new List<SaveSniffCandidate>();
            foreach (var tier in tiers ?? new IEnumerable<SaveSniffCandidate>[0])
            {
                if (tier != null)
                {
                    all.AddRange(tier);
                }
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var result = new List<SaveSniffCandidate>();

            foreach (var candidate in all
                .OrderByDescending(c => c.Score)
                .ThenBy(c => c.Source, StringComparer.Ordinal))
            {
                var key = (candidate.EffectivePath ?? string.Empty)
                    .Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();
                if (key.Length == 0 || !seen.Add(key))
                {
                    continue;
                }

                result.Add(candidate);
            }

            return result;
        }
    }
}
