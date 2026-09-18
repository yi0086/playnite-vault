using System;
using System.Diagnostics;
using VaultDemo.Models;
using VaultDemo.Services;

namespace VaultDemo.Sync
{
    /// <summary>
    /// 节流进度上报。
    ///
    /// 并发下载 / 上传时会有 6~8 条工作线程同时回调，所以这里必须加锁：
    /// 之前的实现直接读写 lastReportMs，多线程下会「一次放行好几个、然后又长时间不放」，
    /// 上报间隔不均匀，界面看起来就是一顿一顿的。
    ///
    /// 速度也不再用「从启动到现在的累计平均」——那种算法在开头会剧烈爬升，
    /// 中途一旦卡顿就会长时间回不来，读起来非常迟钝。
    /// 改成「按上报间隔算瞬时速度，再做指数平滑」：跟手，又不会跳。
    /// </summary>
    public class ThrottledReporter
    {
        /// <summary>两次上报之间的最小间隔（毫秒）。ProgressHeartbeat 复用同一节拍。</summary>
        public const int IntervalMs = 120;

        /// <summary>指数平滑系数：越大越跟手，越小越平滑。</summary>
        private const double Smoothing = 0.35;

        /// <summary>两次采样之间至少间隔这么久才重新估算速度，避免除出噪声。</summary>
        private const long MinSampleMs = 80;

        private readonly Action<SyncProgress> callback;
        private readonly object gate = new object();
        private readonly Stopwatch watch = Stopwatch.StartNew();

        private long lastReportMs;
        private long lastSampleMs;
        private long lastBytes;
        private double smoothedSpeed;
        private bool hasSample;

        public ThrottledReporter(Action<SyncProgress> callback)
        {
            this.callback = callback;
        }

        public void Report(SyncProgress progress, bool force)
        {
            if (callback == null || progress == null)
            {
                return;
            }

            // 整个「判断 + 采样 + 回调」放在锁里：多线程下才是均匀的节拍，
            // 而不是好几条线程同时挤进来。
            lock (gate)
            {
                var now = watch.ElapsedMilliseconds;

                if (!force && now - lastReportMs < IntervalMs)
                {
                    return;
                }

                if (!hasSample)
                {
                    // 首帧只建立基线；没有参考点算不出速度，先留空
                    hasSample = true;
                    lastSampleMs = now;
                    lastBytes = progress.BytesDone;
                }
                else
                {
                    var dt = now - lastSampleMs;
                    if (dt >= MinSampleMs)
                    {
                        var delta = progress.BytesDone - lastBytes;
                        if (delta < 0)
                        {
                            // BytesDone 本该单调递增；真出现倒退说明上游有 bug，
                            // 这里按 0 处理，免得给用户显示负速度
                            delta = 0;
                        }

                        var instant = delta * 1000.0 / dt;
                        smoothedSpeed = smoothedSpeed <= 0
                            ? instant
                            : smoothedSpeed * (1 - Smoothing) + instant * Smoothing;

                        lastSampleMs = now;
                        lastBytes = progress.BytesDone;
                    }

                    progress.BytesPerSecond = smoothedSpeed;
                }

                progress.IsForced = force;
                lastReportMs = now;

                try
                {
                    callback(progress);
                }
                catch (Exception ex)
                {
                    VaultLog.Error("进度回调异常", ex);
                }
            }
        }
    }
}
