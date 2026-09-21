using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using PlayniteVault.Models;
using PlayniteVault.Services;

namespace VaultSelfTest
{
    /// <summary>
    /// v1.5.0 自检：把「自动更新」和「自动刷新」这两条链路全部真跑一遍。
    ///
    /// 覆盖范围（都是实跑，不是「应该能」）：
    ///   1. 版本比较（含 v 前缀 / 预发布 / 10 vs 9 的段比较）
    ///   2. release JSON 解析（GitHub 与 Gitee 两种形状），挑 zip 而不是解包器 exe
    ///   3. 更新包校验：路径穿越 / 缺 dll / 非 PE / 版本不一致，全部必须被拒
    ///   4. 完整链路：本地假服务 → 探测两源 → 下载 → 解压暂存 → 识别「待应用」
    ///   5. 镜像自动切换：主源慢到地板以下 → 自动换另一个源
    ///   6. 两个源都慢 → 仍然用主源不限速把包下完（慢 ≠ 装不上）
    ///   7. apply-update.cmd 真执行：进程还在时不动文件 → 退出后替换 → 重新拉起
    ///      （目标目录刻意用中文名，顺带验证 .bat 的 OEM 编码处理）
    ///   8. 索引指纹：只有 UpdatedAt 变化时指纹不变（这是「不白刷库」的判据）
    ///
    /// 退出码 0 = 全通过，1 = 有失败项。
    /// </summary>
    internal class Program
    {
        private static readonly List<string> Report = new List<string>();
        private static int passed;
        private static int failed;

        // ---------- 入口 ----------

        private static int Main(string[] args)
        {
            var early = EarlyMode(args);
            if (early >= 0)
            {
                return early;
            }

            try
            {
                Console.OutputEncoding = new UTF8Encoding(false);
            }
            catch
            {
                // 控制台设置失败不影响报告文件
            }

            var outPath = ArgValue(args, "--out");
            if (string.IsNullOrEmpty(outPath))
            {
                outPath = Path.Combine(Path.GetTempPath(), "VaultSelfTest-report.txt");
            }

            var root = Path.Combine(Path.GetTempPath(),
                "vault-selftest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var dataPath = Path.Combine(root, "data");
            Directory.CreateDirectory(dataPath);
            VaultLog.Init(dataPath);

            Line("VaultSelfTest —— 插件 v" + VaultUpdater.CurrentVersion()
                 + "（编译期兜底 " + VaultUpdater.FallbackVersion + "）");
            Line("临时目录：" + root);
            Line("OEM 代码页：" + VaultUpdater.OemCodePage());
            Line("系统代理：" + PlayniteVault.Net.HttpFetch.DescribeSystemProxy());

            var settings = new VaultSettings();
            var updater = new VaultUpdater(dataPath, settings);

            try
            {
                RunVersionTests();
                RunReleaseJsonTests();
                RunArchiveEntryTests();
                RunZipValidationTests(root, updater);

                using (var server = new MiniHttpServer())
                {
                    RunNetworkTests(root, dataPath, settings, updater, server);
                }

                RunFingerprintTests();
                RunApplyScriptTests(root, updater);
                RunPendingStagedTests(root, updater, dataPath);
                RunDataMigrationTests(root);
                RunThemeSyncTests(root);
                RunSaveTests(root);
                RunSidebarPanelTests();
            }
            catch (Exception ex)
            {
                // 中途炸掉也要把已跑过的结果落到报告里 —— 只看到「崩溃」而不知道
                // 前面哪些项过了，排查成本会高得多。
                Check(false, "测试执行期间没有未捕获异常",
                    ex.GetType().Name + ": " + ex.Message);
                Line("");
                Line("异常堆栈：");
                Line(ex.ToString());
            }

            Line("");
            Line("==============================================");
            Line("通过 " + passed + " 项，失败 " + failed + " 项");
            Line("==============================================");

            var text = string.Join(Environment.NewLine, Report.ToArray()) + Environment.NewLine;
            try
            {
                File.WriteAllText(outPath, text, new UTF8Encoding(false));
                Console.WriteLine("报告已写入：" + outPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine("写报告失败：" + ex.Message);
            }

            TryCleanup(root);
            return failed == 0 ? 0 : 1;
        }

        /// <summary>
        /// 特殊参数模式。**刻意只碰 System 类型**：这个 exe 会被复制成 victim.exe / launcher.exe
        /// 单独跑（模拟 Playnite 进程），那时旁边不一定有 Playnite.SDK.dll，
        /// 所以这条路径绝不能碰到插件类型。
        /// </summary>
        private static int EarlyMode(string[] args)
        {
            if (args == null || args.Length == 0)
            {
                return -1;
            }

            if (args[0] == "--sleep")
            {
                var seconds = 5;
                if (args.Length > 1)
                {
                    int.TryParse(args[1], out seconds);
                }
                Thread.Sleep(Math.Max(0, seconds) * 1000);
                return 0;
            }

            if (args[0] == "--launcher")
            {
                // 模拟「被脚本重新拉起的 Playnite」：留个记号就退出
                File.WriteAllText(
                    Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launcher-ran.txt"),
                    DateTime.Now.ToString("o"));
                return 0;
            }

            return -1;
        }

        private static string ArgValue(string[] args, string name)
        {
            if (args == null)
            {
                return null;
            }
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == name)
                {
                    return args[i + 1];
                }
            }
            return null;
        }

        // ---------- 断言 ----------

        private static void Group(string title)
        {
            Line("");
            Line("== " + title + " ==");
        }

        private static void Check(bool ok, string name, string detail = null)
        {
            if (ok)
            {
                passed++;
                Line("  [通过] " + name);
            }
            else
            {
                failed++;
                Line("  [失败] " + name + (string.IsNullOrEmpty(detail) ? "" : " —— " + detail));
            }
        }

        private static void Line(string text)
        {
            Report.Add(text);
            Console.WriteLine(text);
        }

        // ---------- 1. 版本比较 ----------

        private static void RunVersionTests()
        {
            Group("版本比较 SemVersion");

            Check(SemVersion.IsNewer("1.5.0", "1.4.0"), "1.5.0 > 1.4.0");
            Check(SemVersion.IsNewer("v1.5.0", "1.4.9"), "tag 上的 v 前缀能吃下");
            Check(!SemVersion.IsNewer("1.4.0", "1.4.0"), "同版本不算新");
            Check(!SemVersion.IsNewer("1.4.0", "1.5.0"), "旧版本不算新");
            Check(SemVersion.IsNewer("1.10.0", "1.9.9"), "1.10.0 > 1.9.9（按段比，不是按字符串）");
            // "1.5" 会补成 "1.5.0"，所以两者是同一个版本，不能算「有新版本」（不然会死循环更新）
            Check(!SemVersion.IsNewer("1.5.0", "1.5"), "1.5 与 1.5.0 视为同版本（不会反复提示更新）");
            Check(SemVersion.IsNewer("1.5.1", "1.5"), "1.5.1 > 1.5");
            Check(SemVersion.IsNewer("2.0.0.1", "2.0.0"), "四段版本号也能比");
            Check(SemVersion.IsNewer("1.5.0", "1.5.0-beta.1"), "正式版 > 预发布版");
            Check(!SemVersion.IsNewer("1.5.0-beta.1", "1.5.0"), "预发布版 < 正式版");

            Check(SemVersion.NormalizeText("PlayniteVault-1.5.0.zip") == "1.5.0",
                "从素材名里也能抠出版本", SemVersion.NormalizeText("PlayniteVault-1.5.0.zip"));
            Check(SemVersion.NormalizeText("v2.0.1") == "2.0.1", "规范化 v2.0.1 → 2.0.1");
            Check(SemVersion.NormalizeText("1.6") == "1.6.0", "1.6 补成 1.6.0");
            Check(SemVersion.NormalizeText("latest") == null, "解析不了返回 null");
            Check(VaultUpdater.ReadYamlVersion("Id: A\nVersion: 2.3.4\nModule: B\n") == "2.3.4",
                "从 extension.yaml 里读出 Version");
            Check(VaultUpdater.ReadYamlVersion("没有版本行") == null, "没有 Version 行返回 null");
        }

        // ---------- 2. release JSON ----------

        private static void RunReleaseJsonTests()
        {
            Group("release JSON 解析");

            var github = VaultUpdater.ParseGitHubRelease(
                Fixtures.GitHubReleaseJson("1.5.0", "https://example/PlayniteVault-1.5.0.zip", 12345));
            Check(github != null && github.Version == "1.5.0", "GitHub 解析出版本");
            Check(github != null && github.Assets.Count == 2, "两个附件都读到了",
                github == null ? "" : github.Assets.Count.ToString());

            var pick = github == null ? null : github.PickPluginAsset();
            Check(pick != null && pick.Name == "PlayniteVault-1.5.0.zip",
                "挑出的是插件 zip，不是同一个 release 里的解包器 exe",
                pick == null ? "(null)" : pick.Name);
            Check(pick != null && pick.Size == 12345, "附件体积读到了");
            Check(github != null && !string.IsNullOrEmpty(github.Notes), "更新说明读到了");

            var gitee = VaultUpdater.ParseGiteeRelease(
                Fixtures.GiteeReleaseJson("1.6.0", "https://example/PlayniteVault-1.6.0.zip", 999));
            Check(gitee != null && gitee.Version == "1.6.0", "Gitee 解析出版本");
            var giteePick = gitee == null ? null : gitee.PickPluginAsset();
            Check(giteePick != null && giteePick.DownloadUrl == "https://example/PlayniteVault-1.6.0.zip",
                "Gitee 的 browser_download_url 正确取用",
                giteePick == null ? "(null)" : giteePick.DownloadUrl);

            Check(VaultUpdater.ParseGitHubRelease("{不是 JSON") == null, "坏 JSON 返回 null 而不是抛异常");
            Check(VaultUpdater.ParseGitHubRelease("{\"assets\":[]}") == null, "没有 tag/name 时返回 null");
        }

        // ---------- 3. 归档条目名 ----------

        private static void RunArchiveEntryTests()
        {
            Group("zip 条目路径判定");

            Check(VaultUpdater.IsSafeEntryName("Playnite-Vault/PlayniteVault.dll"), "正常条目放行");
            Check(!VaultUpdater.IsSafeEntryName("../evil.dll"), "../ 被拦");
            Check(!VaultUpdater.IsSafeEntryName("a/../../evil.dll"), "中途的 .. 也被拦");
            Check(!VaultUpdater.IsSafeEntryName("/etc/evil.dll"), "绝对路径被拦");
            Check(!VaultUpdater.IsSafeEntryName("C:/evil.dll"), "带盘符被拦");
            Check(VaultUpdater.DepthOf("PlayniteVault.dll") == 0, "DepthOf 顶层 = 0");
            Check(VaultUpdater.DepthOf("Playnite-Vault/PlayniteVault.dll") == 1, "DepthOf 一层 = 1");
            Check(VaultUpdater.DepthOf("a/b/c.dll") == 2, "DepthOf 两层 = 2");
            Check(VaultUpdater.LeafName("Playnite-Vault/使用说明.txt") == "使用说明.txt", "LeafName 取最后一段");
        }

        // ---------- 4. 更新包校验 ----------

