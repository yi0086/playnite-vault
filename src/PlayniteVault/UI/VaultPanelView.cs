using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PlayniteVault.Models;
using PlayniteVault.Services;

namespace PlayniteVault.UI
{
    /// <summary>
    /// 侧边栏「仓库管家」那一页。
    ///
    /// 为什么是这里：Playnite 的 <c>SidebarItem.Opened</c> 返回的控件会被直接嵌进主窗口当一页用
    /// （<c>SidebarItem.Type = SiderbarItemType.View</c>），所以不用自己开窗口——
    /// 这也让界面自然继承了 Playnite 的主题画刷与缩放。
    ///
    /// <para><b>版面结构</b>（v1.9 起）：</para>
    /// <code>
    /// ┌──────────────┬────────────────────────────────────┐
    /// │ 仓库管家   ● │                                    │
    /// │ Vault@1.9.0  │                                    │
    /// │ 概览         │                                    │
    /// │ 主题         │            内容（滚动）             │
    /// │ 仓库         │                                    │
    /// │ 任务         │                                    │
    /// │ 设置         │                                    │
    /// ├──────────────┴────────────────────────────────────┤
    /// │ ▓▓▓▓▓▓▓░░░  2 个任务 · 归档 节奏医生 62%   [详]    │ ← 只在有任务时出现
    /// └───────────────────────────────────────────────────┘
    /// </code>
    /// 整页顶栏是去掉了的。状态点（●）从「整页右上角」挪到了左栏标题「仓库管家」的右上角 ——
    /// 原来那个位置正好压在 Playnite 的最小化/最大化/关闭按钮上，点刷新会顺手关掉 Playnite。
    ///
    /// <para><b>配色</b>：全部走 <see cref="VaultPalette"/> 的语义令牌，
    /// 本文件里不再出现任何字面颜色值。</para>
    /// </summary>
    public class VaultPanelView : UserControl
    {
        private readonly VaultPlugin plugin;
        private readonly VaultService service;
        private readonly VaultSettingsViewModel settingsVm;
        private readonly string themesRoot;

        // ---- 配色 ----
        private VaultPalette p;

        // ---- 骨架 ----
        private ContentControl body;
        private readonly StackPanel navPanel = new StackPanel();
        private readonly Dictionary<string, Button> navButtons = new Dictionary<string, Button>();
        private readonly Dictionary<string, Func<UIElement>> sectionBuilders =
            new Dictionary<string, Func<UIElement>>();
        private readonly Dictionary<string, UIElement> sectionCache = new Dictionary<string, UIElement>();
        private string currentSection = "overview";
        private bool settingsSavedHooked;

        // ---- 状态点 ----
        private Border healthDot;
        private Border healthRing;
        private Storyboard healthPulse;
        private RepositoryHealth health;
        private bool healthChecking;

        // ---- 概览 ----
        private StackPanel overviewHost;
        private bool statsLoading;

        // ---- 底部传输条 + 下载管理 ----
        private ScrollViewer bodyScroll;
        private Border transferBar;
        private Border transferFill;
        private ColumnDefinition transferFillCol;
        private Grid transferTrack;
        private TextBlock transferCaption;
        private DispatcherTimer transferTimer;
        private bool transferHooked;
        private StackPanel taskListHost;

        /// <summary>
        /// 每个任务行的「只改数值」回调。
        ///
        /// 为什么要这一层：进度每 250 毫秒变一次，若每次都重建整棵列表，
        /// 用户悬停高亮会闪、「取消」按钮会在鼠标按下与弹起之间被换掉。
        /// 所以只在**任务集合变了**时重建行，平时的字节数只走这些回调。
        /// </summary>
        private readonly List<Action> taskRowUpdaters = new List<Action>();

        // ---- 设置页的保存反馈（重建视觉树后要能续上，所以存在字段里） ----
        private TextBlock saveStatus;
        private string saveStatusText;
        private bool? saveStatusOk;

        // ---- 仓库：卡片墙 + 内联删除 / 取回 / 卸载 ----
        private WrapPanel repoWallGrid;
        private TextBlock repoEmpty;
        private TextBlock repoSummary;
        private TextBlock repoStatus;
        private TextBox repoSearch;
        private TextBlock repoSearchHint;
        private readonly Dictionary<string, Button> repoFilterButtons = new Dictionary<string, Button>();
        private readonly List<RepoCardVisual> repoCards = new List<RepoCardVisual>();
        private string repoFilterMode = "all";
        private string repoFilterText = string.Empty;
        private bool repoLoading;

        // ---- 主题：卡片勾选 ----
        private WrapPanel themeWall;
        private TextBlock themeSummary;
        private TextBlock themeStatus;
        private TextBlock themeRootText;
        private RadioButton dirUpload;
        private RadioButton dirDownload;
        private RadioButton dirBoth;
        private CheckBox chkDryRun;
        private CheckBox chkForce;
        private readonly List<ThemeCardVisual> themeCards = new List<ThemeCardVisual>();
        private bool themesLoading;

        public VaultPanelView(VaultPlugin plugin, VaultService service,
                              VaultSettingsViewModel settingsVm, string themesRoot)
        {
            this.plugin = plugin;
            this.service = service;
            this.settingsVm = settingsVm;
            this.themesRoot = themesRoot;

            Build();

            Loaded += (s, e) =>
            {
                RefreshHealth();
                RefreshStats();
            };
        }

        // ================================================================ 配色

        /// <summary>
        /// 取 Playnite 主题里的画刷，取不到用兜底。
        /// 侧边栏图标也走这里 —— 在浅色主题里硬写白色笔画会直接看不见。
        ///
        /// 保留成 public static 是因为它是本插件与 Playnite 主题之间**唯一**的取色口，
        /// 自检直接对着它断言（见 VaultSelfTest「主题里没有这个画刷时返回兜底」）。
        /// </summary>
        public static Brush ThemedBrush(string key, Brush fallback)
        {
            var app = Application.Current;
            if (app == null)
            {
                return fallback;
            }

            try
            {
                return (app.TryFindResource(key) as Brush) ?? fallback;
            }
            catch
            {
                return fallback;
            }
        }

        /// <summary>当前设置对应的一套令牌。</summary>
        private VaultPalette Palette()
        {
            return VaultPalette.Resolve(VaultPalette.ParseMode(service.Settings.UiTheme));
        }

        /// <summary>
        /// 用户改了明暗档位后重建整棵视觉树。
        ///
        /// 为什么要整棵重建：颜色是**建控件时**烘进去的（没有绑到动态资源上），
        /// 所以换色必须重建。这比把每个控件都改成 DynamicResource 要简单得多，
        /// 代价是切换时会重建一次设置区（ViewModel 是字段，不会被重建，值不丢）。
        /// </summary>
        private void RebuildForTheme()
        {
            var keep = currentSection;
            navPanel.Children.Clear();
            navButtons.Clear();
            sectionBuilders.Clear();
            sectionCache.Clear();
            healthPulse = null;

            Build();
            ShowSection(keep);
            RefreshHealth();
        }

        // ================================================================ 骨架

        private void Build()
        {
            p = Palette();
            Background = p.Bg;

            // 分区是懒建的：点开哪个才构造哪个（设置那一页尤其重）
            sectionBuilders["overview"] = BuildOverview;
            sectionBuilders["themes"] = BuildThemeSync;
            sectionBuilders["repo"] = BuildRepo;
            sectionBuilders["tasks"] = BuildTasks;
            sectionBuilders["settings"] = BuildSettings;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var nav = new Border
            {
                Background = p.Surface,
                BorderBrush = p.Border,
                BorderThickness = new Thickness(0, 0, 2, 0),
                Padding = new Thickness(14, 18, 14, 14)
            };

            // 标题行：「仓库管家」在左，健康状态小点在它右上角。
            // 小点必须挂在这一行里而不是整页右上角 —— 那里会跟 Playnite 的关闭按钮重叠。
            var titleRow = new Grid { Margin = new Thickness(2, 0, 0, 3) };
            titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            titleRow.ColumnDefinitions.Add(
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var navTitle = new TextBlock
            {
                Text = "仓库管家",
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = p.Text,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(navTitle, 0);
            titleRow.Children.Add(navTitle);

            var navDot = BuildHealthDot();
            navDot.HorizontalAlignment = HorizontalAlignment.Right;
            navDot.VerticalAlignment = VerticalAlignment.Center;
            navDot.Margin = new Thickness(6, 2, 0, 0);
            Grid.SetColumn(navDot, 1);
            titleRow.Children.Add(navDot);

            navPanel.Children.Add(titleRow);

            // 「Playnite Vault @1.8.0」——版本号贴在产品名后面，
            // 省掉了原来「关于」页里那行「当前版本 X」。
            var brand = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(2, 0, 0, 18)
            };
            brand.Children.Add(new TextBlock
            {
                Text = "Playnite Vault",
                FontSize = 11.5,
                Foreground = p.TextMuted,
                VerticalAlignment = VerticalAlignment.Center
            });
            brand.Children.Add(new TextBlock
            {
                Text = "@" + VaultUpdater.CurrentVersion(),
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = p.Accent,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(5, 0, 0, 0)
            });
            navPanel.Children.Add(brand);

            AddSection("overview", "概览");
            AddSection("themes", "主题");
            AddSection("repo", "仓库");
            AddSection("tasks", "任务");
            AddSection("settings", "设置");

            var navScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = navPanel
            };
            Grid.SetColumn(navScroll, 0);

            // 右侧只剩内容本身 —— 状态点已经搬到左栏标题行上去了。
            var right = new Grid();
            right.RowDefinitions.Add(
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            body = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
            bodyScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(20, 18, 20, 24),
                Content = body
            };
            Grid.SetRow(bodyScroll, 0);
            right.Children.Add(bodyScroll);

            // 底部：传输任务的聚合进度条（没有任务时整条收起来，不占版面）
            transferBar = BuildTransferBar();
            Grid.SetRow(transferBar, 1);
            right.Children.Add(transferBar);

            Grid.SetColumn(right, 1);
            grid.Children.Add(nav);
            nav.Child = navScroll;
            grid.Children.Add(right);

            Content = grid;

            // 底部传输条与队列挂钩。挂一次就够（重建视觉树不会重建队列）。
            HookTransfers();
            RefreshTransferBar();

            ShowSection(currentSection);
        }

        private void AddSection(string key, string title)
        {
            var button = new Button
            {
                Content = title,
                Tag = key,
                Margin = new Thickness(0, 0, 0, 4),
                Padding = new Thickness(11, 9, 11, 9),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Foreground = p.Text,
                Cursor = Cursors.Hand
            };
            button.Click += (s, e) => ShowSection(key);

            // 悬停：只有「没被选中」的那个才跟着鼠标亮起来，
            // 否则鼠标划过当前页会把选中态擦掉，看起来像点错了。
            button.MouseEnter += (s, e) =>
            {
                if (currentSection != key)
                {
                    button.Background = p.SurfaceAlt;
                }
            };
            button.MouseLeave += (s, e) =>
            {
                if (currentSection != key)
                {
                    button.Background = Brushes.Transparent;
                }
            };

            navButtons[key] = button;
            navPanel.Children.Add(button);
        }

        private void ShowSection(string key)
        {
            Func<UIElement> builder;
            if (!sectionBuilders.TryGetValue(key, out builder))
            {
                return;
            }

            UIElement section;
            if (!sectionCache.TryGetValue(key, out section))
            {
                section = builder();
                sectionCache[key] = section;
            }

            body.Content = section;
            currentSection = key;

            foreach (var pair in navButtons)
            {
                var selected = pair.Key == key;
                pair.Value.Background = selected ? p.Accent : Brushes.Transparent;
                pair.Value.Foreground = selected ? p.AccentInk : p.Text;
            }

            if (key == "tasks" && plugin != null && plugin.Transfers != null)
            {
                RenderTasks(plugin.Transfers.Snapshot());
            }

            if (key == "overview")
            {
                RefreshStats();
            }
        }

