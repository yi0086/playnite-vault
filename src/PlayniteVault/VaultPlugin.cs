using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using PlayniteVault.Controllers;
using PlayniteVault.Models;
using PlayniteVault.Net;
using PlayniteVault.Services;
using PlayniteVault.UI;

namespace PlayniteVault
{
    /// <summary>
    /// 库插件：把 NAS 上的 WebDAV 仓库当成一个游戏库来源。
    /// 远端条目 → 库里的游戏条目；未安装的条目由 Playnite 自动显示「安装」按钮。
    /// </summary>
    public class VaultPlugin : LibraryPlugin
    {
        public static readonly Guid PluginGuid = Guid.Parse("5e76bf50-cb8a-4a87-ad24-1912c746c6f0");

        public static VaultPlugin Instance { get; private set; }

        private readonly VaultService service;
        private readonly VaultSettingsViewModel settingsVm;
        private readonly VaultUpdater updater;
        private readonly LibraryAutoRefresh autoRefresh;

        /// <summary>正在进行的传输数（安装 / 归档 / 修复）。自动刷新要避开它们。</summary>
        private int transferCount;

        /// <summary>更新检查正在跑（避免启动检查和手动检查撞车）。</summary>
        private int updateCheckBusy;

        public override Guid Id { get; } = PluginGuid;

        public override string Name
        {
            get { return "Playnite Vault (NAS Library)"; }
        }

        public VaultService Service
        {
            get { return service; }
        }

        public VaultUpdater Updater
        {
            get { return updater; }
        }

        public VaultPlugin(IPlayniteAPI api) : base(api)
        {
            Instance = this;

            Properties = new LibraryPluginProperties
            {
                HasSettings = true
            };

            // 插件改过名（VaultDemo_<guid> → Playnite-Vault），而 Playnite 是拿 Id 当
            // 扩展数据目录名的 —— 先把老目录里的设置/索引搬过来，再建服务。
            // 日志目录也跟着新路径，搬家过程才记得下来。
            var dataPath = GetPluginUserDataPath();
            VaultLog.Init(dataPath);
            VaultDataMigration.MigrateIfNeeded(dataPath);

            service = new VaultService(api, dataPath);
            settingsVm = new VaultSettingsViewModel(service);
            updater = new VaultUpdater(service.DataPath, service.Settings);

            // 设置落盘后要让后台的定时器跟着变（改间隔 / 开关自动刷新）
            settingsVm.SettingsSaved += OnSettingsSaved;

            autoRefresh = new LibraryAutoRefresh(service, ApplyRemoteIndex, DispatchToUi);
            autoRefresh.Completed += OnAutoRefreshCompleted;

            VaultLog.Info("VaultPlugin 已构造，数据目录=" + service.DataPath
                + "，版本=" + VaultUpdater.CurrentVersion());
        }

        private void OnSettingsSaved()
        {
            try
            {
                autoRefresh.Rearm();
            }
            catch (Exception ex)
            {
                VaultLog.Error("重设定时器失败", ex);
            }
        }

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            VaultLog.Info("OnApplicationStarted，WebDAV=" + service.Settings.WebDavUrl);

