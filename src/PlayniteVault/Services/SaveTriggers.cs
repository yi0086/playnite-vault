using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteVault.Models;

namespace PlayniteVault.Services
{
    /// <summary>
    /// 云存档的触发层：把 Playnite 的「游戏要启动了 / 启动了 / 结束了」接成
    /// 存档同步动作。设置里选的是哪一档，这里就照哪一档来。
    ///
    /// <para><b>为什么全部塞在一个类里</b>：这三个钩子的顺序与彼此的前置条件
    /// （指纹必须**在游戏启动前**录完、退出后要做差分、自动上传只在内容变了才发生）
    /// 是一组紧密相关的约束，拆开放在三处就很容易改坏其中一条而没人发现。
    /// 一起放着，谁读都能一眼看完整条时间线。</para>
    ///
    /// <para><b>不阻塞启动</b>：<see cref="OnGameStarting"/> 跑在 Playnite 的启动路径上，
    /// 卡住它就是把游戏启动卡住。所以这里的网络探测都用**短超时**，而且只在用户
    /// 明确选了「启动前问一句」那一档时才做。</para>
    /// </summary>
    public class SaveTriggers
    {
        /// <summary>启动前问询时，探测远端最多允许花多久。超过就当作「这次不问了」。</summary>
        private const int StartupProbeTimeoutSeconds = 4;

        /// <summary>录指纹的时间预算。超过就截断，差分结果会自带「打折」标记。</summary>
        private const int FingerprintBudgetMs = 4000;

        private readonly VaultPlugin plugin;
        private readonly VaultSaveService saves;
        private readonly IPlayniteAPI api;

        public SaveTriggers(VaultPlugin plugin, VaultSaveService saves, IPlayniteAPI api)
        {
            this.plugin = plugin;
            this.saves = saves;
            this.api = api;
        }

        private VaultSettings Settings
        {
            get { return saves.Settings; }
        }

        // ==================================================================
        //  启动前
        // ==================================================================

        /// <summary>
        /// 游戏启动前。两件事，顺序不能反：
        /// <list type="number">
        /// <item>录一份目录指纹（必须**在游戏开始写文件之前**完成，否则差分就废了）；</item>
        /// <item>如果设置里要求，问一句「远端有更新要不要拉」。默认不覆盖本地。</item>
        /// </list>
        /// </summary>
        public void OnGameStarting(Game game)
        {
            if (game == null || !Settings.SaveSyncEnabled)
            {
                return;
            }

            // ---- 1. 指纹 ----
            if (Settings.SaveSessionSniff)
            {
                CaptureFingerprint(game);
            }

            // ---- 2. 远端有更新要不要拉 ----
            if (!saves.ShouldAskOnStart(game))
            {
                return;
            }

            try
            {
                AskForRemoteUpdate(game);
            }
            catch (Exception ex)
            {
                // 启动路径上出错绝不能拦住游戏
                VaultLog.Error("启动前检查远端存档失败（已忽略，游戏照常启动）", ex);
            }
        }

        private void CaptureFingerprint(Game game)
        {
            try
            {
                var roots = SaveSniffer.WatchRoots(game.InstallDirectory);
                if (roots.Count == 0)
                {
                    return;
                }

                var limits = new SaveSniffLimits
                {
                    MaxDepth = 3,
                    MaxEntries = 30000,
                    MaxMilliseconds = FingerprintBudgetMs
                };

                var map = SaveSniffer.CaptureAll(roots, limits);
                saves.SaveFingerprint(game.Id.ToString(), map);

                VaultLog.Info("已录下启动前指纹：" + game.Name + "，监听 " + map.Count + " 个根，共 "
                              + map.Values.Sum(f => f.Count) + " 个文件");
            }
            catch (Exception ex)
            {
                VaultLog.Warn("录启动前指纹失败（会话差分这一档本轮不可用）：" + ex.Message);
            }
        }