        private static void RunZipValidationTests(string root, VaultUpdater updater)
        {
            Group("更新包校验");

            var dir = Path.Combine(root, "zips");

            var good = Fixtures.WriteFile(Path.Combine(dir, "good.zip"),
                Fixtures.BuildPluginZip("9.9.9", 0));
            var staged = updater.Stage(good, "9.9.9");
            Check(staged.Files.Contains("PlayniteVault.dll") && staged.Files.Contains("extension.yaml")
                  && staged.Files.Contains("使用说明.txt"),
                "正常包装出 dll / extension.yaml / 中文名文件",
                string.Join(",", staged.Files.ToArray()));

            ExpectReject("拒绝路径穿越的包", updater,
                Fixtures.WriteFile(Path.Combine(dir, "evil.zip"),
                    Fixtures.BuildZip("9.9.9", "../../evil.dll", true, true)), "9.9.9");

            ExpectReject("拒绝缺 PlayniteVault.dll 的包", updater,
                Fixtures.WriteFile(Path.Combine(dir, "nodll.zip"),
                    Fixtures.BuildZip("9.9.9", "keep.dll", false, true)), "9.9.9");

            ExpectReject("拒绝缺 extension.yaml 的包", updater,
                Fixtures.WriteFile(Path.Combine(dir, "noyaml.zip"),
                    Fixtures.BuildZip("9.9.9", "PlayniteVault.dll", true, false)), "9.9.9");

            ExpectReject("拒绝包内版本 ≠ release 版本的包", updater,
                Fixtures.WriteFile(Path.Combine(dir, "mismatch.zip"),
                    Fixtures.BuildPluginZip("1.0.0", 0)), "9.9.9");

            ExpectReject("拒绝 dll 不是 PE 文件的包", updater,
                Fixtures.WriteFile(Path.Combine(dir, "notpe.zip"),
                    Fixtures.BuildZipWithRawDll("9.9.9", new byte[] { 0x41, 0x42, 0x43, 0x44 })), "9.9.9");
        }

        private static void ExpectReject(string name, VaultUpdater updater, string zipPath,
            string expectedVersion)
        {
            try
            {
                updater.Stage(zipPath, expectedVersion);
                Check(false, name, "居然通过了");
            }
            catch (InvalidDataException)
            {
                Check(true, name);
            }
            catch (Exception ex)
            {
                Check(false, name, "抛的是 " + ex.GetType().Name + "：" + ex.Message);
            }
        }

        // ---------- 5. 网络链路 ----------

        private static void RunNetworkTests(string root, string dataPath, VaultSettings settings,
            VaultUpdater updater, MiniHttpServer server)
        {
            Group("更新链路（本地假 GitHub / 假 Gitee）");

            // 150 KB 的包：够大，能让速度地板在 3 秒判定点之前还没传完
            var zip = Fixtures.BuildPluginZip("9.9.9", 140 * 1024);
            Line("  测试包体积：" + zip.Length + " 字节");

            var urlGitHub = server.BaseUrl + "dl/github/PlayniteVault-9.9.9.zip";
            var urlGitee = server.BaseUrl + "dl/gitee/PlayniteVault-9.9.9.zip";
            var urlGitHubSlow = server.BaseUrl + "dl/github-slow/PlayniteVault-9.9.9.zip";
            var urlGiteeSlow = server.BaseUrl + "dl/gitee-slow/PlayniteVault-9.9.9.zip";

            server.Set("dl/github/PlayniteVault-9.9.9.zip",
                new MockRoute { Body = zip, ContentType = "application/octet-stream" });
            server.Set("dl/gitee/PlayniteVault-9.9.9.zip",
                new MockRoute { Body = zip, ContentType = "application/octet-stream" });

            // ~34 KB/s（4 KB / 120 ms）—— 明显低于 40 KB/s 的生产地板
            server.Set("dl/github-slow/PlayniteVault-9.9.9.zip", MockRoute.Throttled(zip, 4096, 120));
            server.Set("dl/gitee-slow/PlayniteVault-9.9.9.zip", MockRoute.Throttled(zip, 4096, 120));

            Environment.SetEnvironmentVariable("VAULT_UPDATE_TEST", "1");
            Environment.SetEnvironmentVariable("VAULT_UPDATE_GITHUB_LATEST",
                server.BaseUrl + "github/releases/latest");
            Environment.SetEnvironmentVariable("VAULT_UPDATE_GITEE_LATEST",
                server.BaseUrl + "gitee/releases/latest");
            // 自检把「3 秒采样点」和「地板」调紧，让「太慢换源」在几秒内就能触发而不是几十秒
            Environment.SetEnvironmentVariable("VAULT_UPDATE_MIN_SPEED_KB", "500");
            Environment.SetEnvironmentVariable("VAULT_UPDATE_SPEED_FLOOR_BYTES", "20480");

            try
            {
                // --- 快照 1：两个源都正常
                server.Set("github/releases/latest",
                    MockRoute.Text(Fixtures.GitHubReleaseJson("9.9.9", urlGitHub, zip.Length)));
                server.Set("gitee/releases/latest",
                    MockRoute.Text(Fixtures.GiteeReleaseJson("9.9.9", urlGitee, zip.Length)));

                server.ResetHits();
                var check = updater.Check();

                Check(check.HasUpdate, "检测到新版本", check.Error);
                Check(check.LatestVersion == "9.9.9", "最新版本号 = 9.9.9", check.LatestVersion);
                Check(check.Candidates.Count == 2, "两个源都可用时给出 2 个候选",
                    check.Candidates.Count.ToString());
                Check(check.Candidates.Count > 0 && check.Candidates[0].Asset.Name == "PlayniteVault-9.9.9.zip",
                    "候选里选中的是插件 zip");

                var staged = SafeDownload(updater, check, out string downloadError);
                Check(staged != null && staged.Version == "9.9.9", "下载并暂存成功", downloadError);
                if (staged != null)
                {
                    Check(File.Exists(Path.Combine(staged.StagingDir, "PlayniteVault.dll")), "暂存目录里有 dll");
                    Check(File.Exists(Path.Combine(staged.StagingDir, "使用说明.txt")), "中文文件名也解出来了");
                    Check(File.Exists(staged.ZipPath), "下载下来的原始 zip 留在 update/ 里（便于回滚/诊断）");
                }

                // --- 快照 2：主源（GitHub）慢到底板以下 → 必须自动换 Gitee
                // 给 Gitee 的探测加 700ms 延迟，保证 GitHub 排在主源位置
                server.Set("github/releases/latest",
                    MockRoute.Text(Fixtures.GitHubReleaseJson("9.9.9", urlGitHubSlow, zip.Length)));
                server.Set("gitee/releases/latest",
                    new MockRoute
                    {
                        Body = new UTF8Encoding(false).GetBytes(
                            Fixtures.GiteeReleaseJson("9.9.9", urlGitee, zip.Length)),
                        ContentType = "application/json; charset=utf-8",
                        HeadDelayMs = 700
                    });

                server.ResetHits();
                var check2 = updater.Check();
                Check(check2.Candidates.Count == 2, "慢源场景下仍然两个候选都探到");
                Check(check2.Candidates.Count > 0 && check2.Candidates[0].Mirror == "github",
                    "低延迟的 GitHub 被选为主源",
                    check2.Candidates.Count > 0 ? check2.Candidates[0].Describe() : "(无候选)");

                var staged2 = SafeDownload(updater, check2, out string switchError);
                Check(server.HitCount("dl/github-slow/PlayniteVault-9.9.9.zip") >= 1,
                    "确实先试了慢的主源");
                Check(server.HitCount("dl/gitee/PlayniteVault-9.9.9.zip") >= 1,
                    "慢到地板以下后自动换了 Gitee");
                Check(staged2 != null && staged2.Version == "9.9.9", "换源之后仍然装上了 9.9.9",
                    switchError);

                // --- 快照 3：两个源都慢 → 不许直接失败，要用主源不限速下完
                server.Set("github/releases/latest",
                    MockRoute.Text(Fixtures.GitHubReleaseJson("9.9.9", urlGitHubSlow, zip.Length)));
                server.Set("gitee/releases/latest",
                    MockRoute.Text(Fixtures.GiteeReleaseJson("9.9.9", urlGiteeSlow, zip.Length)));

                server.ResetHits();
                var check3 = updater.Check();
                var staged3 = SafeDownload(updater, check3, out string floorError);
                Check(staged3 != null && staged3.Version == "9.9.9",
                    "所有源都低于地板时，用主源不限速仍然把包装完（慢 ≠ 装不上）",
                    floorError);
                Check(server.HitCount("dl/gitee-slow/PlayniteVault-9.9.9.zip") >= 1
                      || server.HitCount("dl/github-slow/PlayniteVault-9.9.9.zip") >= 2,
                    "兜底重试确实又打了一次下载接口");

                // --- 快照 4：源不可达（连接被掐）→ 另一个源顶上
                server.Set("github/releases/latest", new MockRoute { Drop = true });
                server.Set("gitee/releases/latest",
                    MockRoute.Text(Fixtures.GiteeReleaseJson("9.9.9", urlGitee, zip.Length)));

                server.ResetHits();
                var check4 = updater.Check();
                Check(check4.HasUpdate, "GitHub 连不通时，Gitee 顶上", check4.Error);
                Check(check4.Candidates.Count == 1
                      && check4.Candidates[0].Mirror == "gitee",
                    "候选只剩 Gitee",
                    check4.Candidates.Count.ToString());
                Check(check4.Attempts.Count >= 2, "探测记录里两个源都记下来了");

                // --- 快照 5：远端没有新版本
                server.Set("github/releases/latest",
                    MockRoute.Text(Fixtures.GitHubReleaseJson("1.0.0", urlGitHub, zip.Length)));
                server.Set("gitee/releases/latest",
                    MockRoute.Text(Fixtures.GiteeReleaseJson("1.0.0", urlGitee, zip.Length)));

                var check5 = updater.Check();
                Check(!check5.HasUpdate && string.IsNullOrEmpty(check5.Error),
                    "远端版本比当前旧时不提示更新",
                    check5.LatestVersion + " / " + check5.Error);

                // --- 快照 6：「跳过此版本」
                // 两个源都要设成 9.9.9：候选是按延迟排序的，只改 Gitee 的话
                // 并列延迟下 GitHub 仍会排在主源位置，结果读到的还是上一个快照的 1.0.0。
                settings.SkippedVersion = "9.9.9";
                server.Set("github/releases/latest",
                    MockRoute.Text(Fixtures.GitHubReleaseJson("9.9.9", urlGitHub, zip.Length)));
                server.Set("gitee/releases/latest",
                    MockRoute.Text(Fixtures.GiteeReleaseJson("9.9.9", urlGitee, zip.Length)));
                var check6 = updater.Check();
                Check(check6.HasUpdate == false && check6.SkippedByUser,
                    "已跳过的版本不再提示",
                    "HasUpdate=" + check6.HasUpdate + " Skipped=" + check6.SkippedByUser
                    + " latest=" + check6.LatestVersion + " error=" + check6.Error);
                settings.SkippedVersion = string.Empty;
            }
            finally
            {
                Environment.SetEnvironmentVariable("VAULT_UPDATE_TEST", null);
                Environment.SetEnvironmentVariable("VAULT_UPDATE_GITHUB_LATEST", null);
                Environment.SetEnvironmentVariable("VAULT_UPDATE_GITEE_LATEST", null);
                Environment.SetEnvironmentVariable("VAULT_UPDATE_MIN_SPEED_KB", null);
                Environment.SetEnvironmentVariable("VAULT_UPDATE_SPEED_FLOOR_BYTES", null);
            }
        }

        /// <summary>
        /// 候选为空时 <c>Download</c> 会抛 <see cref="InvalidOperationException"/> ——
        /// 这是产品代码的正确行为（没源就别装），但自检里不该因为它中断后面所有断言。
        /// </summary>
        private static StagedUpdate SafeDownload(VaultUpdater updater, UpdateCheckResult check,
            out string error)
        {
            error = null;
            if (check == null || check.Candidates.Count == 0)
            {
                error = "没有可用候选（" + (check == null ? "check 为 null" : check.Error) + "）";
                return null;
            }

            try
            {
                return updater.Download(check, null, null);
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return null;
            }
        }

        // ---------- 6. 索引指纹 ----------

