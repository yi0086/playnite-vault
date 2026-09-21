using System;
using System.Collections.Generic;
using System.Threading;
using PlayniteVault.Models;

namespace PlayniteVault.Services
{
    /// <summary>队列里这一条是什么操作。</summary>
    public enum TransferKind
    {
        /// <summary>本地 → NAS（归档）。</summary>
        Upload = 0,

        /// <summary>NAS → 本地（安装 / 取回）。</summary>
        Download = 1,

        /// <summary>主题镜像同步。</summary>
        ThemeSync = 2
    }

    public enum TransferState
    {
        Queued = 0,
        Running = 1,
        Done = 2,
        Failed = 3,
        Canceled = 4
    }

    /// <summary>
    /// 「下载管理」里的一条任务。
    ///
    /// <para>为什么需要它：原来的归档/安装走的是 Playnite 的
    /// <c>ActivateGlobalProgress</c> —— 那是个**模态**对话框，任务跑着的时候整个 Playnite
    /// 都被按住。改成队列之后，界面线程只负责画画，真正的传输在后台线程上跑，
    /// 用户可以去干别的，进度落在页面底部那条聚合进度条上。</para>
    /// </summary>
    public sealed class TransferTask
    {
        internal readonly Action<TransferTask, CancellationToken> Body;

        private readonly CancellationTokenSource cancel = new CancellationTokenSource();

        internal TransferTask(TransferKind kind, string title,
            Action<TransferTask, CancellationToken> body)
        {
            Kind = kind;
            Title = title;
            Body = body;
            EnqueuedAt = DateTime.Now;
        }

        public string Id { get; private set; } = Guid.NewGuid().ToString("n");

        public TransferKind Kind { get; private set; }

        /// <summary>标题行，例如「归档 节奏医生」。</summary>
        public string Title { get; private set; }

        /// <summary>副标题 / 当前阶段，由任务体自己写。</summary>
        public string Detail { get; set; } = string.Empty;

        public TransferState State { get; internal set; } = TransferState.Queued;

        public long BytesDone { get; internal set; }

        public long BytesTotal { get; internal set; }

        public string Error { get; internal set; }

        public DateTime EnqueuedAt { get; private set; }

        public DateTime? FinishedAt { get; internal set; }

        public CancellationToken Token
        {
            get { return cancel.Token; }
        }

        public bool IsFinished
        {
            get
            {
                return State == TransferState.Done
                    || State == TransferState.Failed
                    || State == TransferState.Canceled;
            }
        }

        /// <summary>0~1；总量未知时返回 0（界面会切成「不确定」样式）。</summary>
        public double Fraction
        {
            get
            {
                if (BytesTotal <= 0)
                {
                    return 0;
                }

                var f = (double)BytesDone / BytesTotal;
                return f < 0 ? 0 : (f > 1 ? 1 : f);
            }
        }

