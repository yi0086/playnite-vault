using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;

namespace PlayniteVault.Services
{
    /// <summary>
    /// 插件的**运行时状态**（与用户配置分开放）。
    ///
    /// 为什么不塞进 settings.json：设置页是「打快照 → 改副本 → 落盘」的模型
    /// （<see cref="UI.VaultSettingsViewModel"/> 的 BeginEdit/EndEdit）。
    /// 自动刷新在后台线程上写状态，如果写的是同一份对象，
    /// 用户此时点开设置窗再保存，就会把刚写进去的状态覆盖回旧值。
    /// 分开两个文件，两边互不干扰。
    /// </summary>
    public class VaultRuntimeState
    {
        /// <summary>最近一次「已应用到本地库」的远端索引指纹。用来判断索引有没有变。</summary>
        public string AppliedIndexHash { get; set; } = string.Empty;

        /// <summary>最近一次成功拉到远端索引的时间（UTC）。</summary>
        public DateTime? LastIndexCheckUtc { get; set; }

        /// <summary>最近一次真正写库（索引有变化）的时间（UTC）。</summary>
        public DateTime? LastLibraryWriteUtc { get; set; }

        /// <summary>最近一次检查插件更新的时间（UTC）。</summary>
        public DateTime? LastUpdateCheckUtc { get; set; }

        /// <summary>最近一次检查时看到的远端最新版本（诊断用）。</summary>
        public string LastSeenVersion { get; set; } = string.Empty;

        /// <summary>最近一次实际使用的下载镜像：github / gitee，空 = 还没测过。</summary>
        public string LastMirror { get; set; } = string.Empty;

        /// <summary>上次成功探测到的镜像延迟（毫秒），诊断用。</summary>
        public int LastMirrorLatencyMs { get; set; }

        /// <summary>
        /// 自动刷新连续失败次数。连续失败时把间隔指数放宽，
        /// 免得 NAS 不可达时每 5 分钟就去敲一次（白等超时、还刷日志）。
        /// </summary>
        public int ConsecutiveRefreshFailures { get; set; }

        public static string FileName
        {
            get { return "state.json"; }
        }
    }

    /// <summary>state.json 的读写。任何异常都不外抛 —— 状态丢了顶多多刷一次库。</summary>
    public static class VaultStateStore
    {
        private static readonly JsonSerializerSettings Settings = new JsonSerializerSettings
        {
            Formatting = Formatting.Indented,
            DateTimeZoneHandling = DateTimeZoneHandling.Utc,
            NullValueHandling = NullValueHandling.Ignore
        };

        private static readonly object Gate = new object();

        public static VaultRuntimeState Load(string dataPath)
        {
            lock (Gate)
            {
                try
                {
                    var path = Path.Combine(dataPath, VaultRuntimeState.FileName);
                    if (File.Exists(path))
                    {
                        var state = JsonConvert.DeserializeObject<VaultRuntimeState>(
                            File.ReadAllText(path, Encoding.UTF8));
                        if (state != null)
                        {
                            return state;
                        }
                    }
                }
                catch (Exception ex)
                {
                    VaultLog.Warn("读取 state.json 失败，用默认值：" + ex.Message);
                }

                return new VaultRuntimeState();
            }
        }

        public static void Save(string dataPath, VaultRuntimeState state)
        {
            if (state == null)
            {
                return;
            }

            lock (Gate)
            {
                try
                {
                    var path = Path.Combine(dataPath, VaultRuntimeState.FileName);
                    File.WriteAllText(path,
                        JsonConvert.SerializeObject(state, Settings), new UTF8Encoding(false));
                }
                catch (Exception ex)
                {
                    VaultLog.Warn("写 state.json 失败：" + ex.Message);
                }
            }
        }
    }
}