        private static void RunFingerprintTests()
        {
            Group("索引指纹（自动刷新「不白刷库」的判据）");

            var a = Index(DateTime.UtcNow, "x", "X", "1.0", 10);
            var b = Index(DateTime.UtcNow.AddDays(3), "x", "X", "1.0", 10);
            Check(LibraryAutoRefresh.Fingerprint(a) == LibraryAutoRefresh.Fingerprint(b),
                "只有 UpdatedAt 变了 → 指纹不变（避免每次归档都白刷一次库）");

            Check(LibraryAutoRefresh.Fingerprint(a)
                  != LibraryAutoRefresh.Fingerprint(Index(DateTime.UtcNow, "x", "X", "1.1", 10)),
                "版本变了 → 指纹变");
            Check(LibraryAutoRefresh.Fingerprint(a)
                  != LibraryAutoRefresh.Fingerprint(Index(DateTime.UtcNow, "x", "X 改名", "1.0", 10)),
                "名称变了 → 指纹变");
            Check(LibraryAutoRefresh.Fingerprint(a)
                  != LibraryAutoRefresh.Fingerprint(Index(DateTime.UtcNow, "x", "X", "1.0", 11)),
                "体积变了 → 指纹变");
            Check(LibraryAutoRefresh.Fingerprint(a)
                  != LibraryAutoRefresh.Fingerprint(Index(DateTime.UtcNow, "y", "X", "1.0", 10)),
                "Id 变了 → 指纹变");

            var reordered = new RepositoryIndex();
            reordered.Apps.Add(new AppEntry { Id = "y", Name = "Y", Version = "1.0", TotalBytes = 20 });
            reordered.Apps.Add(new AppEntry { Id = "x", Name = "X", Version = "1.0", TotalBytes = 10 });
            var reordered2 = new RepositoryIndex();
            reordered2.Apps.Add(new AppEntry { Id = "x", Name = "X", Version = "1.0", TotalBytes = 10 });
            reordered2.Apps.Add(new AppEntry { Id = "y", Name = "Y", Version = "1.0", TotalBytes = 20 });
            Check(LibraryAutoRefresh.Fingerprint(reordered) == LibraryAutoRefresh.Fingerprint(reordered2),
                "条目顺序不同不影响指纹（索引重写不会误判成有变化）");

            Check(LibraryAutoRefresh.Fingerprint(null) == string.Empty, "null 索引返回空指纹");
            Check(LibraryAutoRefresh.Clamp(1) == LibraryAutoRefresh.MinMinutes, "间隔下限被钳住");
            Check(LibraryAutoRefresh.Clamp(999999) == LibraryAutoRefresh.MaxMinutes, "间隔上限被钳住");
            Check(LibraryAutoRefresh.Clamp(60) == 60, "合法间隔原样保留");
        }

        private static RepositoryIndex Index(DateTime updatedAt, string id, string name,
            string version, long bytes)
        {
            var index = new RepositoryIndex { UpdatedAt = updatedAt };
            index.Apps.Add(new AppEntry
            {
                Id = id,
                Name = name,
                Version = version,
                TotalBytes = bytes,
                LaunchExe = "app.exe"
            });
            return index;
        }

        // ---------- 7. apply-update.cmd 真执行 ----------

        private static void RunApplyScriptTests(string root, VaultUpdater updater)
        {
            Group("apply-update.cmd 真执行（等进程退出 → 替换 → 重新拉起）");

            var e2e = Path.Combine(root, "e2e");
            // 目标目录刻意用中文名：.bat 是按 OEM 代码页解析的，编码搞错这里必炸
            var dest = Path.Combine(e2e, "插件目录");
            var stage = Path.Combine(e2e, "staging");
            var logPath = Path.Combine(e2e, "apply.log");
            Directory.CreateDirectory(dest);
            Directory.CreateDirectory(stage);

            File.WriteAllText(Path.Combine(dest, "PlayniteVault.dll"), "OLD", Encoding.UTF8);
            File.WriteAllText(Path.Combine(dest, "keep.txt"), "保留我", Encoding.UTF8);
            File.WriteAllText(Path.Combine(stage, "PlayniteVault.dll"), "NEW", Encoding.UTF8);
            File.WriteAllText(Path.Combine(stage, "extension.yaml"), "Version: 9.9.9\n", Encoding.UTF8);
            File.WriteAllText(Path.Combine(stage, "icon.png"), "PNG", Encoding.UTF8);
            File.WriteAllText(Path.Combine(stage, "使用说明.txt"), "说明", Encoding.UTF8);

            var selfDir = AppDomain.CurrentDomain.BaseDirectory;
            var selfExe = Path.Combine(selfDir, "VaultSelfTest.exe");
            var victim = Path.Combine(e2e, "victim.exe");
            var launcher = Path.Combine(e2e, "launcher.exe");

            CopySelf(selfExe, victim);
            CopySelf(selfExe, launcher);

            var victimProcess = Process.Start(new ProcessStartInfo(victim, "--sleep 9")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
            Check(victimProcess != null, "起了一个「正在运行的 Playnite」占位进程（victim.exe）");
            Thread.Sleep(1200);
            // 万一 victim 自己起不来（缺依赖/被杀软拦下），后面「等进程退出」就变成了空测 ——
            // 必须先确认它真的活着，否则这项测试会以「假通过」收场。
            // 注意 ExitCode 只能在进程**已退出**时读，否则它自己会抛 ——
            // 所以诊断文本必须在这之前就算好，不能写在 Check 的实参里。
            string victimDetail = null;
            if (victimProcess == null)
            {
                victimDetail = "(Process.Start 返回 null)";
            }
            else if (victimProcess.HasExited)
            {
                victimDetail = "已退出，ExitCode=" + victimProcess.ExitCode;
            }

            Check(victimProcess != null && !victimProcess.HasExited,
                "victim 进程确实在运行（否则「等退出」这一项就是空测）", victimDetail);

            var scriptPath = Path.Combine(e2e, "apply-update.cmd");
            var script = VaultUpdater.BuildApplyScript("victim.exe", stage, dest, launcher,
                "--launcher", logPath);
            Check(script.Contains("chcp " + VaultUpdater.OemCodePage()),
                "脚本里显式设了代码页（中文路径不乱码的前提）");
            Check(script.Contains("tasklist") && script.Contains("xcopy"),
                "脚本用的是 tasklist 等待 + xcopy 覆盖");
            // 这条是回归防线：只要脚本里出现裸的 find/ping/tasklist/xcopy，
            // 装了 Git / MSYS2 的机器上就会被 Unix 版同名程序抢走，等待循环立刻失效。
            Check(script.Contains("%SystemRoot%\\System32\\find.exe")
                  && script.Contains("%SystemRoot%\\System32\\tasklist.exe")
                  && !script.Contains("\nfind /I")
                  && !script.Contains(">nul | find")
                  && !script.Contains("\nping -n"),
                "外部命令一律走 System32 绝对路径（PATH 上有 Git 的 Unix find 也不会误判）");
            File.WriteAllText(scriptPath, script, VaultUpdater.OemEncoding());

            string runError;
            Check(VaultUpdater.RunApplyScript(scriptPath, out runError), "更新脚本能无窗口拉起来", runError);

            Thread.Sleep(2500);
            Check(SafeRead(Path.Combine(dest, "PlayniteVault.dll")) == "OLD",
                "Playnite 还开着的时候绝不动文件（不然覆盖必然失败）",
                "当前内容 = " + SafeRead(Path.Combine(dest, "PlayniteVault.dll")));

            var marker = Path.Combine(e2e, "launcher-ran.txt");
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline && !File.Exists(marker))
            {
                Thread.Sleep(250);
            }

            Check(File.ReadAllText(Path.Combine(dest, "PlayniteVault.dll")) == "NEW",
                "进程退出后文件被替换成新版本",
                SafeRead(Path.Combine(dest, "PlayniteVault.dll")));
            Check(File.Exists(Path.Combine(dest, "extension.yaml")), "extension.yaml 也替换了");
            Check(SafeRead(Path.Combine(dest, "使用说明.txt")) == "说明",
                "中文文件名在脚本里没乱码（OEM 编码 + chcp 生效）");
            Check(SafeRead(Path.Combine(dest, "keep.txt")) == "保留我",
                "dest 里不在暂存包里的文件没被删掉");
            Check(File.Exists(marker), "脚本把 Playnite 重新拉起来了（launcher-ran.txt 已生成）");
            Check(!Directory.Exists(stage), "复制完成后清掉了暂存目录");
            Check(File.Exists(logPath) && SafeReadOem(logPath).Contains("已重新拉起"),
                "脚本日志里记下了「已重新拉起」",
                File.Exists(logPath) ? "日志大小 " + new FileInfo(logPath).Length + " 字节" : "(日志不存在)");

            if (victimProcess != null && !victimProcess.HasExited)
            {
                try { victimProcess.Kill(); } catch { }
            }
        }

        /// <summary>复制自身并带上 .config —— 复制出来的 exe 要有独立的进程名。</summary>
        private static void CopySelf(string sourceExe, string targetExe)
        {
            File.Copy(sourceExe, targetExe, true);

            var config = Path.ChangeExtension(sourceExe, ".exe.config");
            if (File.Exists(config))
            {
                File.Copy(config, Path.ChangeExtension(targetExe, ".exe.config"), true);
            }

            // victim/launcher 只走 System 类型那条路径，但把依赖放齐更保险
            foreach (var dll in new[] { "Playnite.SDK.dll", "Newtonsoft.Json.dll" })
            {
                var from = Path.Combine(Path.GetDirectoryName(sourceExe), dll);
                if (File.Exists(from))
                {
                    File.Copy(from, Path.Combine(Path.GetDirectoryName(targetExe), dll), true);
                }
            }
        }

        // ---------- 8. 「已下载未应用」的识别 ----------

        private static void RunPendingStagedTests(string root, VaultUpdater updater, string dataPath)
        {
            Group("暂存包识别（下载了但没来得及重启）");

            // 上一步 Stage 的最后一个是 notpe 之前那个失败的包；这里重新放一个明确更新的
            var newer = Fixtures.WriteFile(Path.Combine(root, "pending-newer.zip"),
                Fixtures.BuildPluginZip("9.9.9", 0));
            updater.Stage(newer, "9.9.9");
            var pending = updater.PendingStaged();
            Check(pending != null && pending.Version == "9.9.9",
                "识别出「已下载的 9.9.9 比当前版本新，等重启就装上」",
                pending == null ? "(null)" : pending.Version);

            var older = Fixtures.WriteFile(Path.Combine(root, "pending-older.zip"),
                Fixtures.BuildPluginZip("1.0.0", 0));
            updater.Stage(older, "1.0.0");
            Check(updater.PendingStaged() == null,
                "暂存的是更旧的版本时，不认作待应用（不会把用户降级）");

            // 运行时状态：真写一遍、真读回来。它是「自动刷新不白刷库」的落地点，
            // 必须确认它是独立文件、且能完整往返（尤其是 UTC 时间）。
            var state = VaultStateStore.Load(dataPath);
            state.AppliedIndexHash = "deadbeef";
            state.LastMirror = "gitee";
            state.ConsecutiveRefreshFailures = 3;
            state.LastIndexCheckUtc = new DateTime(2026, 9, 19, 3, 4, 5, DateTimeKind.Utc);
            VaultStateStore.Save(dataPath, state);

            var statePath = Path.Combine(dataPath, VaultRuntimeState.FileName);
            Check(File.Exists(statePath), "运行时状态写到独立的 state.json（不污染 settings.json）");
            Check(!File.Exists(Path.Combine(dataPath, "settings.json")),
                "写状态不会顺手生成 settings.json");

            var reloaded = VaultStateStore.Load(dataPath);
            var expectedUtc = new DateTime(2026, 9, 19, 3, 4, 5, DateTimeKind.Utc);
            Check(reloaded.AppliedIndexHash == "deadbeef"
                  && reloaded.LastMirror == "gitee"
                  && reloaded.ConsecutiveRefreshFailures == 3
                  && reloaded.LastIndexCheckUtc.HasValue
                  && reloaded.LastIndexCheckUtc.Value.ToUniversalTime() == expectedUtc,
                "state.json 能完整往返（指纹 / 镜像 / 失败计数 / UTC 时间都留住了）",
                reloaded.AppliedIndexHash + " / " + reloaded.LastMirror + " / "
                + reloaded.ConsecutiveRefreshFailures + " / "
                + (reloaded.LastIndexCheckUtc.HasValue
                    ? reloaded.LastIndexCheckUtc.Value.ToString("o") : "(null)"));
        }