        /// <summary>用户点了取消。任务体要自己检查 <see cref="Token"/> 才会真的停。</summary>
        public void RequestCancel()
        {
            try
            {
                cancel.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public string KindText
        {
            get
            {
                switch (Kind)
                {
                    case TransferKind.Upload:
                        return "上传";
                    case TransferKind.Download:
                        return "下载";
                    default:
                        return "主题";
                }
            }
        }

        public string StateText
        {
            get
            {
                switch (State)
                {
                    case TransferState.Queued:
                        return "排队中";
                    case TransferState.Running:
                        return "进行中";
                    case TransferState.Done:
                        return "已完成";
                    case TransferState.Failed:
                        return "失败";
                    default:
                        return "已取消";
                }
            }
        }

        /// <summary>一行的进度描述：「62% · 1.2 GB / 2.0 GB · 88 MB/s」。</summary>
        public string DescribeProgress()
        {
            if (State == TransferState.Queued)
            {
                return "排队中";
            }

            if (IsFinished)
            {
                return StateText + (string.IsNullOrEmpty(Error) ? "" : "：" + Error);
            }

            if (BytesTotal <= 0)
            {
                return string.IsNullOrEmpty(Detail) ? "正在准备…" : Detail;
            }

            var head = ((int)Math.Round(Fraction * 100)) + "%";
            var size = SyncProgress.FormatSize(BytesDone) + " / " + SyncProgress.FormatSize(BytesTotal);
            var line = head + " · " + size;
            if (!string.IsNullOrEmpty(Detail))
            {
                line += " · " + Detail;
            }

            return line;
        }
    }

    /// <summary>
    /// 把同步引擎的 <see cref="SyncProgress"/> 写进队列任务。
    ///
    /// <para>和 <c>ProgressSink</c>（写 Playnite 全局进度窗的那版）的分工：
    /// 那个要拼富文本、算速度、还要照顾进度窗的重绘开销；这里只往任务对象上写
    /// 两个 long 和一个短字符串，因为读它的是**界面自己的轮询定时器**，
    /// 写得再勤也只是覆盖字段，没有重绘成本。</para>
    /// </summary>
    public sealed class TaskProgressSink
    {
        private readonly TransferTask task;
        private readonly System.Diagnostics.Stopwatch watch =
            System.Diagnostics.Stopwatch.StartNew();

        private long lastDetailMs;
        private string lastDetail;

        public TaskProgressSink(TransferTask task)
        {
            this.task = task;
        }

        public void Apply(SyncProgress p)
        {
            if (task == null || p == null)
            {
                return;
            }

            // 字节数每次都写：底部进度条的顺滑全靠它
            task.BytesDone = p.BytesDone < 0 ? 0 : p.BytesDone;
            if (p.BytesTotal > 0)
            {
                task.BytesTotal = p.BytesTotal;
            }

            // 文本降到约 3 次/秒，而且内容没变就不写
            var now = watch.ElapsedMilliseconds;
            if (!p.IsForced && now - lastDetailMs < 320)
            {
                return;
            }

            var text = p.DescribeRich();
            if (string.IsNullOrEmpty(text) || text == lastDetail)
            {
                return;
            }

            lastDetailMs = now;
            lastDetail = text;
            task.Detail = text;
        }
    }

    /// <summary>队列的聚合视图，给底部那条进度条用。</summary>
    public sealed class TransferAggregate
    {
        public int Active;
        public int Finished;
        public int Failed;

        /// <summary>0~1 的「预估总进度」。所有在跑/排队的任务按体积加权；体积未知的按等权。</summary>
        public double Progress;

        public string Caption = string.Empty;

        public bool Indeterminate;

        public bool HasAny
        {
            get { return Active > 0 || Finished > 0; }
        }
    }

    /// <summary>
    /// 串行执行的传输队列。
    ///
    /// <para><b>为什么串行</b>：传输的瓶颈在 NAS 的写入侧（实测下载 88 MB/s、上传只有
    /// 20~40 MB/s），两条任务并发只会互相抢带宽、还让两条进度条都不准。
    /// 串行 + 队列，用户看到的就是「一条一条来」，每条的百分比是真实速率。</para>
    ///
    /// <para><b>通知策略</b>：这个类只在**结构变化**（入队 / 结束 / 清除）时发
    /// <see cref="Changed"/>；字节数每 100 毫秒都在变，让界面用它自己的定时器去轮询
    /// <see cref="Snapshot"/> —— 反过来把每次字节变化都推给界面，会把 UI 线程淹掉。</para>
    /// </summary>
    public sealed class TransferQueue
    {
        private readonly List<TransferTask> tasks = new List<TransferTask>();
        private readonly object gate = new object();
        private readonly Action<Action> marshal;

        private Thread worker;

        /// <param name="marshalToUi">
        /// 把回调送回界面线程。**必须传**：<see cref="Changed"/> 的订阅者是 WPF 控件，
        /// 在后台线程上碰它们会直接抛跨线程访问异常。
        /// </param>
        public TransferQueue(Action<Action> marshalToUi)
        {
            marshal = marshalToUi ?? (a => a());
        }

        /// <summary>结构变化（入队 / 完成 / 清除）。</summary>
        public event Action Changed;

        public TransferTask[] Snapshot()
        {
            lock (gate)
            {
                return tasks.ToArray();
            }
        }

        /// <summary>把一条任务排到队尾。返回的对象可用来查进度/取消。</summary>
        public TransferTask Enqueue(TransferKind kind, string title,
            Action<TransferTask, CancellationToken> body)
        {
            var task = new TransferTask(kind, title, body);
            lock (gate)
            {
                tasks.Add(task);
            }

            VaultLog.Info("传输队列：入队 " + task.KindText + " · " + title);
            Raise();
            StartWorker();
            return task;
        }

        public void CancelAll()
        {
            foreach (var task in Snapshot())
            {
                if (!task.IsFinished)
                {
                    task.RequestCancel();
                }
            }
        }

        /// <summary>清掉已经结束的条目，返回清掉几条。</summary>
        public int ClearFinished()
        {
            int removed;
            lock (gate)
            {
                removed = tasks.RemoveAll(t => t.IsFinished);
            }

            if (removed > 0)
            {
                Raise();
            }

            return removed;
        }

        public bool HasActive
        {
            get
            {
                lock (gate)
                {
                    foreach (var task in tasks)
                    {
                        if (!task.IsFinished)
                        {
                            return true;
                        }
                    }
                }

                return false;
            }
        }

        public TransferAggregate Aggregate()
        {
            var agg = new TransferAggregate();
            TransferTask current = null;
            double weightSum = 0;
            double weightDone = 0;
            var queued = 0;

            foreach (var task in Snapshot())
            {
                if (task.IsFinished)
                {
                    agg.Finished++;
                    if (task.State == TransferState.Failed)
                    {
                        agg.Failed++;
                    }

                    continue;
                }

                agg.Active++;
                if (task.State == TransferState.Running)
                {
                    if (current == null)
                    {
                        current = task;
                    }
                }
                else
                {
                    queued++;
                }

                // 体积已知的按体积算权重（大文件就该占更大一格），
                // 还没量出体积的按 1 算 —— 否则它会让整条进度条在开始前就显示 100%。
                var weight = task.BytesTotal > 0 ? (double)task.BytesTotal : 1.0;
                weightSum += weight;
                weightDone += weight * task.Fraction;
            }

            if (weightSum > 0)
            {
                agg.Progress = weightDone / weightSum;
            }

            agg.Indeterminate = agg.Active > 0 && weightSum <= 0;

            if (current != null)
            {
                agg.Caption = current.Title + " · " + current.DescribeProgress();
                if (queued > 0)
                {
                    agg.Caption += "　（还有 " + queued + " 个排队）";
                }
            }
            else if (queued > 0)
            {
                agg.Caption = queued + " 个任务排队中";
            }
            else if (agg.Active > 0)
            {
                agg.Caption = "正在准备…";
            }
            else if (agg.Finished > 0)
            {
                agg.Caption = agg.Failed > 0
                    ? agg.Finished + " 个任务已结束，其中 " + agg.Failed + " 个失败"
                    : "全部任务已完成（" + agg.Finished + " 个）";
            }

            return agg;
        }

        // ---------------------------------------------------------------- 工作线程

        private void StartWorker()
        {
            lock (gate)
            {
                if (worker != null)
                {
                    return;
                }

                worker = new Thread(Loop)
                {
                    IsBackground = true,
                    Name = "VaultTransferQueue"
                };
                worker.Start();
            }
        }

        private void Loop()
        {
            while (true)
            {
                TransferTask next = null;
                lock (gate)
                {
                    foreach (var task in tasks)
                    {
                        if (task.State == TransferState.Queued)
                        {
                            next = task;
                            break;
                        }
                    }

                    if (next == null)
                    {
                        // 与 Enqueue 用同一把锁：这里置空之后，Enqueue 的 StartWorker
                        // 一定会看到一个空 worker 并重新起线程，不会漏任务。
                        worker = null;
                        return;
                    }

                    next.State = TransferState.Running;
                }

                Raise();

                try
                {
                    next.Body(next, next.Token);
                    next.State = next.Token.IsCancellationRequested
                        ? TransferState.Canceled
                        : TransferState.Done;
                }
                catch (OperationCanceledException)
                {
                    next.State = TransferState.Canceled;
                }
                catch (Exception ex)
                {
                    next.State = TransferState.Failed;
                    next.Error = ex.Message;
                    VaultLog.Error("传输任务失败：" + next.Title, ex);
                }
                finally
                {
                    next.FinishedAt = DateTime.Now;
                    if (next.BytesTotal > 0)
                    {
                        next.BytesDone = next.BytesTotal;
                    }

                    Raise();
                }
            }
        }

        private void Raise()
        {
            var handler = Changed;
            if (handler == null)
            {
                return;
            }

            try
            {
                marshal(() => handler());
            }
            catch (Exception ex)
            {
                // 界面可能正在销毁；通知丢了不影响任务本身，但别把工作线程带崩。
                VaultLog.Warn("传输队列：通知界面失败（" + ex.Message + "）");
            }
        }
    }
}
