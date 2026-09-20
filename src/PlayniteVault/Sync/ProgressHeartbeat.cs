using System;
using System.Threading;
using PlayniteVault.Models;
using PlayniteVault.Services;

namespace PlayniteVault.Sync
{
    /// <summary>
    /// 进度心跳：按固定节拍补推进度，让进度窗永远在动。
    ///
    /// 为什么需要它：
    /// 原本所有进度上报都挂在 I/O 回调上（每写完一个缓冲区才报一次）。
    /// 并发下载 / 上传时，只要所有工作线程同时阻塞，界面就会**整段静止**：
    ///   · 上传尾段，客户端已经把数据塞进 TCP 缓冲，线程全部卡在 GetResponse()
    ///     等服务端把积压落盘，实测这一段能静 1.6 秒没有任何上报；
    ///   · 下载时若服务端某一刻不回数据，客户端 read 阻塞，同样静默。
    /// 表现就是进度条「卡住了」——哪怕底层其实还在传。
    ///
    /// 心跳把「上报节拍」和「I/O 回调」解耦：
    /// 即使一个字节都没动，计时器也会按 IntervalMs 推一帧，
    /// 于是速度读数会如实掉到低速（说明还在传，只是慢），而不是整个界面冻死。
    /// 上报本身仍由 <see cref="ThrottledReporter"/> 统一节流，不会打爆 UI 线程。
    /// </summary>
    public sealed class ProgressHeartbeat : IDisposable
    {
        private readonly ThrottledReporter reporter;
        private readonly SyncProgress progress;
        private readonly Timer timer;
        private readonly object gate = new object();
        private bool disposed;

        public ProgressHeartbeat(ThrottledReporter reporter, SyncProgress progress, int periodMs = 0)
        {
            if (reporter == null || progress == null)
            {
                return;
            }

            this.reporter = reporter;
            this.progress = progress;

            // 周期与节流间隔一致，节拍最均匀；比它短会被节流丢掉、反而出现忽长忽短的抖动。
            var period = periodMs > 0 ? periodMs : ThrottledReporter.IntervalMs;
            timer = new Timer(Tick, null, period, period);
        }

        private void Tick(object state)
        {
            lock (gate)
            {
                if (disposed || reporter == null)
                {
                    return;
                }

                reporter.Report(progress, false);
            }
        }

        public void Dispose()
        {
            if (timer == null)
            {
                return;
            }

            // 先在锁里置位并等掉「正在跑的那一次」，再停表。
            // 否则可能出现：任务已结束、进度窗已关闭，心跳又补推一帧打到已释放的窗口上。
            lock (gate)
            {
                if (disposed)
                {
                    return;
                }
                disposed = true;
            }

            timer.Dispose();
        }
    }
}