        // ================================================================ 传输条 / 下载管理

        private void HookTransfers()
        {
            if (transferHooked || plugin == null || plugin.Transfers == null)
            {
                return;
            }

            transferHooked = true;
            plugin.Transfers.Changed += OnTransfersChanged;

            // 字节数不推给界面（会淹掉 UI 线程），改为界面自己轮询；
            // 没任务的时候定时器是停的，空转不耗时。
            transferTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            transferTimer.Tick += (s, e) => RefreshTransferBar();
        }

        private void OnTransfersChanged()
        {
            if (transferBar == null || plugin == null || plugin.Transfers == null)
            {
                return;
            }

            RenderTasks(plugin.Transfers.Snapshot());
            RefreshTransferBar();
        }

        /// <summary>
        /// 底部那条聚合进度条：「预估总进度」= 每条任务按体积加权后的平均。
        /// 体积还没量出来的任务按等权算 —— 否则它会让总进度在开跑前就显示 100%。
        /// </summary>
        private Border BuildTransferBar()
        {
            transferTrack = MakeBar(6, out transferFill, out transferFillCol);
            transferTrack.Margin = new Thickness(0, 6, 0, 0);

            transferCaption = new TextBlock
            {
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = p.Text,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var left = new StackPanel();
            left.Children.Add(transferCaption);
            left.Children.Add(transferTrack);

            var detail = CardButton("下载管理", null);
            detail.Margin = new Thickness(12, 0, 0, 0);
            detail.VerticalAlignment = VerticalAlignment.Center;
            detail.Click += (s, e) => ShowSection("tasks");

            var row = new DockPanel();
            DockPanel.SetDock(detail, Dock.Right);
            row.Children.Add(detail);
            row.Children.Add(left);

            var bar = new Border
            {
                Background = p.Surface,
                BorderBrush = p.Border,
                BorderThickness = new Thickness(0, 2, 0, 0),
                Padding = new Thickness(20, 9, 20, 11),
                Child = row,
                Cursor = Cursors.Hand,
                Visibility = Visibility.Collapsed,
                ToolTip = "点这里看每个任务的详情"
            };
            bar.MouseLeftButtonUp += (s, e) => ShowSection("tasks");
            return bar;
        }

        private void RefreshTransferBar()
        {
            if (transferBar == null || plugin == null || plugin.Transfers == null)
            {
                return;
            }

            var tasks = plugin.Transfers.Snapshot();
            transferBar.Visibility = tasks.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

            var agg = plugin.Transfers.Aggregate();
            if (transferCaption != null)
            {
                transferCaption.Text = string.IsNullOrEmpty(agg.Caption) ? "传输任务" : agg.Caption;
            }

            SetBar(transferTrack, transferFillCol, agg.Indeterminate ? 0.12 : agg.Progress);
            if (transferFill != null)
            {
                transferFill.Background = agg.Failed > 0 ? p.Danger : p.Accent;
            }

            if (transferTimer != null)
            {
                if (agg.Active > 0 && !transferTimer.IsEnabled)
                {
                    transferTimer.Start();
                }
                else if (agg.Active == 0 && transferTimer.IsEnabled)
                {
                    transferTimer.Stop();
                }
            }

            RefreshTaskRows();
        }

        private void RefreshTaskRows()
        {
            foreach (var update in taskRowUpdaters.ToArray())
            {
                try
                {
                    update();
                }
                catch (Exception ex)
                {
                    VaultLog.Warn("刷新任务行失败：" + ex.Message);
                }
            }
        }

        /// <summary>
        /// 「下载管理」：队列里每条任务的详情。
        ///
        /// 为什么要有独立一页而不是弹个窗：一次归档要跑几十分钟，用户会关掉它去干别的，
        /// 而底部那条进度条随时能把他带回这里。弹窗一关就什么都没了。
        /// </summary>
        private UIElement BuildTasks()
        {
            var root = new StackPanel();

            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
            var back = FlatButton("← 回到仓库", null);
            back.Click += (s, e) => ShowSection("repo");
            DockPanel.SetDock(back, Dock.Right);
            head.Children.Add(back);

            var titleStack = new StackPanel();
            titleStack.Children.Add(new TextBlock
            {
                Text = "下载管理",
                FontSize = 17,
                FontWeight = FontWeights.Bold,
                Foreground = p.Text
            });
            titleStack.Children.Add(new TextBlock
            {
                Text = "上传与下载串行跑（并发只会互相抢 NAS 的写入带宽）。离开这一页任务照跑。",
                FontSize = 11.5,
                Foreground = p.TextMuted,
                Margin = new Thickness(0, 3, 0, 0)
            });
            head.Children.Add(titleStack);
            root.Children.Add(head);

            var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };

            var clear = FlatButton("清除已结束", null);
            clear.Margin = new Thickness(0, 0, 8, 8);
            clear.Click += (s, e) =>
            {
                if (plugin != null && plugin.Transfers != null)
                {
                    plugin.Transfers.ClearFinished();
                }

                OnTransfersChanged();
            };
            actions.Children.Add(clear);

            var cancelAll = FlatButton("全部取消", null);
            cancelAll.Margin = new Thickness(0, 0, 8, 8);
            cancelAll.Click += (s, e) =>
            {
                if (plugin != null && plugin.Transfers != null)
                {
                    plugin.Transfers.CancelAll();
                }

                OnTransfersChanged();
            };
            actions.Children.Add(cancelAll);
            root.Children.Add(actions);

            taskListHost = new StackPanel();
            root.Children.Add(taskListHost);

            // 队列可能还不存在（插件对象尚未初始化完就被要求建页）—— 那就当空队列显示，
            // 不要把整页构造打断。
            RenderTasks(plugin != null && plugin.Transfers != null
                ? plugin.Transfers.Snapshot()
                : null);
            return root;
        }

        private void RenderTasks(TransferTask[] tasks)
        {
            if (taskListHost == null)
            {
                return;
            }

            taskRowUpdaters.Clear();
            taskListHost.Children.Clear();

            if (tasks == null || tasks.Length == 0)
            {
                taskListHost.Children.Add(VaultCharts.Card(p, VaultCharts.Muted(p,
                    "队列是空的。在「仓库」页点「归档到 NAS」或「取回本机」，任务就会出现在这里。")));
                return;
            }

            // 没结束的排前面 —— 用户最关心「现在这条」
            var ordered = tasks.OrderBy(t => t.IsFinished ? 1 : 0).ToArray();
            foreach (var task in ordered)
            {
                taskListHost.Children.Add(BuildTaskRow(task));
            }
        }

        private UIElement BuildTaskRow(TransferTask task)
        {
            var box = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };

            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var kindBrush = task.Kind == TransferKind.Upload ? p.Info : p.Accent;
            var chip = new Border
            {
                Background = kindBrush,
                Padding = new Thickness(7, 2, 7, 2),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            chip.Child = new TextBlock
            {
                Text = task.KindText,
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = p.OnStatus(kindBrush)
            };
            Grid.SetColumn(chip, 0);
            head.Children.Add(chip);

            var name = new TextBlock
            {
                Text = task.Title,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = p.Text,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(name, 1);
            head.Children.Add(name);

            var state = new TextBlock
            {
                FontSize = 11,
                Foreground = p.TextMuted,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 10, 0)
            };
            Grid.SetColumn(state, 2);
            head.Children.Add(state);

            Button cancel = null;
            if (!task.IsFinished)
            {
                cancel = CardButton("取消", p.Danger);
                cancel.Click += (s, e) =>
                {
                    task.RequestCancel();
                    if (state != null)
                    {
                        state.Text = "正在取消…";
                    }
                };
                Grid.SetColumn(cancel, 2);
            }

            // 取消按钮与状态字挤在同一列：状态字在左、按钮在右
            if (cancel != null)
            {
                head.Children.Remove(state);
                var tail = new StackPanel { Orientation = Orientation.Horizontal };
                state.Margin = new Thickness(10, 0, 10, 0);
                tail.Children.Add(state);
                tail.Children.Add(cancel);
                Grid.SetColumn(tail, 2);
                head.Children.Add(tail);
            }

            box.Children.Add(head);

            Border fill;
            ColumnDefinition fillCol;
            var track = MakeBar(5, out fill, out fillCol);
            track.Margin = new Thickness(0, 7, 0, 0);
            box.Children.Add(track);

            var detail = new TextBlock
            {
                FontSize = 11,
                Foreground = p.TextMuted,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 5, 0, 0)
            };
            box.Children.Add(detail);

            var updater = new Action(() =>
            {
                SetBar(track, fillCol, task.Fraction);
                if (task.State == TransferState.Failed)
                {
                    fill.Background = p.Danger;
                }
                else if (task.State == TransferState.Done)
                {
                    fill.Background = p.Success;
                }

                var line = task.DescribeProgress();
                if (detail.Text != line)
                {
                    detail.Text = line;
                }
            });
            taskRowUpdaters.Add(updater);
            updater();

            var card = VaultCharts.Card(p, box);
            card.Margin = new Thickness(0, 0, 0, 10);
            return card;
        }

