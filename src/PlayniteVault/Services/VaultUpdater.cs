using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using PlayniteVault.Net;

namespace PlayniteVault.Services
{
    /// <summary>镜像选择。</summary>
    public enum UpdateMirror
    {
        /// <summary>探测两个源，谁快用谁，下载慢了自动换另一个。</summary>
        Auto = 0,
        GitHub = 1,
        Gitee = 2
    }

    /// <summary>release 里的一个附件。</summary>
    public class ReleaseAsset
    {
        public string Name { get; set; }
        public string DownloadUrl { get; set; }
        public long Size { get; set; }

        public override string ToString()
        {
            return Name + " (" + (Size / 1024) + " KB)";
        }
    }

    /// <summary>一个源上解析出来的 release。</summary>
    public class ReleaseInfo
    {
        /// <summary>规范化成 x.y.z 的版本号。</summary>
        public string Version { get; set; }

        /// <summary>原始 tag（v1.5.0）。</summary>
        public string TagName { get; set; }

        public string Notes { get; set; }
        public string PageUrl { get; set; }
        public List<ReleaseAsset> Assets { get; set; } = new List<ReleaseAsset>();

        /// <summary>挑出插件包：PlayniteVault-x.y.z.zip。</summary>
        public ReleaseAsset PickPluginAsset()
        {
            if (Assets == null || Assets.Count == 0)
            {
                return null;
            }

            var zip = Assets.Where(a => a.Name != null
                        && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    .ToList();

            // 最精确：PlayniteVault-1.6.0.zip
            var exact = zip.FirstOrDefault(a =>
                a.Name.StartsWith(VaultUpdater.AssetStem + "-", StringComparison.OrdinalIgnoreCase));
            if (exact != null)
            {
                return exact;
            }

            // 宽松一点：名字里带 PlayniteVault 的 zip
            return zip.FirstOrDefault(a =>
                a.Name.IndexOf(VaultUpdater.AssetStem, StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }

    /// <summary>一个「从哪儿下」的具体候选。</summary>
    public class UpdateCandidate
    {
        public string Mirror { get; set; }        // github / gitee
        public ProxyMode Proxy { get; set; }
        public ReleaseInfo Release { get; set; }
        public ReleaseAsset Asset { get; set; }
        public int LatencyMs { get; set; }

        public string Describe()
        {
            return Mirror + "/" + (Proxy == ProxyMode.Direct ? "直连" : "系统代理")
                 + " (" + LatencyMs + " ms)";
        }
    }

    /// <summary>一次检查的完整结果。</summary>
    public class UpdateCheckResult
    {
        public bool HasUpdate { get; set; }
        public string CurrentVersion { get; set; }
        public string LatestVersion { get; set; }
        public List<UpdateCandidate> Candidates { get; set; } = new List<UpdateCandidate>();

        /// <summary>每个源的探测记录，出错时原样展示给用户。</summary>
        public List<string> Attempts { get; set; } = new List<string>();

        /// <summary>整体失败的原因；离线时就是它。</summary>
        public string Error { get; set; }

        /// <summary>有新版本，但用户把它加进了「跳过此版本」。</summary>
        public bool SkippedByUser { get; set; }

        public ReleaseInfo Best
        {
            get { return Candidates.Count > 0 ? Candidates[0].Release : null; }
        }
    }

    /// <summary>暂存好的更新包。</summary>
    public class StagedUpdate
    {
        public string Version { get; set; }
        public string StagingDir { get; set; }
        public string ScriptPath { get; set; }
        public string ZipPath { get; set; }
        public List<string> Files { get; set; } = new List<string>();
    }

    /// <summary>
    /// 插件自更新。
    ///
    /// 三件事必须说清楚：
    /// 1) **为什么要外部脚本**：PlayniteVault.dll 正被 Playnite 的进程加载着（Windows 文件锁），
    ///    运行期无论如何覆盖不了。所以只能「暂存 + 等进程退出后再替换」。
    /// 2) **为什么用 Playnite 自己的 Quit**：`Playnite.PlayniteApplication.Quit(bool)` 是
    ///    Playnite 托盘「退出」走的同一条路（设置里 IsShuttingDown 会置位），
    ///    因此不会触发「非正常退出 → 下次进安全模式」。这个类型不在 Playnite.SDK 里，
    ///    只能反射拿；拿不到就退回发送窗口关闭消息，再不行就如实告诉用户手动退出。
    /// 3) **重启由脚本负责，不由 Playnite 负责**：如果让 Playnite 自己重启（Restart），
    ///    新实例可能在我们覆盖文件之前就起来了 —— 那就会加载到旧 DLL。
    ///    现在退出和拉起都在我们手里，顺序是确定的：退出 → 覆盖 → 拉起。
    /// </summary>
    public class VaultUpdater
    {
        public const string RepoOwner = "yi0086";
        public const string RepoName = "playnite-vault";

        public const string GitHubApiTemplate = "https://api.github.com/repos/{0}/{1}/releases/latest";
        public const string GiteeApiTemplate = "https://gitee.com/api/v5/repos/{0}/{1}/releases/latest";

        /// <summary>
        /// 项目大名。三处命名只认这几个常量，别再各处散着写字符串：
        ///   <see cref="ExtensionId"/>  —— extension.yaml 的 Id，**也是扩展数据目录名**
        ///   <see cref="ModuleFileName"/> —— Playnite 加载的 dll
        ///   <see cref="AssetStem"/>     —— release 附件前缀（PlayniteVault-1.6.0.zip）
        /// </summary>
        public const string ExtensionId = "Playnite-Vault";
        public const string ModuleFileName = "PlayniteVault.dll";
        public const string AssetStem = "PlayniteVault";

        /// <summary>探测单个源的超时。两个源是并发探的，所以整体上限就是这个数。</summary>
        private const int ProbeTimeoutMs = 6000;

        /// <summary>
        /// 下载阶段的速度地板：低于它就换源（KB/s）。
        ///
        /// 定在 40 是刻意的：低于这个速度装一个 12 MB 的包要五分钟以上，
        /// 与其干等不如换一个源试试。但**不是说慢就一定失败** ——
        /// 所有源都低于地板时，还会拿主源再跑一次「不设地板」的兜底下载
        /// （见 <see cref="Download"/> 末尾），所以慢网络照样能装。
        /// </summary>
        private const int MinSpeedKbPerSecond = 40;

        /// <summary>判定速度之前至少先收够这么多字节。可用环境变量覆盖（自检用）。</summary>
        private const long SpeedSampleFloor = 400 * 1024;

        internal static int SpeedFloorKb
        {
            get
            {
                var custom = Environment.GetEnvironmentVariable("VAULT_UPDATE_MIN_SPEED_KB");
                int value;
                if (TestHooksEnabled() && !string.IsNullOrWhiteSpace(custom)
                    && int.TryParse(custom, out value) && value > 0)
                {
                    return value;
                }
                return MinSpeedKbPerSecond;
            }
        }

        internal static long SpeedSampleBytes
        {
            get
            {
                var custom = Environment.GetEnvironmentVariable("VAULT_UPDATE_SPEED_FLOOR_BYTES");
                long value;
                if (TestHooksEnabled() && !string.IsNullOrWhiteSpace(custom)
                    && long.TryParse(custom, out value) && value > 0)
                {
                    return value;
                }
                return SpeedSampleFloor;
            }
        }

        private readonly string dataPath;
        private readonly VaultSettings settings;

        public VaultUpdater(string dataPath, VaultSettings settings)
        {
            this.dataPath = dataPath;
            this.settings = settings;
        }

        public string UpdateRoot
        {
            get { return Path.Combine(dataPath, "update"); }
        }

        public string StagingDir
        {
            get { return Path.Combine(UpdateRoot, "staging"); }
        }

        public string ScriptPath
        {
            get { return Path.Combine(UpdateRoot, "apply-update.cmd"); }
        }

        public string ScriptLogPath
        {
            get { return Path.Combine(UpdateRoot, "apply-update.log"); }
        }

        // ---------- 当前版本 ----------

        /// <summary>编译期兜底版本，extension.yaml 读不到时用它。</summary>
        public const string FallbackVersion = "1.6.0";

        /// <summary>
        /// 当前插件版本。**以 extension.yaml 为准** —— Playnite 也是读这个文件，
        /// 从它读才能保证「界面显示的版本」和「Playnite 认为的版本」一致。
        /// </summary>
        public static string CurrentVersion()
        {
            try
            {
                var dir = PluginDirectory();
                if (dir != null)
                {
                    var yaml = Path.Combine(dir, "extension.yaml");
                    if (File.Exists(yaml))
                    {
                        var version = ReadYamlVersion(File.ReadAllText(yaml, Encoding.UTF8));
                        if (!string.IsNullOrEmpty(version))
                        {
                            return SemVersion.NormalizeText(version) ?? version;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                VaultLog.Warn("读取 extension.yaml 版本失败：" + ex.Message);
            }

            return FallbackVersion;
        }

        /// <summary>插件安装目录（PlayniteVault.dll 所在目录）。</summary>
        public static string PluginDirectory()
        {
            try
            {
                var location = typeof(VaultUpdater).Assembly.Location;
                if (!string.IsNullOrEmpty(location))
                {
                    return Path.GetDirectoryName(location);
                }
            }
            catch
            {
                // 单文件/内存加载时会为空
            }
            return null;
        }

        /// <summary>从 extension.yaml 文本里抠出 Version: 那一行（不引 YAML 库）。</summary>
        public static string ReadYamlVersion(string yaml)
        {
            if (string.IsNullOrEmpty(yaml))
            {
                return null;
            }

            foreach (var raw in yaml.Split('\n'))
            {
                var line = raw.Trim();
                if (!line.StartsWith("Version:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var value = line.Substring("Version:".Length).Trim().Trim('"', '\'');
                return value.Length == 0 ? null : value;
            }
            return null;
        }

        /// <summary>从 extension.yaml 文本里抠出 Id / Module（校验用）。</summary>
        public static string ReadYamlValue(string yaml, string key)
        {
            if (string.IsNullOrEmpty(yaml))
            {
                return null;
            }

            foreach (var raw in yaml.Split('\n'))
            {
                var line = raw.Trim();
                if (!line.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                var value = line.Substring(key.Length + 1).Trim().Trim('"', '\'');
                return value.Length == 0 ? null : value;
            }
            return null;
        }

        // ---------- 检查 ----------

        public UpdateMirror MirrorFromSettings()
        {
            var text = settings.UpdateMirror;
            if (string.Equals(text, "github", StringComparison.OrdinalIgnoreCase)) return UpdateMirror.GitHub;
            if (string.Equals(text, "gitee", StringComparison.OrdinalIgnoreCase)) return UpdateMirror.Gitee;
            return UpdateMirror.Auto;
        }

        /// <summary>
        /// 探测两个源的 `releases/latest`。两个源**并发**探，各自独立超时 ——
        /// 串行探的话国内环境光等 GitHub 超时就要好几秒。
        /// </summary>
        public UpdateCheckResult Check()
        {
            var result = new UpdateCheckResult { CurrentVersion = CurrentVersion() };

            var mirror = MirrorFromSettings();
            var probes = new List<MirrorProbe>();

            if (mirror == UpdateMirror.Auto)
            {
                // 并发探，取各自耗时
                var tasks = new[]
                {
                    StartProbe("github", GitHubApi()),
                    StartProbe("gitee", GiteeApi())
                };
                foreach (var task in tasks)
                {
                    probes.Add(task.Result);
                }

                // 两个都对不上（典型：都得走代理）→ 再用系统代理探一轮
                if (probes.All(p => p.Release == null) && HttpFetch.SystemProxyConfigured())
                {
                    result.Attempts.Add("直连两个源都失败，改用系统代理 " + HttpFetch.DescribeSystemProxy());
                    var viaProxy = new[]
                    {
                        StartProbe("github", GitHubApi(), ProxyMode.System),
                        StartProbe("gitee", GiteeApi(), ProxyMode.System)
                    };
                    probes.AddRange(viaProxy.Select(t => t.Result));
                }
            }
            else
            {
                var forced = mirror == UpdateMirror.GitHub ? "github" : "gitee";
                var url = forced == "github" ? GitHubApi() : GiteeApi();
                probes.Add(StartProbe(forced, url).Result);
                if (probes[0].Release == null)
                {
                    probes.Add(StartProbe(forced, url, ProxyMode.System).Result);
                }
            }

            foreach (var probe in probes)
            {
                result.Attempts.Add(probe.Describe());
            }

            // 成功的按延迟排序，快的当主源
            var ok = probes.Where(p => p.Release != null && p.Asset != null)
                           .OrderBy(p => p.LatencyMs)
                           .ToList();

            foreach (var probe in ok)
            {
                result.Candidates.Add(new UpdateCandidate
                {
                    Mirror = probe.Mirror,
                    Proxy = probe.Proxy,
                    Release = probe.Release,
                    Asset = probe.Asset,
                    LatencyMs = probe.LatencyMs
                });
            }

            // 同一个镜像不同代理都通时只留最快的那条，避免做两次多余的下载尝试
            var deduped = new List<UpdateCandidate>();
            foreach (var candidate in result.Candidates)
            {
                if (deduped.Any(c => c.Mirror == candidate.Mirror && c.LatencyMs <= candidate.LatencyMs))
                {
                    continue;
                }
                deduped.Add(candidate);
            }
            result.Candidates = deduped;

            if (result.Candidates.Count == 0)
            {
                result.Error = probes.Count == 0
                    ? "没有可用的下载源"
                    : "两个源都拿不到 release：" + string.Join("；", probes.Select(p => p.Error));
                return result;
            }

            result.LatestVersion = result.Candidates[0].Release.Version;
            var skipped = settings.SkippedVersion;
            result.HasUpdate = SemVersion.IsNewer(result.LatestVersion, result.CurrentVersion);

            if (result.HasUpdate && !string.IsNullOrWhiteSpace(skipped)
                && SemVersion.Parse(skipped).Equals(SemVersion.Parse(result.LatestVersion)))
            {
                result.HasUpdate = false;
                result.SkippedByUser = true;
                result.Error = "已按设置跳过 " + result.LatestVersion;
            }

            return result;
        }

        private class MirrorProbe
        {
            public string Mirror;
            public ProxyMode Proxy;
            public ReleaseInfo Release;
            public ReleaseAsset Asset;
            public int LatencyMs;
            public string Error;

            public string Describe()
            {
                var where = Mirror + "/" + (Proxy == ProxyMode.Direct ? "直连" : "系统代理");
                return Release == null
                    ? where + " ✗ " + (Error ?? "无结果")
                    : where + " ✓ " + Release.Version + " / " + (Asset == null ? "无 zip 附件" : Asset.Name);
            }
        }

        private System.Threading.Tasks.Task<MirrorProbe> StartProbe(string mirror, string url,
            ProxyMode proxy = ProxyMode.Direct)
        {
            return System.Threading.Tasks.Task.Run(() =>
            {
                var probe = new MirrorProbe { Mirror = mirror, Proxy = proxy };
                int latency;
                string error;
                var json = HttpFetch.GetString(url, proxy, ProbeTimeoutMs, out latency, out error);
                probe.LatencyMs = latency;

                if (json == null)
                {
                    probe.Error = error;
                    return probe;
                }

                var release = mirror == "github" ? ParseGitHubRelease(json) : ParseGiteeRelease(json);
                if (release == null || !SemVersion.Parse(release.Version).IsValid)
                {
                    probe.Error = "release JSON 解析失败（可能还没发过 release）";
                    return probe;
                }

                probe.Release = release;
                probe.Asset = release.PickPluginAsset();
                if (probe.Asset == null)
                {
                    probe.Error = "release 里没有 " + AssetStem + "-*.zip 附件";
                }
                return probe;
            });
        }

        public static string GitHubApi()
        {
            var custom = Environment.GetEnvironmentVariable("VAULT_UPDATE_GITHUB_LATEST");
            if (!string.IsNullOrWhiteSpace(custom) && TestHooksEnabled())
            {
                return custom;
            }
            return string.Format(GitHubApiTemplate, RepoOwner, RepoName);
        }

        public static string GiteeApi()
        {
            var custom = Environment.GetEnvironmentVariable("VAULT_UPDATE_GITEE_LATEST");
            if (!string.IsNullOrWhiteSpace(custom) && TestHooksEnabled())
            {
                return custom;
            }
            return string.Format(GiteeApiTemplate, RepoOwner, RepoName);
        }

        /// <summary>自定义端点只在显式开了测试开关时生效，避免线上被环境变量改坏。</summary>
        public static bool TestHooksEnabled()
        {
            return string.Equals(Environment.GetEnvironmentVariable("VAULT_UPDATE_TEST"), "1",
                StringComparison.Ordinal);
        }

        // ---------- release JSON ----------

        /// <summary>GitHub：tag_name / body / html_url / assets[].name, browser_download_url, size</summary>
        public static ReleaseInfo ParseGitHubRelease(string json)
        {
            return ParseRelease(json, "browser_download_url");
        }

        /// <summary>Gitee：字段名基本一致，附件里也可能只有 url。</summary>
        public static ReleaseInfo ParseGiteeRelease(string json)
        {
            return ParseRelease(json, "browser_download_url", "download_url");
        }

        private static ReleaseInfo ParseRelease(string json, params string[] urlKeys)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            JObject root;
            try
            {
                root = JObject.Parse(json);
            }
            catch (Exception ex)
            {
                VaultLog.Warn("release JSON 不是对象：" + ex.Message);
                return null;
            }

            var tag = (string)(root["tag_name"] ?? root["tagName"]);
            var name = (string)root["name"];
            var version = SemVersion.NormalizeText(tag);
            if (string.IsNullOrEmpty(version))
            {
                version = SemVersion.NormalizeText(name);
            }
            if (string.IsNullOrEmpty(version))
            {
                return null;
            }

            var info = new ReleaseInfo
            {
                Version = version,
                TagName = tag ?? name,
                Notes = (string)root["body"],
                PageUrl = (string)(root["html_url"] ?? root["htmlUrl"])
            };

            var assets = root["assets"] as JArray;
            if (assets != null)
            {
                foreach (var token in assets.OfType<JObject>())
                {
                    var assetName = (string)(token["name"] ?? token["filename"]);
                    if (string.IsNullOrWhiteSpace(assetName))
                    {
                        continue;
                    }

                    string url = null;
                    foreach (var key in urlKeys)
                    {
                        url = (string)token[key];
                        if (!string.IsNullOrWhiteSpace(url))
                        {
                            break;
                        }
                    }
                    if (string.IsNullOrWhiteSpace(url))
                    {
                        url = (string)token["url"];
                    }
                    if (string.IsNullOrWhiteSpace(url))
                    {
                        continue;
                    }

                    long size = 0;
                    var sizeToken = token["size"];
                    if (sizeToken != null)
                    {
                        try { size = (long)sizeToken; }
                        catch { size = 0; }
                    }

                    info.Assets.Add(new ReleaseAsset
                    {
                        Name = assetName,
                        DownloadUrl = url,
                        Size = size
                    });
                }
            }

            return info;
        }

        // ---------- 下载 + 暂存 ----------

        /// <summary>
        /// 按候选顺序下载并解压到暂存目录。第一个源太慢/失败会自动换下一个；
        /// 所有源都「太慢」时会拿主源再跑一次**不设地板**的兜底下载 ——
        /// 「慢」不该等于「装不上」。
        /// </summary>
        public StagedUpdate Download(UpdateCheckResult check,
            Action<string, long, long> onProgress,
            Func<bool> shouldCancel)
        {
            if (check == null || check.Candidates.Count == 0)
            {
                throw new InvalidOperationException("没有可用的下载候选");
            }

            Directory.CreateDirectory(UpdateRoot);
            var errors = new List<string>();
            var abandoned = new List<string>();
            var version = check.LatestVersion;
            var zipPath = Path.Combine(UpdateRoot, AssetStem + "-" + version + ".zip");

            foreach (var candidate in check.Candidates)
            {
                if (shouldCancel != null && shouldCancel())
                {
                    throw new OperationCanceledException();
                }

                VaultLog.Info("尝试从 " + candidate.Describe() + " 下载 " + candidate.Asset.Name);

                string note;
                bool tooSlow;
                var staged = Attempt(candidate, zipPath, version, true, onProgress, shouldCancel,
                    out note, out tooSlow);
                if (staged != null)
                {
                    return staged;
                }

                if (note == null)
                {
                    throw new OperationCanceledException();
                }

                // 注意：这里**不能**用 note.StartsWith("太慢") 去判断 —— note 前面带着
                // 「github/直连 (0 ms) → 」这样的前缀，靠字符串嗅探会永远判不出来，
                // 于是「太慢」被错当成硬失败，速度地板的兜底重试就再也不会触发。
                // 所以由 Attempt 用一个显式的布尔值告诉我们。
                if (tooSlow)
                {
                    abandoned.Add(note);
                }
                else
                {
                    errors.Add(note);
                }
                VaultLog.Warn("下载未成功，换源：" + note);
            }

            // 所有源都低于地板：拿主源再试一次，这次不设地板
            if (abandoned.Count > 0 && errors.Count == 0)
            {
                VaultLog.Warn("所有源都低于 " + SpeedFloorKb + " KB/s，改为主源不限速再试一次");
                if (onProgress != null)
                {
                    onProgress("所有源都偏慢，正在以不限速方式重试…", 0, 0);
                }

                string note;
                bool tooSlow;
                var staged = Attempt(check.Candidates[0], zipPath, version, false, onProgress,
                    shouldCancel, out note, out tooSlow);
                if (staged != null)
                {
                    return staged;
                }
                if (note == null)
                {
                    throw new OperationCanceledException();
                }
                errors.Add(note);
            }

            var all = errors.Concat(abandoned).ToList();
            if (errors.Count == 0 && abandoned.Count > 0)
            {
                throw new IOException("所有源都太慢（地板 " + SpeedFloorKb + " KB/s）：\n· "
                                      + string.Join("\n· ", abandoned));
            }

            throw new IOException("从所有源下载都失败了：\n· " + string.Join("\n· ", all));
        }

        /// <summary>
        /// 单次尝试。<paramref name="note"/> 为 null 表示「用户取消」；
        /// <paramref name="tooSlow"/> 为 true 表示是被速度地板拦下的（调用方据此决定是否兜底重试）。
        /// </summary>
        private StagedUpdate Attempt(UpdateCandidate candidate, string zipPath, string version,
            bool enforceSpeedFloor, Action<string, long, long> onProgress, Func<bool> shouldCancel,
            out string note, out bool tooSlow)
        {
            note = null;
            tooSlow = false;
            var watch = Stopwatch.StartNew();
            string detail = null;
            var floorKb = SpeedFloorKb;
            var floorBytes = SpeedSampleBytes;

            string error;
            var ok = HttpFetch.DownloadToFile(
                candidate.Asset.DownloadUrl, zipPath, candidate.Proxy,
                ProbeTimeoutMs, 30000, candidate.Asset.Size,
                (done, total) =>
                {
                    if (onProgress != null)
                    {
                        onProgress("正在下载插件更新（" + candidate.Mirror + "）", done, total);
                    }
                    if (shouldCancel != null && shouldCancel())
                    {
                        return false;
                    }

                    // 一个字节都没收到就别干等了
                    if (done == 0)
                    {
                        if (watch.Elapsed.TotalSeconds > 15.0)
                        {
                            detail = "太慢：15 秒没收到任何数据";
                            return false;
                        }
                        return true;
                    }

                    if (!enforceSpeedFloor || done < floorBytes || watch.Elapsed.TotalSeconds <= 3.0)
                    {
                        return true;
                    }

                    // 跑过一小段之后还不到地板速度 → 换源
                    var kbPerSecond = done / 1024.0 / watch.Elapsed.TotalSeconds;
                    if (kbPerSecond < floorKb)
                    {
                        detail = string.Format("太慢：速度只有 {0:F0} KB/s（低于 {1} KB/s 地板）",
                            kbPerSecond, floorKb);
                        return false;
                    }
                    return true;
                },
                out error);

            if (!ok && shouldCancel != null && shouldCancel())
            {
                return null;   // note 保持 null = 用户取消
            }

            if (!ok)
            {
                tooSlow = detail != null && detail.StartsWith("太慢", StringComparison.Ordinal);
                note = candidate.Describe() + " → " + (detail ?? error);
                return null;
            }

            if (candidate.Asset.Size > 0)
            {
                var actual = new FileInfo(zipPath).Length;
                if (actual != candidate.Asset.Size)
                {
                    note = candidate.Describe() + " → 字节数不符：期望 "
                           + candidate.Asset.Size + "，实际 " + actual;
                    return null;
                }
            }

            var staged = Stage(zipPath, version);
            staged.ZipPath = zipPath;
            return staged;
        }

        /// <summary>
        /// 解压 + 校验。这里把住三道门：路径穿越、必须有的文件、版本对不对。
        /// </summary>
        public StagedUpdate Stage(string zipPath, string expectedVersion)
        {
            if (!File.Exists(zipPath))
            {
                throw new FileNotFoundException("没有下载到更新包", zipPath);
            }

            // 重来一遍就清空，避免上一版残留的文件混进来
            if (Directory.Exists(StagingDir))
            {
                Directory.Delete(StagingDir, true);
            }
            Directory.CreateDirectory(StagingDir);

            var staged = new StagedUpdate { Version = expectedVersion, StagingDir = StagingDir };

            using (var archive = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        continue;   // 目录项
                    }

                    var name = entry.FullName.Replace('\\', '/');

                    if (!IsSafeEntryName(name))
                    {
                        throw new InvalidDataException("更新包里含有不安全的路径：" + entry.FullName);
                    }

                    // 发布包里是 Playnite-Vault/<文件>，只取一层目录里的文件。
                    // 更深的层级不属于插件根目录，直接忽略。
                    if (DepthOf(name) > 1)
                    {
                        continue;
                    }

                    var leaf = LeafName(name);
                    if (leaf == null)
                    {
                        continue;
                    }

                    var target = Path.Combine(StagingDir, leaf);
                    entry.ExtractToFile(target, true);
                    staged.Files.Add(leaf);
                }
            }

            // 必须有 dll 与 yaml，否则这就是个坏包
            var dll = Path.Combine(StagingDir, ModuleFileName);
            var yaml = Path.Combine(StagingDir, "extension.yaml");
            if (!File.Exists(dll) || !File.Exists(yaml))
            {
                throw new InvalidDataException("更新包里缺少 " + ModuleFileName + " 或 extension.yaml");
            }

            if (!LooksLikeManagedAssembly(dll))
            {
                throw new InvalidDataException(ModuleFileName + " 不是有效的 PE 文件");
            }

            var yamlText = File.ReadAllText(yaml, Encoding.UTF8);
            var packageVersion = SemVersion.NormalizeText(ReadYamlVersion(yamlText));
            if (packageVersion == null)
            {
                throw new InvalidDataException("更新包里的 extension.yaml 没有 Version 字段");
            }
            if (!string.Equals(packageVersion, expectedVersion, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("包内版本(" + packageVersion + ")与 release 版本("
                                               + expectedVersion + ")不一致");
            }

            var id = ReadYamlValue(yamlText, "Id");
            if (!string.IsNullOrEmpty(id)
                && !string.Equals(id, ExtensionId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("更新包的插件 Id 不是 " + ExtensionId + "：" + id);
            }

            staged.Version = packageVersion;
            VaultLog.Info("更新已暂存：" + packageVersion + "，文件 " + string.Join(", ", staged.Files));
            return staged;
        }

        /// <summary>只保留路径的最后一段，并挡掉 ".."、绝对路径、盘符。</summary>
        public static string LeafName(string entryName)
        {
            var normalized = entryName.Replace('\\', '/').TrimEnd('/');
            var index = normalized.LastIndexOf('/');
            var leaf = index >= 0 ? normalized.Substring(index + 1) : normalized;
            return string.IsNullOrWhiteSpace(leaf) ? null : leaf;
        }

        public static bool IsSafeEntryName(string entryName)
        {
            if (string.IsNullOrWhiteSpace(entryName))
            {
                return false;
            }

            var normalized = entryName.Replace('\\', '/');
            if (normalized.StartsWith("/", StringComparison.Ordinal))
            {
                return false;
            }
            if (normalized.Contains(":"))
            {
                return false;
            }

            foreach (var segment in normalized.Split('/'))
            {
                if (segment == ".." || segment == ".")
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>路径深度：顶层文件 0，<c>Playnite-Vault/PlayniteVault.dll</c> 是 1。</summary>
        public static int DepthOf(string entryName)
        {
            if (string.IsNullOrWhiteSpace(entryName))
            {
                return 0;
            }

            var normalized = entryName.Replace('\\', '/').Trim('/');
            var depth = 0;
            foreach (var ch in normalized)
            {
                if (ch == '/')
                {
                    depth++;
                }
            }
            return depth;
        }

        /// <summary>PE 文件头必须是 "MZ"。</summary>
        public static bool LooksLikeManagedAssembly(string path)
        {
            try
            {
                using (var stream = File.OpenRead(path))
                {
                    if (stream.Length < 0x40)
                    {
                        return false;
                    }
                    return stream.ReadByte() == 'M' && stream.ReadByte() == 'Z';
                }
            }
            catch
            {
                return false;
            }
        }

        // ---------- 应用 ----------

        /// <summary>已经暂存好、且比当前版本新的包（上次下载了没来得及重启）。</summary>
        public StagedUpdate PendingStaged()
        {
            try
            {
                var yaml = Path.Combine(StagingDir, "extension.yaml");
                if (!File.Exists(yaml) || !File.Exists(Path.Combine(StagingDir, ModuleFileName)))
                {
                    return null;
                }

                var version = SemVersion.NormalizeText(ReadYamlVersion(File.ReadAllText(yaml, Encoding.UTF8)));
                if (version == null || !SemVersion.IsNewer(version, CurrentVersion()))
                {
                    return null;
                }

                return new StagedUpdate
                {
                    Version = version,
                    StagingDir = StagingDir,
                    ScriptPath = ScriptPath,
                    Files = Directory.GetFiles(StagingDir).Select(Path.GetFileName).ToList()
                };
            }
            catch (Exception ex)
            {
                VaultLog.Warn("检查暂存目录失败：" + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// 写「等 Playnite 退出 → 覆盖 → 重新拉起」的批处理。
        ///
        /// 用 .bat 而不是自己写 exe：这是唯一能在**主进程已经退出**之后还在跑、
        /// 又不需要额外分发二进制的办法。
        ///
        /// 批处理文件的编码是有讲究的：cmd.exe 按 OEM 代码页解析 .bat，
        /// 所以这里必须用 <c>CurrentCulture.TextInfo.OEMCodePage</c> 写，
        /// 否则 Playnite 装在中文路径下就会读成乱码。
        /// </summary>
        public string WriteApplyScript(StagedUpdate staged, string targetDir,
            string waitProcessName, string launchExe, string launchArgs)
        {
            Directory.CreateDirectory(UpdateRoot);

            var script = BuildApplyScript(waitProcessName, staged.StagingDir, targetDir,
                launchExe, launchArgs, ScriptLogPath);

            var encoding = OemEncoding();
            File.WriteAllText(ScriptPath, script, encoding);
            VaultLog.Info("更新脚本已写出：" + ScriptPath + "（编码=" + encoding.WebName + "）");
            return ScriptPath;
        }

        /// <summary>cmd.exe 解析 .bat 用的编码（中文系统上是 GBK）。</summary>
        public static Encoding OemEncoding()
        {
            try
            {
                return Encoding.GetEncoding(OemCodePage());
            }
            catch
            {
                return Encoding.Default;
            }
        }

        /// <summary>当前系统的 OEM 代码页（中文 Windows = 936）。</summary>
        public static int OemCodePage()
        {
            try
            {
                return CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
            }
            catch
            {
                return 936;
            }
        }

        /// <summary>
        /// 脚本里一律用 System32 的**绝对路径**调外部命令。
        ///
        /// 为什么不能直接写 <c>find</c> / <c>ping</c> / <c>xcopy</c>：
        /// Git for Windows、MSYS2、Cygwin 都会把它们的 <c>usr\bin</c> 塞进 PATH，
        /// 那里有一个 **Unix 版的 find.exe**。它不认识 <c>/I</c>，会立刻报错退出（退出码非 0），
        /// 而脚本正是用「find 退出码」判断 Playnite 有没有退出的 ——
        /// 于是脚本会误判成「已经退出了」，**在 Playnite 还开着的时候就去覆盖文件**，
        /// 结果必然覆盖失败（DLL 被占用），插件更新半途而废。
        /// 这是实测出来的后果，不是理论风险。
        /// </summary>
        private const string Sys32 = "%SystemRoot%\\System32\\";

        /// <summary>
        /// 脚本本体。参数全部由调用方注入，脚本不做任何解析 ——
        /// 这样它既好读，也能被自检工具原样跑一遍（见 tools/VaultSelfTest）。
        /// </summary>
        public static string BuildApplyScript(string waitProcessName, string stageDir,
            string targetDir, string launchExe, string launchArgs, string logPath)
        {
            var builder = new StringBuilder();
            builder.AppendLine("@echo off");
            builder.AppendLine("rem 由 Playnite Vault 插件自动生成 —— 等 Playnite 退出后替换插件文件并重新拉起。");
            // cmd.exe 是**边读边解析**批处理的：这一行之后的行都按这个代码页解释，
            // 所以中文路径（Playnite 装在中文目录下）不会变乱码。
            builder.AppendLine("chcp " + OemCodePage() + " >nul 2>&1");
            builder.AppendLine("setlocal enableextensions");
            builder.AppendLine("set \"TARGET=" + waitProcessName + "\"");
            builder.AppendLine("set \"STAGE=" + stageDir + "\"");
            builder.AppendLine("set \"DEST=" + targetDir + "\"");
            builder.AppendLine("set \"EXE=" + launchExe + "\"");
            builder.AppendLine("set \"ARGS=" + (launchArgs ?? string.Empty) + "\"");
            builder.AppendLine("set \"LOG=" + logPath + "\"");
            builder.AppendLine("");
            builder.AppendLine("echo [%date% %time%] 等待 %TARGET% 退出 >> \"%LOG%\"");
            builder.AppendLine("set /a WAITED=0");
            builder.AppendLine(":wait");
            builder.AppendLine("\"" + Sys32 + "tasklist.exe\" /FI \"IMAGENAME eq %TARGET%\" /NH 2>nul"
                               + " | \"" + Sys32 + "find.exe\" /I \"%TARGET%\" >nul");
            builder.AppendLine("if errorlevel 1 goto apply");
            builder.AppendLine("set /a WAITED+=1");
            builder.AppendLine("if %WAITED% GEQ 900 goto giveup");
            builder.AppendLine("\"" + Sys32 + "ping.exe\" -n 2 127.0.0.1 >nul");
            builder.AppendLine("goto wait");
            builder.AppendLine("");
            builder.AppendLine(":apply");
            builder.AppendLine("\"" + Sys32 + "ping.exe\" -n 2 127.0.0.1 >nul");
            builder.AppendLine("set /a TRIES=0");
            builder.AppendLine(":copy");
            builder.AppendLine("set /a TRIES+=1");
            builder.AppendLine("echo [%date% %time%] 第 %TRIES% 次复制 %STAGE% -^> %DEST% >> \"%LOG%\"");
            builder.AppendLine("\"" + Sys32 + "xcopy.exe\" /E /Y /I /Q \"%STAGE%\\*\" \"%DEST%\\\""
                               + " >> \"%LOG%\" 2>&1");
            builder.AppendLine("if not errorlevel 1 goto done");
            builder.AppendLine("if %TRIES% GEQ 15 goto giveup");
            builder.AppendLine("\"" + Sys32 + "ping.exe\" -n 3 127.0.0.1 >nul");
            builder.AppendLine("goto copy");
            builder.AppendLine("");
            builder.AppendLine(":done");
            builder.AppendLine("echo [%date% %time%] 复制完成，启动 %EXE% >> \"%LOG%\"");
            builder.AppendLine("rd /S /Q \"%STAGE%\" >nul 2>&1");
            builder.AppendLine("start \"\" \"%EXE%\" %ARGS%");
            builder.AppendLine("echo [%date% %time%] 已重新拉起 Playnite >> \"%LOG%\"");
            builder.AppendLine("goto end");
            builder.AppendLine("");
            builder.AppendLine(":giveup");
            builder.AppendLine("echo [%date% %time%] 放弃（等待超时或复制一直失败）>> \"%LOG%\"");
            builder.AppendLine("goto end");
            builder.AppendLine("");
            builder.AppendLine(":end");
            builder.AppendLine("endlocal");
            builder.AppendLine("exit /b 0");
            return builder.ToString();
        }

        /// <summary>把脚本以「无窗口」方式拉起来。</summary>
        public static bool RunApplyScript(string scriptPath, out string error)
        {
            error = null;
            try
            {
                var info = new ProcessStartInfo
                {
                    FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                    Arguments = "/c \"" + scriptPath + "\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = Path.GetDirectoryName(scriptPath)
                };

                Process.Start(info);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        // ---------- 重启 Playnite ----------

        /// <summary>
        /// 请求 Playnite 干净退出（插件替换由脚本在退出后完成）。
        ///
        /// `Playnite.PlayniteApplication.Quit(bool saveSettings)` 正是托盘「退出」那条路，
        /// 所以不会把下次启动带进安全模式。返回 false 时调用方应退回「提示用户手动退出」。
        /// </summary>
        public static bool TryQuitPlaynite(out string error)
        {
            error = null;
            try
            {
                var type = FindPlayniteApplicationType();
                if (type == null)
                {
                    error = "找不到 Playnite.PlayniteApplication 类型";
                    return false;
                }

                var currentProperty = type.GetProperty("Current",
                    BindingFlags.Public | BindingFlags.Static);
                if (currentProperty == null)
                {
                    error = "找不到 PlayniteApplication.Current";
                    return false;
                }

                var instance = currentProperty.GetValue(null, null);
                if (instance == null)
                {
                    error = "PlayniteApplication.Current 为 null（Playnite 还在启动中？）";
                    return false;
                }

                var quit = type.GetMethod("Quit", new[] { typeof(bool) });
                if (quit == null)
                {
                    error = "找不到 PlayniteApplication.Quit(bool)";
                    return false;
                }

                VaultLog.Info("调用 PlayniteApplication.Quit(true) 请求退出");
                quit.Invoke(instance, new object[] { true });
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                VaultLog.Error("请求 Playnite 退出失败", ex);
                return false;
            }
        }

        private static Type FindPlayniteApplicationType()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var type = assembly.GetType("Playnite.PlayniteApplication", false);
                    if (type != null)
                    {
                        return type;
                    }
                }
                catch
                {
                    // 有些程序集 GetType 会抛，跳过
                }
            }

            try
            {
                return Assembly.Load("Playnite").GetType("Playnite.PlayniteApplication", false);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>当前 Playnite 进程的可执行文件与进程名（脚本要靠它们等待+拉起）。</summary>
        public static bool TryDescribeCurrentProcess(out string exePath, out string processName)
        {
            exePath = null;
            processName = null;
            try
            {
                var process = Process.GetCurrentProcess();
                processName = process.ProcessName + ".exe";

                try
                {
                    var module = process.MainModule;
                    if (module != null && !string.IsNullOrEmpty(module.FileName))
                    {
                        exePath = module.FileName;
                    }
                }
                catch (Exception ex)
                {
                    VaultLog.Warn("读主模块路径失败：" + ex.Message);
                }

                if (string.IsNullOrEmpty(exePath))
                {
                    // Playnite 是便携版时，进程所在目录就是程序目录
                    exePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, processName);
                }

                return File.Exists(exePath);
            }
            catch (Exception ex)
            {
                VaultLog.Error("获取当前进程信息失败", ex);
                return false;
            }
        }
    }
}