        // ---------- 9. 插件改名后的数据目录迁移 ----------

        /// <summary>
        /// v1.6.0 把 Id 从 VaultDemo_&lt;guid&gt; 改成 Playnite-Vault ——
        /// Playnite 用 Id 当扩展数据目录名，所以老数据必须自己搬过来，
        /// 否则用户要重新填 WebDAV 地址、本地库索引全丢。
        /// 这里全用真目录真文件跑，别拿「应该会搬」当结论。
        /// </summary>
        // ---------- 主题同步（对内存版 WebDAV 全实跑） ----------

        /// <summary>
        /// 主题同步的关键行为都在这里被真跑一遍：上传/幂等/增量/强制/dry-run/
        /// 下载/只增不删/双向冲突留档/路径穿越/索引归属。
        /// 刻意不碰真实 NAS —— 这个自检必须能离线跑。
        /// </summary>
        private static void RunThemeSyncTests(string root)
        {
            Group("主题同步（远端 = 内存版 WebDAV，全实跑）");

            using (var server = new MockWebDavServer())
            {
                var local = Path.Combine(root, "themes-a");
                var state = Path.Combine(root, "theme-state-a.json");

                WriteLocal(Path.Combine(local, "Desktop", "Alpha", "theme.yaml"),
                    "Id: Alpha\r\nName: Alpha Theme\r\nVersion: 1.0\r\nMode: Desktop\r\n");
                WriteLocal(Path.Combine(local, "Desktop", "Alpha", "desktop.xaml"), "<Grid/>");
                WriteLocal(Path.Combine(local, "Desktop", "Alpha", "Resources", "logo.txt"), "hello");
                WriteLocal(Path.Combine(local, "Fullscreen", "Beta", "theme.yaml"),
                    "Id: Beta\r\nName: Beta Theme\r\nVersion: 2.1\r\nMode: Fullscreen\r\n");
                WriteLocal(Path.Combine(local, "Fullscreen", "Beta", "fullscreen.xaml"), "<StackPanel/>");

                // 干扰项：没有 theme.yaml 的目录不算主题；.part 是下载残留
                WriteLocal(Path.Combine(local, "Desktop", "NotATheme", "readme.txt"), "not a theme");
                WriteLocal(Path.Combine(local, "Desktop", "Alpha", "desktop.xaml.part"), "junk");

                // ---- 1. 首次上传 ----
                var r1 = SyncThemes(server, local, state, ThemeSyncMode.Upload);
                Check(r1.Counters.ThemesUploaded == 2, "首次上传：两个主题都上去了",
                    "实际 " + r1.Counters.ThemesUploaded);
                Check(r1.Counters.FilesUploaded == 5, "首次上传：文件数 = 3 + 2",
                    "实际 " + r1.Counters.FilesUploaded);
                Check(server.Has("themes/Desktop/Alpha/theme.yaml")
                      && server.Has("themes/Fullscreen/Beta/fullscreen.xaml"),
                    "两个模式各自落到 themes/Desktop 与 themes/Fullscreen 下");
                Check(server.GetText("themes/Desktop/Alpha/Resources/logo.txt") == "hello",
                    "嵌套子目录里的文件也传上去了，内容一致");
                Check(server.Has("themes/Desktop/Alpha/manifest.json"), "每个主题写了 manifest.json");
                Check(server.Has("themes/index.json"), "写出了 themes/index.json");
                Check(server.GetText("themes/index.json").Contains("Alpha Theme"),
                    "索引里带上了 theme.yaml 里的 Name");
                Check(server.GetText("themes/index.json").Contains("\"Version\": \"1.0\"")
                      || server.GetText("themes/index.json").Contains("1.0"),
                    "索引里带上了 theme.yaml 里的 Version");
                Check(!server.AllFiles().Contains("themes/Desktop/NotATheme/readme.txt"),
                    "没有 theme.yaml 的目录不会被当成主题");
                Check(!server.AllFiles().Contains("themes/Desktop/Alpha/desktop.xaml.part"),
                    ".part 残留不会被上传");

                // ---- 2. 幂等 ----
                var r2 = SyncThemes(server, local, state, ThemeSyncMode.Upload);
                Check(r2.Counters.FilesUploaded == 0 && r2.Counters.ThemesUnchanged == 2,
                    "第二次跑：指纹一致，一个文件都不再传",
                    string.Format("传了 {0} 个文件，未变 {1} 个主题",
                        r2.Counters.FilesUploaded, r2.Counters.ThemesUnchanged));

                // ---- 3. 改一个文件：只重传那一个 ----
                WriteLocal(Path.Combine(local, "Desktop", "Alpha", "desktop.xaml"),
                    "<Grid Background=\"red\"/>");
                var r3 = SyncThemes(server, local, state, ThemeSyncMode.Upload);
                Check(r3.Counters.FilesUploaded == 1, "改一个文件只重传那一个",
                    "实际传了 " + r3.Counters.FilesUploaded);
                Check(server.GetText("themes/Desktop/Alpha/desktop.xaml") == "<Grid Background=\"red\"/>",
                    "远端内容已更新");
                Check(r3.Counters.FilesSkipped == 2, "同主题里没变的两个文件被跳过",
                    "实际跳过 " + r3.Counters.FilesSkipped);

                // ---- 4. 强制整树重传 ----
                var r4 = SyncThemes(server, local, state, ThemeSyncMode.Upload, false, true);
                Check(r4.Counters.FilesUploaded == 5,
                    "--force 连「指纹一致」的主题也重传（共 5 个文件）",
                    "实际 " + r4.Counters.FilesUploaded);

                // ---- 5. dry-run 只列计划 ----
                WriteLocal(Path.Combine(local, "Desktop", "Gamma", "theme.yaml"),
                    "Id: Gamma\r\nMode: Desktop\r\n");
                var r5 = SyncThemes(server, local, state, ThemeSyncMode.Upload, true);
                Check(!server.Has("themes/Desktop/Gamma/theme.yaml"), "--dry-run 不往远端写任何东西");
                Check(r5.Counters.ThemesUploaded == 0 && r5.Plan.Count > 0,
                    "dry-run 把计划列出来但计数器保持为零");

                // ---- 6. 空目录整树拉下来 ----
                var pulled = Path.Combine(root, "themes-pulled");
                var pulledState = Path.Combine(root, "theme-state-pulled.json");
                var r6 = SyncThemes(server, pulled, pulledState, ThemeSyncMode.Download);
                Check(r6.Counters.ThemesDownloaded == 2, "空目录能把远端整树拉下来",
                    "实际 " + r6.Counters.ThemesDownloaded);
                Check(ReadLocal(Path.Combine(pulled, "Desktop", "Alpha", "desktop.xaml"))
                      == "<Grid Background=\"red\"/>", "下载回来的字节与远端一致");
                Check(ReadLocal(Path.Combine(pulled, "Desktop", "Alpha", "Resources", "logo.txt")) == "hello",
                    "子目录结构照搬");
                Check(!File.Exists(Path.Combine(pulled, "Desktop", "Alpha", "manifest.json")),
                    "远端清单不会被当成主题文件拉到本地");

                var r6b = SyncThemes(server, pulled, pulledState, ThemeSyncMode.Upload);
                Check(r6b.Counters.FilesUploaded == 0,
                    "刚拉下来的树再上传是幂等的（两边指纹算法一致）",
                    "实际传了 " + r6b.Counters.FilesUploaded);

                // ---- 7. 本地删除不镜像到远端 ----
                Directory.Delete(Path.Combine(pulled, "Fullscreen", "Beta"), true);
                var r7 = SyncThemes(server, pulled, pulledState, ThemeSyncMode.Upload);
                Check(server.Has("themes/Fullscreen/Beta/theme.yaml"),
                    "本地删掉的主题，远端必须还在（远端只增不减）");
                Check(r7.Counters.RemoteOnly.Contains("Fullscreen/Beta"),
                    "被删掉的那个只列入「远端独有」提示");
                Check(server.Deleted.Count == 0, "整套上传模式从未对远端发过 DELETE");

                // ---- 8. 冲突：本地更新 → 本地胜，远端那份留档 ----
                var newer = Path.Combine(root, "themes-newer");
                var newerState = Path.Combine(root, "theme-state-newer.json");
                var future = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                WriteLocalAt(Path.Combine(newer, "Desktop", "Alpha", "theme.yaml"),
                    "Id: Alpha\r\nName: Alpha Local\r\nVersion: 9.9\r\nMode: Desktop\r\n", future);
                WriteLocalAt(Path.Combine(newer, "Desktop", "Alpha", "desktop.xaml"),
                    "<Grid Background=\"local\"/>", future);

                var r8 = SyncThemes(server, newer, newerState, ThemeSyncMode.Upload);
                Check(r8.Counters.Conflicts == 1, "两边都改过 → 判定为冲突",
                    "实际 " + r8.Counters.Conflicts);
                Check(server.GetText("themes/Desktop/Alpha/desktop.xaml") == "<Grid Background=\"local\"/>",
                    "冲突时修改时间新的一方胜出（本轮本地赢）");

                var desktopDirs = server.DirsUnder("themes/Desktop");
                var remoteLoser = desktopDirs.Find(d =>
                    d.StartsWith("Alpha.conflict-remote-", StringComparison.OrdinalIgnoreCase));
                Check(remoteLoser != null, "输掉的远端那份留了 .conflict-remote-* 副本",
                    string.Join("、", desktopDirs.ToArray()));
                if (remoteLoser != null)
                {
                    Check(server.GetText("themes/Desktop/" + remoteLoser + "/desktop.xaml")
                          == "<Grid Background=\"red\"/>",
                        "留档副本里是冲突前那份旧内容（一个字节都没丢）");
                    Check(server.Has("themes/Desktop/" + remoteLoser + "/manifest.json"),
                        "留档副本里还留着当时的清单");
                }

                // ---- 9. 冲突：远端更新 → 远端胜，本地那份留档 ----
                var older = Path.Combine(root, "themes-older");
                var olderState = Path.Combine(root, "theme-state-older.json");
                var past = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                WriteLocalAt(Path.Combine(older, "Desktop", "Alpha", "theme.yaml"),
                    "Id: Alpha\r\nName: Old\r\nMode: Desktop\r\n", past);
                WriteLocalAt(Path.Combine(older, "Desktop", "Alpha", "desktop.xaml"),
                    "<Grid Background=\"old\"/>", past);

                var r9 = SyncThemes(server, older, olderState, ThemeSyncMode.Both);
                Check(r9.Counters.Conflicts == 1, "反向冲突同样被识别");
                Check(ReadLocal(Path.Combine(older, "Desktop", "Alpha", "desktop.xaml"))
                      == "<Grid Background=\"local\"/>",
                    "远端较新时本地被覆盖成远端那份");
                var localDirs = Directory.GetDirectories(Path.Combine(older, "Desktop"));
                Check(Array.Exists(localDirs, d => Path.GetFileName(d)
                        .StartsWith("Alpha.conflict-local-", StringComparison.OrdinalIgnoreCase)),
                    "输掉的本地那份留了 .conflict-local-* 副本");

                // ---- 10. 远端清单里的路径穿越必须被拦住 ----
                var evil = Path.Combine(root, "themes-evil");
                Directory.CreateDirectory(evil);
                server.SeedText("themes/Desktop/Evil/theme.yaml", "Id: Evil\r\nMode: Desktop\r\n");
                server.SeedText("themes/Desktop/Evil/manifest.json",
                    "{\"Id\":\"Evil\",\"Mode\":\"Desktop\",\"Fingerprint\":\"x\",\"Bytes\":4,"
                    + "\"UpdatedAt\":\"2026-01-01T00:00:00Z\","
                    + "\"Files\":[{\"Path\":\"../escaped.txt\",\"Sha1\":\"x\",\"Bytes\":4}]}");
                server.SeedText("themes/index.json",
                    "{\"Kind\":\"playnite-vault-themes\",\"Schema\":1,"
                    + "\"UpdatedAt\":\"2026-01-01T00:00:00Z\",\"Themes\":[{\"Id\":\"Evil\","
                    + "\"Name\":\"Evil\",\"Mode\":\"Desktop\",\"Files\":1,\"Bytes\":4,"
                    + "\"Fingerprint\":\"x\",\"UpdatedAt\":\"2026-01-01T00:00:00Z\"}]}");

                var blocked = false;
                try
                {
                    SyncThemes(server, evil, Path.Combine(root, "theme-state-evil.json"),
                        ThemeSyncMode.Download);
                }
                catch (Exception ex)
                {
                    blocked = ex.ToString().Contains("路径不安全");
                }
                Check(blocked, "远端清单里的 ../ 路径穿越被拒绝");
                Check(!File.Exists(Path.Combine(evil, "Desktop", "escaped.txt"))
                      && !File.Exists(Path.Combine(root, "escaped.txt")),
                    "没有文件逃出主题目录");

                // ---- 11. 索引不是本插件的 → 停下来不覆盖 ----
                var foreign = Path.Combine(root, "themes-foreign");
                Directory.CreateDirectory(foreign);
                server.SeedText("themes/index.json", "{\"Kind\":\"someone-else\",\"Themes\":[]}");

                var refused = false;
                try
                {
                    SyncThemes(server, foreign, Path.Combine(root, "theme-state-foreign.json"),
                        ThemeSyncMode.Upload);
                }
                catch (Exception ex)
                {
                    refused = ex.Message.Contains("不是本插件的主题索引");
                }
                Check(refused, "远端 index.json 不是本插件的 → 拒绝覆盖");
                Check(server.GetText("themes/index.json").Contains("someone-else"),
                    "拒绝之后远端索引原样未动");

                Check(server.Deleted.Count == 0, "全部场景跑完，一次 DELETE 都没发过");
            }
        }

