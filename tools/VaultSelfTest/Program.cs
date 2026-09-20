using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