            StartAutoRefresh();
            ScheduleUpdateCheck();
        }

        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
            VaultLog.Info("OnApplicationStopped");
            try
            {
                autoRefresh.Dispose();
            }
            catch (Exception ex)
            {
                VaultLog.Warn("关闭自动刷新失败：" + ex.Message);
            }
        }

        // ---------- 运行游戏时暂停自动刷新 ----------

        public override void OnGameStarted(OnGameStartedEventArgs args)
        {
            VaultLog.Info("游戏已启动" + (args == null || args.Game == null ? "" : "：" + args.Game.Name)
                          + "，自动刷新暂停");
            autoRefresh.SetGameRunning(true);
        }

        public override void OnGameStopped(OnGameStoppedEventArgs args)
        {
            VaultLog.Info("游戏已结束，自动刷新恢复");
            autoRefresh.SetGameRunning(false);
        }

        // ---------- 传输占用 ----------

        /// <summary>
        /// 传输期间禁止自动刷新去抢 NAS 带宽。
        /// 用法：<c>using (BeginTransfer("归档")) { ... }</c>
        /// </summary>
        public IDisposable BeginTransfer(string what)
        {
            System.Threading.Interlocked.Increment(ref transferCount);
            autoRefresh.SetTransferBusy(true);
            VaultLog.Info("开始传输（" + what + "），自动刷新暂时让路");
            return new TransferScope(this, what);
        }

        public bool IsTransfering
        {
            get { return System.Threading.Volatile.Read(ref transferCount) > 0; }
        }

        private class TransferScope : IDisposable
        {
            private readonly VaultPlugin owner;
            private readonly string what;
            private bool done;

            public TransferScope(VaultPlugin owner, string what)
            {
                this.owner = owner;
                this.what = what;
            }

            public void Dispose()
            {
                if (done)
                {
                    return;
                }
                done = true;

                var left = System.Threading.Interlocked.Decrement(ref owner.transferCount);
                owner.autoRefresh.SetTransferBusy(left > 0);
                if (left <= 0)
                {
                    VaultLog.Info("传输结束（" + what + "）");
                }
            }
        }

        // ---------- 库导入 ----------

        public override IEnumerable<GameMetadata> GetGames(LibraryGetGamesArgs args)
        {
            var result = new List<GameMetadata>();

            try
            {
                string source;
                string error;
                var index = service.GetIndex(out source, out error);

                if (index == null || index.Apps == null || index.Apps.Count == 0)
                {
                    VaultLog.Warn("库导入：没有拿到任何条目" + (error == null ? "" : "，原因：" + error));
                    return result;
                }

                VaultLog.Info(string.Format("库导入：来源={0}，条目数={1}{2}",
                    source, index.Apps.Count, error == null ? "" : "，错误：" + error));

                var local = service.GetLocalIndex();

                foreach (var app in index.Apps)
                {
                    result.Add(BuildGameMetadata(app, local));
                }
            }
            catch (Exception ex)
            {
                VaultLog.Error("GetGames 异常", ex);
            }

            return result;
        }

        /// <summary>远端条目 → Playnite 的 GameMetadata（库导入与手动刷新共用）。</summary>
        private GameMetadata BuildGameMetadata(AppEntry app, LocalIndex local)
        {
            var entry = local.Find(app.Id);
            var installed = entry != null && Directory.Exists(entry.InstallDir);

            // 落盘目录名来自随包元数据的 InstallDirName（上传前原始安装目录的最后一段），
            // 不用 Id、更不用 Playnite 里的中文显示名 —— 中文目录装不了某些游戏。
            var installDir = installed
                ? entry.InstallDir
                : service.GetInstallDir(app.Id, app.Metadata == null ? null : app.Metadata.InstallDirName);
            var launchExe = string.IsNullOrWhiteSpace(app.LaunchExe) ? "app.exe" : app.LaunchExe;

            var metadata = new GameMetadata
            {
                GameId = app.Id,
                Name = app.Name,
                IsInstalled = installed,
                InstallDirectory = installDir
            };

            var play = new GameAction
            {
                Name = "启动",
                Type = GameActionType.File,
                Path = Path.Combine(installDir, launchExe),
                WorkingDir = installDir,
                IsPlayAction = true
            };

            // 随包元数据直接回填，换机器重装后不需要再联网刮削
            ApplyMetadata(metadata, app.Metadata, app.Id);
            if (app.Metadata != null && !string.IsNullOrWhiteSpace(app.Metadata.LaunchArguments))
            {
                play.Arguments = app.Metadata.LaunchArguments;
            }

            metadata.GameActions = new List<GameAction> { play };

            if (app.TotalBytes > 0)
            {
                // GameMetadata.InstallSize 是 ulong?（Playnite 内部按无符号字节统计）
                metadata.InstallSize = (ulong)app.TotalBytes;
            }

            return metadata;
        }

        // ---------- 元数据 ----------

        /// <summary>把随包元数据回填到 GameMetadata（库导入用）。</summary>
        private void ApplyMetadata(GameMetadata target, AppMetadata meta, string appId)
        {
            if (meta == null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(meta.SortingName)) target.SortingName = meta.SortingName;
            if (!string.IsNullOrWhiteSpace(meta.Description)) target.Description = meta.Description;
            if (!string.IsNullOrWhiteSpace(meta.Version)) target.Version = meta.Version;
            if (!string.IsNullOrWhiteSpace(meta.Source)) target.Source = new MetadataNameProperty(meta.Source);

            target.Developers = Props(meta.Developers);
            target.Publishers = Props(meta.Publishers);
            target.Genres = Props(meta.Genres);
            target.Categories = Props(meta.Categories);
            target.Tags = Props(meta.Tags);
            target.Features = Props(meta.Features);
            target.Series = Props(meta.Series);
            target.Platforms = Props(meta.Platforms);
            target.Regions = Props(meta.Regions);

            target.CommunityScore = meta.CommunityScore;
            target.CriticScore = meta.CriticScore;
            target.UserScore = meta.UserScore;

            var release = ParseReleaseDate(meta.ReleaseDate);
            if (release.HasValue)
            {
                target.ReleaseDate = release.Value;
            }

            if (meta.Links != null && meta.Links.Count > 0)
            {
                target.Links = meta.Links
                    .Select(l => new Link(l.Name ?? l.Url, l.Url))
                    .ToList();
            }

            if (meta.AgeRatings != null && meta.AgeRatings.Count > 0)
            {
                target.AgeRatings = Props(meta.AgeRatings);
            }

            // 图片：先从仓库抓到本地缓存，再把本地路径喂给 Playnite
            var icon = service.EnsureLocalImage(appId, "icon", meta);
            if (icon != null) target.Icon = new MetadataFile(icon);

            var cover = service.EnsureLocalImage(appId, "cover", meta);
            if (cover != null) target.CoverImage = new MetadataFile(cover);

            var background = service.EnsureLocalImage(appId, "background", meta);
            if (background != null) target.BackgroundImage = new MetadataFile(background);
        }

        /// <summary>从 Playnite 的 Game 抓元数据，随包一起归档。</summary>
        private AppMetadata BuildMetadata(Game game)
        {
            var meta = new AppMetadata
            {
                Name = game.Name,
                SortingName = game.SortingName,
                Description = game.Description,
                Version = game.Version,
                Source = game.Source == null ? null : game.Source.Name,
                Developers = NamesOf(game.Developers),
                Publishers = NamesOf(game.Publishers),
                Genres = NamesOf(game.Genres),
                Categories = NamesOf(game.Categories),
                Tags = NamesOf(game.Tags),
                Features = NamesOf(game.Features),
                Series = NamesOf(game.Series),
                Platforms = NamesOf(game.Platforms),
                Regions = NamesOf(game.Regions),
                AgeRatings = NamesOf(game.AgeRatings),
                CommunityScore = game.CommunityScore,
                CriticScore = game.CriticScore,
                UserScore = game.UserScore,
                Hidden = game.Hidden,
                Favorite = game.Favorite,
                ReleaseDate = FormatReleaseDate(game.ReleaseDate),
                Links = LinksOf(game.Links),
                InstallDirName = InstallDirNameOf(game)
            };

            var play = game.GameActions == null
                ? null
                : game.GameActions.FirstOrDefault(a => a.IsPlayAction);
            if (play != null)
            {
                meta.LaunchArguments = play.Arguments;
                meta.LaunchWorkingDir = play.WorkingDir;
            }

            meta.Images = CollectImages(game);
            return meta;
        }

        /// <summary>
        /// 归档前该应用在 Playnite 里的安装目录名（路径最后一级文件夹名）。
        /// 例：D:\Games\Brotato → "Brotato"。
        ///
        /// 解包端拿它当本地子目录名，这样「解出来放哪儿」和「上传前放哪儿」一致；
        /// 取不到就返回 null，读取端回退成 app id。
        /// </summary>
        private static string InstallDirNameOf(Game game)
        {
            try
            {
                var dir = game == null ? null : game.InstallDirectory;
                if (string.IsNullOrWhiteSpace(dir))
                {
                    return null;
                }

                var trimmed = dir.TrimEnd('\\', '/');
                if (trimmed.Length == 0)
                {
                    return null;
                }

                var name = Path.GetFileName(trimmed);
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 本地封面 / 背景 / 图标 → 本地绝对路径。
        /// 归档时 VaultService 会把它们上传到 apps/{id}/meta/ 并把这里的值改写成仓库相对路径。
        /// </summary>
        private Dictionary<string, string> CollectImages(Game game)
        {
            var images = new Dictionary<string, string>();
            AddImage(images, "cover", game.CoverImage);
            AddImage(images, "background", game.BackgroundImage);
            AddImage(images, "icon", game.Icon);
            return images;
        }

        private void AddImage(Dictionary<string, string> images, string key, string path)
        {
            var resolved = ResolveImagePath(path);
            if (resolved != null)
            {
                images[key] = resolved;
            }
        }

        private string ResolveImagePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            try
            {
                if (Path.IsPathRooted(path))
                {
                    return File.Exists(path) ? path : null;
                }

                var root = PlayniteApi.Paths.ConfigurationPath;
                if (string.IsNullOrWhiteSpace(root))
                {
                    return null;
                }

                var full = Path.Combine(root, "library", "files",
                    path.Replace('/', Path.DirectorySeparatorChar));
                return File.Exists(full) ? full : null;
            }
            catch (Exception ex)
            {
                VaultLog.Warn("解析图片路径失败：" + path + "，" + ex.Message);
                return null;
            }
        }

        /// <summary>Playnite 的数据库对象列表（或字符串列表）→ 名字列表。</summary>
        private static List<string> NamesOf(object items)
        {
            var result = new List<string>();
            var enumerable = items as System.Collections.IEnumerable;
            if (enumerable == null)
            {
                return result;
            }

            foreach (var item in enumerable)
            {
                if (item == null)
                {
                    continue;
                }

                var text = item as string;
                if (text != null)
                {
                    if (text.Length > 0)
                    {
                        result.Add(text);
                    }
                    continue;
                }

                var db = item as DatabaseObject;
                if (db != null && !string.IsNullOrWhiteSpace(db.Name))
                {
                    result.Add(db.Name);
                }
            }

            return result;
        }

        private static List<LinkEntry> LinksOf(IEnumerable<Link> links)
        {
            var result = new List<LinkEntry>();
            if (links == null)
            {
                return result;
            }

            foreach (var link in links)
            {
                if (link == null || string.IsNullOrWhiteSpace(link.Url))
                {
                    continue;
                }
                result.Add(new LinkEntry { Name = link.Name, Url = link.Url });
            }

            return result;
        }

        private static string FormatReleaseDate(ReleaseDate? date)
        {
            if (!date.HasValue)
            {
                return null;
            }

            try
            {
                // 用 SDK 自己的序列化，避免依赖 Year/Month/Day 的可空性差异
                return date.Value.Serialize();
            }
            catch (Exception ex)
            {
                VaultLog.Warn("序列化发行日期失败：" + ex.Message);
                return null;
            }
        }

        private static ReleaseDate? ParseReleaseDate(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            try
            {
                ReleaseDate value;
                if (ReleaseDate.TryDeserialize(text, out value))
                {
                    return value;
                }
            }
            catch (Exception ex)
            {
                VaultLog.Warn("解析发行日期失败：" + text + "，" + ex.Message);
            }
            return null;
        }

        /// <summary>字符串集合 → Playnite 的 MetadataProperty 集合（按名字匹配）。</summary>
        private static HashSet<MetadataProperty> Props(List<string> names)
        {
            var set = new HashSet<MetadataProperty>();
            if (names == null)
            {
                return set;
            }

            foreach (var name in names)
            {
                if (!string.IsNullOrWhiteSpace(name))
                {
                    set.Add(new MetadataNameProperty(name));
                }
            }

            return set;
        }

        // ---------- 安装 / 卸载 ----------

        public override IEnumerable<InstallController> GetInstallActions(GetInstallActionsArgs args)
        {
            if (args.Game == null || args.Game.PluginId != Id)
            {
                yield break;
            }

            if (args.Game.IsInstalled)
            {
                yield break;
            }

            VaultLog.Info("提供安装控制器：" + args.Game.Name);
            yield return new VaultInstallController(args.Game, service);
        }

        public override IEnumerable<UninstallController> GetUninstallActions(GetUninstallActionsArgs args)
        {
            if (args.Game == null || args.Game.PluginId != Id)
            {
                yield break;
            }

            yield return new VaultUninstallController(args.Game, service);
        }

        // ---------- 菜单 ----------

        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            var items = new List<GameMenuItem>();

            items.Add(new GameMenuItem
            {
                MenuSection = "Vault",
                Description = "从 NAS 刷新库条目（立即生效）",
                Action = a => RefreshLibraryEntries(true)
            });

            items.Add(new GameMenuItem
            {
                MenuSection = "Vault",
                Description = "测试仓库连接",
                Action = a => TestConnection()
            });

            items.Add(new GameMenuItem
            {
                MenuSection = "Vault",
                Description = "归档选中应用到 NAS",
                Action = a => ArchiveGames(a.Games)
            });

            items.Add(new GameMenuItem
            {
                MenuSection = "Vault",
                Description = "修复安装（只补缺失文件）",
                Action = a => RepairGames(a.Games)
            });

            items.Add(new GameMenuItem
            {
                MenuSection = "Vault",
                Description = "管理仓库应用（删除，需管理口令）",
                Action = a => ManageRepository()
            });

            items.Add(new GameMenuItem
            {
                MenuSection = "Vault",
                Description = "打开插件数据目录",
                Action = a => OpenPath(service.DataPath)
            });

            items.Add(new GameMenuItem
            {
                MenuSection = "Vault",
                Description = "打开插件日志",
                Action = a => OpenPath(VaultLog.LogPath)
            });

            return items;
        }

        /// <summary>
        /// 打开仓库管理窗口（删除应用）。进门先过管理口令这一关：
        ///  · 仓库还没设置过口令 → 明确提示，并允许当场设置；
        ///  · 设置过 → 输一次，最多 3 次机会。
        ///
        /// 口令只是**防误触闸门**：派生值明文存在仓库根的 vault-admin.json，
        /// 真正拦住外人的是 WebDAV 账号。别把它当权限系统用。
        /// </summary>
        private void ManageRepository()
        {
            if (!service.Settings.IsConfigured)
            {
                PlayniteApi.Dialogs.ShowMessage("还没有配置 WebDAV 地址，请先到插件设置里填写。", "Playnite Vault");
                return;
            }

            VaultAdmin admin;
            try
            {
                admin = service.FetchAdmin();
            }
            catch (Exception ex)
            {
                VaultLog.Error("读取仓库管理口令失败", ex);
                PlayniteApi.Dialogs.ShowErrorMessage("连接仓库失败：" + ex.Message, "Playnite Vault");
                return;
            }

            if (admin == null || !admin.IsSet)
            {
                if (!EnsureAdminPassword())
                {
                    return;
                }
            }
            else if (!PromptAdminPassword())
            {
                return;
            }

            new VaultAdminWindow(service).ShowDialog();
        }

        /// <summary>「还没设置口令」时的引导：解释清楚它是什么，再当场设置。</summary>
        private bool EnsureAdminPassword()
        {
            var answer = PlayniteApi.Dialogs.ShowMessage(
                "这个仓库还没有设置管理口令。\n\n"
                + "管理口令用来保护「删除仓库应用」这类破坏性操作。\n"
                + "请注意它是防误触闸门、不是账号体系：口令的派生值保存在仓库根的 "
                + VaultAdmin.FileName + "，任何能读写这个仓库的人都能拿到它。\n"
                + "真正拦住外人的是 WebDAV 账号本身。\n\n"
                + "现在设置吗？",
                "Vault 仓库管理",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);

            if (answer != System.Windows.MessageBoxResult.Yes)
            {
                return false;
            }

            var fresh = PasswordPrompt.Ask(null, "设置管理口令", "给这个仓库设置管理口令（至少 4 位）：", true);
            if (fresh == null)
            {
                return false;
            }

            if (fresh.Length < 4)
            {
                PlayniteApi.Dialogs.ShowMessage("口令至少 4 位，已取消。", "Vault 仓库管理");
                return false;
            }

            try
            {
                service.SetAdminPassword(fresh);
                PlayniteApi.Dialogs.ShowMessage(
                    "管理口令已保存在仓库根的 " + VaultAdmin.FileName + "。\n"
                    + "换机器用同一个仓库时会自动读到它，不需要重新设置。",
                    "Vault 仓库管理");
                return true;
            }
            catch (Exception ex)
            {
                VaultLog.Error("设置管理口令失败", ex);
                PlayniteApi.Dialogs.ShowErrorMessage("设置失败：" + ex.Message, "Vault 仓库管理");
                return false;
            }
        }

        /// <summary>已设置口令时的校验循环。返回 true 表示通过。</summary>
        private bool PromptAdminPassword()
        {
            const int maxAttempts = 3;

            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var tip = attempt == 1
                    ? "请输入仓库管理口令："
                    : string.Format("口令不正确，再试一次（第 {0}/{1} 次）：", attempt, maxAttempts);

                var password = PasswordPrompt.Ask(null, "仓库管理口令", tip, false);
                if (password == null)
                {
                    return false;
                }

                bool isSet;
                bool ok;
                try
                {
                    ok = service.VerifyAdminPassword(password, out isSet);
                }
                catch (Exception ex)
                {
                    VaultLog.Error("校验管理口令失败", ex);
                    PlayniteApi.Dialogs.ShowErrorMessage("校验失败：" + ex.Message, "Vault 仓库管理");
                    return false;
                }

                if (!isSet)
                {
                    // 远端 vault-admin.json 在本次会话期间被删掉了：让用户重新走一遍引导
                    PlayniteApi.Dialogs.ShowMessage(
                        "仓库的管理口令似乎已被移除，请重新打开本功能。", "Vault 仓库管理");
                    return false;
                }

                if (ok)
                {
                    return true;
                }
            }

            PlayniteApi.Dialogs.ShowMessage("口令连续输错 3 次，已取消。", "Vault 仓库管理");
            return false;
        }

        private void OpenPath(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path))
                {
                    return;
                }

                if (File.Exists(path))
                {
                    Process.Start(new ProcessStartInfo("notepad.exe", "\"" + path + "\"") { UseShellExecute = true });
                    return;
                }

                if (Directory.Exists(path))
                {
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                VaultLog.Error("打开路径失败：" + path, ex);
                PlayniteApi.Dialogs.ShowErrorMessage("打开失败：" + ex.Message, "Playnite Vault");
            }
        }

        /// <summary>
        /// 把远端索引同步成一等公民的库条目，不依赖 Playnite 的「更新游戏库」。
        ///
        /// 背景：Playnite 的库更新由设置项 CheckForLibraryUpdates 控制，默认是「从不」，
        /// 于是 GetGames 根本不会被调用，新上传的应用永远不出现在库里
        /// （这正是「只有几条无法安装的旧 demo、没有冰与火之舞」的原因）。
        /// 这里直接走 IGameDatabase.ImportGame，一次性把差异补齐：
        /// 远端已删除的条目移除、缺失的新增、已有的更新名称与安装状态。
        /// </summary>
        /// <param name="interactive">
        /// true = 用户点的（有弹窗）；false = 后台自动刷新（只写日志，不打断用户）。
        /// </param>
        public void RefreshLibraryEntries(bool interactive)
        {
            if (!service.Settings.IsConfigured)
            {
                if (interactive)
                {
                    PlayniteApi.Dialogs.ShowMessage("还没有配置 WebDAV 地址。\n请到 设置 → 扩展 → Playnite Vault 里填写。", "Playnite Vault");
                }
                return;
            }

            try
            {
                string source;
                string error;
                var index = service.GetIndex(out source, out error);

                if (index == null || index.Apps == null || index.Apps.Count == 0)
                {
                    var message = "没有从仓库拿到任何条目。\n\n" + (error ?? "远端索引为空");
                    if (interactive)
                    {
                        PlayniteApi.Dialogs.ShowErrorMessage(message, "Playnite Vault");
                    }
                    else
                    {
                        VaultLog.Warn(message);
                    }
                    return;
                }

                var outcome = WriteLibraryFromIndex(index);

                // 手动刷新也要更新指纹：否则紧接着的一次自动刷新会把它当成「有新变化」再写一遍
                RememberAppliedIndex(index, source);

                VaultLog.Info(string.Format("刷新完成：来源={0}，新增 {1}，更新 {2}，移除 {3}",
                    source, outcome.Added, outcome.Updated, outcome.Removed));

                if (interactive)
                {
                    PlayniteApi.Dialogs.ShowMessage(
                        string.Format(
                            "库条目已刷新。\n\n来源：{0}\n远端条目：{1}\n\n新增：{2}\n更新：{3}\n移除：{4}\n\n" +
                            "如果左侧列表里还看不到，请检查过滤器面板是否只勾选了某个库来源（把「库」过滤器全部取消勾选即可）。",
                            DescribeSource(source), index.Apps.Count,
                            outcome.Added, outcome.Updated, outcome.Removed),
                        "Playnite Vault");
                }
            }
            catch (Exception ex)
            {
                VaultLog.Error("刷新库条目失败", ex);
                if (interactive)
                {
                    PlayniteApi.Dialogs.ShowErrorMessage("刷新失败：" + ex.Message, "Playnite Vault");
                }
            }
        }

        /// <summary>
        /// 真正动数据库的那一段。**必须在 UI 线程上调用** ——
        /// Playnite 的库写入会驱动界面重排，从后台线程直接写会偶发 UI 异常。
        /// </summary>
        private AutoRefreshOutcome WriteLibraryFromIndex(RepositoryIndex index)
        {
            var outcome = new AutoRefreshOutcome();

            var remoteIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var app in index.Apps)
            {
                remoteIds.Add(app.Id);
            }

            var local = service.GetLocalIndex();

            PlayniteApi.Database.BeginBufferUpdate();
            try
            {
                // 1) 远端已经没有的旧条目（含早期 mock 服务留下的 demo-game 等）就地清掉
                var managed = PlayniteApi.Database.Games.Where(g => g.PluginId == Id).ToList();
                foreach (var game in managed)
                {
                    if (string.IsNullOrWhiteSpace(game.GameId) || !remoteIds.Contains(game.GameId))
                    {
                        PlayniteApi.Database.Games.Remove(game.Id);
                        outcome.Removed++;
                        VaultLog.Info("刷新：移除已下架条目 " + game.Name + "（" + game.GameId + "）");
                    }
                }

                // 2) 远端条目逐个对齐（重新取一次，前面的删除已经生效）
                var current = PlayniteApi.Database.Games.Where(g => g.PluginId == Id).ToList();
                foreach (var app in index.Apps)
                {
                    var match = current.FirstOrDefault(g =>
                        string.Equals(g.GameId, app.Id, StringComparison.OrdinalIgnoreCase));

                    if (match == null)
                    {
                        PlayniteApi.Database.ImportGame(BuildGameMetadata(app, local), this);
                        outcome.Added++;
                        VaultLog.Info("刷新：新增条目 " + app.Name + "（" + app.Id + "）");
                    }
                    else
                    {
                        UpdateEntry(match, app, local);
                        PlayniteApi.Database.Games.Update(match);
                        outcome.Updated++;
                    }
                }
            }
            finally
            {
                PlayniteApi.Database.EndBufferUpdate();
            }

            outcome.Ran = true;
            outcome.Changed = true;
            outcome.WroteLibrary = true;
            return outcome;
        }

        /// <summary>
        /// 记下「这份索引已经应用过了」。
        /// 只有远端来源才记 —— 缓存索引的指纹和已应用的不同，记下来会把判据带偏。
        /// </summary>
        private void RememberAppliedIndex(RepositoryIndex index, string source)
        {
            if (!string.Equals(source, "remote", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            service.State.AppliedIndexHash = LibraryAutoRefresh.Fingerprint(index);
            service.State.LastIndexCheckUtc = DateTime.UtcNow;
            service.State.LastLibraryWriteUtc = DateTime.UtcNow;
            service.State.ConsecutiveRefreshFailures = 0;
            service.SaveState();
        }

        /// <summary>LibraryAutoRefresh 的写库回调 —— 它已经判断过「该不该刷」，这里只管写。</summary>
        private AutoRefreshOutcome ApplyRemoteIndex(RepositoryIndex index, string source)
        {
            var outcome = WriteLibraryFromIndex(index);
            outcome.Source = source;
            outcome.Total = index.Apps == null ? 0 : index.Apps.Count;
            return outcome;
        }

        /// <summary>索引来源 → 给人看的说法。</summary>
        private static string DescribeSource(string source)
        {
            if (string.Equals(source, "remote", StringComparison.OrdinalIgnoreCase))
            {
                return "NAS 远端索引";
            }
            if (string.Equals(source, "cache", StringComparison.OrdinalIgnoreCase))
            {
                return "本地缓存索引（NAS 不可达）";
            }
            return "本地安装记录（NAS 不可达）";
        }

        /// <summary>把动作丢回 UI 线程。数据库写入必须在 UI 线程上做。</summary>
        private static void DispatchToUi(Action action)
        {
            var app = System.Windows.Application.Current;
            if (app == null || app.Dispatcher.CheckAccess())
            {
                action();
                return;
            }
            app.Dispatcher.Invoke(action);
        }

        /// <summary>把远端条目的可变字段同步到已有库条目的身上（不动元数据，避免覆盖用户手改内容）。</summary>
        private void UpdateEntry(Game game, AppEntry app, LocalIndex local)
        {
            var entry = local.Find(app.Id);
            var installed = entry != null && Directory.Exists(entry.InstallDir);
            var installDir = installed
                ? entry.InstallDir
                : service.GetInstallDir(app.Id, app.Metadata == null ? null : app.Metadata.InstallDirName);
            var launchExe = string.IsNullOrWhiteSpace(app.LaunchExe) ? "app.exe" : app.LaunchExe;

            game.Name = app.Name;
            game.IsInstalled = installed;
            game.InstallDirectory = installDir;

            var play = new GameAction
            {
                Name = "启动",
                Type = GameActionType.File,
                Path = Path.Combine(installDir, launchExe),
                WorkingDir = installDir,
                IsPlayAction = true
            };

            if (app.Metadata != null && !string.IsNullOrWhiteSpace(app.Metadata.LaunchArguments))
            {
                play.Arguments = app.Metadata.LaunchArguments;
            }

            game.GameActions = new System.Collections.ObjectModel.ObservableCollection<GameAction> { play };
        }

        private void TestConnection()
        {
            if (!service.Settings.IsConfigured)
            {
                PlayniteApi.Dialogs.ShowMessage("还没有配置 WebDAV 地址。\n请到 设置 → 扩展 → Playnite Vault 里填写。", "Playnite Vault");
                return;
            }

            try
            {
                var client = service.CreateClient();
                var count = client.TestConnection();
                var hasIndex = client.Exists("index.json");

                PlayniteApi.Dialogs.ShowMessage(
                    string.Format("连接成功。\n\n根目录可见条目：{0}\nindex.json：{1}",
                        count, hasIndex ? "存在" : "不存在"),
                    "Playnite Vault");
            }
            catch (Exception ex)
            {
                VaultLog.Error("测试连接失败", ex);
                PlayniteApi.Dialogs.ShowErrorMessage(
                    "连接失败：\n\n" + WebDavDiagnostics.Describe(ex,
                        service.Settings.WebDavUrl, service.Settings.Username),
                    "Playnite Vault");
            }
        }

        /// <summary>
        /// 把选中的应用归档到 NAS。源目录取游戏自身的 InstallDirectory，
        /// 因此对手动添加的本地应用同样可用。
        /// </summary>
        private void ArchiveGames(List<Game> games)
        {
            if (!service.Settings.IsConfigured)
            {
                PlayniteApi.Dialogs.ShowMessage("还没有配置 WebDAV 地址，请先到插件设置里填写。", "Playnite Vault");
                return;
            }

            var targets = new List<Game>();
            foreach (var game in games)
            {
                if (string.IsNullOrWhiteSpace(game.InstallDirectory) || !Directory.Exists(game.InstallDirectory))
                {
                    VaultLog.Warn("跳过（没有有效的安装目录）：" + game.Name);
                    continue;
                }
                targets.Add(game);
            }

            if (targets.Count == 0)
            {
                PlayniteApi.Dialogs.ShowMessage("选中的应用都没有可用的安装目录，无法归档。", "Playnite Vault");
                return;
            }

            var summary = string.Join("\n", targets.Select(g => "· " + g.Name + "  →  " + g.InstallDirectory));
            var answer = PlayniteApi.Dialogs.ShowMessage(
                "将把以下应用归档到 NAS：\n\n" + summary + "\n\n源目录里的文件会被上传，本地文件不会被删除。是否继续？",
                "Playnite Vault",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);

            if (answer != System.Windows.MessageBoxResult.Yes)
            {
                return;
            }

            PlayniteApi.Dialogs.ActivateGlobalProgress(progress =>
            {
                using (BeginTransfer("归档"))
                {
                foreach (var game in targets)
                {
                    if (progress.CancelToken.IsCancellationRequested)
                    {
                        break;
                    }

                    // Id 取「上传前安装目录的最后一段」压出来的 ASCII slug。
                    // 已是本插件条目时**一律沿用原 Id**：改 Id 会让库条目与已装目录失联，
                    // 而落盘目录名现在由元数据 InstallDirName 决定，跟 Id 已经解耦了。
                    var appId = game.PluginId == Id
                        ? game.GameId
                        : VaultService.MakeAppId(InstallDirNameOf(game), game.Name);
                    var launchExe = GuessLaunchExe(game);

                    try
                    {
                        // 元数据随包一起上传，下次安装就不用重新刮削
                        var metadata = BuildMetadata(game);

                        var options = service.BuildSyncOptions();
                        var sink = new ProgressSink(progress, "正在归档 " + game.Name);

                        var result = service.ArchiveApp(
                            appId,
                            game.Name,
                            game.InstallDirectory,
                            launchExe,
                            "1.0",
                            metadata,
                            options,
                            sink.Apply,
                            progress.CancelToken);

                        VaultLog.Info(string.Format("归档完成：{0}，{1}", game.Name, result.Describe()));
                    }
                    catch (Exception ex)
                    {
                        VaultLog.Error("归档失败：" + game.Name, ex);
                        PlayniteApi.Dialogs.ShowErrorMessage("归档「" + game.Name + "」失败：" + ex.Message, "Playnite Vault");
                        return;
                    }
                }
                }
            },
            new GlobalProgressOptions("归档到 NAS")
            {
                IsIndeterminate = false,
                Cancelable = true
            });
        }

        /// <summary>
        /// 增量修复：只补齐本地缺失或大小不符的文件，已一致的文件直接跳过。
        /// </summary>
        private void RepairGames(List<Game> games)
        {
            if (!service.Settings.IsConfigured)
            {
                PlayniteApi.Dialogs.ShowMessage("还没有配置 WebDAV 地址，请先到插件设置里填写。", "Playnite Vault");
                return;
            }

            var targets = new List<Game>();
            foreach (var game in games)
            {
                if (game.PluginId == Id)
                {
                    targets.Add(game);
                }
            }

            if (targets.Count == 0)
            {
                PlayniteApi.Dialogs.ShowMessage("只能修复由本插件导入的应用。", "Playnite Vault");
                return;
            }

            PlayniteApi.Dialogs.ActivateGlobalProgress(progress =>
            {
                var options = service.BuildSyncOptions();

                using (BeginTransfer("修复"))
                {
                foreach (var game in targets)
                {
                    if (progress.CancelToken.IsCancellationRequested)
                    {
                        break;
                    }

                    try
                    {
                        // 修复目标就是库条目当前的安装目录（它已经按元数据 InstallDirName 算好了）；
                        // 只有拿不到才回退到「按 Id 从索引里找」，那会多一次网络请求。
                        var dir = string.IsNullOrWhiteSpace(game.InstallDirectory)
                            ? service.GetInstallDir(game.GameId)
                            : game.InstallDirectory;
                        var sink = new ProgressSink(progress, "正在修复 " + game.Name);

                        var result = service.InstallApp(game.GameId, dir, options, sink.Apply,
                            progress.CancelToken);

                        VaultLog.Info(string.Format("修复完成：{0}，{1}", game.Name, result.Describe()));
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        VaultLog.Error("修复失败：" + game.Name, ex);
                        PlayniteApi.Dialogs.ShowErrorMessage("修复「" + game.Name + "」失败：" + ex.Message, "Playnite Vault");
                        return;
                    }
                }
                }
            },
            new GlobalProgressOptions("修复安装")
            {
                // 修复同样会先读到清单和总量，用确定态即可；indeterminate 中途
                // 切成 determinate 会让进度条明显跳一下
                IsIndeterminate = false,
                Cancelable = true
            });
        }

        /// <summary>从 PlayAction 推断可执行文件相对路径；推断不出就取根目录第一个 exe。</summary>
        private static string GuessLaunchExe(Game game)
        {
            try
            {
                var playAction = game.GameActions == null
                    ? null
                    : game.GameActions.FirstOrDefault(a => a.IsPlayAction);

                if (playAction != null && !string.IsNullOrWhiteSpace(playAction.Path)
                    && !string.IsNullOrWhiteSpace(game.InstallDirectory))
                {
                    var full = Path.GetFullPath(playAction.Path);
                    var root = Path.GetFullPath(game.InstallDirectory);
                    if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    {
                        return VaultService.MakeRelative(root, full).Replace('\\', '/');
                    }
                }

                if (!string.IsNullOrWhiteSpace(game.InstallDirectory))
                {
                    // 走名字匹配而不是「路径最短」：Unity 游戏里的 UnityCrashHandler64.exe
                    // 文件名往往比游戏本体还短，旧的启发式会选错。
                    return VaultService.GuessLaunchExe(game.InstallDirectory, game.Name);
                }
            }
            catch (Exception ex)
            {
                VaultLog.Error("推断启动程序失败：" + game.Name, ex);
            }

            return "app.exe";
        }

        // ---------- 自动刷新 ----------

        private System.Threading.Timer startupRefreshTimer;

        private void StartAutoRefresh()
        {
            autoRefresh.Rearm();

            if (!service.Settings.AutoRefreshEnabled || !service.Settings.AutoRefreshOnStartup)
            {
                return;
            }

            // 延迟 25 秒：启动瞬间 Playnite 自己还在导入库/读缓存，挤在一起只会互相拖慢
            startupRefreshTimer = new System.Threading.Timer(_ =>
            {
                var timer = startupRefreshTimer;
                startupRefreshTimer = null;
                if (timer != null)
                {
                    try { timer.Dispose(); } catch { }
                }

                VaultLog.Info("自动刷新：启动后的首次检查");
                autoRefresh.TriggerNow(false);
            }, null, 25000, System.Threading.Timeout.Infinite);
        }

        /// <summary>自动刷新跑完后的通知。只在「真的改了库」或「出错了」时提示，无变化时闭嘴。</summary>
        private void OnAutoRefreshCompleted(AutoRefreshOutcome outcome, bool automatic)
        {
            if (outcome == null || !automatic)
            {
                return;
            }

            try
            {
                if (outcome.WroteLibrary)
                {
                    Notify("vault-lib-refresh",
                        string.Format("Vault：远端库有更新，已同步（新增 {0}、更新 {1}、移除 {2}）",
                            outcome.Added, outcome.Updated, outcome.Removed),
                        NotificationType.Info);
                }
                else if (outcome.Error != null)
                {
                    Notify("vault-lib-refresh",
                        "Vault：自动刷新远端库失败 —— " + outcome.Error, NotificationType.Error);
                }
                // 索引无变化 / 游戏运行中 / NAS 不可达 → 不打扰用户
            }
            catch (Exception ex)
            {
                VaultLog.Warn("发送通知失败：" + ex.Message);
            }
        }

        /// <summary>设置页上的「立即刷新远端库」走这里。</summary>
        public void TriggerAutoRefreshNow()
        {
            autoRefresh.TriggerNow(false);
        }

        // ---------- 插件自动更新 ----------

        private System.Threading.Timer updateCheckTimer;

        private void ScheduleUpdateCheck()
        {
            if (!service.Settings.AutoUpdateEnabled)
            {
                VaultLog.Info("自动更新：未启用");
                return;
            }

            // 延迟 12 秒再联网：给 Playnite 的启动让路，也避免界面还没出来就弹窗
            updateCheckTimer = new System.Threading.Timer(_ =>
            {
                var timer = updateCheckTimer;
                updateCheckTimer = null;
                if (timer != null)
                {
                    try { timer.Dispose(); } catch { }
                }

                RunUpdateCheck(false);
            }, null, 12000, System.Threading.Timeout.Infinite);
        }

        /// <summary>
        /// 检查并（按设置）自动更新到最新版。
        /// <paramref name="interactive"/> = true 时所有结果都用弹窗回答（用户手动点的）。
        /// </summary>
        public void RunUpdateCheck(bool interactive)
        {
            if (System.Threading.Interlocked.CompareExchange(ref updateCheckBusy, 1, 0) != 0)
            {
                if (interactive)
                {
                    PlayniteApi.Dialogs.ShowMessage("更新检查正在进行中，请稍候。", "Vault 更新");
                }
                return;
            }

            try
            {
                if (autoRefresh.IsGameRunning)
                {
                    VaultLog.Info("正在运行游戏，本次更新检查跳过");
                    if (interactive)
                    {
                        PlayniteApi.Dialogs.ShowMessage(
                            "正在运行游戏，暂不检查更新。\n游戏结束后下次启动会自动再检查。", "Vault 更新");
                    }
                    return;
                }

                var check = updater.Check();

                service.State.LastUpdateCheckUtc = DateTime.UtcNow;
                if (!string.IsNullOrEmpty(check.LatestVersion))
                {
                    service.State.LastSeenVersion = check.LatestVersion;
                }
                if (check.Candidates.Count > 0)
                {
                    service.State.LastMirror = check.Candidates[0].Mirror;
                    service.State.LastMirrorLatencyMs = check.Candidates[0].LatencyMs;
                }
                service.SaveState();

                if (!check.HasUpdate)
                {
                    ReportNoUpdate(check, interactive);
                    return;
                }

                var release = check.Best;

                // 先问再做（只在开了「更新前先询问」时）
                if (service.Settings.AutoUpdatePrompt && !ConfirmUpdate(release))
                {
                    service.Settings.SkippedVersion = release.Version;
                    service.SaveSettings(service.Settings);
                    VaultLog.Info("用户选择跳过版本 " + release.Version);
                    PlayniteApi.Dialogs.ShowMessage(
                        "已跳过 " + release.Version + "。\n\n"
                        + "之后想装的话，到 设置 → 扩展 → Playnite Vault 点「清除『跳过版本』」即可。",
                        "Vault 更新");
                    return;
                }

                var staged = DownloadUpdate(check);
                if (staged == null)
                {
                    return;   // 失败或用户取消，原因已经报过了
                }

                ApplyAndRestart(staged, interactive);
            }
            catch (Exception ex)
            {
                VaultLog.Error("更新检查失败", ex);
                if (interactive)
                {
                    PlayniteApi.Dialogs.ShowErrorMessage("检查更新失败：" + ex.Message, "Vault 更新");
                }
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref updateCheckBusy, 0);
            }
        }

        private void ReportNoUpdate(UpdateCheckResult check, bool interactive)
        {
            if (check.SkippedByUser)
            {
                VaultLog.Info("更新检查：" + check.Error);
                if (interactive)
                {
                    PlayniteApi.Dialogs.ShowMessage(
                        "远端最新版是 " + check.LatestVersion + "，已被你设为「跳过」。\n\n"
                        + "想安装的话，到 设置 → 扩展 → Playnite Vault 点「清除『跳过版本』」。",
                        "Vault 更新");
                }
                return;
            }

            if (!string.IsNullOrEmpty(check.Error))
            {
                VaultLog.Warn("更新检查未拿到结果：" + check.Error);
                if (interactive)
                {
                    PlayniteApi.Dialogs.ShowErrorMessage(
                        "没能连上更新源。\n\n"
                        + string.Join("\n", check.Attempts.ToArray())
                        + "\n\n系统代理：" + HttpFetch.DescribeSystemProxy()
                        + "\n\n提示：到 设置 → 扩展 → Playnite Vault 把「下载镜像」显式设成 GitHub 或 Gitee 再试。",
                        "Vault 更新");
                }
                return;
            }

            VaultLog.Info("更新检查：已是最新版本 " + check.CurrentVersion);
            if (interactive)
            {
                PlayniteApi.Dialogs.ShowMessage(
                    "已是最新版本 " + check.CurrentVersion + "。", "Vault 更新");
            }
        }

        /// <summary>「更新前先询问」的对话框。返回 true = 现在更新。</summary>
        private bool ConfirmUpdate(ReleaseInfo release)
        {
            var notes = release.Notes ?? "(这个 release 没有写说明)";
            if (notes.Length > 900)
            {
                notes = notes.Substring(0, 900) + "…";
            }

            var answer = PlayniteApi.Dialogs.ShowMessage(
                string.Format(
                    "发现新版本 {0}（当前 {1}）。\n\n"
                    + "———— 更新说明 ————\n{2}\n\n"
                    + "点「是」= 现在下载，下载完成后自动重启 Playnite 应用更新\n"
                    + "点「否」= 跳过这个版本（设置里可以清除）",
                    release.Version, VaultUpdater.CurrentVersion(), notes),
                "Vault 更新",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);

            return answer == System.Windows.MessageBoxResult.Yes;
        }

        /// <summary>带进度窗下载 + 校验。返回 null 表示没成功（原因已提示）。</summary>
        private StagedUpdate DownloadUpdate(UpdateCheckResult check)
        {
            StagedUpdate staged = null;
            string failure = null;
            var cancelled = false;

            PlayniteApi.Dialogs.ActivateGlobalProgress(progress =>
            {
                progress.IsIndeterminate = true;
                progress.Text = "正在连接更新源...";

                try
                {
                    staged = updater.Download(check,
                        (text, done, total) =>
                        {
                            progress.Text = text;
                            if (total > 0)
                            {
                                progress.IsIndeterminate = false;
                                progress.ProgressMaxValue = total;
                                progress.CurrentProgressValue = done;
                            }
                        },
                        () => progress.CancelToken.IsCancellationRequested);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                }
                catch (Exception ex)
                {
                    VaultLog.Error("下载更新失败", ex);
                    failure = ex.Message;
                }
            },
            new GlobalProgressOptions("正在下载 Vault 插件更新 " + check.LatestVersion)
            {
                IsIndeterminate = true,
                Cancelable = true
            });

            if (cancelled)
            {
                VaultLog.Info("用户取消了更新下载");
                return null;
            }

            if (failure != null)
            {
                PlayniteApi.Dialogs.ShowErrorMessage(
                    "下载更新失败。\n\n" + failure
                    + "\n\n可以到 设置 → 扩展 → Playnite Vault 换一个「下载镜像」再试。",
                    "Vault 更新");
                return null;
            }

            return staged;
        }

        /// <summary>
        /// 写替换脚本 → 拉起它 → 请 Playnite 干净退出。
        ///
        /// 顺序很关键：**先起脚本、再退出**。脚本从第一毫秒就在等 Playnite 的进程消失，
        /// 进程一走它就复制新文件，复制完自己把 Playnite 拉起来。
        /// 不让 Playnite 自己「重启」是因为那样新实例可能抢先启动、加载到旧 DLL。
        /// </summary>
        private void ApplyAndRestart(StagedUpdate staged, bool interactive)
        {
            var pluginDir = VaultUpdater.PluginDirectory();
            if (string.IsNullOrEmpty(pluginDir) || !Directory.Exists(pluginDir))
            {
                PlayniteApi.Dialogs.ShowErrorMessage(
                    "找不到插件安装目录，无法自动替换。\n\n"
                    + "请手动把下面的文件覆盖到插件目录：\n" + staged.StagingDir,
                    "Vault 更新");
                return;
            }

            string exePath;
            string processName;
            if (!VaultUpdater.TryDescribeCurrentProcess(out exePath, out processName))
            {
                PlayniteApi.Dialogs.ShowErrorMessage(
                    "拿不到 Playnite 的进程信息，无法自动重启。\n\n"
                    + "更新已经下载好了（" + staged.Version + "），"
                    + "下次 Playnite 启动时会在 12 秒后重新尝试。",
                    "Vault 更新");
                return;
            }

            string scriptPath;
            try
            {
                scriptPath = updater.WriteApplyScript(staged, pluginDir, processName, exePath,
                    PreserveLaunchArguments());
            }
            catch (Exception ex)
            {
                VaultLog.Error("写更新脚本失败", ex);
                PlayniteApi.Dialogs.ShowErrorMessage(
                    "写更新脚本失败：" + ex.Message + "\n\n暂存目录：" + staged.StagingDir, "Vault 更新");
                return;
            }

            string scriptError;
            if (!VaultUpdater.RunApplyScript(scriptPath, out scriptError))
            {
                PlayniteApi.Dialogs.ShowErrorMessage(
                    "更新脚本没能启动：" + scriptError
                    + "\n\n可以手动双击运行它完成更新：\n" + scriptPath,
                    "Vault 更新");
                return;
            }

            if (interactive)
            {
                PlayniteApi.Dialogs.ShowMessage(
                    string.Format(
                        "更新 {0} 已下载校验完毕。\n\n"
                        + "现在会关闭 Playnite 并在后台完成替换，随后自动重新打开。\n"
                        + "整个过程通常几秒钟。\n\n"
                        + "如果没自动重启，请从托盘图标右键退出 Playnite —— 退出后同样会完成更新。",
                        staged.Version),
                    "Vault 更新");
            }

            Notify("vault-update", "Vault 已更新到 " + staged.Version + "，正在重启 Playnite…",
                NotificationType.Info);

            // 给通知一点时间落到界面上
            System.Threading.Thread.Sleep(800);

            string quitError;
            if (!VaultUpdater.TryQuitPlaynite(out quitError))
            {
                VaultLog.Warn("自动退出失败：" + quitError);
                PlayniteApi.Dialogs.ShowMessage(
                    "更新已经下载好了，但没能自动关闭 Playnite（" + quitError + "）。\n\n"
                    + "请手动退出 Playnite：更新会在退出后自动完成，并把 Playnite 重新打开。\n\n"
                    + "注意要从托盘图标右键选择「退出」（设置里开了关闭到托盘时，"
                    + "点窗口的 × 只会最小化）。",
                    "Vault 更新");
            }
        }

        /// <summary>把当前进程的命令行参数原样带回去，避免重启后丢了用户自己的启动参数。</summary>
        private static string PreserveLaunchArguments()
        {
            try
            {
                var args = Environment.GetCommandLineArgs();
                if (args == null || args.Length <= 1)
                {
                    return string.Empty;
                }

                return string.Join(" ", args.Skip(1).Select(a =>
                    a.IndexOf(' ') >= 0 ? "\"" + a + "\"" : a).ToArray());
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>弹通知。通知只是锦上添花，失败绝不影响主流程。</summary>
        private void Notify(string id, string text, NotificationType type)
        {
            try
            {
                PlayniteApi.Notifications.Add(id, text, type);
            }
            catch (Exception ex)
            {
                VaultLog.Warn("发送通知失败：" + ex.Message);
            }
        }

        // ---------- 主菜单 ----------

        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            return new List<MainMenuItem>
            {
                new MainMenuItem
                {
                    MenuSection = "@Vault",
                    Description = "检查插件更新",
                    Action = a => RunUpdateCheck(true)
                },
                new MainMenuItem
                {
                    MenuSection = "@Vault",
                    Description = "从 NAS 刷新库条目",
                    Action = a => RefreshLibraryEntries(true)
                },
                new MainMenuItem
                {
                    MenuSection = "@Vault",
                    Description = "打开插件日志",
                    Action = a => OpenPath(VaultLog.LogPath)
                }
            };
        }

        // ---------- 设置页用的状态文本 ----------

        /// <summary>设置页「插件自动更新」那一段的状态行。</summary>
        public string DescribeUpdateStatus()
        {
            var state = service.State;
            var text = new StringBuilder();

            text.AppendLine("当前版本：" + VaultUpdater.CurrentVersion());
            text.Append("上次检查：");
            text.Append(state.LastUpdateCheckUtc.HasValue
                ? state.LastUpdateCheckUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                : "还没检查过");
            if (!string.IsNullOrEmpty(state.LastSeenVersion))
            {
                text.Append("　远端最新：" + state.LastSeenVersion);
            }
            text.AppendLine();

            if (!string.IsNullOrEmpty(state.LastMirror))
            {
                text.AppendLine("上次命中的源：" + state.LastMirror
                                + "（探测 " + state.LastMirrorLatencyMs + " ms）");
            }

            text.Append("系统代理：" + HttpFetch.DescribeSystemProxy());

            var pending = updater.PendingStaged();
            if (pending != null)
            {
                text.AppendLine();
                text.Append("⚠ 已下载但尚未应用：v" + pending.Version
                            + "（重启 Playnite 会自动装上）");
            }

            return text.ToString();
        }

        /// <summary>设置页「自动刷新远端库」那一段的状态行。</summary>
        public string DescribeAutoRefreshStatus()
        {
            var state = service.State;
            var settings = service.Settings;
            var text = new StringBuilder();

            text.AppendLine(settings.AutoRefreshEnabled
                ? "状态：已启用，每 " + settings.AutoRefreshMinutes + " 分钟检查一次"
                : "状态：未启用");
            text.AppendLine("上次检查索引：" + FormatLocal(state.LastIndexCheckUtc));
            text.AppendLine("上次真正写库：" + FormatLocal(state.LastLibraryWriteUtc));

            var fingerprint = state.AppliedIndexHash;
            text.AppendLine("已应用索引指纹：" + (string.IsNullOrEmpty(fingerprint)
                ? "(还没有)"
                : fingerprint.Substring(0, Math.Min(16, fingerprint.Length)) + "…"));

            if (state.ConsecutiveRefreshFailures > 0)
            {
                text.AppendLine("连续失败 " + state.ConsecutiveRefreshFailures
                                + " 次，下一次间隔已放宽到 "
                                + (settings.AutoRefreshMinutes * (1 << Math.Min(state.ConsecutiveRefreshFailures, 3)))
                                + " 分钟");
            }

            if (autoRefresh.IsGameRunning)
            {
                text.AppendLine("当前正在运行游戏，已暂停");
            }

            return text.ToString().TrimEnd();
        }

        private static string FormatLocal(DateTime? utc)
        {
            return utc.HasValue
                ? utc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                : "还没发生过";
        }

        /// <summary>设置页的小按钮用：立刻把 VM 里的编辑结果落盘。</summary>
        public void SaveSettingsImmediately(VaultSettingsViewModel viewModel)
        {
            if (viewModel == null)
            {
                return;
            }

            try
            {
                viewModel.EndEdit();
            }
            catch (Exception ex)
            {
                VaultLog.Error("立即保存设置失败", ex);
            }
        }

        // ---------- 设置 ----------

        public override ISettings GetSettings(bool firstRunSettings)
        {
            return settingsVm;
        }

        public override System.Windows.Controls.UserControl GetSettingsView(bool firstRunView)
        {
            return new VaultSettingsView(settingsVm, service);
        }
    }
}