        /// <summary>
        /// 云存档（v1.7.0）。三块一起验：
        ///   · <b>路径自适应</b> —— 折叠/展开的往返、最长根优先、「写了占位符却当成普通路径」必须被拦；
        ///   · <b>同步引擎</b> —— 上传/幂等/增量/分支/保留/恢复/留底/坏对象拒收，全在内存版 WebDAV 上真跑；
        ///   · <b>嗅探</b> —— 启发式的打分与黑名单、会话差分「向上爬」的作用域、PCGamingWiki 的模板切参数。
        /// 刻意不碰真实 NAS：这一组必须能离线跑。
        /// </summary>
        private static void RunSaveTests(string root)
        {
            Group("云存档 · 路径自适应");

            var fakeGameDir = Path.Combine(root, "saves-adapter", "HollowKnight");
            Directory.CreateDirectory(fakeGameDir);

            var localAppData = SavePathAdapter.Expand(SavePathAdapter.LocalAppData, fakeGameDir);
            Check(!string.IsNullOrEmpty(localAppData),
                "本机能展开 {LocalAppData}（拿不到根的话下面几条都没意义）");

            if (!string.IsNullOrEmpty(localAppData))
            {
                var absolute = Path.Combine(localAppData, "TeamCherry", "Hollow Knight");
                var folded = SavePathAdapter.Collapse(absolute, fakeGameDir);
                Check(folded == SavePathAdapter.LocalAppData + Path.DirectorySeparatorChar
                       + "TeamCherry" + Path.DirectorySeparatorChar + "Hollow Knight",
                    "绝对路径折成 {LocalAppData}\\... 形式", folded);

                var back = SavePathAdapter.Expand(folded, fakeGameDir);
                Check(string.Equals(back, absolute, StringComparison.OrdinalIgnoreCase),
                    "折叠后再展开能回到原路径（往返一致）", back);
                Check(SavePathAdapter.Collapse(folded, fakeGameDir) == folded,
                    "折叠是幂等的（对已经带 token 的路径再折一次不变）");
            }

            // LocalLow 必须优先于 UserProfile：先命中最长的那个根
            var profile = SavePathAdapter.Expand(SavePathAdapter.UserProfile, fakeGameDir);
            if (!string.IsNullOrEmpty(profile))
            {
                var localLow = Path.Combine(profile, "AppData", "LocalLow", "Team Cherry", "HK");
                var folded = SavePathAdapter.Collapse(localLow, fakeGameDir);
                Check(folded.StartsWith(SavePathAdapter.LocalAppDataLow, StringComparison.OrdinalIgnoreCase),
                    "LocalLow 下的路径折成 {LocalAppDataLow}，不会被 {UserProfile} 吞掉", folded);
            }

            Check(SavePathAdapter.Collapse(Path.Combine(fakeGameDir, "save"), fakeGameDir)
                  == SavePathAdapter.GameDir + Path.DirectorySeparatorChar + "save",
                "游戏安装目录内的路径折成 {GameDir}\\save");

            // 这一条防的是「把 {{p|hkcu}} 当成目录名真建出来」：
            // PCGamingWiki 没映射到的模板不是 {Xxx} 的形状，只查 TokenShape 会漏掉
            var spec = new SavePathSpec
            {
                Title = "坏路径",
                Type = SaveElementType.Directory,
                Path = "{{p|hkcu}}\\Software\\SomeGame",
                AutoAdaptive = true
            };
            List<string> unresolved;
            var resolved = SavePathAdapter.Resolve(spec, fakeGameDir, out unresolved);
            Check(resolved.Length == 0 && unresolved.Count > 0,
                "没映射到的 {{p|...}} 模板被标成未解析，不会当成普通路径", resolved);
            Check(SavePathAdapter.HasToken("{{p|hkcu}}\\Software"),
                "HasToken() 认得出 {{p|...}} 这种形状");

            var literalSpec = new SavePathSpec
            {
                Title = "没勾自适应却写了 token",
                Type = SaveElementType.Directory,
                Path = "{LocalAppData}\\Foo",
                AutoAdaptive = false
            };
            resolved = SavePathAdapter.Resolve(literalSpec, fakeGameDir, out unresolved);
            Check(resolved.Length == 0 && unresolved.Count > 0,
                "写着 token 却没勾「自适应」→ 拒绝当普通路径用（否则会建出字面量目录）");

            var wiki = SavePathAdapter.FromPcgamingWiki("{{p|userprofile}}\\Documents\\My Games\\HK");
            var expectedWiki = SavePathAdapter.UserProfile + Path.DirectorySeparatorChar
                               + "Documents" + Path.DirectorySeparatorChar + "My Games"
                               + Path.DirectorySeparatorChar + "HK";
            Check(wiki == expectedWiki, "PCGamingWiki 的 {{p|userprofile}} 翻成 {UserProfile}", wiki);

            // ================================================================
            Group("云存档 · 同步引擎（远端 = 内存版 WebDAV）");

            using (var server = new MockWebDavServer())
            {
                var data = Path.Combine(root, "save-data");
                var gameDir = Path.Combine(root, "save-game");
                Directory.CreateDirectory(data);
                Directory.CreateDirectory(gameDir);

                var saveRoot = Path.Combine(root, "save-local", "Saves");
                WriteLocal(Path.Combine(saveRoot, "slot1.sav"), "SLOT-ONE");
                WriteLocal(Path.Combine(saveRoot, "slot2.sav"), "SLOT-TWO");
                WriteLocal(Path.Combine(saveRoot, "sub", "meta.dat"), "META-A");

                var specs = new List<SavePathSpec>
                {
                    new SavePathSpec
                    {
                        Title = "saves",
                        Type = SaveElementType.Directory,
                        Path = saveRoot,
                        AutoAdaptive = false,
                        Source = SaveSource.Plugin
                    }
                };

                const string GameId = "11111111-2222-3333-4444-555555555555";
                const string GameName = "Hollow Knight";

                // ---- 1. 首次上传 ----
                var engine = SaveEngine(server, data, "PC-A");
                var manifest = SaveManifestFor(GameId, GameName, specs);
                var o1 = engine.Upload(GameId, GameName, gameDir, manifest,
                    new SaveSyncOptions { Origin = SaveSnapshotOrigin.Manual });

                Check(o1.Ok, "首次上传成功", o1.Failure);
                Check(o1.Counters.SnapshotsUploaded == 1 && o1.Counters.FilesUploaded == 3,
                    "三个文件都传上去了", o1.Counters.Describe());
                Check(server.Has("saves/" + GameId + "/manifest.json"), "写出了 manifest.json");
                Check(server.Has("saves/" + GameId + "/snapshots/main/" + o1.SnapshotId + ".json"),
                    "快照清单落在了 snapshots/main/ 下");
                Check(server.Has("saves/index.json"), "写出了全局索引");
                Check(server.GetText("saves/index.json").Contains(GameName),
                    "索引里带上了游戏名");

                var sha = SaveSyncEngine.Sha1Text("SLOT-ONE");
                Check(server.Has("saves/" + GameId + "/objects/" + sha.Substring(0, 2) + "/" + sha),
                    "内容按 sha1 寻址落到 objects/xx/ 下");
                Check(server.GetText("saves/" + GameId + "/objects/" + sha.Substring(0, 2) + "/" + sha)
                      == "SLOT-ONE", "对象内容与本地字节一致");

                var snapshotJson = server.GetText("saves/" + GameId + "/snapshots/main/"
                                                 + o1.SnapshotId + ".json");
                Check(snapshotJson.Contains("slot1.sav") && snapshotJson.Contains("\"Sha1\""),
                    "快照清单里记了相对路径与内容指纹");

                // 路径定义（Paths）里带用户自己写的路径是合理的；要卡的是**文件条目**
                // 一律用相对路径 —— 混进一个 C:\... 就会让这份快照在别的机器上用不了。
                var loaded = engine.LoadSnapshot(GameId, SaveBranch.Default, o1.SnapshotId, true);
                var filePaths = loaded == null
                    ? new List<string>()
                    : loaded.Files.Select(f => f.Path).ToList();
                Check(loaded != null && filePaths.Count == 3
                      && filePaths.All(p => !Path.IsPathRooted(p) && p.IndexOf(':') < 0
                                            && !p.StartsWith("/", StringComparison.Ordinal)),
                    "快照里每个文件都是相对路径（没有本机绝对路径，换台机器才用得了）",
                    string.Join("；", filePaths.ToArray()));

                // ---- 2. 幂等：内容没变不造新快照 ----
                var m2 = engine.LoadManifest(GameId);
                Check(m2 != null, "能从远端读回 manifest.json");
                var o2 = engine.Upload(GameId, GameName, gameDir, m2, new SaveSyncOptions());
                Check(o2.Ok && o2.Counters.SnapshotsUnchanged == 1
                      && o2.Counters.SnapshotsUploaded == 0,
                    "内容一致时不造新快照（免得刷出一堆空历史）", o2.Counters.Describe());

                // ---- 3. 改一个文件：只传新内容，别的靠对象复用 ----
                WriteLocal(Path.Combine(saveRoot, "slot1.sav"), "SLOT-ONE-V2");
                var o3 = engine.Upload(GameId, GameName, gameDir, m2, new SaveSyncOptions());
                Check(o3.Ok && o3.Counters.SnapshotsUploaded == 1,
                    "改一个文件 → 造一份新快照", o3.Counters.Describe());
                Check(o3.Counters.FilesUploaded == 1,
                    "只上传了变化的那一个对象", o3.Counters.Describe());
                Check(o3.Counters.ObjectsReused >= 2,
                    "没变的两个对象被复用（内容寻址去重）", o3.Counters.Describe());

                // ---- 4. 分支互不影响 ----
                var m4 = engine.LoadManifest(GameId);
                var o4 = engine.Upload(GameId, GameName, gameDir, m4,
                    new SaveSyncOptions { Branch = "ng+", Comment = "二周目" });
                Check(o4.Ok, "能在新分支上上传", o4.Failure);

                var m4b = engine.LoadManifest(GameId);
                Check(m4b.SnapshotsIn(SaveBranch.Default).Count == 2,
                    "main 分支仍是两条快照，没被新分支挤掉",
                    m4b.SnapshotsIn(SaveBranch.Default).Count.ToString());
                Check(m4b.SnapshotsIn("ng+").Count == 1, "ng+ 分支有一条自己的快照");
                Check(m4b.SnapshotsIn("ng+")[0].Comment == "二周目", "快照的注释也存下来了");

                var ngEngine = SaveEngine(server, data, "PC-A");
                var ngManifest = ngEngine.LoadManifest(GameId);
                var o4c = ngEngine.Upload(GameId, GameName, gameDir, ngManifest, new SaveSyncOptions());
                Check(o4c.Ok && o4c.Counters.SnapshotsUnchanged == 1,
                    "幂等是按分支分别算的（ng+ 分支自己比自己的头）", o4c.Counters.Describe());

                // ---- 5. 恢复：远端回到旧版，本地多出来的文件不许删 ----
                WriteLocal(Path.Combine(saveRoot, "slot1.sav"), "SLOT-ONE-V3-本地乱改");
                WriteLocal(Path.Combine(saveRoot, "extra-新文件.sav"), "本地多出来的");
                WriteLocal(Path.Combine(saveRoot, "slot2.sav"), "SLOT-TWO-也被改了");

                var m5 = engine.LoadManifest(GameId);
                var o5 = engine.Download(GameId, GameName, gameDir, m5,
                    new SaveSyncOptions { SnapshotId = o1.SnapshotId });
                Check(o5.Ok, "恢复成功", o5.Failure);
                Check(ReadLocal(Path.Combine(saveRoot, "slot1.sav")) == "SLOT-ONE",
                    "slot1 回到快照里的内容（覆盖式恢复）");
                Check(ReadLocal(Path.Combine(saveRoot, "slot2.sav")) == "SLOT-TWO",
                    "slot2 也回到快照里的内容");
                Check(File.Exists(Path.Combine(saveRoot, "extra-新文件.sav")),
                    "本地多出来的文件没有被删掉（恢复只做加法）");
                Check(o5.Counters.SafetyBackups >= 1,
                    "恢复前留了底（本地这份）", o5.Counters.Describe());

                var backupRoot = Path.Combine(data, "save-backup", GameId);
                Check(Directory.Exists(backupRoot)
                      && Directory.GetFiles(backupRoot, "*", SearchOption.AllDirectories).Length > 0,
                    "本地留底目录里真有文件（不是只报了一句话）");

                var m5b = engine.LoadManifest(GameId);
                var beforeRestore = m5b.SnapshotsIn(SaveBranch.Default)
                    .Count(s => s.Origin == SaveSnapshotOrigin.BeforeRestore);
                Check(beforeRestore >= 1,
                    "恢复前还往远端推了一份「恢复前」快照（第二层保险）", beforeRestore + " 份");

                // ---- 6. 坏对象必须拒收，不能拿去覆盖存档 ----
                var badSha = SaveSyncEngine.Sha1Text("SLOT-TWO");
                server.SeedText("saves/" + GameId + "/objects/" + badSha.Substring(0, 2) + "/" + badSha,
                    "被篡改成坏内容了");
                WriteLocal(Path.Combine(saveRoot, "slot2.sav"), "恢复前先写上本地内容");

                var badData = Path.Combine(root, "save-data-bad");
                Directory.CreateDirectory(badData);
                var badEngine = SaveEngine(server, badData, "PC-A");
                var m6 = badEngine.LoadManifest(GameId);
                var o6 = badEngine.Download(GameId, GameName, gameDir, m6,
                    new SaveSyncOptions { SnapshotId = o1.SnapshotId, SnapshotBeforeRestore = false });
                Check(o6.Ok && o6.Counters.FilesSkipped >= 1,
                    "远端坏对象被跳过并计入跳过数", o6.Counters.Describe());
                Check(ReadLocal(Path.Combine(saveRoot, "slot2.sav")) != "被篡改成坏内容了",
                    "坏内容没有被写进存档（校验不过就不落盘）");

                // ---- 7. dry-run 不写远端 ----
                using (var dryServer = new MockWebDavServer())
                {
                    var dryEngine = SaveEngine(dryServer, data, "PC-A");
                    var dryManifest = SaveManifestFor("dry-game", "Dry Game", specs);
                    var o7 = dryEngine.Upload("dry-game", "Dry Game", gameDir, dryManifest,
                        new SaveSyncOptions { DryRun = true });
                    Check(o7.Ok && o7.DryRun && dryServer.AllFiles().Count == 0,
                        "dry-run 一份快照都不写（也不漏报为真跑了）");
                    Check(o7.Plan.Count > 0, "dry-run 会列出计划，让人知道将要发生什么");
                }

                // ---- 8. 保留策略：按分支算、标星不删、最新必留 ----
                using (var keepServer = new MockWebDavServer())
                {
                    var keepEngine = SaveEngine(keepServer, Path.Combine(root, "save-data-keep"), "PC-K");
                    var keepManifest = SaveManifestFor("keep-game", "Keep Game", specs);
                    var keptIds = new List<string>();

                    for (var i = 0; i < 5; i++)
                    {
                        WriteLocal(Path.Combine(saveRoot, "slot1.sav"), "V" + i);
                        var outcome = keepEngine.Upload("keep-game", "Keep Game", gameDir,
                            keepManifest, new SaveSyncOptions { Force = true, Pinned = i == 0 });
                        keptIds.Add(outcome.SnapshotId);
                    }

                    Check(keepManifest.SnapshotsIn(SaveBranch.Default).Count == 5,
                        "不带删除许可时：一份都不删（只是报告）",
                        keepManifest.SnapshotsIn(SaveBranch.Default).Count.ToString());
                    Check(keepServer.Deleted.Count == 0,
                        "没开删除许可 → 一次 DELETE 都没发过");

                    var pruned = keepEngine.Prune("keep-game", keepManifest,
                        new SaveSyncOptions { KeepPerBranch = 2, AllowDelete = true });
                    var remaining = keepManifest.SnapshotsIn(SaveBranch.Default);
                    Check(pruned.Ok && remaining.Count == 3,
                        "keep=2 时：最新的 2 份 + 1 份标星的 = 3 份",
                        remaining.Count + " 份（"
                        + string.Join("、", remaining.Select(s => s.Id).ToArray()) + "）");
                    Check(remaining.Any(s => s.Id == keptIds[0]), "标星的那份不会被保留策略吃掉");
                    Check(remaining.Any(s => s.Id == keptIds[4]), "最新的一份永远留着");
                    Check(remaining.Count(s => s.Id == keptIds[1]) == 0,
                        "中间那些没标星的旧快照被裁掉了");
                    Check(keepServer.Deleted.Count > 0, "开了删除许可后才真发 DELETE");
                    Check(keepServer.Has("saves/keep-game/snapshots/main/" + keptIds[0] + ".json"),
                        "标星快照的文件在远端也还在");
                }

                // ---- 9. 远端清单不是本插件的 → 停下来 ----
                using (var foreignServer = new MockWebDavServer())
                {
                    foreignServer.SeedText("saves/foreign-game/manifest.json",
                        "{\"Kind\":\"someone-else\",\"Schema\":1,\"GameId\":\"foreign-game\"}");
                    var foreignEngine = SaveEngine(foreignServer,
                        Path.Combine(root, "save-data-foreign"), "PC-F");
                    var refused = false;
                    try
                    {
                        foreignEngine.LoadManifest("foreign-game");
                    }
                    catch (Exception ex)
                    {
                        refused = ex.Message.Contains("不是本插件的存档清单");
                    }

                    Check(refused, "远端 manifest.json 不是本插件的 → 拒绝当成存档仓库");
                    Check(foreignServer.GetText("saves/foreign-game/manifest.json")
                          .Contains("someone-else"), "拒绝之后远端清单原样未动");
                }
            }

            // ================================================================
            Group("云存档 · 嗅探");

            var sniffRoot = Path.Combine(root, "sniff");
            var sniffGame = Path.Combine(sniffRoot, "HollowKnight");
            Directory.CreateDirectory(sniffGame);

            // 像存档的：游戏目录下的 Saves
            WriteLocal(Path.Combine(sniffGame, "Saves", "user1.dat"), "SAVE-DATA-0123456789");
            // 不像存档的：缓存/日志目录必须在打分阶段就被黑名单拦掉
            WriteLocal(Path.Combine(sniffGame, "Cache", "user1.dat"), "SAVE-DATA-0123456789");
            WriteLocal(Path.Combine(sniffGame, "logs", "output_log.txt"), "log line");

            var ctx = new SaveSniffContext
            {
                GameName = "Hollow Knight",
                GameInstallDir = sniffGame,
                Budget = TimeSpan.FromSeconds(5)
            };

            var found = SaveSniffer.Heuristic(ctx);
            var saves = found.Find(c => SavePathAdapter.StartsWithRoot(c.AbsolutePath,
                Path.Combine(sniffGame, "Saves")));
            Check(saves != null, "游戏目录下的 Saves 被嗅探到",
                string.Join("；", found.Select(c => c.AbsolutePath).ToArray()));
            Check(found.Find(c => c.AbsolutePath.EndsWith("Cache", StringComparison.OrdinalIgnoreCase))
                  == null, "Cache 目录被黑名单拦掉（不当作候选）");
            Check(found.Find(c => c.AbsolutePath.EndsWith("logs", StringComparison.OrdinalIgnoreCase))
                  == null, "logs 目录被黑名单拦掉");
            Check(saves == null || saves.TokenPath.StartsWith(SavePathAdapter.GameDir,
                      StringComparison.OrdinalIgnoreCase),
                "游戏目录下的候选折成了 {GameDir}\\...（换台机器照样能用）",
                saves == null ? "(没找到)" : saves.TokenPath);
            Check(saves == null || saves.Reasons.Count > 0,
                "候选带上了「为什么推荐它」的理由，不是光秃秃一个分数");
            Check(saves == null || !saves.HasUnresolved, "候选里没有解析不了的占位符");

            // 名字不像、也不含存档词的目录不该被推荐
            WriteLocal(Path.Combine(sniffGame, "Textures", "a.dds"), "binary");
            var textures = found.Find(c =>
                c.AbsolutePath.EndsWith("Textures", StringComparison.OrdinalIgnoreCase));
            Check(textures == null, "既不沾游戏名、又不含存档词的目录不推荐",
                textures == null ? null : "分数 " + textures.Score);

            // 会话差分
            var watchRoot = Path.Combine(sniffRoot, "watch");
            WriteLocal(Path.Combine(watchRoot, "GameA", "cfg.ini"), "before-config");
            WriteLocal(Path.Combine(watchRoot, "GameA", "profile.dat"), "before-profile");
            WriteLocal(Path.Combine(watchRoot, "Other", "untouched.dat"), "never-changed");
            WriteLocal(Path.Combine(watchRoot, "GameA", "sub", "deeper.sav"), "before-deep");

            var limits = new SaveSniffLimits { MaxDepth = 4, MaxEntries = 5000, MaxMilliseconds = 5000 };
            var before = SaveSniffer.Capture(watchRoot, limits);

            WriteLocal(Path.Combine(watchRoot, "GameA", "profile.dat"), "AFTER-profile-changed");
            WriteLocal(Path.Combine(watchRoot, "GameA", "sub", "deeper.sav"), "AFTER-deep-changed");

            var after = SaveSniffer.Capture(watchRoot, limits);
            var diff = SaveSniffer.Diff(before, after, ctx);

            Check(diff.Count >= 1, "会话差分至少给出一个候选",
                string.Join("；", diff.Select(c => c.AbsolutePath).ToArray()));
            Check(diff.Any(c => c.AbsolutePath.EndsWith("GameA", StringComparison.OrdinalIgnoreCase)),
                "变了两个文件的 GameA 被判为作用域（向上爬的结果）",
                string.Join("；", diff.Select(c => c.AbsolutePath).ToArray()));
            Check(!diff.Any(c => c.AbsolutePath.Equals(watchRoot, StringComparison.OrdinalIgnoreCase)),
                "没有爬到监听根：Other/ 没变，所以作用域停在 GameA");

            var noChange = SaveSniffer.Diff(after, after, ctx);
            Check(noChange.Count == 0, "什么都没变时一个候选都不给（不硬凑）");

            Check(SaveSniffer.WatchRoots(sniffGame).Count > 0,
                "会话差分有明确的监听根清单（不是整个盘）");

            // 游戏名切词
            var tokens = SaveSniffer.GameNameTokens(new SaveSniffContext
            {
                GameName = "Hollow Knight: Voidheart Edition"
            });
            Check(tokens.Contains("hollow") && tokens.Contains("knight"),
                "游戏名切出了 hollow / knight", string.Join("、", tokens.ToArray()));
            Check(!tokens.Contains("edition"),
                "版本类噪声词（edition）被丢掉，免得把同名目录误判成存档");

            // PCGamingWiki 解析：路径里的 {{p|...}} 自带竖线，切参数时不能被它带偏
            const string wikitext = "{{Game data|\n"
                + "{{Game data/saves|Windows|{{p|userprofile}}\\Documents\\My Games\\Hollow Knight}}\n"
                + "{{Game data/saves|Linux|{{p|home}}/.config/unity3d/Team Cherry}}\n"
                + "{{Game data/config|Windows|{{p|hkcu}}\\Software\\TeamCherry\\Hollow Knight}}\n"
                + "{{Game data/saves|Microsoft Store|{{p|localappdata}}\\Packages\\HK\\Saves}}\n"
                + "}}";

            var wikiRows = SaveSniffer.EnumerateGameDataRows(wikitext).ToList();
            Check(wikiRows.Count == 4, "四条 Game data 行都找出来了（含 config 与跨平台）",
                wikiRows.Count.ToString());
            Check(wikiRows.Any(r => r.Os == "Windows" && r.Kind.EndsWith("saves"))
                  && wikiRows.First(r => r.Os == "Windows" && r.Kind.EndsWith("saves"))
                      .RawPath.Contains("My Games"),
                "Windows 那条的参数切对了（{{p|userprofile}} 里的竖线没把参数切坏）",
                string.Join(" | ", wikiRows.Select(r => r.Kind + "=" + r.RawPath).ToArray()));

            var wikiCandidates = SaveSniffer.FromPcgamingWiki(wikitext, ctx);
            Check(wikiCandidates.Count == 2,
                "只留下 Windows 能用的两条：saves + Microsoft Store（Linux 与注册表都跳过）",
                string.Join("；", wikiCandidates.Select(c => c.TokenPath).ToArray()));
            var wikiSaves = wikiCandidates.Find(c => c.TokenPath.Contains("My Games"));
            Check(wikiSaves != null
                  && wikiSaves.TokenPath.StartsWith(SavePathAdapter.UserProfile,
                      StringComparison.OrdinalIgnoreCase),
                "抓来的路径已经翻成我们的 token 形式",
                wikiSaves == null ? "(没找到)" : wikiSaves.TokenPath);
            Check(wikiSaves != null && wikiSaves.Source == SaveSniffSource.PcgamingWiki,
                "候选带上了来源标签（用来说明「这条是抓来的」）");
            Check(wikiCandidates.Find(c => c.TokenPath.Contains("hkcu") || c.TokenPath.Contains("{{"))
                  == null, "HKcu 注册表位置与没映射的模板都不作为候选给出");

            // 合并去重
            var merged = PcgamingWikiClient.Merge(wikiCandidates, found);
            Check(merged.Count > 0, "三档结果能合并成一个列表", merged.Count + " 条");
            Check(merged.SequenceEqual(merged.OrderByDescending(c => c.Score)),
                "合并后按分数降序（最能确定的排前面）");

            // ================================================================
            Group("云存档 · 默认值与结果对象");

            Check(new SaveSyncOptions().KeepPerBranch == 10, "默认每个分支保留 10 份");
            Check(new SaveSyncOptions().SafetyBackup, "恢复前留底默认是开的");
            Check(new SaveSyncOptions().SnapshotBeforeRestore, "恢复前推快照默认也是开的");
            Check(new SaveSyncOptions().AllowDelete == false, "默认不发 DELETE（要显式开）");

            var failed = SaveSyncOutcome.Fail("远端连不上");
            Check(!failed.Ok && failed.Describe().Contains("远端连不上"),
                "失败结果保留了原因，不是只说一句「失败」");

            var planned = new SaveSyncOutcome
            {
                Ok = true,
                DryRun = true,
                Counters = new SaveSyncCounters { SnapshotsUploaded = 2, BytesUp = 2048 }
            };
            Check(planned.Describe().Contains("预演"), "预演的结果会被显式标注");
        }