        /// <summary>
        /// 探测远端有没有「我没推过的更新」，有就问一句。
        /// 探测走短超时：NAS 不通的时候宁可这次不问，也不能让游戏卡在这儿。
        /// </summary>
        private void AskForRemoteUpdate(Game game)
        {
            SaveSnapshot remote = null;
            SaveGameManifest manifest = null;
            string problem = null;

            var done = new ManualResetEventSlim(false);

            Task.Run(() =>
            {
                try
                {
                    // 短超时：这是启动路径，不能让 NAS 的响应速度决定游戏能不能开起来
                    var client = saves.Service.CreateClient(
                        Math.Min(Settings.TimeoutSeconds, StartupProbeTimeoutSeconds));
                    var engine = new SaveSyncEngine(client, saves.SaveDataPath,
                        saves.Service.BuildSyncOptions(), new NullSaveReporter(),
                        CancellationToken.None);

                    List<string> notes;
                    manifest = saves.GetManifest(engine, game, false, out notes);
                    if (manifest != null)
                    {
                        remote = engine.FindRemoteUpdate(saves.GetGameKey(game), SaveBranch.Default,
                            manifest);
                    }
                }
                catch (Exception ex)
                {
                    problem = ex.Message;
                    VaultLog.Warn("探测远端存档更新失败（这次不问）：" + ex.Message);
                }
                finally
                {
                    done.Set();
                }
            });

            if (!done.Wait(TimeSpan.FromMilliseconds(
                    StartupProbeTimeoutSeconds * 1000 + 500)))
            {
                VaultLog.Warn("探测远端存档超时（超过 " + StartupProbeTimeoutSeconds
                              + " 秒），这次不问，直接启动游戏");
                return;
            }

            if (remote == null)
            {
                if (problem != null)
                {
                    VaultLog.Info("远端没有可拉取的存档更新（" + problem + "）");
                }

                return;
            }

            var answer = api.Dialogs.ShowMessage(
                "远端有一份比本地新的存档：" + Environment.NewLine + Environment.NewLine
                + remote.Describe() + Environment.NewLine + Environment.NewLine
                + "要现在拉下来覆盖本地吗？" + Environment.NewLine
                + "（选「否」就照本地这份继续玩，远端那份原样留着）",
                "Vault 云存档", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes)
            {
                // 记下「这份我问过了，用户不要」—— 否则每次启动都问一遍
                var client = saves.Service.CreateClient(
                    Math.Min(Settings.TimeoutSeconds, StartupProbeTimeoutSeconds));
                var engine2 = new SaveSyncEngine(client, saves.SaveDataPath,
                    saves.Service.BuildSyncOptions(), new NullSaveReporter(), CancellationToken.None);
                engine2.SnoozeRemoteUpdate(saves.GetGameKey(game), SaveBranch.Default, remote.Id);
                return;
            }

            RestoreFrom(game, manifest, remote.Id);
        }

        private void RestoreFrom(Game game, SaveGameManifest manifest, string snapshotId)
        {
            var engine = saves.CreateEngine(CancellationToken.None, new NullSaveReporter());
            var outcome = engine.Download(game.Id.ToString(), game.Name, game.InstallDirectory,
                manifest, new SaveSyncOptions
                {
                    Branch = SaveBranch.Default,
                    SnapshotId = snapshotId,
                    // 这里**不是**用户手点的恢复，而是启动路径上的自动补齐。
                    // 依然先留底，但不再多推一份快照 —— 免得每次启动都往远端加东西。
                    SafetyBackup = true,
                    SnapshotBeforeRestore = false,
                    KeepLocalBackups = Settings.SaveKeepLocalBackups
                });

            VaultLog.Info("启动前拉取远端存档：" + (outcome.Ok ? "成功 " : "失败 ")
                          + outcome.Counters.Describe());
        }

        // ==================================================================
        //  退出后
        // ==================================================================

