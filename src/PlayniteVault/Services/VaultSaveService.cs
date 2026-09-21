using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteVault.Models;
using PlayniteVault.Net;

namespace PlayniteVault.Services
{
    /// <summary>
    /// 插件侧的存档服务：把「一个 Playnite 游戏」翻译成引擎要的
    /// 「路径定义 + 安装目录 + 清单」。
    ///
    /// <para>这一层是**唯一**允许碰 Playnite SDK 的存档代码；引擎
    /// （<see cref="SaveSyncEngine"/>）与嗅探（<see cref="SaveSniffer"/>）都不认识 Playnite，
    /// 所以它们能在无头自检里原样跑。</para>
    ///
    /// <para><b>路径来源两份，Playnite 优先</b>：先读 <see cref="Game.SavePaths"/>
    /// （用户在游戏编辑里填的），再叠加插件自己存的一份（可以带忽略规则这类
    /// Playnite 没有的字段），两边按 <c>Title</c> 对齐 —— 同 Title 以 Playnite 为准。
    /// 来源会在界面上标出来，免得用户搞不清某一条是谁写的。</para>
    /// </summary>
    public class VaultSaveService
    {
        /// <summary>插件自己那份路径定义的文件名（存在插件数据目录里）。</summary>
        private const string StoreFile = "save-paths.json";

        private readonly VaultService service;
        private readonly IPlayniteAPI api;
        private readonly string dataPath;
        private readonly string storePath;

        private SavePathStore store;

        public VaultSaveService(VaultService service, IPlayniteAPI api, string dataPath)
        {
            if (service == null)
            {
                throw new ArgumentNullException("service");
            }

            this.service = service;
            this.api = api;
            this.dataPath = dataPath;
            storePath = Path.Combine(dataPath ?? Path.GetTempPath(), StoreFile);
        }

        public VaultSettings Settings
        {
            get { return service.Settings; }
        }

        /// <summary>存档相关的本地文件（对象缓存、留底、同步状态）都放这儿，和游戏仓库分开。</summary>
        public string SaveDataPath
        {
            get { return Path.Combine(dataPath ?? Path.GetTempPath(), "saves"); }
        }

        public VaultService Service
        {
            get { return service; }
        }

        /// <summary>插件这份路径定义存哪儿（界面上「路径来源」那列用的是相对说法，这里给完整路径）。</summary>
        public string StorePath
        {
            get { return storePath; }
        }

        // ==================================================================
        //  插件自己的路径定义（叠加在 Playnite 之上）
        // ==================================================================

        private SavePathStore Store
        {
            get
            {
                if (store == null)
                {
                    store = LoadStore();
                }

                return store;
            }
        }

        private SavePathStore LoadStore()
        {
            try
            {
                if (!File.Exists(storePath))
                {
                    return new SavePathStore();
                }

                var loaded = JsonConvert.DeserializeObject<SavePathStore>(
                    File.ReadAllText(storePath, Encoding.UTF8));
                if (loaded == null)
                {
                    return new SavePathStore();
                }

                if (!string.Equals(loaded.Kind, SavePathStore.StoreKind, StringComparison.Ordinal))
                {
                    // 不是我们写的文件：宁可当成空，也不要把别人的东西覆盖掉
                    VaultLog.Warn("save-paths.json 不是本插件的（Kind="
                                  + (loaded.Kind ?? "空") + "），本次按空处理，不会写回它");
                    return new SavePathStore { ReadOnly = true };
                }

                if (loaded.Games == null)
                {
                    loaded.Games = new Dictionary<string, List<SavePathSpec>>(
                        StringComparer.OrdinalIgnoreCase);
                }

                return loaded;
            }
            catch (Exception ex)
            {
                VaultLog.Warn("读 save-paths.json 失败（按空处理）：" + ex.Message);
                return new SavePathStore();
            }
        }

        /// <summary>插件这份里某个游戏的路径定义。</summary>
        public List<SavePathSpec> PluginPaths(string gameId)
        {
            List<SavePathSpec> list;
            return Store.Games.TryGetValue(gameId ?? string.Empty, out list) && list != null
                ? list
                : new List<SavePathSpec>();
        }

