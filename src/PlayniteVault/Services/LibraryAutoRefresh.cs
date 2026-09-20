using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using PlayniteVault.Models;

namespace PlayniteVault.Services
{
    /// <summary>一次自动刷新尝试的结果。</summary>
    public class AutoRefreshOutcome
    {
        public bool Ran { get; set; }              // 真的走完了检查
        public bool Changed { get; set; }          // 远端索引和上次应用的不一样
        public bool WroteLibrary { get; set; }     // 真的动了 Playnite 的库
        public string Source { get; set; }
        public string Error { get; set; }
        public string SkipReason { get; set; }
        public int Added { get; set; }
        public int Updated { get; set; }
        public int Removed { get; set; }
        public int Total { get; set; }

        public string Describe()
        {
            if (SkipReason != null)
            {
                return "已跳过：" + SkipReason;
            }
            if (Error != null)
            {
                return "失败：" + Error;
            }
            if (!Changed)
            {
                return "索引无变化，未改动本地库";
            }
            return string.Format("索引有变化 → 新增 {0}、更新 {1}、移除 {2}（远端共 {3} 条）",
                Added, Updated, Removed, Total);
        }
    }

    /// <summary>
    /// 定时把远端索引同步进 Playnite 的库。
    ///
    /// 三条设计约束（对应「尽量降低额外性能开销」）：
    /// 1) **每次只拉一个 index.json**（几十 KB 的一次 GET），不碰 manifest、不碰区块；
    /// 2) **先比指纹再动库**：指纹一样就完全跳过，一次数据库写入都不做
    ///    —— Playnite 的库写入会触发 UI 重排，这才是真正贵的地方；
    /// 3) 只在**远端**索引可用时才写库。三级降级拿到的缓存索引内容必然与已应用的一致，
    ///    拿它去覆盖只会白跑一趟。
    /// </summary>
    public class LibraryAutoRefresh : IDisposable
    {
        /// <summary>间隔的合法范围（分钟）。</summary>
        public const int MinMinutes = 5;
        public const int MaxMinutes = 24 * 60;

        /// <summary>应用阶段的实现（由插件提供：要动 Playnite 的数据库）。</summary>
        public delegate AutoRefreshOutcome ApplyDelegate(RepositoryIndex index, string source);

        /// <summary>把动作丢到 UI 线程执行。数据库写入必须在 UI 线程上做。</summary>
        public delegate void DispatchDelegate(Action action);

        private readonly VaultService service;
        private readonly ApplyDelegate apply;
        private readonly DispatchDelegate dispatch;

        private Timer timer;
        private int busy;                       // Interlocked 互斥：上一次还没跑完就别插队
        private volatile bool gameRunning;
        private volatile bool transferBusy;
        private bool disposed;

        /// <summary>刷新完成后的回调（用于弹通知）。参数：结果、是否自动触发。</summary>
        public event Action<AutoRefreshOutcome, bool> Completed;

        public LibraryAutoRefresh(VaultService service, ApplyDelegate apply, DispatchDelegate dispatch)
        {
            this.service = service;
            this.apply = apply;
            this.dispatch = dispatch;
        }

        public bool IsGameRunning
        {
            get { return gameRunning; }
        }

        public static int Clamp(int minutes)
        {
            if (minutes < MinMinutes) return MinMinutes;
            if (minutes > MaxMinutes) return MaxMinutes;
            return minutes;
        }

        /// <summary>按当前设置重新定时。设置改了就调用它。</summary>
        public void Rearm()
        {
            Stop();

            var settings = service.Settings;
            if (settings == null || !settings.AutoRefreshEnabled || disposed)
            {
                VaultLog.Info("自动刷新：未启用");
                return;
            }

            var minutes = Clamp(settings.AutoRefreshMinutes);
            timer = new Timer(OnTick, null, TimeSpan.FromSeconds(30), Timeout.InfiniteTimeSpan);
            VaultLog.Info(string.Format("自动刷新：已启用，间隔 {0} 分钟（{1} 秒后先跑一次）",
                minutes, 30));
        }

        public void Stop()
        {
            var old = timer;
            timer = null;
            if (old != null)
            {
                try { old.Dispose(); }
                catch { }
            }
        }

        /// <summary>开始 / 结束游戏时调用。运行游戏期间自动暂停。</summary>
        public void SetGameRunning(bool running)
        {
            var was = gameRunning;
            gameRunning = running;

            if (!running && was)
            {
                // 游戏刚结束：如果这一轮间隔已经过掉了，补一次
                var state = service.State;
                var minutes = Clamp(service.Settings.AutoRefreshMinutes);
                var due = !state.LastIndexCheckUtc.HasValue
                          || DateTime.UtcNow - state.LastIndexCheckUtc.Value > TimeSpan.FromMinutes(minutes);
                if (due && service.Settings.AutoRefreshEnabled)
                {
                    VaultLog.Info("自动刷新：游戏已结束，补一次检查");
                    TriggerNow(false);
                }
            }
        }

        /// <summary>安装/归档这类传输进行中时调用，避免和它们抢 NAS 带宽。</summary>
        public void SetTransferBusy(bool busy)
        {
            transferBusy = busy;
        }

        /// <summary>立即跑一次。<paramref name="force"/> = true 时忽略指纹，强制写库。</summary>
        public void TriggerNow(bool force)
        {
            if (disposed)
            {
                return;
            }
            Run(force, false);
        }

        private void OnTick(object state)
        {
            Run(false, true);
        }