        /// <summary>造一个连内存版 WebDAV 的存档引擎（自检里别去碰真实 NAS）。</summary>
        private static SaveSyncEngine SaveEngine(MockWebDavServer server, string dataPath,
            string machine)
        {
            var client = new PlayniteVault.Net.WebDavClient(server.BaseUrl, null, null, 30);
            return new SaveSyncEngine(client, dataPath, new SyncOptions { MaxRetries = 1 },
                null, CancellationToken.None, machine);
        }

        private static SaveGameManifest SaveManifestFor(string gameId, string gameName,
            List<SavePathSpec> specs)
        {
            var manifest = SaveGameManifest.NewFor(gameId, gameName);
            manifest.Paths = specs.Select(s => s.GetCopy()).ToList();
            return manifest;
        }

        private static ThemeSyncResult SyncThemes(MockWebDavServer server, string localRoot,
            string stateFile, ThemeSyncMode mode, bool dryRun = false, bool force = false)
        {
            var client = new PlayniteVault.Net.WebDavClient(server.BaseUrl, null, null, 30);
            var engine = new ThemeSyncEngine(client, localRoot, stateFile,
                new SyncOptions { MaxRetries = 1 }, null, CancellationToken.None);
            return engine.Run(new ThemeSyncOptions { Mode = mode, DryRun = dryRun, Force = force });
        }