        /// <summary>
        /// 游戏退出后。先做会话差分（这一档的「收获」要存下来给下次用），
        /// 再按设置决定要不要自动上传。
        /// </summary>
        public void OnGameStopped(Game game)
        {
            if (game == null || !Settings.SaveSyncEnabled)
            {
                return;
            }

            if (Settings.SaveSessionSniff)
            {
                CollectSessionCandidates(game);
            }

            if (!saves.ShouldUploadOnStop(game))
            {
                return;
            }

            try
            {
                AutoUpload(game);
            }
            catch (Exception ex)
            {
                VaultLog.Error("游戏退出后自动上传存档失败：" + game.Name, ex);
            }
        }

        /// <summary>把「启动前 / 退出后」两份指纹差分掉，候选存起来等用户确认。</summary>
        private void CollectSessionCandidates(Game game)
        {
            try
            {
                var gameId = game.Id.ToString();
                var before = saves.LoadFingerprint(gameId);
                if (before.Count == 0)
                {
                    return;
                }

                var ctx = new SaveSniffContext
                {
                    GameName = game.Name,
                    GameInstallDir = game.InstallDirectory,
                    Budget = TimeSpan.FromSeconds(5)
                };

                var found = new List<SaveSniffCandidate>();
                foreach (var root in before.Keys.ToList())
                {
                    var after = SaveSniffer.Capture(root, new SaveSniffLimits
                    {
                        MaxDepth = 3,
                        MaxEntries = 30000,
                        MaxMilliseconds = FingerprintBudgetMs
                    });

                    found.AddRange(SaveSniffer.Diff(before[root], after, ctx));
                }

                // 只留真的可能：分数太低的当场扔掉，免得界面上堆一堆噪声
                var kept = PcgamingWikiClient.Merge(found)
                    .Where(c => c.Score >= 50)
                    .ToList();

                if (kept.Count > 0)
                {
                    saves.SaveDetected(gameId, kept);
                    VaultLog.Info("会话差分得到 " + kept.Count + " 个候选：" + game.Name);
                }

                saves.ClearFingerprint(gameId);
            }
            catch (Exception ex)
            {
                VaultLog.Error("会话差分失败：" + game.Name, ex);
            }
        }

        private void AutoUpload(Game game)
        {
            List<string> notes;
            if (saves.MergedPaths(game, out notes).Count(s => s.Enabled) == 0)
            {
                // 没配路径就没什么可传的。会话差分已经把线索存下了，不用在这儿打扰用户。
                return;
            }

            using (plugin.BeginTransfer("存档同步"))
            {
                var engine = saves.CreateEngine(CancellationToken.None, new NullSaveReporter());
                var manifest = saves.GetManifest(engine, game, true, out notes);

                var outcome = engine.Upload(game.Id.ToString(), game.Name, game.InstallDirectory,
                    manifest, new SaveSyncOptions
                    {
                        Branch = BranchOrDefault(),
                        KeepPerBranch = Settings.SaveKeepPerBranch,
                        Origin = SaveSnapshotOrigin.AutoOnStop
                    });

                if (!outcome.Ok)
                {
                    VaultLog.Warn("退出后自动上传存档失败：" + game.Name + "：" + outcome.Failure);
                    return;
                }

                var summary = outcome.Counters.SnapshotsUploaded > 0
                    ? "上传 " + outcome.Counters.SnapshotsUploaded + " 份（"
                      + SyncProgress.FormatSize(outcome.Counters.BytesUp) + "）"
                    : "内容没变，没造新快照";

                VaultLog.Info("退出后自动上传存档：" + game.Name + "：" + summary
                              + "，跳过 " + outcome.Counters.FilesSkipped + " 个");
            }
        }

        private string BranchOrDefault()
        {
            var name = (Settings.SaveDefaultBranch ?? string.Empty).Trim();
            return name.Length == 0 ? SaveBranch.Default : name;
        }
    }
}