        /// <summary>覆盖式写入插件这份（<c>null</c> / 空列表 = 删掉这个游戏的条目）。</summary>
        public void SetPluginPaths(string gameId, List<SavePathSpec> specs)
        {
            if (string.IsNullOrWhiteSpace(gameId))
            {
                return;
            }

            if (Store.ReadOnly)
            {
                throw new InvalidOperationException(
                    "save-paths.json 不是本插件写的，已拒绝改写。请先把它挪走或改名。");
            }

            if (specs == null || specs.Count == 0)
            {
                Store.Games.Remove(gameId);
            }
            else
            {
                Store.Games[gameId] = specs;
            }

            SaveStore();
        }

        private void SaveStore()
        {
            if (Store.ReadOnly)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(storePath));
                File.WriteAllText(storePath,
                    JsonConvert.SerializeObject(Store, Formatting.Indented),
                    new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                VaultLog.Error("写 save-paths.json 失败", ex);
                throw new InvalidOperationException("保存存档路径定义失败：" + ex.Message, ex);
            }
        }

        // ==================================================================
        //  合并两份来源
        // ==================================================================

        /// <summary>
        /// 把 Playnite 的 <see cref="Game.SavePaths"/> 与插件这份合并起来。
        /// <paramref name="notes"/> 收下「这一条是从哪来的、做了什么转换」这类说明。
        /// </summary>
        public List<SavePathSpec> MergedPaths(Game game, out List<string> notes)
        {
            notes = new List<string>();
            var result = new List<SavePathSpec>();
            if (game == null)
            {
                return result;
            }

            // ---- 1. Playnite 那份优先 ----
            if (game.SavePaths != null)
            {
                foreach (var path in game.SavePaths)
                {
                    if (path == null)
                    {
                        continue;
                    }

                    var spec = FromPlayniteSavePath(path);
                    if (spec == null)
                    {
                        continue;
                    }

                    if (result.Any(s => SameTitle(s, spec)))
                    {
                        notes.Add("Playnite 里有两条同名的「" + spec.Title + "」，只取第一条");
                        continue;
                    }

                    result.Add(spec);
                }
            }

            // ---- 2. 叠加插件那份（同 Title 的不覆盖 Playnite 的） ----
            foreach (var pluginSpec in PluginPaths(game.Id.ToString()))
            {
                if (pluginSpec == null || string.IsNullOrWhiteSpace(pluginSpec.Path))
                {
                    continue;
                }

                if (result.Any(s => SameTitle(s, pluginSpec)))
                {
                    notes.Add("插件里的「" + pluginSpec.Title + "」与 Playnite 那份同名，以 Playnite 为准");
                    continue;
                }

                result.Add(pluginSpec.GetCopy());
            }

            // ---- 3. 逐条体检：能不能在本机展开 ----
            foreach (var spec in result)
            {
                List<string> unresolved;
                var resolved = SavePathAdapter.Resolve(spec, game.InstallDirectory, out unresolved);

                if (unresolved.Count > 0)
                {
                    notes.Add("「" + spec.Title + "」里有本机解析不了的写法（"
                              + string.Join("、", unresolved.ToArray()) + "），同步时会跳过这一条");
                    continue;
                }

                if (string.IsNullOrEmpty(resolved))
                {
                    notes.Add("「" + spec.Title + "」是空路径，已跳过");
                    continue;
                }

                if (spec.Type == SaveElementType.Directory)
                {
                    if (!Directory.Exists(resolved))
                    {
                        notes.Add("「" + spec.Title + "」的目录在本机不存在：" + resolved);
                    }
                }
                else if (!File.Exists(resolved))
                {
                    notes.Add("「" + spec.Title + "」的文件在本机不存在：" + resolved);
                }
            }

            return result;
        }