        private static void WriteLocal(string path, string text)
        {
            WriteLocalAt(path, text, null);
        }
        private static void WriteLocalAt(string path, string text, DateTime? modifiedUtc)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(path, text, new UTF8Encoding(false));
            if (modifiedUtc != null)
            {
                File.SetLastWriteTimeUtc(path, modifiedUtc.Value);
            }
        }

        private static string ReadLocal(string path)
        {
            return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null;
        }

        /// <summary>
        /// 侧边栏「仓库管家」那一页。界面本身没法在无头环境里点，但它的**契约**可以验：
        ///  · 结果对象（ThemeSyncOutcome / ThemeLibraryStats）的说法与算术；
        ///  · 页面类型确实挂在 VaultPlugin.GetSidebarItems 上，且构造签名没被改坏；
        ///  · 侧边栏图标真能造出来、几何路径能解析（图省事写错一串坐标就会在这里炸）。
        /// 这些正是「页面变成空白」和「图标变成方框」之前会先出错的地方。
        /// </summary>
        private static void RunSidebarPanelTests()
        {
            Group("侧边栏「仓库管家」页");

            // --- 1. 结果对象：三种收场各有各的说法，别把「取消」说成「失败」 ---
            Check(ThemeSyncOutcome.Fail("配置不全").Describe().StartsWith("同步失败：配置不全"),
                "失败时 Describe() 以「同步失败：」开头");

            var cancelled = ThemeSyncOutcome.Fail(null);
            cancelled.Failure = null;
            cancelled.Cancelled = true;
            Check(cancelled.Describe().StartsWith("已取消"),
                "取消时 Describe() 说「已取消」，不冒充失败");

            var dry = new ThemeSyncOutcome
            {
                Ok = true,
                DryRun = true,
                Counters = new ThemeSyncCounters { ThemesUploaded = 3, FilesUploaded = 5 }
            };
            Check(dry.Describe().StartsWith("【预演，没有落盘】"),
                "预演的结果会被显式标注（免得以为真传了）");
            Check(dry.Describe().Contains("上传 3 个主题"),
                "预演也照样把计数说清楚");

            var withRemote = new ThemeSyncOutcome
            {
                Ok = true,
                Counters = new ThemeSyncCounters
                {
                    ThemesDownloaded = 1,
                    RemoteOnly = new List<string> { "Desktop/Only-On-Nas" }
                }
            };
            Check(withRemote.Describe().Contains("远端独有 1 个")
                  && withRemote.Describe().Contains("Desktop/Only-On-Nas"),
                "远端独有条目会列出来（并说明不会删除）");

            // --- 2. 统计快照：总数就是两个模式之和 ---
            var stats = new ThemeLibraryStats { DesktopThemes = 76, FullscreenThemes = 2 };
            Check(stats.TotalThemes == 78, "主题总数 = 桌面 + 全屏",
                "实际 " + stats.TotalThemes);

            // --- 3. 页面确实挂在侧边栏上，且构造签名没被改坏 ---
            var pluginType = typeof(PlayniteVault.VaultPlugin);
            var sidebarMethod = pluginType.GetMethod("GetSidebarItems",
                BindingFlags.Public | BindingFlags.Instance);

            Check(sidebarMethod != null && sidebarMethod.DeclaringType == pluginType,
                "VaultPlugin 自己覆写了 GetSidebarItems（不是继承基类的空实现）");

            var panelType = typeof(PlayniteVault.UI.VaultPanelView);
            var ctor = panelType.GetConstructor(new[]
            {
                pluginType,
                typeof(VaultService),
                typeof(PlayniteVault.UI.VaultSettingsViewModel),
                typeof(string)
            });
            Check(ctor != null, "VaultPanelView 的构造签名 (plugin, service, settingsVm, themesRoot) 没变");

            // --- 4. 图标：能造出来、路径能解析、笔画不是空的 ---
            // 注意：Path 是 FrameworkElement，只能在 STA 线程上 new ——
            // 自检主线程是 MTA，直接 Invoke 会抛「调用线程必须为 STA」。
            // 这本身也是一条有用的信息：Playnite 是在 UI 线程上调 GetSidebarItems 的。
            var iconMethod = pluginType.GetMethod("BuildSidebarIcon",
                BindingFlags.NonPublic | BindingFlags.Static);
            Check(iconMethod != null, "BuildSidebarIcon 还在（侧边栏图标的生产者）");

            if (iconMethod != null)
            {
                object icon = null;
                var isVector = false;
                var hasData = false;
                var hasStroke = false;
                string iconType = null;

                var staError = RunSta(() =>
                {
                    icon = iconMethod.Invoke(null, null);
                    var shape = icon as System.Windows.Shapes.Path;
                    isVector = shape != null;
                    if (shape != null)
                    {
                        hasData = shape.Data != null && !shape.Data.IsEmpty();
                        hasStroke = shape.Stroke != null;
                    }
                });

                Check(staError == null, "侧边栏图标能在 STA 线程上造出来（Playnite 调它时就是这种情况）",
                    staError == null ? null : staError.Message);

                if (icon != null)
                {
                    iconType = icon.GetType().FullName;
                }

                Check(isVector,
                    "图标是矢量 Path（不是字体字形，换机器不会变方框）",
                    icon == null ? "返回了 null" : iconType);

                if (isVector)
                {
                    // 只对矢量那条分支断言：返回 null 时上面那条已经报过了
                    var ok = hasData && hasStroke;
                    if (ok)
                    {
                        Check(true, "图标几何数据非空，且描边有色（坐标写错 Geometry.Parse 会直接抛）");
                    }
                    else
                    {
                        Check(false, "图标几何数据非空，且描边有色",
                            hasData ? "描边是空的（深色主题里等于看不见）" : "几何数据为空");
                    }
                }
            }

            // --- 5. 取不到主题画刷时必须退回兜底，不能返回 null ---
            var fallback = System.Windows.Media.Brushes.Magenta;
            System.Windows.Media.Brush resolved = null;
            var brushError = RunSta(() =>
            {
                resolved = PlayniteVault.UI.VaultPanelView.ThemedBrush(
                    "这个资源键肯定不存在-VaultSelfTest", fallback);
            });
            Check(brushError == null && ReferenceEquals(resolved, fallback),
                "主题里没有这个画刷时，ThemedBrush 返回兜底而不是 null",
                brushError != null ? brushError.Message : null);

            // --- 6. 口令闸门不能被侧边栏绕开 ---
            // 仓库管理里是**不可逆的删除**，主菜单那条路会先验管理口令。
            // 侧边栏页如果自己 new VaultAdminWindow，就等于开后门了 ——
            // 这条断言直接从 IL 里查「有没有 newobj 到 VaultAdminWindow」。
            var openManager = pluginType.GetMethod("OpenRepositoryManager",
                BindingFlags.Public | BindingFlags.Instance);
            Check(openManager != null, "VaultPlugin 公开了 OpenRepositoryManager（口令闸门的唯一入口）");

            var adminCtor = typeof(PlayniteVault.UI.VaultAdminWindow)
                .GetConstructor(new[] { typeof(VaultService) });
            Check(adminCtor != null, "VaultAdminWindow(VaultService) 这个构造还在");

            if (adminCtor != null)
            {
                Check(!InstantiatesAdminWindow(panelType),
                    "侧边栏页没有直接 new 仓库管理窗口（否则绕开管理口令）");

                // 正对照：同一个检测器去查插件自己 —— 那里**确实**有这句 new。
                // 没有这一条，「没找到」和「检测器坏了」就分不出来。
                Check(InstantiatesAdminWindow(pluginType),
                    "（正对照）检测器能在插件里找到这句 new，说明上面那条不是空过");
            }
        }