        private void Run(bool force, bool automatic)
        {
            // 上一次没跑完就直接跳过这一拍，绝不排队
            if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
            {
                VaultLog.Info("自动刷新：上一轮还没结束，跳过这一次");
                return;
            }

            var outcome = new AutoRefreshOutcome();
            try
            {
                outcome = Execute(force);
            }
            catch (Exception ex)
            {
                VaultLog.Error("自动刷新异常", ex);
                outcome.Error = ex.Message;
            }
            finally
            {
                Interlocked.Exchange(ref busy, 0);
                ScheduleNext(outcome);
            }

            var handler = Completed;
            if (handler != null)
            {
                try { handler(outcome, automatic); }
                catch (Exception ex) { VaultLog.Warn("自动刷新回调异常：" + ex.Message); }
            }
        }

        private AutoRefreshOutcome Execute(bool force)
        {
            var outcome = new AutoRefreshOutcome();

            var settings = service.Settings;
            if (!settings.IsConfigured)
            {
                outcome.SkipReason = "还没配置 WebDAV 地址";
                return outcome;
            }
            if (gameRunning)
            {
                outcome.SkipReason = "正在运行游戏";
                return outcome;
            }
            if (transferBusy)
            {
                outcome.SkipReason = "正在安装/归档";
                return outcome;
            }

            outcome.Ran = true;

            string source;
            string error;
            var index = service.GetIndex(out source, out error);
            outcome.Source = source;
            service.State.LastIndexCheckUtc = DateTime.UtcNow;

            if (index == null || index.Apps == null || index.Apps.Count == 0)
            {
                outcome.Error = error ?? "远端索引为空";
                Fail(outcome.Error);
                return outcome;
            }

            if (source != "remote")
            {
                // 缓存 / 本地兜底：内容必然已经应用过，动库纯属浪费
                outcome.SkipReason = "NAS 不可达，用的是" +
                    (source == "cache" ? "本地缓存索引" : "本地安装记录") + "（" + (error ?? "未知原因") + "）";
                service.State.ConsecutiveRefreshFailures++;
                service.SaveState();
                return outcome;
            }

            var fingerprint = Fingerprint(index);

            if (!force && string.Equals(fingerprint, service.State.AppliedIndexHash, StringComparison.Ordinal))
            {
                outcome.Changed = false;
                outcome.Total = index.Apps.Count;
                service.State.ConsecutiveRefreshFailures = 0;
                service.SaveState();
                VaultLog.Info("自动刷新：索引无变化（" + index.Apps.Count + " 条），未改动本地库");
                return outcome;
            }

            outcome.Changed = true;
            outcome.Total = index.Apps.Count;

            AutoRefreshOutcome fromApply = null;
            Action write = () => { fromApply = apply(index, source); };
            if (dispatch != null)
            {
                dispatch(write);
            }
            else
            {
                write();
            }

            if (fromApply != null)
            {
                outcome.Added = fromApply.Added;
                outcome.Updated = fromApply.Updated;
                outcome.Removed = fromApply.Removed;
                outcome.WroteLibrary = true;
            }

            service.State.AppliedIndexHash = fingerprint;
            service.State.LastLibraryWriteUtc = DateTime.UtcNow;
            service.State.ConsecutiveRefreshFailures = 0;
            service.SaveState();

            VaultLog.Info("自动刷新：" + outcome.Describe());
            return outcome;
        }

        private void Fail(string message)
        {
            service.State.ConsecutiveRefreshFailures++;
            service.SaveState();
            VaultLog.Warn("自动刷新失败（连续 " + service.State.ConsecutiveRefreshFailures + " 次）：" + message);
        }

        /// <summary>
        /// 下一拍等多久。连续失败时指数放宽（最多 8 倍）——
        /// NAS 不在的时候每 5 分钟去敲一次、每次白等 10 秒超时，毫无意义还刷日志。
        /// </summary>
        private void ScheduleNext(AutoRefreshOutcome outcome)
        {
            var settings = service.Settings;
            if (settings == null || !settings.AutoRefreshEnabled || disposed || timer == null)
            {
                return;
            }

            var minutes = Clamp(settings.AutoRefreshMinutes);
            var failures = Math.Min(service.State.ConsecutiveRefreshFailures, 3);
            var factor = 1 << failures;   // 1 / 2 / 4 / 8
            var delay = TimeSpan.FromMinutes(minutes * factor);

            try
            {
                timer.Change(delay, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
                // 正在 Stop()，忽略
            }
        }

        public void Dispose()
        {
            disposed = true;
            Stop();
        }

        // ---------- 指纹 ----------

        /// <summary>
        /// 远端索引的指纹。**刻意不含 UpdatedAt** —— 那个字段只要 index.json 被重写就会变，
        /// 哪怕应用列表一个字节都没动；按它判断会导致每次归档都白刷一次库。
        /// 这里只取「会影响库内容」的字段。
        /// </summary>
        public static string Fingerprint(RepositoryIndex index)
        {
            if (index == null || index.Apps == null)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            builder.Append("schema=").Append(index.Schema).Append('\n');

            foreach (var app in index.Apps.OrderBy(a => a.Id, StringComparer.OrdinalIgnoreCase))
            {
                builder.Append(app.Id).Append('|')
                       .Append(app.Name).Append('|')
                       .Append(app.Version).Append('|')
                       .Append(app.TotalBytes).Append('|')
                       .Append(app.FileCount).Append('|')
                       .Append(app.ChunkCount).Append('|')
                       .Append(app.PartCount).Append('|')
                       .Append(app.LaunchExe).Append('|')
                       .Append(app.Metadata == null ? null : app.Metadata.InstallDirName)
                       .Append('\n');
            }

            using (var sha1 = SHA1.Create())
            {
                var hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
                var text = new StringBuilder(hash.Length * 2);
                foreach (var b in hash)
                {
                    text.Append(b.ToString("x2"));
                }
                return text.ToString();
            }
        }
    }
}
