using System;
using System.Diagnostics;
using Playnite.SDK;
using VaultDemo.Models;

namespace VaultDemo.UI
{
    /// <summary>
    /// 把 <see cref="SyncProgress"/> 推进 Playnite 的全局进度窗口。
    ///
    /// 为什么单独抽出来（而不是在每处回调里手写三行）：
    ///
    /// 1. **数值便宜、文本贵**。进度条的 CurrentProgressValue 只是一次 long 赋值，
    ///    每次上报都更新，进度条就最跟手；而富文本要算速度、剩余时间再格式化字符串，
    ///    每次赋值还会让 Playnite 重新排版那段文字。所以文本降到约 4 次/秒，
    ///    并且**内容没变就干脆不写** —— 这是消除「文字闪」最关键的一条。
    /// 2. **阶段切换要立刻可见**。收尾、换阶段这类时刻走 IsForced，不等降频窗口。
    /// 3. **一开始就进确定态**。Playnite 的进度条从 indeterminate 切到 determinate
    ///    会看到一次明显跳变，不如从头就是确定态、从 0% 起步。
    /// 4. 多行文本带一个固定的标题行（哪个游戏 / 哪个操作），数值怎么变都不会丢上下文。
    /// </summary>
    public class ProgressSink
    {
        /// <summary>文本重建的最小间隔（毫秒）。</summary>
        private const int TextIntervalMs = 250;

        private readonly GlobalProgressActionArgs progress;
        private readonly Stopwatch watch = Stopwatch.StartNew();
        private readonly object gate = new object();

        private long lastTextMs;
        private string lastText;

        public ProgressSink(GlobalProgressActionArgs progress, string title)
        {
            this.progress = progress;

            if (progress == null)
            {
                return;
            }

            progress.IsIndeterminate = false;
            progress.ProgressMaxValue = 1;
            progress.CurrentProgressValue = 0;

            if (!string.IsNullOrEmpty(title))
            {
                progress.Text = title;
                lastText = title;
            }
        }

        /// <summary>标题行，会一直保留在数值上方。</summary>
        public string Title { get; set; }

        /// <summary>进度回调入口，直接交给 VaultService / SyncEngine 调用。</summary>
        public void Apply(SyncProgress p)
        {
            if (progress == null || p == null)
            {
                return;
            }

            lock (gate)
            {
                // 数值：每次上报都写，进度条因此最跟手
                progress.IsIndeterminate = false;
                progress.ProgressMaxValue = p.BytesTotal <= 0 ? 1 : p.BytesTotal;
                progress.CurrentProgressValue = p.BytesDone < 0 ? 0 : p.BytesDone;

                var now = watch.ElapsedMilliseconds;
                if (!p.IsForced && now - lastTextMs < TextIntervalMs)
                {
                    return;
                }

                var text = Compose(p);
                if (text == lastText)
                {
                    return;
                }

                lastTextMs = now;
                lastText = text;
                progress.Text = text;
            }
        }

        private string Compose(SyncProgress p)
        {
            var body = p.DescribeRich();
            return string.IsNullOrEmpty(Title) ? body : Title + Environment.NewLine + body;
        }
    }
}