        /// <summary>
        /// 在某个类型（含它自己生成的闭包类）的所有方法 IL 里找「有没有 newobj 到仓库管理窗口」。
        /// 直接搜 4 字节元数据令牌：newobj 的操作数就是它，误命中概率可以忽略。
        /// </summary>
        private static bool InstantiatesAdminWindow(Type owner)
        {
            var ctor = typeof(PlayniteVault.UI.VaultAdminWindow)
                .GetConstructor(new[] { typeof(VaultService) });
            if (ctor == null)
            {
                return false;
            }

            var token = BitConverter.GetBytes(ctor.MetadataToken);
            var types = new List<Type> { owner };
            types.AddRange(owner.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic));

            foreach (var type in types)
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                                           | BindingFlags.Instance | BindingFlags.Static
                                           | BindingFlags.DeclaredOnly;

                var methods = type.GetMethods(flags);
                foreach (var method in methods)
                {
                    MethodBody body;
                    try
                    {
                        body = method.GetMethodBody();
                    }
                    catch
                    {
                        continue;
                    }

                    if (body != null && ContainsToken(body.GetILAsByteArray(), token))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool ContainsToken(byte[] il, byte[] token)
        {
            if (il == null || token == null || il.Length < token.Length)
            {
                return false;
            }

            for (var i = 0; i <= il.Length - token.Length; i++)
            {
                var hit = true;
                for (var j = 0; j < token.Length; j++)
                {
                    if (il[i + j] != token[j])
                    {
                        hit = false;
                        break;
                    }
                }

                if (hit)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 在 STA 线程上跑一段动作并把异常带回来。
        /// WPF 的 FrameworkElement 只能在 STA 上构造，而自检主线程是 MTA —— 这一层必须转一下。
        /// </summary>
        private static Exception RunSta(Action action)
        {
            Exception error = null;
            var thread = new Thread(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    error = ex is TargetInvocationException && ex.InnerException != null
                        ? ex.InnerException
                        : ex;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();
            thread.Join();
            return error;
        }

        private static void RunDataMigrationTests(string root)
        {
            Group("插件改名后的数据目录迁移");

            var baseDir = Path.Combine(root, "migrate");
            var extensionsData = Path.Combine(baseDir, "ExtensionsData");
            var newDir = Path.Combine(extensionsData, "Playnite-Vault");
            var legacyGuidDir = Path.Combine(extensionsData, VaultDataMigration.LegacyGuid);

            // --- 场景 A：老目录是纯 GUID，新目录还没有 ---
            Directory.CreateDirectory(legacyGuidDir);
            File.WriteAllText(Path.Combine(legacyGuidDir, "settings.json"),
                "{\"WebDavUrl\":\"http://nas/dav\"}", Encoding.UTF8);
            File.WriteAllText(Path.Combine(legacyGuidDir, "local-index.json"),
                "{\"Apps\":[]}", Encoding.UTF8);
            File.WriteAllText(Path.Combine(legacyGuidDir, "state.json"), "{}", Encoding.UTF8);
            // 这个不在迁移清单里，搬过去反而占地方
            File.WriteAllText(Path.Combine(legacyGuidDir, "vault-demo.log"), "旧日志", Encoding.UTF8);
            // 随包图片缓存：必须整个目录搬（否则下次导入要重新从 NAS 下一遍）
            var legacyMetaCache = Path.Combine(legacyGuidDir, "meta-cache", "miside");
            Directory.CreateDirectory(legacyMetaCache);
            File.WriteAllBytes(Path.Combine(legacyMetaCache, "cover.jpg"),
                new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3 });

            var moved = VaultDataMigration.MigrateIfNeeded(newDir);
            Check(moved.Length == 4, "老数据目录（纯 GUID）里的 3 个文件 + 1 个目录都搬过来了",
                "实际 " + moved.Length + "：" + string.Join(", ", moved));
            Check(File.Exists(Path.Combine(newDir, "settings.json"))
                  && File.Exists(Path.Combine(newDir, "local-index.json"))
                  && File.Exists(Path.Combine(newDir, "state.json")),
                "搬过来的文件在新目录里确实存在");
            Check(SafeRead(Path.Combine(newDir, "settings.json")).Contains("http://nas/dav"),
                "内容逐字节搬过来了，不是建了个空文件");
            Check(!File.Exists(Path.Combine(newDir, "vault-demo.log")),
                "不在清单里的文件（旧日志）不搬");
            Check(File.Exists(Path.Combine(newDir, "meta-cache", "miside", "cover.jpg")),
                "meta-cache 里的嵌套文件也搬过来了（递归拷贝）");
            Check(File.ReadAllBytes(Path.Combine(newDir, "meta-cache", "miside", "cover.jpg")).Length == 7,
                "meta-cache 里的图片逐字节一致，没被截断");
            Check(File.Exists(Path.Combine(legacyGuidDir, "settings.json")),
                "老目录原地保留（搬错了还能人工翻回去）");

            // --- 场景 B：新目录已经有数据 → 绝不能被老数据盖回去 ---
            File.WriteAllText(Path.Combine(newDir, "settings.json"),
                "{\"WebDavUrl\":\"http://new-nas/dav\"}", Encoding.UTF8);
            var again = VaultDataMigration.MigrateIfNeeded(newDir);
            Check(again.Length == 0, "新目录已有数据时不再搬（返回 0 个文件）",
                "实际 " + again.Length);
            Check(SafeRead(Path.Combine(newDir, "settings.json")).Contains("http://new-nas/dav"),
                "新目录里的设置没有被老数据覆盖");

            // --- 场景 C：老目录叫 VaultDemo_<guid>（那种手工放过数据的机器）---
            // 老目录必须是新目录的**兄弟**目录 —— 迁移就是在同级里找老名字。
            var extensionsData2 = Path.Combine(baseDir, "ExtensionsData2");
            var newDir2 = Path.Combine(extensionsData2, "Playnite-Vault");
            var legacyNamedDir2 = Path.Combine(extensionsData2,
                "VaultDemo_" + VaultDataMigration.LegacyGuid);
            Directory.CreateDirectory(legacyNamedDir2);
            File.WriteAllText(Path.Combine(legacyNamedDir2, "cache-index.json"),
                "{\"Apps\":[1]}", Encoding.UTF8);
            var moved2 = VaultDataMigration.MigrateIfNeeded(newDir2);
            Check(moved2.Length == 1 && moved2[0] == "cache-index.json",
                "老目录叫 VaultDemo_<guid> 时也认得出",
                "实际 " + moved2.Length);

            // --- 场景 D：什么都没有 / 路径离谱 → 静默返回，不能炸 ---
            var newDir3 = Path.Combine(baseDir, "ExtensionsData3", "Playnite-Vault");
            Check(VaultDataMigration.MigrateIfNeeded(newDir3).Length == 0,
                "老目录不存在时安静地什么都不做（并且把新目录建出来）");
            Check(Directory.Exists(newDir3), "顺手把新数据目录创建好");
            Check(VaultDataMigration.MigrateIfNeeded(null).Length == 0, "传 null 不炸");
            Check(VaultDataMigration.MigrateIfNeeded("").Length == 0, "传空串不炸");

            // --- 场景 E：新目录只有 meta-cache（没 settings）也算「已有数据」，不能覆盖 ---
            var extensionsData4 = Path.Combine(baseDir, "ExtensionsData4");
            var newDir4 = Path.Combine(extensionsData4, "Playnite-Vault");
            var legacyDir4 = Path.Combine(extensionsData4, VaultDataMigration.LegacyGuid);
            Directory.CreateDirectory(Path.Combine(newDir4, "meta-cache", "app-x"));
            File.WriteAllText(Path.Combine(newDir4, "meta-cache", "app-x", "cover.jpg"), "新图",
                Encoding.UTF8);
            Directory.CreateDirectory(legacyDir4);
            File.WriteAllText(Path.Combine(legacyDir4, "settings.json"), "{\"old\":1}", Encoding.UTF8);
            Check(VaultDataMigration.MigrateIfNeeded(newDir4).Length == 0,
                "新目录里只有 meta-cache 时也判定为已有数据，不搬");
            Check(!File.Exists(Path.Combine(newDir4, "settings.json")),
                "于是老目录的 settings.json 没被搬进来（不会把新装的配置搞乱）");
        }

        // ---------- 工具 ----------

        private static string SafeRead(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : "(不存在)";
            }
            catch (Exception ex)
            {
                return "(读失败：" + ex.Message + ")";
            }
        }

        /// <summary>
        /// 读 .bat 自己写出来的日志。**必须按 OEM 代码页读** —— cmd.exe 在 chcp 936 下
        /// 输出的中文是 GBK，按 UTF-8 读会变成乱码，于是「内容明明写对了却断言失败」。
        /// </summary>
        private static string SafeReadOem(string path)
        {
            try
            {
                return File.Exists(path)
                    ? File.ReadAllText(path, VaultUpdater.OemEncoding())
                    : "(不存在)";
            }
            catch (Exception ex)
            {
                return "(读失败：" + ex.Message + ")";
            }
        }

        private static void TryCleanup(string root)
        {
            try
            {
                Directory.Delete(root, true);
            }
            catch
            {
                // 清理失败无所谓，临时目录里留着便于事后翻查
            }
        }
    }
}