        /// <summary>一条细进度条。返回轨道，并用 out 交出填充块与它的列 ——
        /// 之后只改列宽就能更新进度，不用重建控件。</summary>
        private Grid MakeBar(double height, out Border fill, out ColumnDefinition fillCol)
        {
            var track = new Grid
            {
                Height = height,
                Background = p.SurfaceAlt,
                ClipToBounds = true
            };

            var f = new Border { Background = p.Accent };
            var fc = new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) };
            var rest = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };

            Grid.SetColumn(f, 0);
            track.ColumnDefinitions.Add(fc);
            track.ColumnDefinitions.Add(rest);
            track.Children.Add(f);

            fill = f;
            fillCol = fc;
            return track;
        }

        private static void SetBar(Grid track, ColumnDefinition fillCol, double fraction)
        {
            if (track == null || fillCol == null)
            {
                return;
            }

            var f = fraction < 0 ? 0 : (fraction > 1 ? 1 : fraction);
            fillCol.Width = new GridLength(f, GridUnitType.Star);
            track.ColumnDefinitions[1].Width = new GridLength(1 - f, GridUnitType.Star);
        }

        // ================================================================ 状态点

        /// <summary>
        /// 右上角那个小圆点：外圈是个会呼吸的环，里面是实心点。
        /// 点它重新探测一次 —— v1.9 起它是**唯一**的刷新入口
        /// （概览页里那个「刷新」按钮已经删掉，一个动作只留一个入口）。
        /// </summary>
        private Grid BuildHealthDot()
        {
            var host = new Grid
            {
                Width = 22,
                Height = 22,
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent
            };

            healthRing = new Border
            {
                Width = 20,
                Height = 20,
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(2),
                BorderBrush = p.TextMuted,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1, 1)
            };
            host.Children.Add(healthRing);

            healthDot = new Border
            {
                Width = 10,
                Height = 10,
                CornerRadius = new CornerRadius(5),
                Background = p.TextMuted,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            host.Children.Add(healthDot);

            host.ToolTip = VaultCharts.Muted(p, "正在探测仓库…");
            ToolTipService.SetInitialShowDelay(host, 250);
            ToolTipService.SetShowDuration(host, 60000);

            host.MouseLeftButtonUp += (s, e) => RefreshHealth();
            return host;
        }

        /// <summary>探测仓库连通性。网络在后台线程，改界面回 Dispatcher。</summary>
        private void RefreshHealth()
        {
            if (healthChecking)
            {
                return;
            }

            healthChecking = true;
            ApplyHealth(new RepositoryHealth
            {
                State = VaultHealthState.Checking,
                Summary = "正在探测仓库…",
                Detail = "正在连接 " + (service.Settings.WebDavUrl ?? "(未配置)"),
                Url = service.Settings.WebDavUrl
            });

            Task.Run(() =>
            {
                RepositoryHealth result;
                try
                {
                    result = plugin.CheckRepositoryHealth();
                }
                catch (Exception ex)
                {
                    VaultLog.Warn("侧边栏页：探测仓库失败：" + ex.Message);
                    result = new RepositoryHealth
                    {
                        State = VaultHealthState.Failed,
                        Summary = "探测本身出错了",
                        Detail = ex.Message
                    };
                }

                Dispatcher.Invoke(() =>
                {
                    healthChecking = false;
                    ApplyHealth(result);
                });
            });
        }

        private void ApplyHealth(RepositoryHealth result)
        {
            health = result;

            if (healthDot == null)
            {
                return;
            }

            // 提示的第一行要能**一句话回答「现在到底是什么状态」**，
            // 所以带上具体地址 —— 「已连接」三个字看不出连的是哪台 NAS。
            var url = service.Settings.WebDavUrl;
            var address = string.IsNullOrWhiteSpace(url) ? "(未配置)" : url.TrimEnd('/');

            Brush color;
            string title;
            switch (result.State)
            {
                case VaultHealthState.Ok:
                    color = p.Success;
                    title = "当前已连接 " + address;
                    break;
                case VaultHealthState.Timeout:
                    color = p.Warning;
                    title = "连接 " + address + " 超时";
                    break;
                case VaultHealthState.Failed:
                    color = p.Danger;
                    title = "连接 " + address + " 失败";
                    break;
                case VaultHealthState.NotConfigured:
                    color = p.TextMuted;
                    title = "还没配置仓库地址";
                    break;
                default:
                    color = p.Info;
                    title = "正在连接 " + address + "…";
                    break;
            }

            healthDot.Background = color;
            healthRing.BorderBrush = color;

            // 提示浮窗：用自己的一套边框，避免跟着系统 ToolTip 的样式走
            var stack = new StackPanel { MaxWidth = 380 };
            stack.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 12.5,
                FontWeight = FontWeights.Bold,
                Foreground = p.Text
            });
            if (!string.IsNullOrEmpty(result.Detail))
            {
                stack.Children.Add(VaultCharts.Muted(p, result.Detail, 11.5));
            }
            stack.Children.Add(new TextBlock
            {
                Text = "探测时间 " + result.CheckedAt.ToString("HH:mm:ss") + "　·　点这个点可重新探测",
                FontSize = 10.5,
                Foreground = p.TextMuted,
                Margin = new Thickness(0, 7, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });

            var tip = new Border
            {
                Background = p.Surface,
                BorderBrush = p.Border,
                BorderThickness = new Thickness(2),
                Padding = new Thickness(12, 10, 12, 10),
                Child = stack
            };

            var host = healthDot.Parent as FrameworkElement;
            if (host != null)
            {
                host.ToolTip = tip;
            }

            StartPulse(result.State);
        }

        /// <summary>
        /// 只在「还没定论」的状态上做呼吸动效：
        /// 探测中（快）、超时（慢，因为多数是暂时性的）。已连接/失败都是静态的。
        /// </summary>
        private void StartPulse(VaultHealthState state)
        {
            if (healthRing == null)
            {
                return;
            }

            if (healthPulse != null)
            {
                healthPulse.Stop();
                healthRing.BeginAnimation(OpacityProperty, null);
                healthRing.Opacity = 1;
                if (healthRing.RenderTransform is ScaleTransform)
                {
                    healthRing.RenderTransform = new ScaleTransform(1, 1);
                }
                healthPulse = null;
            }

            double seconds;
            switch (state)
            {
                case VaultHealthState.Checking:
                    seconds = 0.8;
                    break;
                case VaultHealthState.Timeout:
                    seconds = 1.8;
                    break;
                default:
                    return;   // 有定论了就别再动，省得抢注意力
            }

            // 系统关了动画就别放：无障碍设置里那个「在 Windows 中显示动画」
            // 是给前庭功能敏感的用户准备的，插件不该绕过它。
            if (!AnimationsAllowed())
            {
                return;
            }

            var ring = healthRing;
            var storyboard = new Storyboard { RepeatBehavior = RepeatBehavior.Forever, AutoReverse = true };

            var fade = new DoubleAnimation
            {
                From = 0.9,
                To = 0.15,
                Duration = TimeSpan.FromSeconds(seconds)
            };
            Storyboard.SetTarget(fade, ring);
            Storyboard.SetTargetProperty(fade, new PropertyPath(OpacityProperty));
            storyboard.Children.Add(fade);

            var scale = new ScaleTransform(1, 1);
            ring.RenderTransform = scale;
            var grow = new DoubleAnimation
            {
                From = 0.82,
                To = 1.12,
                Duration = TimeSpan.FromSeconds(seconds)
            };
            Storyboard.SetTarget(grow, ring);
            Storyboard.SetTargetProperty(grow, new PropertyPath("RenderTransform.ScaleX"));
            storyboard.Children.Add(grow);

            var growY = new DoubleAnimation
            {
                From = 0.82,
                To = 1.12,
                Duration = TimeSpan.FromSeconds(seconds)
            };
            Storyboard.SetTarget(growY, ring);
            Storyboard.SetTargetProperty(growY, new PropertyPath("RenderTransform.ScaleY"));
            storyboard.Children.Add(growY);

            healthPulse = storyboard;
            // 显式给 containing object：只用 SetTarget 的话，Begin() 在某些宿主里
            // 会因为找不到动画作用域而抛「No applicable name scope」。
            storyboard.Begin(ring, true);
        }

        private static bool AnimationsAllowed()
        {
            try
            {
                return SystemParameters.ClientAreaAnimation;
            }
            catch
            {
                return false;
            }
        }

        // ================================================================ 概览

        /// <summary>
        /// 概览页：几个指标块 + 三张图 + 一行状态。
        /// 原来那一片「常用操作」按钮已经散回各自所属的页面 ——
        /// 概览只负责「看」，不负责「做」。
        /// </summary>
        private UIElement BuildOverview()
        {
            var root = new StackPanel();

            var head = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };

            var titleStack = new StackPanel();
            titleStack.Children.Add(new TextBlock
            {
                Text = "概览",
                FontSize = 17,
                FontWeight = FontWeights.Bold,
                Foreground = p.Text
            });
            titleStack.Children.Add(new TextBlock
            {
                // 刷新按钮已经去掉：入口统一到左栏标题旁那个状态点上，
                // 「点它重新探测」写在悬停提示里。
                Text = "仓库、主题、连通状态一眼看完；数据与左栏标题旁那个小圆点是同一份，点它可以重新探测。",
                FontSize = 11.5,
                Foreground = p.TextMuted,
                Margin = new Thickness(0, 3, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
            head.Children.Add(titleStack);
            root.Children.Add(head);

            overviewHost = new StackPanel();
            root.Children.Add(overviewHost);

            RenderOverview(null);
            return root;
        }

        /// <summary>把一份统计快照画成界面。传 null 表示「还在读」。</summary>
        private void RenderOverview(VaultStats s)
        {
            if (overviewHost == null)
            {
                return;
            }

            overviewHost.Children.Clear();

            var loading = s == null;
            var apps = loading ? 0 : s.Apps;
            var repoBytes = loading ? 0 : s.RepoBytes;
            var themeStats = loading ? null : s.Themes;
            var themeBytes = themeStats == null ? 0 : themeStats.TotalBytes;
            var themeCount = themeStats == null ? 0 : themeStats.TotalThemes;
            var conflicts = themeStats == null ? 0 : themeStats.ConflictCopies;

            // ---- 指标块 ----
            var tiles = new WrapPanel();
            tiles.Children.Add(VaultCharts.StatTile(p, "仓库里的应用", loading ? "…" : apps.ToString(), p.Accent));
            tiles.Children.Add(VaultCharts.StatTile(p, "仓库体积", loading ? "…" : VaultAdminWindow.FormatSize(repoBytes), p.Info));
            tiles.Children.Add(VaultCharts.StatTile(p, "本地主题", loading ? "…" : themeCount.ToString(), p.Success));
            tiles.Children.Add(VaultCharts.StatTile(p, "主题体积", loading ? "…" : VaultAdminWindow.FormatSize(themeBytes), p.Warning));

            if (!loading && conflicts > 0)
            {
                tiles.Children.Add(VaultCharts.StatTile(p, "冲突留档", conflicts.ToString(), p.Danger));
            }

            overviewHost.Children.Add(tiles);

            // ---- 第一行图：存储构成（环形）+ 应用体积排行（条形） ----
            var charts = new Grid { Margin = new Thickness(0, 2, 0, 12) };
            charts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            charts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            charts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var bytesItems = new List<BarItem>
            {
                new BarItem("仓库应用", repoBytes, VaultAdminWindow.FormatSize(repoBytes)) { Color = p.SeriesAt(0) },
                new BarItem("本地主题", themeBytes, VaultAdminWindow.FormatSize(themeBytes)) { Color = p.SeriesAt(1) }
            };

            var donutCard = new StackPanel();
            donutCard.Children.Add(VaultCharts.SectionTitle(p, "存储构成"));
            var donutRow = new Grid();
            donutRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            donutRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var donut = VaultCharts.Donut(p, bytesItems, 136,
                loading ? "…" : VaultAdminWindow.FormatSize(repoBytes + themeBytes),
                "合计占用");
            Grid.SetColumn(donut, 0);
            donutRow.Children.Add(donut);

            var legend = VaultCharts.Legend(p, bytesItems);
            legend.Margin = new Thickness(16, 0, 0, 0);
            legend.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(legend, 1);
            donutRow.Children.Add(legend);
            donutCard.Children.Add(donutRow);

            var leftCard = VaultCharts.Card(p, donutCard);
            Grid.SetColumn(leftCard, 0);
            charts.Children.Add(leftCard);

            var topStack = new StackPanel();
            topStack.Children.Add(VaultCharts.SectionTitle(p, "仓库应用体积 · 前 6"));
            var topItems = new List<BarItem>();
            if (!loading && s.TopApps != null)
            {
                for (var i = 0; i < s.TopApps.Count; i++)
                {
                    var pair = s.TopApps[i];
                    topItems.Add(new BarItem(pair.Key, pair.Value, VaultAdminWindow.FormatSize(pair.Value)));
                }
            }
            topStack.Children.Add(VaultCharts.BarList(p, topItems,
                loading ? "正在读取仓库索引…" : "仓库里还没有归档的应用。"));
            var rightCard = VaultCharts.Card(p, topStack);
            Grid.SetColumn(rightCard, 2);
            charts.Children.Add(rightCard);

            overviewHost.Children.Add(charts);

            // ---- 第二行图：主题构成（堆叠条 + 图例） ----
            var themesCard = new StackPanel();
            themesCard.Children.Add(VaultCharts.SectionTitle(p, "主题构成"));

            var themeItems = new List<BarItem>
            {
                new BarItem("桌面主题", themeStats == null ? 0 : themeStats.DesktopThemes,
                    (themeStats == null ? 0 : themeStats.DesktopThemes) + " 个"),
                new BarItem("全屏主题", themeStats == null ? 0 : themeStats.FullscreenThemes,
                    (themeStats == null ? 0 : themeStats.FullscreenThemes) + " 个")
            };
            themesCard.Children.Add(VaultCharts.StackedBar(p, themeItems, 14));
            var themeLegend = VaultCharts.Legend(p, themeItems);
            themeLegend.Margin = new Thickness(0, 10, 0, 0);
            themesCard.Children.Add(themeLegend);
            if (themeStats != null && themeStats.RootMissing)
            {
                themesCard.Children.Add(new TextBlock
                {
                    Text = "主题目录不存在：" + themesRoot,
                    FontSize = 11,
                    Foreground = p.Warning,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 8, 0, 0)
                });
            }

            var themesCardHost = VaultCharts.Card(p, themesCard);
            themesCardHost.Margin = new Thickness(0, 0, 0, 12);
            overviewHost.Children.Add(themesCardHost);

            // ---- 第三行：状态明细 ----
            var statusCard = new StackPanel();
            statusCard.Children.Add(VaultCharts.SectionTitle(p, "状态"));

            var lines = new List<string>();
            if (loading)
            {
                lines.Add("正在读取…");
            }
            else
            {
                lines.Add("索引来源：" + (string.IsNullOrEmpty(s.Source) ? "未知" : s.Source));
                if (!string.IsNullOrEmpty(s.Error))
                {
                    lines.Add("读取索引时有问题：" + s.Error);
                }

                lines.Add(themeStats != null && themeStats.LastSyncUtc.HasValue
                    ? "上次主题同步：" + themeStats.LastSyncUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                    : "还没同步过主题。");

                if (conflicts > 0)
                {
                    lines.Add("有 " + conflicts + " 份冲突副本留在主题目录里（没删任何东西，可以手工比对后清理）。");
                }

                lines.Add(health == null
                    ? "连通状态：还没探测。"
                    : "连通状态：" + DescribeHealthOneLine(health));
            }

            foreach (var line in lines)
            {
                statusCard.Children.Add(new TextBlock
                {
                    Text = "· " + line,
                    FontSize = 11.5,
                    Foreground = p.TextMuted,
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 18,
                    Margin = new Thickness(0, 0, 0, 2)
                });
            }

            overviewHost.Children.Add(VaultCharts.Card(p, statusCard));
        }

        private static string DescribeHealthOneLine(RepositoryHealth result)
        {
            switch (result.State)
            {
            case VaultHealthState.Ok:
                return "已连接（" + result.CheckedAt.ToString("HH:mm:ss") + "）";
            case VaultHealthState.Timeout:
                return "连接超时 —— 多数是 NAS 没开机或网络不通，把鼠标放到左栏标题旁那个小点上可以看详情";
            case VaultHealthState.Failed:
                return "连接失败 —— 地址、账号口令或证书有问题，鼠标放到左栏标题旁那个小点上看排查步骤";
                case VaultHealthState.NotConfigured:
                    return "还没配置 WebDAV 地址（去「设置」里填）";
                default:
                    return "正在探测…";
            }
        }

        // ================================================================ 主题

        /// <summary>主题卡片在界面上的那一份（勾选框 + 数据）。</summary>
        private sealed class ThemeCardVisual
        {
            public ThemeCatalogItem Item;

            /// <summary>勾选框 —— 卡片整块就是它。</summary>
            public CheckBox Box;

            /// <summary>要挂进卡片墙的那个元素（带描边的外框）。</summary>
            public FrameworkElement Element;
        }

        /// <summary>
        /// 「主题」页：本地与 NAS 上的主题都做成卡片，勾选哪些就传哪些。
        ///
        /// <para><b>为什么改成勾选式</b>：以前只能选方向然后「全传」——
        /// 用户下了一堆主题但只想备份其中两个时，没有任何表达方式。现在「勾选」是唯一的输入，
        /// 方向仍要选，两者相乘才是这次要干的事。</para>
        ///
        /// <para><b>没勾的主题连比对都不参与</b>（<c>ThemeSyncOptions.Only</c>）——
        /// 所以「只同步两个主题」是真的快，而不是「全比一遍再把其他的跳过」。</para>
        /// </summary>
        private UIElement BuildThemeSync()
        {
            var root = new StackPanel();
            root.Children.Add(new TextBlock
            {
                Text = "主题",
                FontSize = 17,
                FontWeight = FontWeights.Bold,
                Foreground = p.Text,
                Margin = new Thickness(0, 0, 0, 8)
            });
            root.Children.Add(VaultCharts.Muted(p,
                "本机与 NAS 上的主题都在下面，勾选哪些就传哪些。"
                + "远端是「普通文件镜像」——NAS 上就是一份能直接翻、能直接拷回来的主题目录树。"));

            // ---- 方向 ----
            var dir = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
            dir.Children.Add(VaultCharts.SectionTitle(p, "方向"));

            dirUpload = DirOption("上传（本地 → NAS）：把勾选的主题推上去", true);
            dirDownload = DirOption("下载（NAS → 本地）：把勾选的主题取回来", false);
            dirBoth = DirOption("双向：两边比对，缺哪补哪（只增不减，不会删东西）", false);
            dir.Children.Add(dirUpload);
            dir.Children.Add(dirDownload);
            dir.Children.Add(dirBoth);
            root.Children.Add(VaultCharts.Card(p, dir));

            // ---- 选项 ----
            var opts = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            opts.Children.Add(VaultCharts.SectionTitle(p, "选项"));

            chkDryRun = new CheckBox
            {
                Content = "预演（只列计划，不落盘）",
                IsChecked = false,
                Foreground = p.Text,
                FontSize = 12.5,
                Margin = new Thickness(0, 0, 0, 5)
            };
            opts.Children.Add(chkDryRun);

            chkForce = new CheckBox
            {
                Content = "强制（内容一样也重传一遍，慢；只有怀疑远端被改坏时才用）",
                IsChecked = false,
                Foreground = p.Text,
                FontSize = 12.5
            };
            opts.Children.Add(chkForce);

            var syncButton = FlatButton("同步勾选的主题", p.Accent);
            syncButton.Margin = new Thickness(0, 12, 0, 0);
            syncButton.HorizontalAlignment = HorizontalAlignment.Left;
            syncButton.Click += (s, e) =>
            {
                var dry = chkDryRun != null && chkDryRun.IsChecked == true;
                var force = chkForce != null && chkForce.IsChecked == true;
                RunSync(SelectedMode(), dry, force);
            };
            opts.Children.Add(syncButton);

            themeRootText = new TextBlock
            {
                FontSize = 11,
                Foreground = p.TextMuted,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 0),
                Text = "主题目录：" + themesRoot
            };
            opts.Children.Add(themeRootText);
            root.Children.Add(VaultCharts.Card(p, opts));

            // ---- 选择工具条 ----
            var bar = new WrapPanel { Margin = new Thickness(0, 14, 0, 6) };

            var all = CardButton("全选", null);
            all.Margin = new Thickness(0, 0, 6, 6);
            all.Click += (s, e) => SetAllThemes(true);
            bar.Children.Add(all);

            var none = CardButton("全不选", null);
            none.Margin = new Thickness(0, 0, 6, 6);
            none.Click += (s, e) => SetAllThemes(false);
            bar.Children.Add(none);

            var rescan = CardButton("重新扫描", null);
            rescan.Margin = new Thickness(0, 0, 6, 6);
            rescan.Click += (s, e) => RefreshThemes();
            bar.Children.Add(rescan);

            themeSummary = new TextBlock
            {
                FontSize = 11.5,
                Foreground = p.TextMuted,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 6)
            };
            bar.Children.Add(themeSummary);
            root.Children.Add(bar);

            themeStatus = new TextBlock
            {
                FontSize = 11.5,
                Foreground = p.TextMuted,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };
            root.Children.Add(themeStatus);

            themeWall = new WrapPanel();
            root.Children.Add(themeWall);

            RefreshThemes();
            return root;
        }

        // ---------------------------------------------------------------- 主题列表

        private void SetAllThemes(bool value)
        {
            foreach (var visual in themeCards)
            {
                if (visual.Box != null)
                {
                    visual.Box.IsChecked = value;
                }
            }

            UpdateThemeSummary();
        }

        /// <summary>勾选的主题键（与引擎内部的 Key 同一格式）。</summary>
        private List<string> SelectedThemeKeys()
        {
            var keys = new List<string>();
            foreach (var visual in themeCards)
            {
                if (visual.Box != null && visual.Box.IsChecked == true && visual.Item != null)
                {
                    keys.Add(visual.Item.Key);
                }
            }

            return keys;
        }

        private void UpdateThemeSummary()
        {
            if (themeSummary == null)
            {
                return;
            }

            themeSummary.Text = string.Format("共 {0} 个主题，已勾选 {1} 个",
                themeCards.Count, SelectedThemeKeys().Count);
        }

        /// <summary>
        /// 拉一遍主题清单（本地扫目录 + 远端读 index.json）。网络那半在后台线程，回 UI 线程再画。
        /// </summary>
        private void RefreshThemes()
        {
            if (themesLoading || themeWall == null)
            {
                return;
            }

            themesLoading = true;
            themeWall.Children.Clear();
            themeCards.Clear();

            if (themeSummary != null)
            {
                themeSummary.Text = "正在读取主题列表…";
            }

            if (themeStatus != null)
            {
                themeStatus.Text = string.Empty;
            }

            Task.Run(() =>
            {
                List<ThemeCatalogItem> items = null;
                string error = null;

                try
                {
                    items = plugin.LoadThemeCatalog(out error);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    VaultLog.Error("侧边栏页：读取主题列表失败", ex);
                }

                Dispatcher.Invoke(() => RenderThemes(items, error));
            });
        }

        private void RenderThemes(List<ThemeCatalogItem> items, string error)
        {
            themesLoading = false;
            if (themeWall == null)
            {
                return;
            }

            themeWall.Children.Clear();
            themeCards.Clear();

            if (items != null)
            {
                foreach (var item in items)
                {
                    var visual = BuildThemeCard(item);
                    themeCards.Add(visual);
                    themeWall.Children.Add(visual.Element);
                }
            }

            UpdateThemeSummary();

            if (themeStatus == null)
            {
                return;
            }

            themeStatus.Foreground = p.TextMuted;

            // 远端读不到不算失败：断网时至少还能看到本地有什么、还能只上传
            if (!string.IsNullOrEmpty(error))
            {
                themeStatus.Text = "读不到 NAS 上的主题列表（" + error + "）；下面只是本机有的主题。";
                themeStatus.Foreground = p.Warning;
                return;
            }

            var conflicts = ListConflictCopies();
            if (conflicts.Count > 0)
            {
                themeStatus.Text = "主题目录里有 " + conflicts.Count
                    + " 份冲突副本（原地保留，没删任何东西）："
                    + string.Join("、", conflicts.Take(6))
                    + (conflicts.Count > 6 ? " …" : "");
                return;
            }

            themeStatus.Text = items == null || items.Count == 0
                ? "两边都还没有主题。把主题放进上面的主题目录，再点「重新扫描」。"
                : "标签颜色：绿=两边都有、橙=只在本机、蓝=只在 NAS。";
        }

        /// <summary>
        /// 一张主题卡片。
        ///
        /// <para>勾选框直接把「名称 + 副标题」当 <c>Content</c>，而不是另外盖一层点击处理 ——
        /// 后者在点到复选框本身时会与它自己的切换撞车（连切两次等于没切）。</para>
        /// </summary>
        private ThemeCardVisual BuildThemeCard(ThemeCatalogItem item)
        {
            var name = new TextBlock
            {
                Text = item.DisplayName,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = p.Text,
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 36
            };

            Brush stateBrush;
            if (item.Local && item.Remote)
            {
                stateBrush = p.Success;
            }
            else if (item.Local)
            {
                stateBrush = p.Warning;
            }
            else
            {
                stateBrush = p.Info;
            }

            var describe = new TextBlock
            {
                Text = item.Describe() + (string.IsNullOrWhiteSpace(item.Version)
                    ? string.Empty
                    : " · v" + item.Version),
                FontSize = 11,
                Foreground = stateBrush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0)
            };

            var col = new StackPanel { Margin = new Thickness(7, 0, 0, 0), MaxWidth = 210 };
            col.Children.Add(name);
            col.Children.Add(describe);

            var box = new CheckBox
            {
                Content = col,
                IsChecked = false,
                Foreground = p.Text,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                VerticalContentAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand
            };
            box.Checked += (s, e) => UpdateThemeSummary();
            box.Unchecked += (s, e) => UpdateThemeSummary();

            var frame = new Border
            {
                Width = 264,
                Margin = new Thickness(0, 0, 10, 10),
                Padding = new Thickness(10, 9, 10, 9),
                Background = p.Surface,
                BorderBrush = p.Border,
                BorderThickness = new Thickness(2),
                Child = box,
                ToolTip = item.DisplayName + "\n" + item.Key + "\n"
                          + (item.LocalFiles > 0 ? "本机 " + item.LocalFiles + " 个文件\n" : "")
                          + (item.RemoteFiles > 0 ? "NAS " + item.RemoteFiles + " 个文件" : "")
            };

            return new ThemeCardVisual { Item = item, Box = box, Element = frame };
        }


        private RadioButton DirOption(string text, bool selected)
        {
            return new RadioButton
            {
                Content = text,
                GroupName = "vaultThemeSyncDir",
                IsChecked = selected,
                Foreground = p.Text,
                FontSize = 12.5,
                Margin = new Thickness(0, 0, 0, 5)
            };
        }

        private ThemeSyncMode SelectedMode()
        {
            if (dirUpload != null && dirUpload.IsChecked == true)
            {
                return ThemeSyncMode.Upload;
            }

            if (dirDownload != null && dirDownload.IsChecked == true)
            {
                return ThemeSyncMode.Download;
            }

            return ThemeSyncMode.Both;
        }

        /// <summary>
        /// 只把**勾选的主题**排进队列。
        ///
        /// <para>一个都没勾就直接拦下 —— 空集合传给引擎要么被当成「全部」、
        /// 要么什么都没做，两种都不是用户想要的，不如在这里说清楚。</para>
        /// </summary>
        private void RunSync(ThemeSyncMode mode, bool dryRun, bool force)
        {
            var keys = SelectedThemeKeys();
            if (keys.Count == 0)
            {
                if (themeStatus != null)
                {
                    themeStatus.Text = "先勾选要同步的主题（或点上面的「全选」）。";
                    themeStatus.Foreground = p.Warning;
                }

                return;
            }

            var task = plugin.EnqueueThemeSync(mode, dryRun, force, keys);
            if (task == null)
            {
                return;
            }

            if (themeStatus != null)
            {
                themeStatus.Text = string.Format("已加入队列：{0} 个主题（{1}{2}）。进度看页面底部。",
                    keys.Count,
                    mode == ThemeSyncMode.Upload ? "上传"
                        : (mode == ThemeSyncMode.Download ? "下载" : "双向"),
                    dryRun ? "，预演" : string.Empty);
                themeStatus.Foreground = p.TextMuted;
            }

            OnTransfersChanged();
        }

        /// <summary>列出主题目录下现存的 .conflict-* 副本（只扫两层，足够）。</summary>
        private List<string> ListConflictCopies()
        {
            var found = new List<string>();
            foreach (var mode in new[] { "Desktop", "Fullscreen" })
            {
                var dir = Path.Combine(themesRoot, mode);
                if (!Directory.Exists(dir))
                {
                    continue;
                }

                foreach (var sub in Directory.GetDirectories(dir))
                {
                    var name = Path.GetFileName(sub);
                    if (name.Contains(".conflict-"))
                    {
                        found.Add(mode + "/" + name);
                    }
                }
            }

            found.Sort(StringComparer.CurrentCulture);
            return found;
        }

        // ================================================================ 仓库

        /// <summary>卡片尺寸。宽度固定，封面 3:4 —— Playnite 的封面基本都是竖版，裁切不会吃掉主体。</summary>
        private const double RepoCardWidth = 176;
        private const double RepoCardCoverHeight = 228;

        /// <summary>
        /// 一张卡片在界面上的那一份视觉对象。
        ///
        /// <para><b>为什么「只在 NAS 的条目」也做成这个类型</b>：以前它们是页面底部单独一列的
        /// 文字行，结果是同一个游戏可能既有一张「未上传」的卡片、又在那一列里出现一次
        /// （老条目没存游戏 ID + 本地已卸载 → 三级命中键全落空）。现在两边合成**同一面墙**，
        /// 一个游戏只可能对应一张卡片，重复从结构上就不可能出现了。</para>
        /// </summary>
        private sealed class RepoCardVisual
        {
            /// <summary>本地 Playnite 库里的那条记录；为 null 表示「只在远端」。</summary>
            public LocalAppCard Card;

            /// <summary>只在远端的仓库条目；Card 非空时为 null。</summary>
            public AppEntry Remote;

            public FrameworkElement Element;

            /// <summary>体积那个文本块 —— 本地体积是后台量出来的，量完回填这里。</summary>
            public TextBlock SizeLabel;

            public string Name
            {
                get
                {
                    if (Card != null)
                    {
                        return Card.Name ?? string.Empty;
                    }

                    if (Remote == null)
                    {
                        return string.Empty;
                    }

                    return string.IsNullOrWhiteSpace(Remote.Name) ? Remote.Id : Remote.Name;
                }
            }

            public bool RemoteOnly
            {
                get { return Card == null; }
            }

            public bool InRepository
            {
                get { return Remote != null || (Card != null && Card.InRepository); }
            }
        }

        /// <summary>
        /// 「仓库」页：本机 Playnite 库里的游戏 + 只在 NAS 上的条目，**合成同一面卡片墙**。
        ///
        /// <para><b>一张卡片能做的事</b>：未上传的 → 归档到 NAS；已上传的 → 从 NAS 删除；
        /// 从 NAS 拉下来装着的 → 卸载（只删本地）；只在 NAS 的 → 取回本机。</para>
        ///
        /// <para><b>为什么是同一面墙而不是两块</b>：拆成两块就会出现「同一个游戏两张脸」——
        /// 一边说未上传、一边说仓库里有。合成一面之后，一个游戏要么占一张本地卡片、
        /// 要么落在一张「只在 NAS」的卡片里，不可能既此又彼。</para>
        ///
        /// <para><b>为什么是卡片而不是表格</b>：这一页要回答的是「我库里这些游戏，
        /// 哪些已经进仓库了」。表格里一列 Id、一列体积，扫过去认不出谁是谁；
        /// 封面才是用户识别游戏的符号，一眼就知道缺哪几个。</para>
        ///
        /// <para><b>上传 / 下载不再弹模态框</b>：点下去只是丢进传输队列，底部会出现一条
        /// 聚合进度条，想细看就点「下载管理」。归档格式与口令闸门
        /// （<c>VaultPlugin.DeleteRepositoryApp</c>，自检有 IL 级断言盯着）一个字节都没动。</para>
        /// </summary>
        private UIElement BuildRepo()
        {
            var root = new StackPanel();
            repoFilterButtons.Clear();

            // ---- 页头：标题 + 刷新 ----
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };

            var refresh = FlatButton("刷新", null);
            refresh.Click += (s, e) =>
            {
                RefreshHealth();
                RefreshRepo();
            };
            DockPanel.SetDock(refresh, Dock.Right);
            head.Children.Add(refresh);

            var titleStack = new StackPanel();
            titleStack.Children.Add(new TextBlock
            {
                Text = "仓库",
                FontSize = 17,
                FontWeight = FontWeights.Bold,
                Foreground = p.Text
            });
            titleStack.Children.Add(new TextBlock
            {
                Text = "本机库里的游戏与 NAS 上的归档在这里对齐；判定按 Playnite 游戏 ID 优先，"
                     + "依据写在卡片提示里。上传 / 下载丢进队列，不阻塞界面。",
                FontSize = 11.5,
                Foreground = p.TextMuted,
                Margin = new Thickness(0, 3, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
            head.Children.Add(titleStack);
            root.Children.Add(head);

            // ---- 工具条：过滤片 + 搜索框 ----
            var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };

            bar.Children.Add(BuildFilterChip("all", "全部"));
            bar.Children.Add(BuildFilterChip("in", "已上传"));
            bar.Children.Add(BuildFilterChip("out", "未上传"));
            bar.Children.Add(BuildFilterChip("remote", "只在 NAS"));

            var searchHost = new Grid { Width = 210, Margin = new Thickness(8, 0, 0, 0) };
            repoSearch = new TextBox
            {
                Padding = new Thickness(9, 5, 9, 5),
                FontSize = 12,
                Background = p.Surface,
                Foreground = p.Text,
                BorderBrush = p.Border,
                BorderThickness = new Thickness(2),
                CaretBrush = p.Text,
                Text = repoFilterText ?? string.Empty
            };
            repoSearch.TextChanged += (s, e) =>
            {
                repoFilterText = repoSearch.Text ?? string.Empty;
                if (repoSearchHint != null)
                {
                    repoSearchHint.Visibility = repoFilterText.Length == 0
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                }

                ApplyRepoFilter();
            };
            searchHost.Children.Add(repoSearch);

            // 占位提示：WPF 的 TextBox 没有 placeholder，用一层不吃点击的文字盖上去。
            // 用 TextBlock 而不是「把提示写进 Text」是因为后者会让过滤逻辑分不清
            // 「用户想看名字里带『按名字过滤』的游戏」和「用户什么都没输」。
            repoSearchHint = new TextBlock
            {
                Text = "按名字过滤…",
                FontSize = 12,
                Foreground = p.TextMuted,
                Margin = new Thickness(11, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                Visibility = string.IsNullOrEmpty(repoFilterText) ? Visibility.Visible : Visibility.Collapsed
            };
            searchHost.Children.Add(repoSearchHint);

            bar.Children.Add(searchHost);
            root.Children.Add(bar);

            // ---- 汇总 + 反馈 ----
            var info = new StackPanel();
            repoSummary = new TextBlock
            {
                Text = "正在读取仓库索引…",
                FontSize = 11.5,
                Foreground = p.Text,
                TextWrapping = TextWrapping.Wrap
            };
            info.Children.Add(repoSummary);

            repoStatus = new TextBlock
            {
                FontSize = 11.5,
                Foreground = p.TextMuted,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            };
            info.Children.Add(repoStatus);

            // 次要入口：这些是一次性/低频操作，留在页脚不占版面
            var extra = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            var refreshLib = CardButton("从 NAS 刷新库条目", null);
            refreshLib.Margin = new Thickness(0, 0, 6, 6);
            refreshLib.Click += (s, e) => plugin.RefreshLibraryEntries(true);
            extra.Children.Add(refreshLib);

            var admin = CardButton("仓库管理窗口（改管理口令）…", null);
            admin.Margin = new Thickness(0, 0, 6, 6);
            admin.Click += (s, e) => OpenAdmin();
            extra.Children.Add(admin);

            var saveManager = CardButton("存档管理…", null);
            saveManager.Margin = new Thickness(0, 0, 6, 6);
            saveManager.Click += (s, e) => plugin.OpenSaveManager(null);
            extra.Children.Add(saveManager);

            info.Children.Add(extra);
            root.Children.Add(VaultCharts.Card(p, info));

            // ---- 卡片墙 ----
            repoWallGrid = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            repoEmpty = new TextBlock
            {
                FontSize = 11.5,
                Foreground = p.TextMuted,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(2, 6, 0, 0),
                Visibility = Visibility.Collapsed
            };

            var wall = new StackPanel();
            wall.Children.Add(repoWallGrid);
            wall.Children.Add(repoEmpty);
            root.Children.Add(wall);

            StyleFilterChips();
            RefreshRepo();
            return root;
        }

        /// <summary>工具条上那三个过滤片。选中态只在这里改，别处不要碰它们的画刷。</summary>
        private Button BuildFilterChip(string mode, string label)
        {
            var button = new Button
            {
                Content = label,
                Tag = mode,
                Padding = new Thickness(11, 4, 11, 4),
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                BorderThickness = new Thickness(2),
                Background = p.SurfaceAlt,
                BorderBrush = p.Border,
                Foreground = p.Text,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 0, 6, 0)
            };

            button.Click += (s, e) =>
            {
                repoFilterMode = mode;
                StyleFilterChips();
                ApplyRepoFilter();
            };

            repoFilterButtons[mode] = button;
            return button;
        }

        private void StyleFilterChips()
        {
            foreach (var pair in repoFilterButtons)
            {
                var selected = pair.Key == repoFilterMode;
                pair.Value.Background = selected ? p.Accent : p.SurfaceAlt;
                pair.Value.Foreground = selected ? p.AccentInk : p.Text;
                pair.Value.BorderBrush = selected ? p.Accent : p.Border;
            }
        }

        private void SetRepoStatus(string text, bool? ok)
        {
            if (repoStatus == null)
            {
                return;
            }

            repoStatus.Text = text ?? string.Empty;
            repoStatus.Foreground = ok == null ? p.TextMuted : (ok.Value ? p.Success : p.Danger);
        }

        // ---------------------------------------------------------------- 读取

        /// <summary>
        /// 拉索引（后台线程），回 UI 线程对账（Playnite 的库集合不是线程安全的），再画。
        ///
        /// <para>封面另走一趟后台：一屏两百张封面按原图解码要吃掉几百 MB，
        /// 所以统一 <c>DecodePixelWidth</c> 缩到卡片宽度，解码完 <c>Freeze()</c> 再交给 UI。</para>
        /// </summary>
        private void RefreshRepo()
        {
            if (repoLoading || repoWallGrid == null)
            {
                return;
            }

            repoLoading = true;
            repoCards.Clear();
            repoWallGrid.Children.Clear();
            repoEmpty.Visibility = Visibility.Collapsed;
            repoSummary.Text = "正在读取仓库索引…";
            SetRepoStatus(string.Empty, null);

            Task.Run(() =>
            {
                RepositoryIndex index = null;
                string source = null;
                string error = null;

                try
                {
                    index = service.GetIndex(out source, out error);
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    VaultLog.Error("仓库与归档：读取索引失败", ex);
                }

                Dispatcher.Invoke(() => RenderRepo(index, source, error));
            });
        }

        private void RenderRepo(RepositoryIndex index, string source, string error)
        {
            repoLoading = false;
            if (repoWallGrid == null)
            {
                return;
            }

            List<LocalAppCard> cards;
            List<AppEntry> remoteOnly;

            // 对账要遍历 Playnite 的库 —— 必须回 UI 线程做
            try
            {
                cards = plugin.BuildLocalAppCards(index);
                remoteOnly = plugin.FindOrphanRepoApps(index, cards);
            }
            catch (Exception ex)
            {
                VaultLog.Error("仓库：对账失败", ex);
                repoWallGrid.Children.Clear();
                repoSummary.Text = "读不出本机游戏列表。";
                repoEmpty.Text = "对账失败：" + ex.Message;
                repoEmpty.Visibility = Visibility.Visible;
                SetRepoStatus(string.Empty, null);
                return;
            }

            // ---- 汇总 ----
            var uploaded = cards.Count(c => c.InRepository);
            var repoCount = index == null ? 0 : index.Apps.Count;
            var bytes = index == null ? 0 : index.Apps.Sum(a => a.TotalBytes);
            repoSummary.Text = string.Format(
                "本机库 {0} 个游戏：{1} 个已归档到 NAS、{2} 个还没传。"
                + "NAS 上共 {3} 个归档（其中 {4} 个本机库里没有对应的游戏），占 {5}。（索引来源：{6}）",
                cards.Count, uploaded, cards.Count - uploaded,
                repoCount, remoteOnly.Count,
                VaultAdminWindow.FormatSize(bytes),
                string.IsNullOrEmpty(source) ? "未知" : source);

            SetRepoStatus(string.Empty, null);
            if (!string.IsNullOrEmpty(error))
            {
                SetRepoStatus("注意：" + error, null);
            }

            // ---- 卡片墙：本地游戏 + 只在 NAS 的条目，合成一面 ----
            // 合成之后「同一个游戏两张脸」从结构上就不可能再出现：
            // 一个游戏要么落在一张本地卡片上，要么落在一张「只在 NAS」的卡片上。
            repoCards.Clear();
            repoWallGrid.Children.Clear();

            var pendingCovers = new List<KeyValuePair<Image, string>>();
            foreach (var card in cards)
            {
                var visual = BuildAppCard(card, pendingCovers);
                repoCards.Add(visual);
                repoWallGrid.Children.Add(visual.Element);
            }

            foreach (var app in remoteOnly)
            {
                var visual = BuildRemoteCard(app);
                repoCards.Add(visual);
                repoWallGrid.Children.Add(visual.Element);
            }

            ApplyRepoFilter();

            // ---- 本地体积：后台量，量完只改那几个文本块 ----
            var measure = repoCards
                .Where(v => v.Card != null && v.Card.IsInstalled
                            && v.Card.LocalBytes <= 0 && v.SizeLabel != null)
                .ToList();
            if (measure.Count > 0)
            {
                MeasureLocalSizes(measure);
            }

            if (pendingCovers.Count > 0)
            {
                LoadCovers(pendingCovers);
            }
        }

        /// <summary>
        /// 后台量本地安装目录的体积。
        ///
        /// <para>为什么要异步：一个 100GB 的游戏要遍历几万个文件，放在 UI 线程上就是肉眼可见的卡顿，
        /// 而用户看这一页主要是为了「谁还没传」—— 体积晚几百毫秒出来完全不影响判断。</para>
        /// </summary>
        private void MeasureLocalSizes(List<RepoCardVisual> visuals)
        {
            Task.Run(() =>
            {
                var measured = new List<KeyValuePair<RepoCardVisual, long>>();
                foreach (var visual in visuals)
                {
                    var dir = visual.Card == null ? null : visual.Card.InstallDir;
                    if (string.IsNullOrWhiteSpace(dir))
                    {
                        continue;
                    }

                    var size = MeasureDirectory(dir);
                    if (size > 0)
                    {
                        measured.Add(new KeyValuePair<RepoCardVisual, long>(visual, size));
                    }
                }

                if (measured.Count == 0)
                {
                    return;
                }

                Dispatcher.Invoke(() =>
                {
                    foreach (var pair in measured)
                    {
                        if (pair.Key.Card != null)
                        {
                            pair.Key.Card.LocalBytes = pair.Value;
                        }

                        if (pair.Key.SizeLabel != null)
                        {
                            pair.Key.SizeLabel.Text = DescribeCardSize(pair.Key);
                        }
                    }
                });
            });
        }

        private static long MeasureDirectory(string dir)
        {
            long total = 0;
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        total += new FileInfo(file).Length;
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
            }
            catch (Exception ex)
            {
                VaultLog.Warn("统计目录体积失败（" + dir + "）：" + ex.Message);
            }

            return total;
        }

        /// <summary>
        /// 卡片上那个体积字的统一出口。
        ///
        /// <para>优先级：**本机占用 → 仓库体积 → 状态字**。用户问「这游戏多大」时，
        /// 他心里想的是占了自己多少磁盘，而不是 NAS 上那份压缩后多大 ——
        /// 后者放在悬停提示里就够了。</para>
        /// </summary>
        private string DescribeCardSize(RepoCardVisual visual)
        {
            if (visual == null)
            {
                return string.Empty;
            }

            if (visual.RemoteOnly)
            {
                return VaultAdminWindow.FormatSize(visual.Remote.TotalBytes);
            }

            var card = visual.Card;
            if (card.LocalBytes > 0)
            {
                return VaultAdminWindow.FormatSize(card.LocalBytes);
            }

            if (card.InRepository)
            {
                return VaultAdminWindow.FormatSize(card.RepoBytes);
            }

            return card.IsInstalled ? "统计中…" : "本机未安装";
        }

        /// <summary>封面在后台上解码，解完一次性贴回 UI。</summary>
        private void LoadCovers(List<KeyValuePair<Image, string>> pending)
        {
            Task.Run(() =>
            {
                var loaded = new List<KeyValuePair<Image, BitmapSource>>();
                foreach (var item in pending)
                {
                    var bitmap = LoadCover(item.Value, (int)RepoCardWidth * 2);
                    if (bitmap != null)
                    {
                        loaded.Add(new KeyValuePair<Image, BitmapSource>(item.Key, bitmap));
                    }
                }

                if (loaded.Count == 0)
                {
                    return;
                }

                Dispatcher.Invoke(() =>
                {
                    foreach (var item in loaded)
                    {
                        item.Key.Source = item.Value;
                    }
                });
            });
        }

        /// <summary>
        /// 读一张封面。<c>OnLoad</c> 是必须的 —— 默认的延迟加载会**握着文件句柄**，
        /// 而游戏正在跑的时候那个文件是活的，锁住它轻则报错重则影响游戏。
        /// </summary>
        private static BitmapSource LoadCover(string path, int decodeWidth)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            try
            {
                using (var stream = new System.IO.MemoryStream(File.ReadAllBytes(path)))
                {
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.DecodePixelWidth = decodeWidth;
                    image.StreamSource = stream;
                    image.EndInit();
                    image.Freeze();          // 冻结之后才能跨线程交给 UI
                    return image;
                }
            }
            catch (Exception ex)
            {
                VaultLog.Warn("仓库与归档：读封面失败（" + path + "）：" + ex.Message);
                return null;
            }
        }

        // ---------------------------------------------------------------- 卡片

        private RepoCardVisual BuildAppCard(LocalAppCard card, List<KeyValuePair<Image, string>> pendingCovers)
        {
            var body = new StackPanel();

            // ---- 封面 ----
            var cover = new Grid
            {
                Width = RepoCardWidth,
                Height = RepoCardCoverHeight,
                Background = p.SurfaceAlt,
                ClipToBounds = true
            };

            if (!string.IsNullOrEmpty(card.CoverPath))
            {
                var image = new Image { Stretch = Stretch.UniformToFill };
                cover.Children.Add(image);
                pendingCovers.Add(new KeyValuePair<Image, string>(image, card.CoverPath));
            }
            else
            {
                // 没封面就退化成一块占位：一个首字，比一片灰更有「这是哪个游戏」的暗示
                cover.Children.Add(new TextBlock
                {
                    Text = InitialOf(card.Name),
                    FontSize = 42,
                    FontWeight = FontWeights.Bold,
                    Foreground = p.BorderStrong,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            body.Children.Add(cover);

            // ---- 文字区 ----
            var text = new StackPanel { Margin = new Thickness(10, 9, 10, 10) };

            text.Children.Add(new TextBlock
            {
                Text = card.Name ?? string.Empty,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = p.Text,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 34
            });

            var stateRow = new Grid { Margin = new Thickness(0, 7, 0, 0) };
            stateRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            stateRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // 状态徽章：绿=已归档、橙=该归档了。**这两个都是实心色块**，
            // 所以字色必须按底色亮度挑（VaultPalette.OnStatus），
            // 否则浅色档下橙底压深字只有 3:1，糊成一片。
            var statusBrush = card.InRepository
                ? p.Success
                : (card.IsInstalled ? p.Warning : null);

            if (statusBrush != null)
            {
                var chip = new Border
                {
                    Background = statusBrush,
                    Padding = new Thickness(7, 2, 7, 2),
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = BuildCardTip(card)
                };
                chip.Child = new TextBlock
                {
                    Text = card.InRepository ? "已在 NAS" : "未归档",
                    FontSize = 10.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = p.OnStatus(statusBrush)
                };
                Grid.SetColumn(chip, 0);
                stateRow.Children.Add(chip);
            }
            else
            {
                var missing = new TextBlock
                {
                    Text = "本机未安装",
                    FontSize = 10.5,
                    Foreground = p.TextMuted,
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = BuildCardTip(card)
                };
                Grid.SetColumn(missing, 0);
                stateRow.Children.Add(missing);
            }

            // 体积字先留空，建完 visual 之后统一走 DescribeCardSize 填 ——
            // 本地体积是后台量出来的，描述规则只能有一处，不能在这里再写一遍。
            var size = new TextBlock
            {
                Text = string.Empty,
                FontSize = 10.5,
                Foreground = p.TextMuted,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(size, 1);
            stateRow.Children.Add(size);
            text.Children.Add(stateRow);

            // ---- 操作 ----
            var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };

            if (card.InRepository)
            {
                var captured = card;

                var remove = CardButton("从 NAS 删除", p.Danger);
                remove.Margin = new Thickness(0, 0, 6, 6);
                remove.ToolTip = "从 NAS 删除这条归档（要管理口令）；本地已下载的文件不动";
                remove.Click += (s, e) => ConfirmDeleteRepoApp(
                    captured.RepoAppId, captured.Name, captured.RepoBytes);
                actions.Children.Add(remove);

                // 只有「从仓库拉下来装着的」才给卸载 —— 从 Steam 之类装的原生游戏
                // 不归我们管，删它的目录是越权。
                if (captured.Downloaded && captured.IsInstalled)
                {
                    var uninstall = CardButton("卸载本地", null);
                    uninstall.Margin = new Thickness(0, 0, 6, 6);
                    uninstall.ToolTip = "删掉本地这份腾磁盘；NAS 上的归档不动，之后可以再取回来";
                    uninstall.Click += (s, e) => ConfirmUninstall(captured);
                    actions.Children.Add(uninstall);
                }
            }
            else if (card.IsInstalled)
            {
                var captured = card;
                var upload = CardButton("归档到 NAS", p.Accent);
                upload.Margin = new Thickness(0, 0, 6, 6);
                upload.ToolTip = "把这个游戏打包上传到 NAS（本地文件不会被删除）；任务会进「下载管理」";
                upload.Click += (s, e) =>
                {
                    plugin.EnqueueArchiveById(captured.GameId);
                    OnTransfersChanged();
                };
                actions.Children.Add(upload);
            }

            if (actions.Children.Count > 0)
            {
                text.Children.Add(actions);
            }

            body.Children.Add(text);

            var frame = new Border
            {
                Width = RepoCardWidth,
                Margin = new Thickness(0, 0, 10, 10),
                Background = p.Surface,
                BorderBrush = p.Border,
                BorderThickness = new Thickness(2),
                Child = body,
                ToolTip = BuildCardTip(card)
            };

            var visual = new RepoCardVisual { Card = card, Element = frame, SizeLabel = size };
            size.Text = DescribeCardSize(visual);
            return visual;
        }

        private string BuildCardTip(LocalAppCard card)
        {
            var lines = new List<string> { card.Name ?? string.Empty };

            if (!string.IsNullOrEmpty(card.MatchNote))
            {
                lines.Add("判定：" + card.MatchNote);
            }

            if (card.InRepository)
            {
                lines.Add("仓库条目：" + card.RepoAppId
                          + "（NAS 上占 " + VaultAdminWindow.FormatSize(card.RepoBytes) + "）");
            }

            if (card.LocalBytes > 0)
            {
                lines.Add("本机占用：" + VaultAdminWindow.FormatSize(card.LocalBytes));
            }

            if (!string.IsNullOrEmpty(card.InstallDir))
            {
                lines.Add("本机目录：" + card.InstallDir);
            }

            if (card.Downloaded)
            {
                lines.Add("这一份是从 NAS 取回的，可以卸载（只删本地）。");
            }

            return string.Join("\n", lines);
        }

        private static string InitialOf(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "?";
            }

            var trimmed = name.Trim();
            // 用 TextElement 的字符而不是 char：代理对（emoji、罕见汉字）截半个 char 会变成方块
            var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(trimmed);
            return enumerator.MoveNext() ? enumerator.GetTextElement() : "?";
        }

        /// <summary>卡片下面那种小按钮：描边 + 透明底，比工具条按钮小一号。</summary>
        private Button CardButton(string text, Brush accent)
        {
            return new Button
            {
                Content = text,
                Padding = new Thickness(10, 4, 10, 4),
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                BorderThickness = new Thickness(2),
                BorderBrush = accent == null ? p.Border : accent,
                Foreground = accent == null ? p.Text : accent,
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Left
            };
        }

        // ---------------------------------------------------------------- 过滤

        private void ApplyRepoFilter()
        {
            if (repoWallGrid == null)
            {
                return;
            }

            var visible = 0;
            foreach (var visual in repoCards)
            {
                var pass = true;

                if (repoFilterMode == "remote")
                {
                    // 「只在 NAS」= 本机库里没有对应游戏的归档
                    pass = visual.RemoteOnly;
                }
                else if (repoFilterMode == "in")
                {
                    pass = visual.InRepository;
                }
                else if (repoFilterMode == "out")
                {
                    pass = visual.Card != null && !visual.Card.InRepository;
                }

                if (pass && repoFilterText.Length > 0)
                {
                    pass = visual.Name.IndexOf(
                        repoFilterText, StringComparison.CurrentCultureIgnoreCase) >= 0;
                }

                visual.Element.Visibility = pass ? Visibility.Visible : Visibility.Collapsed;
                if (pass)
                {
                    visible++;
                }
            }

            if (repoEmpty == null)
            {
                return;
            }

            if (repoCards.Count == 0)
            {
                repoEmpty.Text = "本机 Playnite 库里还没有游戏，NAS 上也没有归档。";
                repoEmpty.Visibility = Visibility.Visible;
                return;
            }

            if (visible == 0)
            {
                repoEmpty.Text = "没有符合当前过滤条件的游戏。";
                repoEmpty.Visibility = Visibility.Visible;
                return;
            }

            repoEmpty.Visibility = Visibility.Collapsed;
        }

        // ---------------------------------------------------------------- 删除

        private void ConfirmDeleteRepoApp(string appId, string name, long bytes)
        {
            if (string.IsNullOrWhiteSpace(appId))
            {
                return;
            }

            var confirm = MessageBox.Show(
                "确定要从 NAS 删除「" + name + "」吗？\n\n"
                + "目录：apps/" + appId + "/\n"
                + "占用：" + VaultAdminWindow.FormatSize(bytes) + "\n\n"
                + "远端归档会被彻底删除（含全部区块文件），之后无法再安装。\n"
                + "本地已下载的游戏文件不受影响。\n\n此操作不可撤销。",
                "删除仓库应用",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            var owner = Window.GetWindow(this);
            var password = PasswordPrompt.Ask(owner, "输入管理口令",
                "删除「" + name + "」需要管理口令：", false);
            if (password == null)
            {
                SetRepoStatus("已取消删除。", null);
                return;
            }

            SetRepoStatus("正在校验口令并删除…", null);
            repoLoading = true;

            Task.Run(() =>
            {
                RepositoryDeleteResult outcome;
                try
                {
                    // 唯一那道闸门。别在这里自己调 service.RemoveApp ——
                    // 自检有一条断言盯着「RemoveApp 的调用方只有这一处」。
                    outcome = plugin.DeleteRepositoryApp(appId, password);
                }
                catch (Exception ex)
                {
                    VaultLog.Error("删除仓库应用失败：" + appId, ex);
                    outcome = RepositoryDeleteResult.Fail(ex.Message);
                }

                Dispatcher.Invoke(() =>
                {
                    repoLoading = false;

                    if (outcome.Ok)
                    {
                        SetRepoStatus(string.Format("已删除「{0}」，清理了 {1} 个远端文件。",
                            name, outcome.RemovedFiles), true);
                        RefreshRepo();
                        return;
                    }

                    if (outcome.PasswordWrong)
                    {
                        SetRepoStatus("口令不正确，删除已取消。", false);
                        return;
                    }

                    if (outcome.PasswordNotSet)
                    {
                        SetRepoStatus("仓库还没有设置管理口令，先用上面的「仓库管理窗口」设一个。", false);
                        return;
                    }

                    SetRepoStatus(string.IsNullOrEmpty(outcome.Error) ? "删除失败。" : outcome.Error, false);
                });
            });
        }

        // ---------------------------------------------------------------- 卸载

        /// <summary>
        /// 卸载：只删本地那份，NAS 上的归档一个字节都不动。
        ///
        /// <para><b>为什么不要口令</b>：口令闸门只拦**不可逆**的动作（删远端归档）。
        /// 卸载本地是可恢复的 —— 归档还在，随时能再取回来，而且「腾磁盘」是日常操作，
        /// 多一道口令只会在每次清盘时挡一下。（别把这里改成走删除闸门：
        /// 闸门只有一道，不是「删东西就要口令」。）</para>
        /// </summary>
        private void ConfirmUninstall(LocalAppCard card)
        {
            if (card == null || string.IsNullOrWhiteSpace(card.RepoAppId))
            {
                return;
            }

            var dir = string.IsNullOrWhiteSpace(card.DownloadedDir)
                ? card.InstallDir
                : card.DownloadedDir;

            var confirm = MessageBox.Show(
                "确定删掉「" + card.Name + "」的本地文件吗？\n\n"
                + "目录：" + (string.IsNullOrWhiteSpace(dir) ? "(默认安装目录)" : dir) + "\n\n"
                + "只删本地这份；NAS 上的归档保留，之后随时可以再取回来。\n"
                + "删完 Playnite 里这个条目会变成「未安装」。",
                "卸载本地文件",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            plugin.EnqueueUninstall(card.RepoAppId, card.Name, dir);
            SetRepoStatus("已加入队列：卸载「" + card.Name + "」的本地文件。", null);
            OnTransfersChanged();
        }

        // ---------------------------------------------------------------- 仅在 NAS 的条目

        /// <summary>
        /// 「只在 NAS」的卡片：本机库里找不到对应游戏（换过机器、或者游戏被从库里删了）。
        ///
        /// <para>它长得和本地卡片一样，只是封面退化成首字占位、动作换成「取回本机 / 从 NAS 删除」。
        /// 做成卡片而不是一行文字，是为了让它和本地卡片**在同一面墙上有同样的分量** ——
        /// 之前做成下面单独一列时，用户根本注意不到它能下载。</para>
        /// </summary>
        private RepoCardVisual BuildRemoteCard(AppEntry app)
        {
            var name = string.IsNullOrWhiteSpace(app.Name) ? app.Id : app.Name;
            var body = new StackPanel();

            // ---- 封面：没有封面就用首字占位 ----
            var cover = new Grid
            {
                Width = RepoCardWidth,
                Height = RepoCardCoverHeight,
                Background = p.SurfaceAlt,
                ClipToBounds = true
            };
            cover.Children.Add(new TextBlock
            {
                Text = InitialOf(name),
                FontSize = 42,
                FontWeight = FontWeights.Bold,
                Foreground = p.BorderStrong,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });

            // 角上挂一个「只在 NAS」的标记 —— 它跟「本机有、还没传」是相反的状态，
            // 光看封面分不出来。
            var corner = p.Info;
            var badge = new Border
            {
                Background = corner,
                Padding = new Thickness(7, 2, 7, 2),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(8, 8, 0, 0)
            };
            badge.Child = new TextBlock
            {
                Text = "只在 NAS",
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = p.OnStatus(corner)
            };
            cover.Children.Add(badge);
            body.Children.Add(cover);

            // ---- 文字区 ----
            var text = new StackPanel { Margin = new Thickness(10, 9, 10, 10) };

            text.Children.Add(new TextBlock
            {
                Text = name,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = p.Text,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 34
            });

            var stateRow = new Grid { Margin = new Thickness(0, 7, 0, 0) };
            stateRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            stateRow.ColumnDefinitions.Add(
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var missing = new TextBlock
            {
                Text = "本机库里没有",
                FontSize = 10.5,
                Foreground = p.TextMuted,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(missing, 0);
            stateRow.Children.Add(missing);

            var size = new TextBlock
            {
                FontSize = 10.5,
                Foreground = p.TextMuted,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(size, 1);
            stateRow.Children.Add(size);
            text.Children.Add(stateRow);

            // ---- 操作 ----
            var actions = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };

            var fetch = CardButton("取回本机", p.Accent);
            fetch.Margin = new Thickness(0, 0, 6, 6);
            fetch.ToolTip = "从 NAS 把这份归档解到本地默认目录；任务会进「下载管理」";
            fetch.Click += (s, e) =>
            {
                plugin.EnqueueInstall(app.Id, name, null);
                OnTransfersChanged();
            };
            actions.Children.Add(fetch);

            var remove = CardButton("从 NAS 删除", p.Danger);
            remove.Margin = new Thickness(0, 0, 6, 6);
            remove.ToolTip = "从 NAS 删除 apps/" + app.Id + "/";
            remove.Click += (s, e) => ConfirmDeleteRepoApp(app.Id, name, app.TotalBytes);
            actions.Children.Add(remove);

            text.Children.Add(actions);
            body.Children.Add(text);

            var frame = new Border
            {
                Width = RepoCardWidth,
                Margin = new Thickness(0, 0, 10, 10),
                Background = p.Surface,
                BorderBrush = p.Info,
                BorderThickness = new Thickness(2),
                Child = body,
                ToolTip = name + "\n仓库条目：" + app.Id
                          + "（" + VaultAdminWindow.FormatSize(app.TotalBytes) + "）\napps/" + app.Id + "/"
            };

            var visual = new RepoCardVisual { Remote = app, Element = frame, SizeLabel = size };
            size.Text = DescribeCardSize(visual);
            return visual;
        }

        // ================================================================ 设置

        private UIElement BuildSettings()
        {
            var root = new StackPanel();
            root.Children.Add(new TextBlock
            {
                Text = "设置",
                FontSize = 17,
                FontWeight = FontWeights.Bold,
                Foreground = p.Text,
                Margin = new Thickness(0, 0, 0, 10)
            });

            // 容器本身不再带一层底色与描边 —— 设置界面内部已经是四个带描边的模块卡片了，
            // 再套一层就是「卡中卡」，看起来像两层没对齐。
            var host = new Border { Padding = new Thickness(0) };

            try
            {
                // 侧边栏这一页是插件自己嵌的，Playnite 不会替我们跑 ISettings 的
                // BeginEdit 生命周期（那套只服务于「扩展设置」页）。不显式打快照的话，
                // 「放弃改动」就没有可回退的基准。
                settingsVm.BeginEdit();

                host.Child = new VaultSettingsView(settingsVm, service, true);

                // 明暗档位一改就得重建这一页 —— 颜色是建控件时烘进去的。
                if (!settingsSavedHooked)
                {
                    settingsSavedHooked = true;
                    settingsVm.SettingsSaved += RebuildForTheme;
                }
            }
            catch (Exception ex)
            {
                VaultLog.Error("侧边栏页：内嵌设置界面失败", ex);
                host.Child = VaultCharts.Muted(p, "内嵌设置界面失败：" + ex.Message);
            }

            root.Children.Add(host);

            // 保存按钮：Playnite 自己的「扩展设置」页面带保存/取消，但**这一页是插件自己嵌的**，
            // 没有宿主提供的按钮。少了它，在这里改的任何东西都只留在内存里 ——
            // 包括刚加的明暗档位，等于做了个改不动的开关。
            var saveRow = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };

            var saveButton = FlatButton("保存设置", p.Accent);
            saveButton.Margin = new Thickness(0, 0, 10, 8);
            saveButton.Click += (s, e) => SaveSettings();
            saveRow.Children.Add(saveButton);

            var reloadButton = FlatButton("放弃改动", null);
            reloadButton.Margin = new Thickness(0, 0, 10, 8);
            reloadButton.Click += (s, e) =>
            {
                settingsVm.CancelEdit();
                settingsVm.BeginEdit();
                RebuildForTheme();
                saveStatusText = "已放弃未保存的改动";
                saveStatusOk = null;
                if (saveStatus != null)
                {
                    saveStatus.Text = saveStatusText;
                    saveStatus.Foreground = p.TextMuted;
                }
            };
            saveRow.Children.Add(reloadButton);

            saveStatus = new TextBlock
            {
                Text = saveStatusText ?? string.Empty,
                FontSize = 11,
                Foreground = saveStatusOk == null
                    ? p.TextMuted
                    : (saveStatusOk.Value ? p.Success : p.Danger),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 8)
            };
            saveRow.Children.Add(saveStatus);

            root.Children.Add(saveRow);
            return root;
        }

        /// <summary>
        /// 落盘。
        ///
        /// <para>三条比原来多做的事：</para>
        /// <list type="number">
        /// <item>**先校验**（地址要带协议头、数值要在范围内）—— 校验不过就不落盘，
        /// 并直接把第一条错误写出来，而不是闷头存下一个永远连不上的地址。</item>
        /// <item>**拿到真实的成败**（<c>SaveSettingsNow</c>）—— 以前写盘失败只会进日志，
        /// 界面照样显示「已保存」，用户看到的就是「保存没生效」。</item>
        /// <item>**存完重打快照** —— <c>EndEdit</c> 之后编辑对象与已保存对象是同一个引用，
        /// 不重打快照的话「放弃改动」就再也回不去了。</item>
        /// </list>
        ///
        /// <para>保存会触发 <see cref="RebuildForTheme"/>（换色需要重建视觉树），
        /// 所以提示语先写进字段，重建后新控件会自己带上它。</para>
        /// </summary>
        private void SaveSettings()
        {
            List<string> errors;
            if (!settingsVm.VerifySettings(out errors) && errors.Count > 0)
            {
                saveStatusOk = false;
                saveStatusText = "还不能保存：" + errors[0]
                    + (errors.Count > 1 ? "（还有 " + (errors.Count - 1) + " 项要改）" : string.Empty);
            }
            else
            {
                string error;
                if (plugin.SaveSettingsNow(settingsVm, out error))
                {
                    saveStatusOk = true;
                    saveStatusText = "已保存并生效 " + DateTime.Now.ToString("HH:mm:ss");

                    // 重新打快照：否则 editing 与 service.Settings 是同一个对象，
                    // 之后任何一次键入都直接改到「已保存」那份上。
                    settingsVm.BeginEdit();
                }
                else
                {
                    saveStatusOk = false;
                    saveStatusText = "保存失败：" + (error ?? "未知错误");
                }
            }

            if (saveStatus != null)
            {
                saveStatus.Text = saveStatusText;
                saveStatus.Foreground = saveStatusOk == true ? p.Success : p.Danger;
            }

            // 保存可能改了明暗档位；重建那条路会刷新配色，这里兜一下没重建的情况
            RefreshHealth();
        }

        // ================================================================ 小部件

        /// <summary>
        /// 扁平按钮。不传强调色 = 中性按钮（Surface 底 + 描边），
        /// 传了就是主操作（强调色底 + 深色字）。
        /// </summary>
        private Button FlatButton(string text, Brush accent)
        {
            var primary = accent != null;
            return new Button
            {
                Content = text,
                Padding = new Thickness(13, 7, 13, 7),
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                BorderThickness = primary ? new Thickness(0) : new Thickness(2),
                BorderBrush = p.Border,
                Background = primary ? accent : p.SurfaceAlt,
                Foreground = primary ? p.AccentInk : p.Text,
                Cursor = Cursors.Hand
            };
        }

        // ================================================================ 数据

        /// <summary>一次统计快照。后台线程只填数据，画图回 UI 线程做。</summary>
        private sealed class VaultStats
        {
            public int Apps;
            public long RepoBytes;
            public string Source;
            public string Error;
            public ThemeLibraryStats Themes;
            public List<KeyValuePair<string, long>> TopApps;
        }

        private void RefreshStats()
        {
            if (statsLoading)
            {
                return;
            }

            statsLoading = true;

            RenderOverview(null);

            Task.Run(() =>
            {
                var stats = new VaultStats();
                try
                {
                    var index = service.GetIndex(out stats.Source, out stats.Error);
                    if (index != null)
                    {
                        stats.Apps = index.Apps.Count;
                        stats.RepoBytes = index.Apps.Sum(a => a.TotalBytes);

                        stats.TopApps = index.Apps
                            .Where(a => a.TotalBytes > 0)
                            .OrderByDescending(a => a.TotalBytes)
                            .Take(6)
                            .Select(a => new KeyValuePair<string, long>(
                                string.IsNullOrEmpty(a.Name) ? a.Id : a.Name, a.TotalBytes))
                            .ToList();
                    }
                }
                catch (Exception ex)
                {
                    stats.Error = ex.Message;
                    VaultLog.Warn("侧边栏页：读仓库索引失败：" + ex.Message);
                }

                try
                {
                    stats.Themes = ScanThemes();
                }
                catch (Exception ex)
                {
                    VaultLog.Warn("侧边栏页：统计主题目录失败：" + ex.Message);
                }

                Dispatcher.Invoke(() =>
                {
                    statsLoading = false;
                    RenderOverview(stats);
                });
            });
        }

        private ThemeLibraryStats ScanThemes()
        {
            var stats = new ThemeLibraryStats();
            if (!Directory.Exists(themesRoot))
            {
                stats.RootMissing = true;
                return stats;
            }

            foreach (var mode in new[] { "Desktop", "Fullscreen" })
            {
                var dir = Path.Combine(themesRoot, mode);
                if (!Directory.Exists(dir))
                {
                    continue;
                }

                var count = 0;
                foreach (var themeDir in Directory.GetDirectories(dir))
                {
                    count++;
                    foreach (var file in Directory.GetFiles(themeDir, "*", SearchOption.AllDirectories))
                    {
                        try
                        {
                            stats.TotalBytes += new FileInfo(file).Length;
                        }
                        catch (IOException)
                        {
                        }
                        catch (UnauthorizedAccessException)
                        {
                        }
                    }
                }

                if (mode == "Desktop")
                {
                    stats.DesktopThemes = count;
                }
                else
                {
                    stats.FullscreenThemes = count;
                }
            }

            foreach (var mode in new[] { "Desktop", "Fullscreen" })
            {
                var dir = Path.Combine(themesRoot, mode);
                if (!Directory.Exists(dir))
                {
                    continue;
                }

                stats.ConflictCopies += Directory.GetDirectories(dir)
                    .Count(d => Path.GetFileName(d).Contains(".conflict-"));
            }

            var stateFile = Path.Combine(service.DataPath, "theme-sync-state.json");
            if (File.Exists(stateFile))
            {
                stats.LastSyncUtc = File.GetLastWriteTimeUtc(stateFile);
            }

            return stats;
        }

        // ================================================================ 动作

        /// <summary>
        /// 打开仓库管理。**必须走插件那条公开入口**，不能直接 new 窗口：
        /// 那里有一道管理口令闸门，而仓库管理里有不可逆的删除。
        /// （自检里有一条 IL 级断言盯着这件事，见 VaultSelfTest 第 6 组。）
        /// </summary>
        private void OpenAdmin()
        {
            try
            {
                plugin.OpenRepositoryManager();
            }
            catch (Exception ex)
            {
                VaultLog.Error("打开仓库管理窗口失败", ex);
                MessageBox.Show("打开仓库管理失败：" + ex.Message, "Playnite Vault");
            }
        }
    }
}
