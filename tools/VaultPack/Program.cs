using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using PlayniteVault.Models;
using PlayniteVault.Net;
using PlayniteVault.Services;
using PlayniteVault.Sync;

namespace VaultPack
{
    /// <summary>
    /// Vault 仓库开发期命令行工具。
    ///
    ///   ls       查看远端索引
    ///   pack     把本地目录打包上传到仓库（走插件同一条生产代码路径）
    ///   install  从仓库下载到本地并逐文件校验（等价于 Playnite 的安装流程）
    ///   dump     导出 Playnite 库元数据（LiteDB games.db）
    /// </summary>
    internal static class Program
    {
        /// <summary>
        /// 插件数据目录名。**v1.6.0 起插件改名为 Playnite-Vault，数据目录也跟着换了**，
        /// 这里得跟着改，否则读的是迁移前留在旧目录里的那份过期设置。
        /// 旧目录仍然兜底，是为了让「还没启动过新版 Playnite」的机器照样能用。
        /// </summary>
        private const string PluginDataFolder = "Playnite-Vault";

        private const string LegacyPluginDataFolder = "5e76bf50-cb8a-4a87-ad24-1912c746c6f0";

        /// <summary>
        /// Playnite 安装目录。优先取环境变量 PLAYNITE_DIR，其次按常见安装位置找。
        /// 便携版就是把整个文件夹解压到任意位置（ExtensionsData / library 都在它下面），
        /// 这种布局用环境变量指过去最省事：
        ///     set PLAYNITE_DIR=D:\Somewhere\Playnite
        /// </summary>
        private static string PlayniteDir()
        {
            var fromEnv = Environment.GetEnvironmentVariable("PLAYNITE_DIR");
            if (!string.IsNullOrWhiteSpace(fromEnv))
            {
                return fromEnv.TrimEnd('\\', '/');
            }

            var candidates = new List<string>();
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(local))
            {
                candidates.Add(Path.Combine(local, "Playnite"));
            }

            foreach (var pf in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     })
            {
                if (!string.IsNullOrWhiteSpace(pf))
                {
                    candidates.Add(Path.Combine(pf, "Playnite"));
                }
            }

            foreach (var candidate in candidates)
            {
                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }

            // 都没找到：返回第一个候选，上层报「找不到设置文件」时至少带一个具体路径
            return candidates.Count > 0 ? candidates[0] : ".";
        }

        /// <summary>默认读插件数据目录里的设置，拿到 WebDAV 地址与账号。</summary>
        private static string DefaultSettingsPath()
        {
            var extensionsData = Path.Combine(PlayniteDir(), "ExtensionsData");

            var current = Path.Combine(extensionsData, PluginDataFolder, "settings.json");
            if (File.Exists(current))
            {
                return current;
            }

            // 迁移前的旧目录（改名之前那串 GUID）
            var legacy = Path.Combine(extensionsData, LegacyPluginDataFolder, "settings.json");
            if (File.Exists(legacy))
            {
                return legacy;
            }

            return current;
        }

        /// <summary>Playnite 的主题目录（Desktop / Fullscreen 两个子目录躺在它下面）。</summary>
        private static string DefaultThemesDir()
        {
            return Path.Combine(PlayniteDir(), "Themes");
        }

        /// <summary>Playnite 库目录（games.db 及各种字典 .db 都在这里）。</summary>
        private static string DefaultLibraryDir()
        {
            return Path.Combine(PlayniteDir(), "library");
        }

        /// <summary>Playnite 库文件。</summary>
        private static string DefaultPlayniteDb()
        {
            return Path.Combine(DefaultLibraryDir(), "games.db");
        }

        // 进度诊断：--progress-log <csv> 会把每一次上报的原值记下来，
        // 用来客观验证「进度条是否单调、节拍是否均匀」，而不是凭肉眼感觉。
        private static StreamWriter ProgressLog;
        private static readonly Stopwatch ProgressWatch = Stopwatch.StartNew();

        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            if (args.Length == 0)
            {
                Usage();
                return 1;
            }

            var command = args[0].Trim().ToLowerInvariant();
            var options = OptionSet.Parse(args.Skip(1));
            var started = DateTime.Now;

            var progressLogPath = options.Get("progress-log");
            if (!string.IsNullOrWhiteSpace(progressLogPath))
            {
                ProgressWatch.Restart();
                ProgressLog = new StreamWriter(progressLogPath, false, new UTF8Encoding(false));
                ProgressLog.AutoFlush = true;
                ProgressLog.WriteLine(
                    "ms,phase,bytesDone,bytesTotal,partsDone,partsInFlight,subName,subDone,subTotal,speed");
            }

            try
            {
                switch (command)
                {
                    case "ls": return CmdList(options);
                    case "tree": return CmdTree(options);
                    case "bench": return CmdBench(options);
                    case "backup": return CmdBackup(options);
                    case "meta": return CmdMeta(options);
                    case "pack": return CmdPack(options);
                    case "install": return CmdInstall(options);
                    case "rm": return CmdRemove(options);
                    case "dump": return CmdDump(options);
                    case "themes-sync": return CmdThemesSync(options);
                    case "saves": return CmdSaves(options);
                    default:
                        Console.Error.WriteLine("未知命令：" + command);
                        Usage();
                        return 1;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("[失败] " + ex.GetType().Name + "：" + ex.Message);
                if (options.Has("trace"))
                {
                    Console.Error.WriteLine(ex.ToString());
                }
                return 2;
            }
            finally
            {
                if (ProgressLog != null)
                {
                    ProgressLog.Flush();
                    ProgressLog.Dispose();
                    ProgressLog = null;
                }
                Console.WriteLine("[耗时] " + (DateTime.Now - started).ToString(@"hh\:mm\:ss"));
            }
        }

        private static void Usage()
        {
            Console.WriteLine(@"VaultPack —— Vault 仓库开发期工具

用法:
  VaultPack ls      [--settings <json>] [--data <dir>]
  VaultPack tree    --id <应用Id> | --path <相对路径> [--settings <json>] [--data <dir>]
  VaultPack bench   --id <应用Id> [--url <覆盖地址>] [--threads 1] [--rounds 1] [--limit 0]
                    [--settings <json>] [--data <dir>]
  VaultPack backup  [--library <库目录>] [--out <备份目录>]
  VaultPack meta    --game <名称或GUID> [--library <库目录>] [--out <json>]
  VaultPack pack    --src <目录> --id <应用Id> [--name <名称>] [--exe <启动程序>]
                    [--version <版本>] [--meta <元数据json>] [--chunk-size <MB>]
                    [--compress] [--force] [--conn <并发路数>]
                    [--upload-conn <并发路数>] [--settings <json>] [--data <dir>]
  VaultPack install --id <应用Id> --dir <目标目录> [--force] [--no-verify]
                    [--conn <并发路数>] [--settings <json>] [--data <dir>]
  VaultPack rm      --id <应用Id> [--yes] [--settings <json>] [--data <dir>]
  VaultPack dump    [--db <games.db>] [--filter <关键字>] [--out <文件>]
  VaultPack themes-sync [--dir <Themes目录>] [--mode up|down|both] [--dry-run] [--force]
                    [--state <json>] [--settings <json>] [--data <dir>]
  VaultPack saves   [--action list|upload|download|delete|branches|prune|sniff]
                    --game <游戏Id> [--name <显示名>] [--install <安装目录>]
                    [--path '标题={LocalAppData}\游戏\存档[;标题2=路径2…]']
                    [--branch <分支>] [--snapshot <快照Id>] [--keep <n>]
                    [--dry-run] [--force] [--allow-delete] [--pin] [--comment <说明>]
                    [--settings <json>] [--data <dir>]

说明:
  themes-sync 把 Playnite 的 Themes 目录同步到仓库的 themes/ 下（纯文件镜像，
              结构与本地同构）。--mode up 只上传（默认，备份方向）、down 只下载、
              both 双向。远端只增不减：本地删掉的主题不会连带删掉远端那份。
              建议先跑一次 --dry-run 看清楚要动什么。
  saves       云存档。与插件界面共用同一个引擎（SaveSyncEngine），所以两边行为一致。
              upload 需要 --path + --install；download/delete/prune 需要 --game。
              路径里写 {LocalAppData}、{GameDir} 这类占位符会自动按「自适应」处理。
              delete 与 prune **必须显式加 --allow-delete** 才真发 DELETE。
              sniff 只把候选打出来，不写任何东西（要采用就自己写进 --path）。
              文件型存档在路径前加 file: 前缀（默认按目录处理）。
  meta        从 Playnite 库里抓元数据（开发商/类型/标签/评分/封面…）写成
              可直接喂给 pack --meta 的 JSON；GUID 会自动解析成名称。
              库文件被 Playnite 占用时也能用（走共享读复制）。
  --settings  默认 " + DefaultSettingsPath() + @"
  --data      工具自己的临时数据目录（默认 %TEMP%\vaultpack-data），
              与 Playnite 插件的数据目录隔离，不会污染插件的本地索引。
");
        }

        // ---------- 命令实现 ----------

        /// <summary>
        /// 备份 Playnite 库。Playnite 运行时会对 .db 加锁，普通复制会失败，
        /// 这里统一用「共享读」方式把文件抓下来，所以运行中也能安全备份。
        /// </summary>
        private static int CmdBackup(OptionSet options)
        {
            var libraryDir = options.Get("library", DefaultLibraryDir());
            var playniteDir = Path.GetDirectoryName(libraryDir.TrimEnd('\\', '/'));
            var outDir = options.Get("out",
                Path.Combine(playniteDir,
                    "_vault-backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")));

            if (!Directory.Exists(libraryDir))
            {
                throw new DirectoryNotFoundException("找不到库目录：" + libraryDir);
            }

            Directory.CreateDirectory(outDir);
            Console.WriteLine("[源] " + libraryDir);
            Console.WriteLine("[备份到] " + outDir);
            Console.WriteLine();

            long totalBytes = 0;
            var failed = new List<string>();

            // 1) 库里的所有 .db
            foreach (var file in Directory.GetFiles(libraryDir, "*.db", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(file);
                try
                {
                    var size = CopySharedTo(file, Path.Combine(outDir, name));
                    totalBytes += size;
                    Console.WriteLine("  OK   " + name + "  (" + SyncProgress.FormatSize(size) + ")");
                }
                catch (Exception ex)
                {
                    failed.Add(name + "：" + ex.Message);
                    Console.WriteLine("  失败 " + name + "：" + ex.Message);
                }
            }

            // 2) 库附属文件（封面 / 图标等）
            var filesDir = Path.Combine(libraryDir, "files");
            if (Directory.Exists(filesDir))
            {
                var target = Path.Combine(outDir, "files");
                var count = CopyTreeShared(filesDir, target);
                Console.WriteLine("  OK   files/  （" + count + " 个文件）");
            }

            // 3) Playnite 根的配置
            foreach (var name in new[] { "config.json", "fullscreenConfig.json", "windowPositions.json" })
            {
                var source = Path.Combine(playniteDir, name);
                if (File.Exists(source))
                {
                    try
                    {
                        File.Copy(source, Path.Combine(outDir, name), true);
                        Console.WriteLine("  OK   " + name);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("  失败 " + name + "：" + ex.Message);
                    }
                }
            }

            Console.WriteLine();
            Console.WriteLine("[合计] " + SyncProgress.FormatSize(totalBytes));
            Console.WriteLine("[位置] " + outDir);

            if (failed.Count > 0)
            {
                Console.WriteLine("[有文件未备份] " + string.Join("；", failed.ToArray()));
                return 3;
            }

            Console.WriteLine("[结果] 备份完成");
            return 0;
        }

        private static long CopySharedTo(string sourcePath, string targetPath)
        {
            using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var target = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                source.CopyTo(target, 1024 * 1024);
                return target.Length;
            }
        }

        private static int CopyTreeShared(string sourceDir, string targetDir)
        {
            Directory.CreateDirectory(targetDir);
            var count = 0;

            foreach (var file in Directory.GetFiles(sourceDir))
            {
                try
                {
                    CopySharedTo(file, Path.Combine(targetDir, Path.GetFileName(file)));
                    count++;
                }
                catch
                {
                    // 单个文件失败不影响整体备份
                }
            }

            foreach (var dir in Directory.GetDirectories(sourceDir))
            {
                count += CopyTreeShared(dir, Path.Combine(targetDir, Path.GetFileName(dir)));
            }

            return count;
        }

        // ---------- 元数据导出 ----------

        /// <summary>
        /// 从 Playnite 库导出 AppMetadata JSON（直接喂给 pack --meta）。
        ///
        /// 库里存的是 DeveloperIds / GenreIds 这类 GUID，这里把 companies.db / genres.db
        /// 等字典表读进来做名称解析，并顺手定位封面 / 背景 / 图标的本地文件，
        /// pack 时 VaultService 会把它们上传到 apps/{id}/meta/ 并改写成远端路径。
        /// </summary>
        private static int CmdMeta(OptionSet options)
        {
            var libraryDir = options.Get("library", DefaultLibraryDir());
            var needle = options.Require("game");
            var outPath = options.Get("out");

            var gamesDb = Path.Combine(libraryDir, "games.db");
            if (!File.Exists(gamesDb))
            {
                throw new FileNotFoundException("找不到库文件：" + gamesDb);
            }

            // Playnite 运行时会独占 games.db，走共享读复制
            var work = CopyShared(gamesDb);
            try
            {
                Console.WriteLine("[库目录] " + libraryDir);
                Console.WriteLine("[查找] " + needle);

                var companies = LoadNameMap(libraryDir, "companies.db", "Company");
                var genres = LoadNameMap(libraryDir, "genres.db", "Genre");
                var tags = LoadNameMap(libraryDir, "tags.db", "Tag");
                var categories = LoadNameMap(libraryDir, "categories.db", "Category");
                var features = LoadNameMap(libraryDir, "features.db", "GameFeature");
                var series = LoadNameMap(libraryDir, "series.db", "Series");
                var platforms = LoadNameMap(libraryDir, "platforms.db", "Platform");
                var regions = LoadNameMap(libraryDir, "regions.db", "Region");
                var ages = LoadNameMap(libraryDir, "ageratings.db", "AgeRating");
                var sources = LoadNameMap(libraryDir, "sources.db", "GameSource");

                Console.WriteLine(string.Format(
                    "[字典] 公司 {0} · 类型 {1} · 标签 {2} · 分类 {3} · 特性 {4} · 系列 {5} · 平台 {6} · 区域 {7} · 分级 {8} · 来源 {9}",
                    companies.Count, genres.Count, tags.Count, categories.Count, features.Count,
                    series.Count, platforms.Count, regions.Count, ages.Count, sources.Count));

                Dictionary<string, object> game = null;
                var scanned = 0;

                using (var db = new LiteDB.LiteDatabase(work))
                {
                    foreach (var doc in db.GetCollection("Game").FindAll())
                    {
                        var obj = BsonToObject(doc) as Dictionary<string, object>;
                        if (obj == null)
                        {
                            continue;
                        }
                        scanned++;

                        var name = Str(obj, "Name") ?? string.Empty;
                        var id = Str(obj, "_id") ?? string.Empty;
                        if (name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0
                            || string.Equals(id, needle, StringComparison.OrdinalIgnoreCase))
                        {
                            game = obj;
                            break;
                        }
                    }
                }

                if (game == null)
                {
                    Console.Error.WriteLine("库里找不到匹配「" + needle + "」的游戏（已扫描 " + scanned + " 条）");
                    return 3;
                }

                var meta = new AppMetadata
                {
                    Name = Str(game, "Name"),
                    SortingName = Str(game, "SortingName"),
                    Description = Str(game, "Description"),
                    Version = Str(game, "Version"),
                    ReleaseDate = Str(game, "ReleaseDate"),
                    Source = Lookup(sources, Str(game, "SourceId")),
                    Developers = Names(game, "DeveloperIds", companies),
                    Publishers = Names(game, "PublisherIds", companies),
                    Genres = Names(game, "GenreIds", genres),
                    Categories = Names(game, "CategoryIds", categories),
                    Tags = Names(game, "TagIds", tags),
                    Features = Names(game, "FeatureIds", features),
                    Series = Names(game, "SeriesIds", series),
                    Platforms = Names(game, "PlatformIds", platforms),
                    Regions = Names(game, "RegionIds", regions),
                    AgeRatings = Names(game, "AgeRatingIds", ages),
                    Links = Links(game),
                    CommunityScore = Num(game, "CommunityScore"),
                    CriticScore = Num(game, "CriticScore"),
                    UserScore = Num(game, "UserScore"),
                    InstallSize = Long(game, "InstallSize"),
                    Hidden = Bool(game, "Hidden"),
                    Favorite = Bool(game, "Favorite"),
                    InstallDirName = DirNameOf(Str(game, "InstallDirectory")),
                    Images = new Dictionary<string, string>()
                };

                // 启动参数 / 工作目录：{InstallDir} 这类占位符要清掉，插件侧按安装目录自己拼
                var play = PlayAction(game);
                if (play != null)
                {
                    var args = Str(play, "Arguments");
                    if (!string.IsNullOrWhiteSpace(args))
                    {
                        meta.LaunchArguments = args;
                    }

                    var wd = Str(play, "WorkingDir");
                    var installDir = Str(game, "InstallDirectory") ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(wd)
                        && !string.Equals(wd.Trim(), "{InstallDir}", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(wd.Trim(), installDir, StringComparison.OrdinalIgnoreCase))
                    {
                        meta.LaunchWorkingDir = wd;
                    }
                }

                // 图片：库里存的是相对 library/files/ 的路径
                var filesRoot = Path.Combine(libraryDir, "files");
                AddImage(meta, "cover", filesRoot, Str(game, "CoverImage"));
                AddImage(meta, "background", filesRoot, Str(game, "BackgroundImage"));
                AddImage(meta, "icon", filesRoot, Str(game, "Icon"));

                var json = JsonConvert.SerializeObject(meta, new JsonSerializerSettings
                {
                    Formatting = Formatting.Indented,
                    NullValueHandling = NullValueHandling.Ignore
                });

                Console.WriteLine();
                Console.WriteLine("[命中] " + meta.Name);
                Console.WriteLine("[开发商] " + Join(meta.Developers));
                Console.WriteLine("[发行商] " + Join(meta.Publishers));
                Console.WriteLine("[类型] " + Join(meta.Genres));
                Console.WriteLine("[标签] " + (meta.Tags == null ? "-" : meta.Tags.Count + " 个"));
                Console.WriteLine("[平台] " + Join(meta.Platforms));
                Console.WriteLine("[发行] " + (meta.ReleaseDate ?? "-")
                    + "   [评分] " + (meta.CommunityScore.HasValue ? meta.CommunityScore.Value.ToString() : "-")
                    + "   [体积] " + (meta.InstallSize.HasValue ? SyncProgress.FormatSize(meta.InstallSize.Value) : "-"));
                Console.WriteLine("[图片] " + (meta.Images.Count == 0
                    ? "-"
                    : string.Join(", ", meta.Images.Keys.ToArray())));

                if (outPath != null)
                {
                    File.WriteAllText(outPath, json, new UTF8Encoding(false));
                    Console.WriteLine("[已写出] " + outPath);
                }
                else
                {
                    Console.WriteLine();
                    Console.WriteLine(json);
                }

                return 0;
            }
            finally
            {
                TryDelete(work);
            }
        }

        /// <summary>读取一个字典 .db（如 companies.db）的 Id → Name 映射。</summary>
        private static Dictionary<string, string> LoadNameMap(string libraryDir, string fileName, string collection)
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var path = Path.Combine(libraryDir, fileName);
            if (!File.Exists(path))
            {
                return map;
            }

            var copy = CopyShared(path);
            try
            {
                using (var db = new LiteDB.LiteDatabase(copy))
                {
                    var col = db.GetCollection(collection);
                    foreach (var doc in col.FindAll())
                    {
                        var obj = BsonToObject(doc) as Dictionary<string, object>;
                        if (obj == null)
                        {
                            continue;
                        }
                        var id = Str(obj, "_id");
                        var name = Str(obj, "Name");
                        if (id != null && !string.IsNullOrWhiteSpace(name))
                        {
                            map[id] = name;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("  （字典 " + fileName + " 读取失败：" + ex.Message + "）");
            }
            finally
            {
                TryDelete(copy);
            }

            return map;
        }

        private static string Str(Dictionary<string, object> doc, string key)
        {
            if (doc == null)
            {
                return null;
            }
            object value;
            if (!doc.TryGetValue(key, out value) || value == null)
            {
                return null;
            }
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static int? Num(Dictionary<string, object> doc, string key)
        {
            var text = Str(doc, key);
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }
            int parsed;
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
                ? (int?)parsed
                : null;
        }

        private static long? Long(Dictionary<string, object> doc, string key)
        {
            var text = Str(doc, key);
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }
            long parsed;
            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed)
                ? (long?)parsed
                : null;
        }

        private static bool? Bool(Dictionary<string, object> doc, string key)
        {
            var text = Str(doc, key);
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }
            bool parsed;
            return bool.TryParse(text, out parsed) ? (bool?)parsed : null;
        }

        private static string Lookup(Dictionary<string, string> map, string id)
        {
            string name;
            if (id != null && map.TryGetValue(id, out name) && !string.IsNullOrWhiteSpace(name))
            {
                // 全零 GUID 表示「没有来源」，别把空名字带进去
                return name;
            }
            return null;
        }

        /// <summary>GUID 列表 → 名称列表（解析不出来的直接丢掉，不塞垃圾数据）。</summary>
        private static List<string> Names(Dictionary<string, object> doc, string key,
            Dictionary<string, string> map)
        {
            var result = new List<string>();
            if (doc == null)
            {
                return result;
            }

            object raw;
            if (!doc.TryGetValue(key, out raw))
            {
                return result;
            }

            var list = raw as List<object>;
            if (list == null)
            {
                return result;
            }

            foreach (var item in list)
            {
                var name = Lookup(map, Convert.ToString(item, CultureInfo.InvariantCulture));
                if (name != null && !result.Contains(name))
                {
                    result.Add(name);
                }
            }

            return result;
        }

        private static List<LinkEntry> Links(Dictionary<string, object> doc)
        {
            var result = new List<LinkEntry>();
            if (doc == null)
            {
                return result;
            }

            object raw;
            if (!doc.TryGetValue("Links", out raw))
            {
                return result;
            }

            var list = raw as List<object>;
            if (list == null)
            {
                return result;
            }

            foreach (var item in list)
            {
                var entry = item as Dictionary<string, object>;
                if (entry == null)
                {
                    continue;
                }
                var url = Str(entry, "Url");
                if (!string.IsNullOrWhiteSpace(url))
                {
                    result.Add(new LinkEntry { Name = Str(entry, "Name"), Url = url });
                }
            }

            return result;
        }

        private static Dictionary<string, object> PlayAction(Dictionary<string, object> game)
        {
            if (game == null)
            {
                return null;
            }

            object raw;
            if (!game.TryGetValue("GameActions", out raw))
            {
                return null;
            }

            var list = raw as List<object>;
            if (list == null)
            {
                return null;
            }

            foreach (var item in list)
            {
                var action = item as Dictionary<string, object>;
                if (action == null)
                {
                    continue;
                }
                var flag = Bool(action, "IsPlayAction");
                if (flag.HasValue && flag.Value)
                {
                    return action;
                }
            }

            return null;
        }

        private static void AddImage(AppMetadata meta, string key, string filesRoot, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative))
            {
                return;
            }

            try
            {
                var full = Path.Combine(filesRoot,
                    relative.Replace('\\', Path.DirectorySeparatorChar)
                            .Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(full))
                {
                    meta.Images[key] = full;
                }
            }
            catch
            {
                // 图片缺失不影响元数据本身
            }
        }

        /// <summary>
        /// 安装目录的最后一级文件夹名，例：D:\Games\Brotato → "Brotato"。
        /// 写进元数据，供解包端当本地子目录名用（取不到就返回 null）。
        /// </summary>
        private static string DirNameOf(string installDir)
        {
            if (string.IsNullOrWhiteSpace(installDir))
            {
                return null;
            }

            var trimmed = installDir.TrimEnd('\\', '/');
            if (trimmed.Length == 0)
            {
                return null;
            }

            var name = Path.GetFileName(trimmed);
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }

        // ---------- 纯传输测速 ----------

        /// <summary>
        /// 只下载分片、不解包，把「网络 + TLS + 客户端下载代码」这一段单独量出来。
        /// 排查吞吐瓶颈时用它对比 http / https、魔改前后，是最干净的基线。
        /// </summary>
        private static int CmdBench(OptionSet options)
        {
            var appId = options.Require("id");
            var rounds = Math.Max(1, int.Parse(options.Get("rounds", "1"), CultureInfo.InvariantCulture));
            var limit = int.Parse(options.Get("limit", "0"), CultureInfo.InvariantCulture);
            var threads = Math.Max(1, int.Parse(options.Get("threads", "1"), CultureInfo.InvariantCulture));

            // HttpWebRequest 默认每个 endpoint 只允许 2 条并发连接，
            // 不放开的话多线程下载会被 ServicePoint 直接串行化。
            if (threads > 2)
            {
                System.Net.ServicePointManager.DefaultConnectionLimit = Math.Max(8, threads);
            }

            var service = CreateService(options);
            var manifest = service.FetchManifest(appId);
            var client = service.CreateClient();
            var opt = service.BuildSyncOptions(true); // 强制重传，避免「已存在跳过」影响测量

            // 布局无关：只关心「要下哪几个远端文件、各多大」。
            // v3 是区块（内容寻址），v2 是分片，都能拿来测纯传输带宽。
            var blobs = new List<BenchBlob>();
            if (manifest.Layout == VaultLayout.Chunks)
            {
                foreach (var chunk in manifest.Chunks)
                {
                    blobs.Add(new BenchBlob { Path = chunk.Path, StoredBytes = chunk.StoredBytes });
                }
            }
            else
            {
                foreach (var part in manifest.Parts)
                {
                    blobs.Add(new BenchBlob { Path = part.Path, StoredBytes = part.StoredBytes });
                }
            }

            if (limit > 0)
            {
                blobs = blobs.Take(limit).ToList();
            }

            if (blobs.Count == 0)
            {
                Console.Error.WriteLine("该应用没有可测的远端块（可能还是 v1 逐文件布局）。");
                return 3;
            }

            var tempDir = Path.Combine(Path.GetTempPath(), "vaultpack-bench");
            Directory.CreateDirectory(tempDir);

            Console.WriteLine("[仓库]     " + service.Settings.WebDavUrl);
            Console.WriteLine("[应用]     " + appId);
            Console.WriteLine("[布局]     " + manifest.Layout);
            Console.WriteLine("[远端块]   " + blobs.Count + " 个，共 " +
                SyncProgress.FormatSize(blobs.Sum(b => b.StoredBytes)));
            Console.WriteLine("[临时目录] " + tempDir);
            Console.WriteLine();

            double best = 0;
            var totalBytes = 0L;
            var totalTime = 0.0;
            var sync = new object();

            try
            {
                for (var round = 1; round <= rounds; round++)
                {
                    Console.WriteLine("--- 第 " + round + " 轮 ---");
                    double roundBytes = 0;
                    double roundTime = 0;

                    var roundWatch = Stopwatch.StartNew();

                    if (threads <= 1)
                    {
                        foreach (var blob in blobs)
                        {
                            var s = TransferOne(client, appId, blob, tempDir, opt);
                            if (s > best) best = s;
                        }
                    }
                    else
                    {
                        // HttpWebRequest 的解密/读取是同步阻塞的，多线程才能把
                        // .NET Framework SslStream 的单连接上限摊到多个核上。
                        var queue = new System.Collections.Concurrent.ConcurrentQueue<BenchBlob>();
                        foreach (var b in blobs)
                        {
                            queue.Enqueue(b);
                        }

                        var workers = new List<Thread>();
                        for (var i = 0; i < threads; i++)
                        {
                            var worker = new Thread(() =>
                            {
                                BenchBlob item;
                                while (queue.TryDequeue(out item))
                                {
                                    try
                                    {
                                        var s = TransferOne(client, appId, item, tempDir, opt);
                                        lock (sync)
                                        {
                                            if (s > best) best = s;
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        lock (sync)
                                        {
                                            Console.WriteLine("  " + item.Path + " 失败：" + ex.Message);
                                        }
                                    }
                                }
                            })
                            {
                                IsBackground = true,
                                Name = "bench-" + i
                            };
                            workers.Add(worker);
                            worker.Start();
                        }

                        foreach (var worker in workers)
                        {
                            worker.Join();
                        }
                    }

                    roundWatch.Stop();
                    var wall = roundWatch.Elapsed.TotalSeconds;
                    roundBytes = blobs.Sum(b => b.StoredBytes);
                    roundTime = wall;

                    totalBytes += (long)roundBytes;
                    totalTime += wall;

                    Console.WriteLine(string.Format("  小结：{0} / {1:0.00} s = {2:0.00} MB/s（{3} 路并发，按墙钟计）",
                        SyncProgress.FormatSize((long)roundBytes), wall,
                        wall <= 0 ? 0 : roundBytes / wall / 1024 / 1024, threads));
                }
            }
            finally
            {
                TryDeleteDirectorySafe(tempDir);
            }

            Console.WriteLine();
            Console.WriteLine(string.Format("[合计] {0} / {1:0.00} s = {2:0.00} MB/s（峰值单分片 {3:0.00} MB/s）",
                SyncProgress.FormatSize(totalBytes), totalTime,
                totalTime <= 0 ? 0 : totalBytes / totalTime / 1024 / 1024, best));
            Console.WriteLine("[参照] 千兆网理论上限约 110-118 MB/s");
            return 0;
        }

        /// <summary>下载一个远端块到临时目录并计时，返回 MB/s。测完即删，避免占盘。</summary>
        private static double TransferOne(WebDavClient client, string appId, BenchBlob blob,
            string tempDir, SyncOptions opt)
        {
            var localPart = Path.Combine(tempDir, Path.GetFileName(blob.Path));
            TryDelete(localPart);

            var watch = Stopwatch.StartNew();
            client.DownloadFile("apps/" + appId + "/" + blob.Path, localPart,
                null, CancellationToken.None, opt);
            watch.Stop();

            var seconds = watch.Elapsed.TotalSeconds;
            var speed = seconds <= 0 ? 0 : blob.StoredBytes / seconds / 1024.0 / 1024.0;

            Console.WriteLine(string.Format("  {0}  {1,10}  {2,7:0.00} s  {3,8:0.00} MB/s",
                Path.GetFileName(blob.Path), SyncProgress.FormatSize(blob.StoredBytes), seconds, speed));

            TryDelete(localPart);
            return speed;
        }

        /// <summary>测速的最小单位：远端一个文件 + 它的字节数（区块或分片都行）。</summary>
        private class BenchBlob
        {
            public string Path { get; set; }
            public long StoredBytes { get; set; }
        }

        private static void TryDeleteDirectorySafe(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, true);
                }
            }
            catch
            {
            }
        }

        // ---------- 远端目录浏览 ----------

        /// <summary>列出仓库某个目录的一级条目（PROPFIND Depth:1）。</summary>
        private static int CmdTree(OptionSet options)
        {
            // --path "" 是合法输入（列仓库根目录），所以用 Has 判断而不是取默认值
            var path = options.Has("path") ? (options.Get("path") ?? string.Empty) : null;
            if (path == null)
            {
                var id = options.Get("id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    throw new ArgumentException("请用 --id <应用Id> 或 --path <相对路径> 指定要列出的目录");
                }
                path = "apps/" + id;
            }

            var service = CreateService(options);
            var client = service.CreateClient();

            Console.WriteLine("[仓库] " + client.BaseUrl);
            Console.WriteLine("[目录] " + path.TrimEnd('/') + "/");
            Console.WriteLine();

            var entries = client.List(path);
            if (entries.Count == 0)
            {
                Console.WriteLine("（空目录）");
                return 0;
            }

            var files = entries.Where(e => !e.IsCollection).ToList();
            var dirs = entries.Where(e => e.IsCollection).OrderBy(e => e.Name).ToList();

            foreach (var dir in dirs)
            {
                Console.WriteLine("  [目录] " + dir.Name + "/");
            }

            foreach (var file in files.OrderByDescending(f => f.Length))
            {
                Console.WriteLine(string.Format("         {0,-28} {1,12}", file.Name, SyncProgress.FormatSize(file.Length)));
            }

            Console.WriteLine();
            Console.WriteLine(string.Format("共 {0} 个文件（{1}），{2} 个子目录",
                files.Count, SyncProgress.FormatSize(files.Sum(f => f.Length)), dirs.Count));
            return 0;
        }

        private static int CmdList(OptionSet options)        {
            var service = CreateService(options);
            Console.WriteLine("[仓库] " + service.Settings.WebDavUrl);

            string source;
            string error;
            var index = service.GetIndex(out source, out error);

            Console.WriteLine("[来源] " + source + (error == null ? "" : "（远端失败：" + error + "）"));
            Console.WriteLine();

            if (index == null || index.Apps == null || index.Apps.Count == 0)
            {
                Console.WriteLine("（仓库为空）");
                return 0;
            }

            Console.WriteLine("{0,-28} {1,-34} {2,-8} {3,12} {4,7} {5,-12} {6}",
                "Id", "名称", "版本", "字节", "文件数", "布局", "启动程序");
            Console.WriteLine(new string('-', 122));
            foreach (var app in index.Apps)
            {
                Console.WriteLine("{0,-28} {1,-34} {2,-8} {3,12} {4,7} {5,-12} {6}",
                    Truncate(app.Id, 28),
                    Truncate(app.Name, 34),
                    Truncate(app.Version, 8),
                    app.TotalBytes,
                    app.FileCount,
                    LayoutOf(app),
                    app.LaunchExe);
            }

            Console.WriteLine();
            Console.WriteLine("共 " + index.Apps.Count + " 个条目，合计 " +
                SyncProgress.FormatSize(index.Apps.Sum(a => a.TotalBytes)));
            return 0;
        }

        /// <summary>索引里的布局文字描述。旧条目没这两个计数字段，按 v1 显示。</summary>
        private static string LayoutOf(AppEntry app)
        {
            if (app.ChunkCount > 0)
            {
                return "v3 区块 " + app.ChunkCount;
            }
            if (app.PartCount > 0)
            {
                return "v2 分片 " + app.PartCount;
            }
            return "v1 直传";
        }

        private static int CmdPack(OptionSet options)
        {
            var src = options.Require("src");
            var appId = options.Require("id");
            var name = options.Get("name", Path.GetFileName(src.TrimEnd('\\', '/')));
            var version = options.Get("version", "1.0");
            var launchExe = options.Get("exe", GuessLaunchExe(src));
            var force = options.Has("force");
            var compress = options.Has("compress");

            // v3 用区块大小；--part-size 是老叫法，仍接住（一个文件可跨块，语义已经不同）
            var chunkSizeMb = int.Parse(
                options.Get("chunk-size", options.Get("part-size", "32")),
                CultureInfo.InvariantCulture);

            if (!Directory.Exists(src))
            {
                throw new DirectoryNotFoundException("源目录不存在：" + src);
            }

            var service = CreateService(options);

            // 元数据：--meta 给 JSON 文件（可含图片本地路径），否则只带名称
            AppMetadata metadata = null;
            var metaFile = options.Get("meta");
            if (metaFile != null)
            {
                if (!File.Exists(metaFile))
                {
                    throw new FileNotFoundException("找不到元数据文件：" + metaFile);
                }
                metadata = JsonConvert.DeserializeObject<AppMetadata>(
                    File.ReadAllText(metaFile, Encoding.UTF8));
                Console.WriteLine("[元数据] " + metaFile);
            }

            if (metadata == null)
            {
                metadata = new AppMetadata();
            }
            if (string.IsNullOrWhiteSpace(metadata.Name)) metadata.Name = name;
            if (string.IsNullOrWhiteSpace(metadata.Version)) metadata.Version = version;

            // 元数据里的名称更权威（通常是库里的完整名，可能含中文）。
            // 走 JSON 传中文可以避开命令行参数的编码问题。
            if (!string.IsNullOrWhiteSpace(metadata.Name))
            {
                name = metadata.Name;
            }
            if (!string.IsNullOrWhiteSpace(metadata.Version))
            {
                version = metadata.Version;
            }

            Console.WriteLine("[仓库] " + service.Settings.WebDavUrl);
            Console.WriteLine("[源目录] " + src);
            Console.WriteLine("[应用Id] " + appId);
            Console.WriteLine("[名称] " + name);
            Console.WriteLine("[启动程序] " + launchExe);
            Console.WriteLine("[区块大小] " + chunkSizeMb + " MB");
            Console.WriteLine("[压缩] " + (compress ? "Deflate" : "不压缩"));
            Console.WriteLine("[模式] " + (force ? "强制全量重传" : "增量（远端已有同样区块则跳过）"));
            Console.WriteLine();

            var opt = service.BuildSyncOptions(force);
            // 必须写 ChunkSize：BuildSyncOptions 已经从设置里填过 32MB，只改 PartSize 不会生效
            opt.ChunkSize = Math.Max(1, chunkSizeMb) * 1024L * 1024L;
            opt.Compress = compress;

            var uploadConn = options.Get("upload-conn");
            if (!string.IsNullOrWhiteSpace(uploadConn))
            {
                int parsed;
                if (!int.TryParse(uploadConn, out parsed) || parsed < 1)
                {
                    throw new ArgumentException("--upload-conn 需要是 ≥1 的整数，当前值：" + uploadConn);
                }
                opt.UploadConcurrency = parsed;
            }

            var result = service.ArchiveApp(
                appId, name, src, launchExe, version, metadata, opt,
                PrintProgress, CancellationToken.None);

            Console.WriteLine();
            Console.WriteLine("[完成] " + result.Describe());

            // 回读远端清单，确认布局（v3 区块 / v2 分片 / v1 逐文件）
            var manifest = service.FetchManifest(appId);
            Console.WriteLine();
            Console.WriteLine("[清单] Schema=" + manifest.Schema
                + "  布局=" + manifest.Layout
                + "  Packed=" + manifest.Packed
                + "  文件=" + manifest.Files.Count
                + "  区块=" + manifest.Chunks.Count
                + "  原始=" + SyncProgress.FormatSize(manifest.TotalBytes)
                + "  存储=" + SyncProgress.FormatSize(manifest.StoredBytes)
                + "  区块大小=" + SyncProgress.FormatSize(manifest.ChunkSize));

            if (manifest.Layout == VaultLayout.Chunks)
            {
                foreach (var chunk in manifest.Chunks.Take(10))
                {
                    var pieces = manifest.Files.Sum(f =>
                        f.Pieces == null ? 0 : f.Pieces.Count(p => p.Chunk == chunk.Index));
                    Console.WriteLine(string.Format("  [{0,4}] {1}  {2,12}  原始 {3,12}  {4} 个片段",
                        chunk.Index, chunk.Path, chunk.StoredBytes, chunk.RawBytes, pieces));
                }

                if (manifest.Chunks.Count > 10)
                {
                    Console.WriteLine("  ...（其余 " + (manifest.Chunks.Count - 10) + " 个区块略）");
                }

                var crossChunk = manifest.Files.Count(f => f.Pieces != null && f.Pieces.Count > 1);
                Console.WriteLine("  跨块文件：" + crossChunk + " 个"
                    + (crossChunk > 0 ? "（正是 v3 相对 v2 的关键区别）" : string.Empty));
            }
            else
            {
                foreach (var part in manifest.Parts)
                {
                    var count = manifest.Files.Count(f => f.Part == part.Index);
                    Console.WriteLine(string.Format("  {0}  {1,12}  原始 {2,12}  {3} 个文件",
                        part.Path, part.StoredBytes, part.RawBytes, count));
                }
            }

            var meta = manifest.Metadata;
            if (meta != null)
            {
                Console.WriteLine("[元数据随包] 名称=" + meta.Name
                    + " 开发商=" + Join(meta.Developers)
                    + " 类型=" + Join(meta.Genres)
                    + " 标签=" + Join(meta.Tags)
                    + " 发行=" + meta.ReleaseDate
                    + " 图片=" + (meta.Images == null ? "-" : string.Join(",", meta.Images.Values.ToArray())));
            }

            Console.WriteLine("[远端] " + service.Settings.WebDavUrl.TrimEnd('/') + "/apps/" + appId + "/");
            return 0;
        }

        private static string Join(List<string> values)
        {
            return values == null || values.Count == 0 ? "-" : string.Join("/", values.ToArray());
        }

        private static int CmdInstall(OptionSet options)
        {
            var appId = options.Require("id");
            var targetDir = options.Require("dir");
            var force = options.Has("force");

            var service = CreateService(options);

            Console.WriteLine("[仓库] " + service.Settings.WebDavUrl);
            Console.WriteLine("[应用Id] " + appId);
            Console.WriteLine("[目标目录] " + targetDir);
            Console.WriteLine();

            var optionsSync = service.BuildSyncOptions(force);

            var result = service.InstallApp(
                appId, targetDir, optionsSync, PrintProgress, CancellationToken.None);

            Console.WriteLine();
            Console.WriteLine("[完成] " + result.Describe());

            if (!options.Has("no-verify"))
            {
                return VerifyInstalled(service, appId, targetDir);
            }
            return 0;
        }

        /// <summary>逐文件比对本地结果与远端清单（存在性 + 字节数）。</summary>
        private static int VerifyInstalled(VaultService service, string appId, string targetDir)
        {
            var manifest = service.FetchManifest(appId);
            Console.WriteLine();
            Console.WriteLine("[校验] 清单 " + manifest.Files.Count + " 个文件，声明总字节 " + manifest.TotalBytes);

            var bad = 0;
            var missing = 0;
            long actualTotal = 0;

            foreach (var file in manifest.Files)
            {
                var localPath = Path.Combine(targetDir, file.Path.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(localPath))
                {
                    missing++;
                    bad++;
                    Console.WriteLine("  缺失: " + file.Path);
                    continue;
                }

                var length = new FileInfo(localPath).Length;
                actualTotal += length;
                if (length != file.Size)
                {
                    bad++;
                    Console.WriteLine("  大小不符: " + file.Path + "  期望 " + file.Size + "，实际 " + length);
                }
            }

            Console.WriteLine("[校验] 实际落盘总字节 " + actualTotal);
            if (bad == 0)
            {
                Console.WriteLine("[校验] 通过：全部 " + manifest.Files.Count + " 个文件字节数一致");
                return 0;
            }

            Console.WriteLine("[校验] 不通过：缺失 " + missing + " 个，异常 " + bad + " 个");
            return 3;
        }

        private static int CmdRemove(OptionSet options)
        {
            var appId = options.Require("id");
            var service = CreateService(options);

            Console.WriteLine("[仓库] " + service.Settings.WebDavUrl);
            Console.WriteLine("[应用] " + appId);

            // 清单可能已经不在了（比如上一次删到一半），此时仍然继续，
            // 按目录前缀把残留清干净，否则会留下删不掉的空壳。
            try
            {
                var manifest = service.FetchManifest(appId);
                Console.WriteLine("[规模] " + manifest.Files.Count + " 个文件 / "
                    + SyncProgress.FormatSize(manifest.TotalBytes) + "，"
                    + manifest.Chunks.Count + " 个区块（" + manifest.Layout + "）");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[提示] 读不到清单（" + ex.Message + "），按残留目录清理");
            }
            Console.WriteLine();

            if (!options.Has("yes"))
            {
                Console.Error.WriteLine("这是不可逆操作：会删掉远端 apps/" + appId
                    + "/ 下的全部数据，并从 index.json 与本地记录里摘除该条目（本地已下载的游戏文件不动）。");
                Console.Error.WriteLine("确认无误后加 --yes 重跑。");
                return 4;
            }

            var count = service.RemoveApp(appId, path => Console.WriteLine("  删除 " + path));

            Console.WriteLine();
            Console.WriteLine("[完成] 已删除 " + count + " 个文件，远端索引与本地记录已更新");
            return 0;
        }

        private static int CmdDump(OptionSet options)
        {
            var dbPath = options.Get("db", DefaultPlayniteDb());
            var filter = options.Get("filter");
            var outPath = options.Get("out");

            if (!File.Exists(dbPath))
            {
                throw new FileNotFoundException("找不到库文件：" + dbPath);
            }

            // Playnite 运行时会对 games.db 加独占锁。先用「允许共享写」的方式复制一份，
            // 再解析副本，就不会打扰正在运行的 Playnite。
            var workingCopy = CopyShared(dbPath);
            try
            {
                return DumpFrom(workingCopy, dbPath, filter, outPath);
            }
            finally
            {
                TryDelete(workingCopy);
            }
        }

        /// <summary>以共享读方式复制被占用的文件，返回副本路径。</summary>
        private static string CopyShared(string sourcePath)
        {
            var copy = Path.Combine(Path.GetTempPath(),
                "vaultpack-dump-" + Guid.NewGuid().ToString("N") + ".db");

            using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var target = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                source.CopyTo(target, 1024 * 1024);
            }

            return copy;
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (path != null && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        private static int DumpFrom(string workingPath, string dbPath, string filter, string outPath)
        {
            var builder = new StringBuilder();
            builder.AppendLine("{");
            builder.AppendLine("  \"db\": " + JsonConvert.ToString(dbPath) + ",");
            builder.AppendLine("  \"collections\": [");

            using (var db = new LiteDB.LiteDatabase(workingPath))
            {
                var names = new List<string>();
                foreach (var name in db.GetCollectionNames())
                {
                    names.Add(name);
                }

                Console.WriteLine("[库文件] " + dbPath);
                Console.WriteLine("[集合] " + string.Join(", ", names.ToArray()));

                // 文档最多的那个集合就是游戏表
                string gameCollection = null;
                var bestCount = -1;
                foreach (var name in names)
                {
                    int count;
                    try
                    {
                        count = db.GetCollection(name).Count();
                    }
                    catch
                    {
                        continue;
                    }
                    if (count > bestCount)
                    {
                        bestCount = count;
                        gameCollection = name;
                    }
                }

                Console.WriteLine("[游戏集合] " + gameCollection + "（" + bestCount + " 条）");

                for (var i = 0; i < names.Count; i++)
                {
                    builder.Append("    ").Append(JsonConvert.ToString(names[i]));
                    builder.AppendLine(i == names.Count - 1 ? "" : ",");
                }
                builder.AppendLine("  ],");

                var docs = new List<object>();
                if (gameCollection != null)
                {
                    foreach (var doc in db.GetCollection(gameCollection).FindAll())
                    {
                        var obj = BsonToObject(doc) as Dictionary<string, object>;
                        if (obj == null)
                        {
                            continue;
                        }

                        if (filter != null)
                        {
                            var nameValue = obj.ContainsKey("Name") ? Convert.ToString(obj["Name"]) : null;
                            if (nameValue == null ||
                                nameValue.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                            {
                                continue;
                            }
                        }

                        docs.Add(obj);
                    }
                }

                builder.AppendLine("  \"games\": " + JsonConvert.SerializeObject(docs, Formatting.Indented) + ",");
                builder.AppendLine("  \"tables\": {");

                // 把每个集合整表导出：Game 里存的是 DeveloperIds / GenreIds 这类 GUID，
                // 只有拿到 companies / genres / tags 等表才能还原成人能看的名字。
                for (var i = 0; i < names.Count; i++)
                {
                    var rows = new List<object>();
                    try
                    {
                        foreach (var doc in db.GetCollection(names[i]).FindAll())
                        {
                            var obj = BsonToObject(doc) as Dictionary<string, object>;
                            if (obj != null)
                            {
                                rows.Add(obj);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("  （集合 " + names[i] + " 读取失败：" + ex.Message + "）");
                    }

                    builder.Append("    ").Append(JsonConvert.ToString(names[i])).Append(": ");
                    builder.Append(JsonConvert.SerializeObject(rows, Formatting.Indented));
                    builder.AppendLine(i == names.Count - 1 ? "" : ",");
                }

                builder.AppendLine("  }");
                builder.AppendLine("}");

                Console.WriteLine("[命中] " + docs.Count + " 条" + (filter == null ? "" : "（过滤：" + filter + "）"));
                Console.WriteLine("[全表] 已导出 " + names.Count + " 个集合用于名称解析");
            }

            var json = builder.ToString();
            if (outPath != null)
            {
                File.WriteAllText(outPath, json, new UTF8Encoding(false));
                Console.WriteLine("[已写出] " + outPath);
            }
            else
            {
                Console.WriteLine(json);
            }

            return 0;
        }

        // ---------- LiteDB BsonValue → 普通对象 ----------

        private static object BsonToObject(LiteDB.BsonValue value)
        {
            if (value == null || value.IsNull)
            {
                return null;
            }

            try
            {
                switch (value.Type)
                {
                    case LiteDB.BsonType.Document:
                        {
                            var result = new Dictionary<string, object>();
                            foreach (var pair in value.AsDocument)
                            {
                                result[pair.Key] = BsonToObject(pair.Value);
                            }
                            return result;
                        }

                    case LiteDB.BsonType.Array:
                        {
                            var result = new List<object>();
                            foreach (var item in value.AsArray)
                            {
                                result.Add(BsonToObject(item));
                            }
                            return result;
                        }

                    case LiteDB.BsonType.DateTime:
                        return value.AsDateTime.ToString("o", CultureInfo.InvariantCulture);

                    case LiteDB.BsonType.Guid:
                        return value.AsGuid.ToString();

                    case LiteDB.BsonType.ObjectId:
                        return value.AsObjectId.ToString();

                    case LiteDB.BsonType.Int32:
                        return value.AsInt32;

                    case LiteDB.BsonType.Int64:
                        return value.AsInt64;

                    case LiteDB.BsonType.Double:
                        return value.AsDouble;

                    case LiteDB.BsonType.Decimal:
                        return (double)value.AsDecimal;

                    case LiteDB.BsonType.Boolean:
                        return value.AsBoolean;

                    case LiteDB.BsonType.String:
                        return value.AsString;

                    default:
                        return value.ToString();
                }
            }
            catch (Exception ex)
            {
                return "<无法解析 " + value.Type + "：" + ex.Message + ">";
            }
        }

        // ---------- 主题同步 ----------

        /// <summary>
        /// 把 Playnite 的 Themes 目录同步到仓库的 themes/ 下。
        /// 引擎（<see cref="ThemeSyncEngine"/>）在插件里，和界面用的是同一份生产代码，
        /// 这里只负责把控制台接上去。
        /// </summary>
        private static int CmdThemesSync(OptionSet options)
        {
            var dir = options.Get("dir", DefaultThemesDir());
            if (!Directory.Exists(dir))
            {
                throw new DirectoryNotFoundException("找不到主题目录：" + dir
                    + "\n（用 --dir 指定，或把环境变量 PLAYNITE_DIR 指到 Playnite 安装目录）");
            }

            var service = CreateService(options);
            var mode = ParseThemeSyncMode(options.Get("mode", "up"));
            var opt = new ThemeSyncOptions
            {
                Mode = mode,
                DryRun = options.Has("dry-run"),
                Force = options.Has("force")
            };

            // 状态文件默认落在**插件数据目录**（跟 settings.json 放一起），
            // 而不是工具自己的临时目录：三方比对的基准必须和界面共享，
            // 否则命令行传过一次之后，界面那边没有基准，会把「我改的」误判成「两边都改了」。
            var settingsPath = options.Get("settings", DefaultSettingsPath());
            var statePath = options.Get("state",
                Path.Combine(Path.GetDirectoryName(settingsPath), "theme-sync-state.json"));

            Console.WriteLine("[主题目录] " + dir);
            Console.WriteLine("[仓库地址] " + service.Settings.WebDavUrl);
            Console.WriteLine("[同步方向] " + DescribeThemeMode(mode)
                + (opt.DryRun ? "   （预演：只列计划，不落盘）" : string.Empty));
            Console.WriteLine();

            var client = service.CreateClient();
            try
            {
                Console.WriteLine("[连通性] OK，仓库根目录下可见 " + client.TestConnection() + " 个条目");
            }
            catch (Exception ex)
            {
                // 不在这里退出：让引擎抛出更具体的错误（例如索引不是本插件的）
                Console.WriteLine("[连通性] 探测失败：" + ex.Message);
            }
            Console.WriteLine();

            var engine = new ThemeSyncEngine(client, dir, statePath,
                service.BuildSyncOptions(false), new ConsoleThemeReporter(), CancellationToken.None);

            var result = engine.Run(opt);
            Console.WriteLine();

            if (result.DryRun)
            {
                Console.WriteLine("[预演结束] 以上是这次会做的事；去掉 --dry-run 即真正执行");
                return 0;
            }

            Console.WriteLine("[结果] " + result.Counters.Describe());
            Console.WriteLine("[状态] " + statePath);
            return 0;
        }

        // ==================================================================
        //  云存档
        // ==================================================================

        /// <summary>
        /// <c>VaultPack saves …</c>。引擎本身不认识 Playnite，所以这里能直接把
        /// 「路径定义 + 游戏目录」喂进去 —— 与插件界面走的是同一条生产代码路径。
        /// </summary>
        private static int CmdSaves(OptionSet options)
        {
            var action = (options.Get("action", "list") ?? "list").Trim().ToLowerInvariant();
            var service = CreateService(options);
            var client = service.CreateClient();
            var dataPath = options.Get("data",
                Path.Combine(Path.GetDirectoryName(DefaultSettingsPath()), "saves"));

            var engine = new SaveSyncEngine(client, dataPath, service.BuildSyncOptions(false),
                new ConsoleSaveReporter(), CancellationToken.None);

            var gameId = options.Get("game");
            var branch = options.Get("branch", SaveBranch.Default);

            Console.WriteLine("[仓库地址] " + service.Settings.WebDavUrl);
            Console.WriteLine("[动作]     " + action);
            if (!string.IsNullOrWhiteSpace(gameId))
            {
                Console.WriteLine("[游戏]     " + gameId + "　分支 " + branch);
            }
            Console.WriteLine();

            switch (action)
            {
                case "list":
                    return SaveList(engine, gameId);

                case "branches":
                    return SaveBranches(engine, options, gameId);

                case "upload":
                    return SaveUpload(engine, options, gameId, branch);

                case "download":
                    return SaveDownload(engine, options, gameId, branch);

                case "delete":
                    return SaveDelete(engine, options, gameId, branch);

                case "prune":
                    return SavePrune(engine, options, gameId, branch);

                case "sniff":
                    return SaveSniff(options, gameId);

                default:
                    throw new ArgumentException("未知的 --action：" + action
                        + "（可用：list / upload / download / delete / branches / prune / sniff）");
            }
        }

        private static int SaveList(SaveSyncEngine engine, string gameId)
        {
            if (string.IsNullOrWhiteSpace(gameId))
            {
                var index = engine.LoadSaveIndex();
                if (index.Games.Count == 0)
                {
                    Console.WriteLine("远端还没有任何存档。");
                    return 0;
                }

                Console.WriteLine("游戏                                                  快照  分支      体积  最后更新");
                foreach (var game in index.Games)
                {
                    Console.WriteLine(string.Format("{0,-50} {1,5} {2,5} {3,10}  {4}",
                        Clip(game.GameName, 50), game.Snapshots, game.Branches,
                        SyncProgress.FormatSize(game.Bytes),
                        game.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")));
                }

                return 0;
            }

            var manifest = engine.LoadManifest(SaveSyncEngine.SafeGameKey(gameId));
            if (manifest == null)
            {
                Console.WriteLine("远端没有这个游戏的存档。");
                return 0;
            }

            Console.WriteLine("[游戏名] " + manifest.GameName);
            Console.WriteLine("[体积]   " + SyncProgress.FormatSize(manifest.TotalBytes)
                              + "（去重后按实际内容算）");
            Console.WriteLine();

            foreach (var b in manifest.Branches)
            {
                Console.WriteLine("分支 " + b.Name + "　" + b.SnapshotCount + " 份"
                                  + (string.IsNullOrEmpty(b.Comment) ? "" : "　" + b.Comment));
                foreach (var s in manifest.SnapshotsIn(b.Name))
                {
                    Console.WriteLine("    " + s.Describe());
                }
            }

            return 0;
        }

        private static int SaveBranches(SaveSyncEngine engine, OptionSet options, string gameId)
        {
            var key = RequireGame(gameId);
            var manifest = engine.LoadManifest(key) ?? SaveGameManifest.NewFor(key, key);

            var create = options.Get("create");
            if (string.IsNullOrWhiteSpace(create))
            {
                // 只是列出来
                foreach (var b in manifest.Branches)
                {
                    Console.WriteLine("* " + b.Name + "　" + b.SnapshotCount + " 份"
                                      + (string.IsNullOrEmpty(b.CreatedFrom) ? "" : "　来自 " + b.CreatedFrom)
                                      + (string.IsNullOrEmpty(b.Comment) ? "" : "　" + b.Comment));
                }

                return 0;
            }

            var from = options.Get("from");
            engine.CreateBranch(key, manifest, create, from,
                "命令行创建" + (string.IsNullOrWhiteSpace(from) ? "" : "（自 " + from + " 分叉）"));

            Console.WriteLine("[完成] 分支「" + create + "」已创建"
                              + (string.IsNullOrWhiteSpace(from) ? "（空分支）" : "，自快照 " + from + " 分叉"));
            Console.WriteLine("[提示] 上传时加 --branch " + create + " 就往这条分支上推");
            return 0;
        }

        private static int SaveUpload(SaveSyncEngine engine, OptionSet options, string gameId,
            string branch)
        {
            var key = RequireGame(gameId);
            var installDir = options.Get("install");
            List<SavePathSpec> specs;
            List<string> parseNotes;
            ParsePathSpecs(options.Get("paths") ?? options.Get("path"), installDir,
                out specs, out parseNotes);

            if (specs.Count == 0)
            {
                throw new ArgumentException(
                    "至少要给一条 --path，格式：--path \"标题={LocalAppData}\\游戏\\存档\"\n"
                    + "多条用分号隔开，文件型加 file: 前缀（例如 file:cfg={GameDir}\\cfg.ini）");
            }

            foreach (var note in parseNotes)
            {
                Console.WriteLine("[路径] " + note);
            }

            var manifest = engine.LoadManifest(key) ?? SaveGameManifest.NewFor(key, key);
            manifest.GameName = options.Get("name", manifest.GameName);
            manifest.Paths = specs;

            var outcome = engine.Upload(key, manifest.GameName, installDir, manifest,
                new SaveSyncOptions
                {
                    Branch = branch,
                    KeepPerBranch = ParseInt(options.Get("keep"), 10),
                    DryRun = options.Has("dry-run"),
                    Force = options.Has("force"),
                    ShowName = options.Get("snapshot-name"),
                    Comment = options.Get("comment"),
                    Pinned = options.Has("pin"),
                    Origin = SaveSnapshotOrigin.Manual
                });

            Console.WriteLine();
            if (!outcome.Ok)
            {
                Console.Error.WriteLine("[失败] " + outcome.Failure);
                return 2;
            }

            if (outcome.DryRun)
            {
                Console.WriteLine("[预演结束] 去掉 --dry-run 即真正执行");
                return 0;
            }

            Console.WriteLine("[完成] 快照 " + (outcome.SnapshotId ?? "(无)")
                              + "　" + outcome.Counters.Describe());
            return 0;
        }

        private static int SaveDownload(SaveSyncEngine engine, OptionSet options, string gameId,
            string branch)
        {
            var key = RequireGame(gameId);
            var manifest = engine.LoadManifest(key);
            if (manifest == null)
            {
                throw new InvalidOperationException("远端没有这个游戏的存档：" + key);
            }

            var outcome = engine.Download(key, manifest.GameName, options.Get("install"), manifest,
                new SaveSyncOptions
                {
                    Branch = branch,
                    SnapshotId = options.Get("snapshot"),
                    DryRun = options.Has("dry-run"),
                    SafetyBackup = !options.Has("no-backup"),
                    SnapshotBeforeRestore = !options.Has("no-backup-snapshot"),
                    KeepLocalBackups = ParseInt(options.Get("keep-local"), 5),
                    KeepPerBranch = ParseInt(options.Get("keep"), 10)
                });

            Console.WriteLine();
            if (!outcome.Ok)
            {
                Console.Error.WriteLine("[失败] " + outcome.Failure);
                return 2;
            }

            if (outcome.DryRun)
            {
                Console.WriteLine("[预演结束] 去掉 --dry-run 即真正执行");
                return 0;
            }

            Console.WriteLine("[完成] 恢复自 " + outcome.SnapshotId + "　"
                              + outcome.Counters.Describe());
            return 0;
        }

        private static int SaveDelete(SaveSyncEngine engine, OptionSet options, string gameId,
            string branch)
        {
            var key = RequireGame(gameId);
            var snapshotId = options.Require("snapshot");
            var manifest = engine.LoadManifest(key);
            if (manifest == null)
            {
                throw new InvalidOperationException("远端没有这个游戏的存档：" + key);
            }

            var outcome = engine.DeleteSnapshot(key, manifest, branch, snapshotId,
                new SaveSyncOptions
                {
                    // 命令行要删就必须明写 --allow-delete：删除是不可逆的，
                    // 不能让一个手滑的命令把它带走
                    AllowDelete = options.Has("allow-delete"),
                    DryRun = options.Has("dry-run"),
                    KeepPerBranch = ParseInt(options.Get("keep"), 10)
                });

            Console.WriteLine();
            if (!outcome.Ok)
            {
                Console.Error.WriteLine("[失败] " + outcome.Failure);
                Console.Error.WriteLine("[提示] 删除需要显式加 --allow-delete");
                return 2;
            }

            Console.WriteLine("[完成] " + outcome.Counters.Describe());
            return 0;
        }

        private static int SavePrune(SaveSyncEngine engine, OptionSet options, string gameId,
            string branch)
        {
            var key = RequireGame(gameId);
            var manifest = engine.LoadManifest(key);
            if (manifest == null)
            {
                throw new InvalidOperationException("远端没有这个游戏的存档：" + key);
            }

            var outcome = engine.Prune(key, manifest, new SaveSyncOptions
            {
                Branch = branch,
                KeepPerBranch = ParseInt(options.Get("keep"), 10),
                AllowDelete = options.Has("allow-delete"),
                DryRun = options.Has("dry-run")
            });

            Console.WriteLine();
            if (!outcome.Ok)
            {
                Console.Error.WriteLine("[失败] " + outcome.Failure);
                return 2;
            }

            Console.WriteLine("[完成] " + outcome.Counters.Describe()
                              + (options.Has("dry-run") ? "（预演）" : ""));
            return 0;
        }

        private static int SaveSniff(OptionSet options, string gameId)
        {
            if (string.IsNullOrWhiteSpace(gameId))
            {
                throw new ArgumentException("嗅探需要 --game（至少用来推路径），建议同时给 --name 与 --install");
            }

            var ctx = new SaveSniffContext
            {
                GameName = options.Get("name", gameId),
                GameInstallDir = options.Get("install"),
                Budget = TimeSpan.FromSeconds(ParseInt(options.Get("budget"), 8)),
                MinScore = ParseInt(options.Get("min-score"), 30)
            };

            Console.WriteLine("[游戏名] " + ctx.GameName);
            Console.WriteLine("[安装目录] " + (string.IsNullOrWhiteSpace(ctx.GameInstallDir)
                ? "(未提供，{GameDir} 有关的线索会缺失)"
                : ctx.GameInstallDir));
            Console.WriteLine();

            var all = new List<SaveSniffCandidate>();

            Console.WriteLine("== 常见位置 ==");
            var heuristic = SaveSniffer.Heuristic(ctx);
            all.AddRange(heuristic);
            PrintCandidates(heuristic);

            if (options.Has("wiki"))
            {
                Console.WriteLine();
                Console.WriteLine("== PCGamingWiki ==");
                string error;
                var wiki = PcgamingWikiClient.TrySniff(ctx.GameName, ctx, out error);
                all.AddRange(wiki);
                if (wiki.Count == 0)
                {
                    Console.WriteLine("  （没抓到：" + (error ?? "未知原因") + "）");
                }
                else
                {
                    PrintCandidates(wiki);
                }
            }

            Console.WriteLine();
            Console.WriteLine("== 合并去重后 ==");
            var merged = PcgamingWikiClient.Merge(all);
            PrintCandidates(merged);

            Console.WriteLine();
            Console.WriteLine("嗅探结果**不会自动写进任何地方**。要采用就在 --path 里显式写上，"
                              + "例如：");
            if (merged.Count > 0)
            {
                Console.WriteLine("  VaultPack saves --action upload --game " + gameId
                                  + " --install \"" + (ctx.GameInstallDir ?? "<安装目录>")
                                  + "\" --path \"" + merged[0].Title + "="
                                  + merged[0].EffectivePath + "\"");
            }

            return 0;
        }

        private static void PrintCandidates(List<SaveSniffCandidate> candidates)
        {
            if (candidates.Count == 0)
            {
                Console.WriteLine("  （没有候选）");
                return;
            }

            foreach (var candidate in candidates)
            {
                Console.WriteLine(candidate.Describe());
            }
        }

        /// <summary>
        /// 解析 <c>--path</c>。每条格式 <c>[file:|dir:][标题=]路径</c>，多条用分号隔开。
        ///
        /// <para>判定「这算不算标题」的规则：<c>=</c> 出现在第一个路径分隔符之前才算标题。
        /// 不这么定的话，一条名字里真带 <c>=</c> 的路径会被切得莫名其妙
        /// （存档文件名里出现 <c>=</c> 并不罕见）。</para>
        /// </summary>
        private static void ParsePathSpecs(string raw, string installDir,
            out List<SavePathSpec> specs, out List<string> notes)
        {
            specs = new List<SavePathSpec>();
            notes = new List<string>();

            foreach (var piece in (raw ?? string.Empty).Split(new[] { ';' }))
            {
                var item = piece.Trim();
                if (item.Length == 0)
                {
                    continue;
                }

                var type = SaveElementType.Directory;
                if (item.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                {
                    type = SaveElementType.File;
                    item = item.Substring(5).Trim();
                }
                else if (item.StartsWith("dir:", StringComparison.OrdinalIgnoreCase))
                {
                    item = item.Substring(4).Trim();
                }

                var title = string.Empty;
                var eq = item.IndexOf('=');
                if (eq > 0)
                {
                    var head = item.Substring(0, eq);
                    if (head.IndexOf('\\') < 0 && head.IndexOf('/') < 0)
                    {
                        title = head.Trim();
                        item = item.Substring(eq + 1).Trim();
                    }
                }

                if (item.Length == 0)
                {
                    continue;
                }

                if (title.Length == 0)
                {
                    title = Path.GetFileName(item.TrimEnd('\\', '/'));
                }

                var spec = new SavePathSpec
                {
                    Title = title,
                    Type = type,
                    Path = item,
                    // 写了占位符就按自适应处理 —— 命令行上再让人多敲一个开关没意义，
                    // 而 {LocalAppData} 这种写法本身就说明「这条要按机器展开」
                    AutoAdaptive = SavePathAdapter.HasToken(item),
                    Enabled = true,
                    Source = SaveSource.Plugin
                };

                List<string> unresolved;
                var resolved = SavePathAdapter.Resolve(spec, installDir, out unresolved);
                if (unresolved.Count > 0)
                {
                    notes.Add("「" + title + "」解析不了：" + string.Join("、", unresolved.ToArray()));
                    continue;
                }

                var exists = spec.Type == SaveElementType.File
                    ? File.Exists(resolved)
                    : Directory.Exists(resolved);

                notes.Add("「" + title + "」" + (spec.AutoAdaptive ? "自适应 " : "字面量 ")
                          + resolved + (exists ? "" : "　（本机还不存在）"));

                specs.Add(spec);
            }
        }

        private static string RequireGame(string gameId)
        {
            if (string.IsNullOrWhiteSpace(gameId))
            {
                throw new ArgumentException("这个动作需要 --game <游戏Id>");
            }

            return SaveSyncEngine.SafeGameKey(gameId);
        }

        private static int ParseInt(string text, int fallback)
        {
            int value;
            return int.TryParse((text ?? string.Empty).Trim(), out value) ? value : fallback;
        }

        private static string Clip(string text, int width)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            return text.Length <= width ? text : text.Substring(0, width - 1) + "…";
        }

        /// <summary>命令行进度：只在阶段变化时打一行，避免刷屏。</summary>
        private class ConsoleSaveReporter : ISaveSyncReporter
        {
            private string lastStage;

            public void Stage(string text)
            {
                if (string.Equals(text, lastStage, StringComparison.Ordinal))
                {
                    return;
                }

                lastStage = text;
                Console.WriteLine("  " + text);
            }

            public void Progress(SyncProgress progress)
            {
                // 命令行下不打进度条：结果与计数在最后一次性给出就够了
            }

            public void Log(string line)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    Console.WriteLine("  " + line);
                }
            }
        }

        private static ThemeSyncMode ParseThemeSyncMode(string value)
        {
            switch ((value ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "":
                case "up":
                case "upload":
                    return ThemeSyncMode.Upload;
                case "down":
                case "download":
                    return ThemeSyncMode.Download;
                case "both":
                case "sync":
                    return ThemeSyncMode.Both;
                default:
                    throw new ArgumentException("--mode 只认 up / down / both，当前值：" + value);
            }
        }

        private static string DescribeThemeMode(ThemeSyncMode mode)
        {
            switch (mode)
            {
                case ThemeSyncMode.Upload: return "只上传（本地 → 仓库）";
                case ThemeSyncMode.Download: return "只下载（仓库 → 本地）";
                default: return "双向";
            }
        }

        /// <summary>把主题同步的日志与进度打到控制台（进度走单行覆盖）。</summary>
        private sealed class ConsoleThemeReporter : IThemeSyncReporter
        {
            private readonly object gate = new object();
            private int lastWidth;

            public void Stage(string text)
            {
                lock (gate)
                {
                    ClearLine();
                    Console.WriteLine("== " + text);
                }
            }

            public void Log(string line)
            {
                lock (gate)
                {
                    ClearLine();
                    Console.WriteLine(line);
                }
            }

            public void Progress(SyncProgress progress)
            {
                lock (gate)
                {
                    var width = ConsoleWidth();
                    var line = progress.Describe();
                    if (line.Length > width)
                    {
                        line = line.Substring(0, width);
                    }

                    var pad = Math.Max(width, lastWidth);
                    Console.Write("\r" + line.PadRight(pad));
                    lastWidth = pad;
                }
            }

            private void ClearLine()
            {
                if (lastWidth <= 0)
                {
                    return;
                }
                Console.Write("\r" + new string(' ', lastWidth) + "\r");
                lastWidth = 0;
            }

            private static int ConsoleWidth()
            {
                try
                {
                    return Math.Max(20, Console.WindowWidth - 1);
                }
                catch
                {
                    // 输出被重定向时 WindowWidth 会抛，给个保守值
                    return 100;
                }
            }
        }

        // ---------- 公共辅助 ----------

        private static VaultService CreateService(OptionSet options)
        {
            var settingsPath = options.Get("settings", DefaultSettingsPath());
            if (!File.Exists(settingsPath))
            {
                throw new FileNotFoundException("找不到插件设置文件：" + settingsPath);
            }

            var settings = JsonConvert.DeserializeObject<VaultSettings>(
                File.ReadAllText(settingsPath, Encoding.UTF8));

            if (settings == null || string.IsNullOrWhiteSpace(settings.WebDavUrl))
            {
                throw new InvalidOperationException("插件设置里的 WebDAV 地址为空，请先在 Playnite 里配置。");
            }

            // 允许临时换地址做对比实验（例如 http/https 吞吐对照），不改动真实设置文件
            var urlOverride = options.Get("url");
            if (!string.IsNullOrWhiteSpace(urlOverride))
            {
                settings.WebDavUrl = urlOverride;
            }

            // 允许临时改并发路数做 A/B 对照（HTTPS 单路受 SslStream 解密限制，
            // 需要多路才能吃满千兆），同样不落盘。
            var connOverride = options.Get("conn");
            if (!string.IsNullOrWhiteSpace(connOverride))
            {
                int conn;
                if (!int.TryParse(connOverride, out conn) || conn < 1)
                {
                    throw new ArgumentException("--conn 需要是 ≥1 的整数，当前值：" + connOverride);
                }
                settings.Concurrency = conn;
            }

            var dataDir = options.Get("data",
                Path.Combine(Path.GetTempPath(), "vaultpack-data"));

            var service = new VaultService(null, dataDir);
            service.SaveSettings(settings);
            VaultLog.Init(dataDir);

            return service;
        }

        /// <summary>
        /// 进度回调：单行覆盖刷新。
        /// 这一行刻意和 Playnite 里的富文本同源，方便脱离 UI 验证进度模型。
        /// </summary>
        private static void PrintProgress(SyncProgress progress)
        {
            if (ProgressLog != null)
            {
                // 记录每一次「真正上报」的原始数值，用来客观检查：
                // 总字节是否单调递增、上报节拍是否均匀、子阶段是否互相打架
                ProgressLog.WriteLine(string.Join(",",
                    ProgressWatch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture),
                    Csv(progress.Phase),
                    progress.BytesDone.ToString(CultureInfo.InvariantCulture),
                    progress.BytesTotal.ToString(CultureInfo.InvariantCulture),
                    progress.PartsDone.ToString(CultureInfo.InvariantCulture),
                    progress.PartsInFlight.ToString(CultureInfo.InvariantCulture),
                    Csv(progress.SubStageName),
                    progress.SubStageBytesDone.ToString(CultureInfo.InvariantCulture),
                    progress.SubStageBytesTotal.ToString(CultureInfo.InvariantCulture),
                    ((long)progress.BytesPerSecond).ToString(CultureInfo.InvariantCulture)));
            }

            var parts = progress.PartsTotal > 1
                ? string.Format("  分片 {0}/{1}", progress.PartsDone, progress.PartsTotal)
                : string.Empty;

            var inFlight = progress.PartsInFlight > 0
                ? string.Format("·{0}传", progress.PartsInFlight)
                : string.Empty;

            var sub = !string.IsNullOrEmpty(progress.SubStageName) && progress.SubStageBytesTotal > 0
                ? string.Format("  {0} {1}%", progress.SubStageName,
                    progress.SubStageBytesDone * 100 / progress.SubStageBytesTotal)
                : string.Empty;

            var line = string.Format("{0}  {1}/{2}  {3} / {4}{5}{6}{7}{8}",
                progress.Phase ?? "传输",
                progress.FilesDone,
                progress.FilesTotal,
                SyncProgress.FormatSize(progress.BytesDone),
                SyncProgress.FormatSize(progress.BytesTotal),
                parts,
                inFlight,
                sub,
                progress.BytesPerSecond > 0
                    ? "  " + SyncProgress.FormatSize((long)progress.BytesPerSecond) + "/s"
                    : string.Empty);

            var width = 0;
            try
            {
                width = Math.Max(20, Console.WindowWidth - 1);
            }
            catch
            {
                width = 100;
            }

            if (line.Length > width)
            {
                line = line.Substring(0, width);
            }

            Console.Write("\r" + line.PadRight(width));
        }

        /// <summary>CSV 字段转义（进度诊断日志用）。</summary>
        private static string Csv(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value.IndexOf(',') >= 0 || value.IndexOf('"') >= 0
                ? "\"" + value.Replace("\"", "\"\"") + "\""
                : value;
        }

        private static string GuessLaunchExe(string dir)
        {
            // 复用插件里的推断逻辑，避免工具与插件两边行为不一致
            return VaultService.GuessLaunchExe(dir, null);
        }

        private static string Truncate(string value, int max)        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }
            return value.Length <= max ? value : value.Substring(0, max - 1) + "…";
        }

        // ---------- 命令行参数 ----------

        private class OptionSet
        {
            private readonly Dictionary<string, string> values =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public static OptionSet Parse(IEnumerable<string> args)
            {
                var set = new OptionSet();
                string pending = null;

                foreach (var arg in args)
                {
                    if (arg.StartsWith("--", StringComparison.Ordinal))
                    {
                        if (pending != null)
                        {
                            set.values[pending] = "true";
                        }
                        pending = arg.Substring(2);
                    }
                    else if (pending != null)
                    {
                        set.values[pending] = arg;
                        pending = null;
                    }
                    else
                    {
                        throw new ArgumentException("无法识别的参数：" + arg);
                    }
                }

                if (pending != null)
                {
                    set.values[pending] = "true";
                }

                return set;
            }

            public bool Has(string key)
            {
                return values.ContainsKey(key);
            }

            public string Get(string key, string fallback = null)
            {
                string value;
                return values.TryGetValue(key, out value) ? value : fallback;
            }

            public string Require(string key)
            {
                var value = Get(key);
                if (string.IsNullOrWhiteSpace(value))
                {
                    throw new ArgumentException("缺少必需参数 --" + key);
                }
                return value;
            }
        }
    }
}
