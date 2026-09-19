using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using VaultDemo.Controllers;
using VaultDemo.Models;
using VaultDemo.Net;
using VaultDemo.Services;
using VaultDemo.UI;

namespace VaultDemo
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

        public override Guid Id { get; } = PluginGuid;

        public override string Name
        {
            get { return "Vault Demo (NAS Library)"; }
        }

        public VaultService Service
        {
            get { return service; }
        }

        public VaultPlugin(IPlayniteAPI api) : base(api)
        {
            Instance = this;

            Properties = new LibraryPluginProperties
            {
                HasSettings = true
            };

            service = new VaultService(api, GetPluginUserDataPath());
            VaultLog.Init(service.DataPath);
            settingsVm = new VaultSettingsViewModel(service);

            VaultLog.Info("VaultPlugin 已构造，数据目录=" + service.DataPath);
        }

        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            VaultLog.Info("OnApplicationStarted，WebDAV=" + service.Settings.WebDavUrl);
        }

        public override void OnApplicationStopped(OnApplicationStoppedEventArgs args)
        {
            VaultLog.Info("OnApplicationStopped");
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
                Action = a => RefreshLibraryEntries()
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
                PlayniteApi.Dialogs.ShowMessage("还没有配置 WebDAV 地址，请先到插件设置里填写。", "Vault Demo");
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
                PlayniteApi.Dialogs.ShowErrorMessage("连接仓库失败：" + ex.Message, "Vault Demo");
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
                PlayniteApi.Dialogs.ShowErrorMessage("打开失败：" + ex.Message, "Vault Demo");
            }
        }

        /// <summary>
        /// 立即把远端索引同步成一等公民的库条目，不依赖 Playnite 的「更新游戏库」。
        ///
        /// 背景：Playnite 的库更新由设置项 CheckForLibraryUpdates 控制，默认是「从不」，
        /// 于是 GetGames 根本不会被调用，新上传的应用永远不出现在库里
        /// （这正是「只有几条无法安装的旧 demo、没有冰与火之舞」的原因）。
        /// 这里直接走 IGameDatabase.ImportGame，一次性把差异补齐：
        /// 远端已删除的条目移除、缺失的新增、已有的更新名称与安装状态。
        /// </summary>
        private void RefreshLibraryEntries()
        {
            if (!service.Settings.IsConfigured)
            {
                PlayniteApi.Dialogs.ShowMessage("还没有配置 WebDAV 地址。\n请到 设置 → 扩展 → Vault Demo 里填写。", "Vault Demo");
                return;
            }

            try
            {
                string source;
                string error;
                var index = service.GetIndex(out source, out error);

                if (index == null || index.Apps == null || index.Apps.Count == 0)
                {
                    PlayniteApi.Dialogs.ShowErrorMessage(
                        "没有从仓库拿到任何条目。\n\n" + (error ?? "远端索引为空"), "Vault Demo");
                    return;
                }

                var remoteIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var app in index.Apps)
                {
                    remoteIds.Add(app.Id);
                }

                var local = service.GetLocalIndex();
                var added = 0;
                var updated = 0;
                var removed = 0;

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
                            removed++;
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
                            added++;
                            VaultLog.Info("刷新：新增条目 " + app.Name + "（" + app.Id + "）");
                        }
                        else
                        {
                            UpdateEntry(match, app, local);
                            PlayniteApi.Database.Games.Update(match);
                            updated++;
                        }
                    }
                }
                finally
                {
                    PlayniteApi.Database.EndBufferUpdate();
                }

                VaultLog.Info(string.Format("刷新完成：来源={0}，新增 {1}，更新 {2}，移除 {3}",
                    source, added, updated, removed));

                PlayniteApi.Dialogs.ShowMessage(
                    string.Format(
                        "库条目已刷新。\n\n来源：{0}\n远端条目：{1}\n\n新增：{2}\n更新：{3}\n移除：{4}\n\n" +
                        "如果左侧列表里还看不到，请检查过滤器面板是否只勾选了某个库来源（把「库」过滤器全部取消勾选即可）。",
                        source, index.Apps.Count, added, updated, removed),
                    "Vault Demo");
            }
            catch (Exception ex)
            {
                VaultLog.Error("刷新库条目失败", ex);
                PlayniteApi.Dialogs.ShowErrorMessage("刷新失败：" + ex.Message, "Vault Demo");
            }
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
                PlayniteApi.Dialogs.ShowMessage("还没有配置 WebDAV 地址。\n请到 设置 → 扩展 → Vault Demo 里填写。", "Vault Demo");
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
                    "Vault Demo");
            }
            catch (Exception ex)
            {
                VaultLog.Error("测试连接失败", ex);
                PlayniteApi.Dialogs.ShowErrorMessage(
                    "连接失败：\n\n" + WebDavDiagnostics.Describe(ex,
                        service.Settings.WebDavUrl, service.Settings.Username),
                    "Vault Demo");
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
                PlayniteApi.Dialogs.ShowMessage("还没有配置 WebDAV 地址，请先到插件设置里填写。", "Vault Demo");
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
                PlayniteApi.Dialogs.ShowMessage("选中的应用都没有可用的安装目录，无法归档。", "Vault Demo");
                return;
            }

            var summary = string.Join("\n", targets.Select(g => "· " + g.Name + "  →  " + g.InstallDirectory));
            var answer = PlayniteApi.Dialogs.ShowMessage(
                "将把以下应用归档到 NAS：\n\n" + summary + "\n\n源目录里的文件会被上传，本地文件不会被删除。是否继续？",
                "Vault Demo",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);

            if (answer != System.Windows.MessageBoxResult.Yes)
            {
                return;
            }

            PlayniteApi.Dialogs.ActivateGlobalProgress(progress =>
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
                        PlayniteApi.Dialogs.ShowErrorMessage("归档「" + game.Name + "」失败：" + ex.Message, "Vault Demo");
                        return;
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
                PlayniteApi.Dialogs.ShowMessage("还没有配置 WebDAV 地址，请先到插件设置里填写。", "Vault Demo");
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
                PlayniteApi.Dialogs.ShowMessage("只能修复由本插件导入的应用。", "Vault Demo");
                return;
            }

            PlayniteApi.Dialogs.ActivateGlobalProgress(progress =>
            {
                var options = service.BuildSyncOptions();

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
                        PlayniteApi.Dialogs.ShowErrorMessage("修复「" + game.Name + "」失败：" + ex.Message, "Vault Demo");
                        return;
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