        private static bool SameTitle(SavePathSpec a, SavePathSpec b)
        {
            if (a == null || b == null)
            {
                return false;
            }

            // Title 是跨机器的对齐键，比较时忽略大小写与首尾空白
            return string.Equals((a.Title ?? string.Empty).Trim(),
                (b.Title ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Playnite 的一条存档路径 → 我们的 <see cref="SavePathSpec"/>。</summary>
        private static SavePathSpec FromPlayniteSavePath(SavePath path)
        {
            var title = path.Title;
            if (string.IsNullOrWhiteSpace(title))
            {
                title = path.Name;
            }

            var raw = (path.Path ?? string.Empty).Trim();
            if (raw.Length == 0)
            {
                return null;
            }

            // Playnite 文档里的 {WinLocalAppData} 之类，以及 %LOCALAPPDATA% 这类写法，
            // 都翻成我们的 token（实测 AutoAdaptivePathHelper 在本版 SDK 里是恒等函数，
            // 没有可对接的展开逻辑，所以只能按文档里的名字映射）
            var mapped = path.AutoAdaptive ? SavePathAdapter.FromPlaynite(raw) : raw;

            if (string.IsNullOrWhiteSpace(title))
            {
                // 没有名字就给一个稳定的：取路径最后一段
                title = Path.GetFileName(mapped.TrimEnd('\\', '/'));
                if (string.IsNullOrWhiteSpace(title))
                {
                    title = mapped;
                }
            }

            return new SavePathSpec
            {
                Title = title.Trim(),
                Type = path.GameSaveType == GameSaveType.File
                    ? SaveElementType.File
                    : SaveElementType.Directory,
                Path = mapped,
                AutoAdaptive = path.AutoAdaptive,
                Enabled = true,
                Source = SaveSource.Playnite
            };
        }

        // ==================================================================
        //  引擎 / 清单
        // ==================================================================

        public static string GameKeyFor(Game game)
        {
            if (game == null)
            {
                throw new ArgumentNullException("game");
            }

            return SaveSyncEngine.SafeGameKey(game.Id.ToString());
        }

        /// <summary>实例版，读起来短一点。</summary>
        public string GetGameKey(Game game)
        {
            return GameKeyFor(game);
        }

        /// <summary>造一个引擎。每次操作造一个新的：计数与缓存都是「一次操作一份」。</summary>
        public SaveSyncEngine CreateEngine(CancellationToken token, ISaveSyncReporter reporter)
        {
            return new SaveSyncEngine(service.CreateClient(), SaveDataPath,
                service.BuildSyncOptions(), reporter, token);
        }

        /// <summary>
        /// 取（或按需新建）某个游戏的远端清单。
        /// 新建时把合并后的路径定义写进去 —— 这样快照与路径定义能一起走。
        /// </summary>
        public SaveGameManifest GetManifest(SaveSyncEngine engine, Game game, bool create,
            out List<string> notes)
        {
            var key = GameKeyFor(game);
            var manifest = engine.LoadManifest(key);
            if (manifest == null && !create)
            {
                notes = new List<string>();
                return null;
            }

            if (manifest == null)
            {
                manifest = SaveGameManifest.NewFor(game.Id.ToString(), game.Name);
            }

            manifest.GameName = game.Name;

            // 本地这份是权威：远端记的路径只是「上一次长什么样」，
            // 用户可能刚改过，所以每次都以本地合并结果为准。
            manifest.Paths = MergedPaths(game, out notes);

            return manifest;
        }

        /// <summary>这个游戏在远端有没有存档（决定界面上是「上传」还是「要看远端」）。</summary>
        public bool HasRemote(Game game)
        {
            try
            {
                var client = service.CreateClient(Math.Min(Settings.TimeoutSeconds, 15));
                return client.Exists(SaveSyncEngine.RemoteRoot + "/" + GameKeyFor(game)
                                     + "/manifest.json");
            }
            catch (Exception ex)
            {
                VaultLog.Warn("探测远端存档失败：" + game.Name + "：" + ex.Message);
                return false;
            }
        }

        // ==================================================================
        //  触发策略
        // ==================================================================

        /// <summary>这个游戏当前该不该在「退出后自动上传」。</summary>
        public bool ShouldUploadOnStop(Game game)
        {
            if (game == null || !Settings.SaveSyncEnabled)
            {
                return false;
            }

            if (Settings.SaveTrigger == SaveTriggerMode.Manual)
            {
                return false;
            }

            // 没配路径就没什么可传的，别白跑一趟
            List<string> notes;
            return MergedPaths(game, out notes).Any(s => s.Enabled);
        }

        /// <summary>启动前是否该问「远端有更新要不要拉」。</summary>
        public bool ShouldAskOnStart(Game game)
        {
            return game != null && Settings.SaveSyncEnabled
                   && Settings.SaveTrigger == SaveTriggerMode.UploadOnStopAskOnStart;
        }

        // ==================================================================
        //  会话差分的指纹存档
        // ==================================================================

        /// <summary>某个游戏上一次「游戏启动前」录的指纹存在哪儿。</summary>
        public string FingerprintPath(string gameId)
        {
            return Path.Combine(SaveDataPath, "sniff",
                SaveSyncEngine.SafeGameKey(gameId) + ".json");
        }

        /// <summary>把「启动前」的指纹存下来，等游戏退出后拿来差分。</summary>
        public void SaveFingerprint(string gameId, Dictionary<string, DirectoryFingerprint> map)
        {
            try
            {
                var path = FingerprintPath(gameId);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonConvert.SerializeObject(map),
                    new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                VaultLog.Warn("写会话指纹失败（不影响同步）：" + ex.Message);
            }
        }

        /// <summary>取「启动前」的指纹；没有就返回空（表示这一档这次用不了）。</summary>
        public Dictionary<string, DirectoryFingerprint> LoadFingerprint(string gameId)
        {
            try
            {
                var path = FingerprintPath(gameId);
                if (!File.Exists(path))
                {
                    return new Dictionary<string, DirectoryFingerprint>(StringComparer.OrdinalIgnoreCase);
                }

                var loaded = JsonConvert.DeserializeObject<Dictionary<string, DirectoryFingerprint>>(
                    File.ReadAllText(path, Encoding.UTF8));
                return loaded ?? new Dictionary<string, DirectoryFingerprint>(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                VaultLog.Warn("读会话指纹失败（当作没有）：" + ex.Message);
                return new Dictionary<string, DirectoryFingerprint>(StringComparer.OrdinalIgnoreCase);
            }
        }

        /// <summary>用完就删：一份指纹只对「紧接着的那一次会话」有意义。</summary>
        public void ClearFingerprint(string gameId)
        {
            try
            {
                var path = FingerprintPath(gameId);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                VaultLog.Warn("清会话指纹失败（跳过）：" + ex.Message);
            }
        }

        // ==================================================================
        //  会话嗅探的「收获」
        // ==================================================================

        /// <summary>
        /// 上一次会话差分出来的候选存哪儿。存下来而不是用完就丢，是因为
        /// 「游戏退出后在弹窗里当场决定」不现实 —— 用户刚玩完，人可能已经离开屏幕了。
        /// 存着，等他下次打开存档管理时还在。
        /// </summary>
        public string DetectedPath(string gameId)
        {
            return Path.Combine(SaveDataPath, "sniff",
                SaveSyncEngine.SafeGameKey(gameId) + "-detected.json");
        }

        public void SaveDetected(string gameId, List<SaveSniffCandidate> candidates)
        {
            try
            {
                var path = DetectedPath(gameId);
                if (candidates == null || candidates.Count == 0)
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }

                    return;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonConvert.SerializeObject(candidates),
                    new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                VaultLog.Warn("写会话嗅探结果失败（不影响同步）：" + ex.Message);
            }
        }

        public List<SaveSniffCandidate> LoadDetected(string gameId)
        {
            try
            {
                var path = DetectedPath(gameId);
                if (!File.Exists(path))
                {
                    return new List<SaveSniffCandidate>();
                }

                return JsonConvert.DeserializeObject<List<SaveSniffCandidate>>(
                           File.ReadAllText(path, Encoding.UTF8)) ?? new List<SaveSniffCandidate>();
            }
            catch (Exception ex)
            {
                VaultLog.Warn("读会话嗅探结果失败（当作没有）：" + ex.Message);
                return new List<SaveSniffCandidate>();
            }
        }

        // ==================================================================
        //  存储模型
        // ==================================================================

        /// <summary>插件这份路径定义的落盘格式。</summary>
        public class SavePathStore
        {
            public const string StoreKind = "playnite-vault-save-paths";

            public string Kind { get; set; } = StoreKind;

            public int Schema { get; set; } = 1;

            /// <summary>游戏 Id → 路径定义。</summary>
            public Dictionary<string, List<SavePathSpec>> Games { get; set; }
                = new Dictionary<string, List<SavePathSpec>>(StringComparer.OrdinalIgnoreCase);

            /// <summary>读到了不是我们写的文件：本次只读，绝不写回（免得把别人的东西盖掉）。</summary>
            [JsonIgnore]
            public bool ReadOnly { get; set; }
        }
    }
}
