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
    /// <para><b>版面结构</b>（v1.8 起）：</para>
    /// <code>
    /// ┌──────────┬────────────────────────────────────────┐
    /// │ 仓库管家  │                                  ●     │ ← 状态点（无标题栏）
    /// │ Vault@1.8│────────────────────────────────────────│
    /// │ 概览      │                                        │
    /// │ 主题同步  │              内容（滚动）               │
    /// │ 仓库与归档│                                        │
    /// │ 设置      │                                        │
    /// └──────────┴────────────────────────────────────────┘
    /// </code>
    /// 顶栏被整个去掉了：标题在左边栏已经有了，仓库地址属于「设置」里的事，
    /// 而原来贴在顶栏右边的「已连接 / 刷新」正好压在 Playnite 的最小化/最大化/关闭按钮上
    /// ——点刷新会顺手关掉 Playnite。所以这里只留一个不占版面的状态点。
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
        private Button refreshButton;
        private bool statsLoading;

        // ---- 设置页的保存反馈（重建视觉树后要能续上，所以存在字段里） ----
        private TextBlock saveStatus;
        private string saveStatusText;
        private bool? saveStatusOk;

        // ---- 仓库与归档：卡片墙 + 内联删除 ----
        private WrapPanel repoWallGrid;
        private TextBlock repoEmpty;
        private StackPanel repoOrphanHost;
        private Border repoOrphanCard;
        private TextBlock repoSummary;
        private TextBlock repoStatus;
        private TextBox repoSearch;
        private TextBlock repoSearchHint;
        private readonly Dictionary<string, Button> repoFilterButtons = new Dictionary<string, Button>();
        private readonly List<RepoCardVisual> repoCards = new List<RepoCardVisual>();
        private string repoFilterMode = "all";
        private string repoFilterText = string.Empty;
        private bool repoLoading;

        // ---- 主题同步（这一页还没按 v1.8 改完，先原样留着） ----
        private TextBlock syncResult;
        private TextBlock themeRootText;
        private RadioButton dirUpload;
        private RadioButton dirDownload;
        private RadioButton dirBoth;
        private CheckBox chkDryRun;
        private CheckBox chkForce;
        private StackPanel conflictList;

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

            navPanel.Children.Add(new TextBlock
            {
                Text = "仓库管家",
                FontSize = 18,
                FontWeight = FontWeights.Bold,
                Foreground = p.Text,
                Margin = new Thickness(2, 0, 0, 3)
            });

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
            AddSection("themes", "主题同步");
            AddSection("repo", "仓库与归档");
            AddSection("settings", "设置");

            var navScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = navPanel
            };
            Grid.SetColumn(navScroll, 0);

            // 右侧：状态点那一行（透明、无边框）+ 内容
            var right = new Grid();
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var statusRow = new Grid
            {
                // 不给底色、不给下边框 —— 它在版面上只是一点留白，
                // 不是「一条栏」。这样既满足「右上角有个小点」，也不违背「把那一栏去掉」。
                Margin = new Thickness(0, 12, 20, 0)
            };
            var dot = BuildHealthDot();
            dot.HorizontalAlignment = HorizontalAlignment.Right;
            dot.VerticalAlignment = VerticalAlignment.Top;
            statusRow.Children.Add(dot);
            Grid.SetRow(statusRow, 0);
            right.Children.Add(statusRow);

            body = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
            var bodyScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(20, 4, 20, 24),
                Content = body
            };
            Grid.SetRow(bodyScroll, 1);
            right.Children.Add(bodyScroll);

            Grid.SetColumn(right, 1);
            grid.Children.Add(nav);
            nav.Child = navScroll;
            grid.Children.Add(right);

            Content = grid;
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

            if (key == "overview")
            {
                RefreshStats();
            }
        }

        // ================================================================ 状态点

        /// <summary>
        /// 右上角那个小圆点：外圈是个会呼吸的环，里面是实心点。
        /// 点它重新探测一次 —— 这是「刷新」的第二入口（主入口在概览页里）。
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

            Brush color;
            string title;
            switch (result.State)
            {
                case VaultHealthState.Ok:
                    color = p.Success;
                    title = "已连接";
                    break;
                case VaultHealthState.Timeout:
                    color = p.Warning;
                    title = "连接超时";
                    break;
                case VaultHealthState.Failed:
                    color = p.Danger;
                    title = "连接失败";
                    break;
                case VaultHealthState.NotConfigured:
                    color = p.TextMuted;
                    title = "未配置仓库";
                    break;
                default:
                    color = p.Info;
                    title = "正在探测";
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

            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
            refreshButton = FlatButton("刷新", null);
            refreshButton.Click += (s, e) =>
            {
                RefreshHealth();
                RefreshStats();
            };
            DockPanel.SetDock(refreshButton, Dock.Right);
            head.Children.Add(refreshButton);

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
                Text = "仓库、主题、连通状态一眼看完；上面那个小圆点是同一份数据。",
                FontSize = 11.5,
                Foreground = p.TextMuted,
                Margin = new Thickness(0, 3, 0, 0)
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
                    return "连接超时 —— 多数是 NAS 没开机或网络不通，可以把鼠标放到右上角小点上看看详情";
                case VaultHealthState.Failed:
                    return "连接失败 —— 地址、账号口令或证书有问题，鼠标放到右上角小点上看排查步骤";
                case VaultHealthState.NotConfigured:
                    return "还没配置 WebDAV 地址（去「设置」里填）";
                default:
                    return "正在探测…";
            }
        }

        // ================================================================ 主题同步

        private UIElement BuildThemeSync()
        {
            var root = new StackPanel();
            root.Children.Add(new TextBlock
            {
                Text = "主题同步",
                FontSize = 17,
                FontWeight = FontWeights.Bold,
                Foreground = p.Text,
                Margin = new Thickness(0, 0, 0, 8)
            });
            root.Children.Add(VaultCharts.Muted(p,
                "把 Playnite 的 Themes 目录和仓库的 themes/ 目录对齐。"
                + "远端是「普通文件镜像」——NAS 上就是一份能直接翻、能直接拷回来的主题目录树。"));

            var dir = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
            dir.Children.Add(VaultCharts.SectionTitle(p, "方向"));

            dirUpload = DirOption("上传（本地 → NAS）：把本地主题推上去，适合刚下完主题", true);
            dirDownload = DirOption("下载（NAS → 本地）：从 NAS 取回主题，适合换机器", false);
            dirBoth = DirOption("双向：两边比对，缺哪补哪（默认只增不减，不会删东西）", false);
            dir.Children.Add(dirUpload);
            dir.Children.Add(dirDownload);
            dir.Children.Add(dirBoth);
            root.Children.Add(VaultCharts.Card(p, dir));

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

            var syncButton = FlatButton("开始同步", null);
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

            var resultBox = new StackPanel();
            resultBox.Children.Add(VaultCharts.SectionTitle(p, "结果"));
            syncResult = new TextBlock
            {
                FontSize = 12,
                Foreground = p.Text,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Consolas, Microsoft YaHei"),
                LineHeight = 19,
                Text = "这一页直接驱动同步引擎，和命令行工具用的是同一段代码。" + Environment.NewLine
                       + "· 两边都改过的主题按修改时间定胜负，输的那份原地留成 .conflict-* 副本，一个字节都不丢"
                       + Environment.NewLine
                       + "· 远端只增不减：本地删掉的主题不会连带删掉 NAS 上那份"
            };
            resultBox.Children.Add(syncResult);

            conflictList = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            resultBox.Children.Add(conflictList);

            var resultCard = VaultCharts.Card(p, resultBox);
            resultCard.Margin = new Thickness(0, 12, 0, 0);
            root.Children.Add(resultCard);

            return root;
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

        private void RunSync(ThemeSyncMode mode, bool dryRun, bool force)
        {
            ThemeSyncOutcome outcome;
            try
            {
                outcome = plugin.RunThemeSyncFromPanel(mode, dryRun, force);
            }
            catch (Exception ex)
            {
                VaultLog.Error("侧边栏页：主题同步失败", ex);
                if (syncResult != null)
                {
                    syncResult.Text = "同步失败：" + ex.Message;
                }
                return;
            }

            if (syncResult == null)
            {
                return;
            }

            syncResult.Text = outcome.Describe();

            if (conflictList != null)
            {
                conflictList.Children.Clear();
                var copied = ListConflictCopies();
                if (copied.Count > 0)
                {
                    conflictList.Children.Add(new TextBlock
                    {
                        Text = "当前存在 " + copied.Count + " 份冲突副本（原地保留，没删任何东西）：",
                        FontSize = 11.5,
                        Foreground = p.Text,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 0, 0, 4)
                    });
                    foreach (var line in copied)
                    {
                        conflictList.Children.Add(new TextBlock
                        {
                            Text = "· " + line,
                            FontSize = 11,
                            Foreground = p.TextMuted,
                            TextWrapping = TextWrapping.Wrap
                        });
                    }
                }
            }

            RefreshStats();
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

        // ================================================================ 仓库与归档

        /// <summary>卡片尺寸。宽度固定，封面 3:4 —— Playnite 的封面基本都是竖版，裁切不会吃掉主体。</summary>
        private const double RepoCardWidth = 176;
        private const double RepoCardCoverHeight = 228;

        /// <summary>一张卡片在界面上的那一份视觉对象（过滤时只翻 Visibility，不重建）。</summary>
        private sealed class RepoCardVisual
        {
            public LocalAppCard Card;
            public FrameworkElement Element;
        }

        /// <summary>
        /// 「仓库与归档」页：本机 Playnite 库里每个游戏一张卡片（封面 + 传没传过），
        /// 下面再挂一列「仓库里有、库里已经没有」的条目。
        ///
        /// <para><b>这一页取代了仓库管理窗口</b>（v1.8）：删除不再需要另开窗口，
        /// 卡片上直接点删除，口令闸门还是那一道
        /// （<c>VaultPlugin.DeleteRepositoryApp</c>，自检有 IL 级断言盯着）。
        /// 窗口留着，但只剩「改管理口令」这种一次性操作。</para>
        ///
        /// <para><b>为什么是卡片墙而不是表格</b>：这一页要回答的是「我库里这些游戏，
        /// 哪些已经进仓库了」。表格里一列 Id、一列体积，扫过去认不出谁是谁；
        /// 封面才是用户识别游戏的符号，一眼就知道缺哪几个。</para>
        ///
        /// <para><b>为什么两种状态都有</b>：只看「库里 → 仓库」会漏掉另一半 ——
        /// 归档完把游戏从库里删掉，条目就成了没人认领的孤儿，
        /// 而它占着 NAS 的空间。<c>MatchNote</c> 那一行是给「凭什么说他传过了」准备的答案。</para>
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
                Text = "仓库与归档",
                FontSize = 17,
                FontWeight = FontWeights.Bold,
                Foreground = p.Text
            });
            titleStack.Children.Add(new TextBlock
            {
                Text = "本机库里的游戏逐个标出「传过没有」；判定按 Playnite 游戏 ID 优先，"
                     + "并在卡片提示里写明依据。",
                FontSize = 11.5,
                Foreground = p.TextMuted,
                Margin = new Thickness(0, 3, 0, 0)
            });
            head.Children.Add(titleStack);
            root.Children.Add(head);

            // ---- 工具条：过滤片 + 搜索框 ----
            var bar = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };

            bar.Children.Add(BuildFilterChip("all", "全部"));
            bar.Children.Add(BuildFilterChip("in", "已上传"));
            bar.Children.Add(BuildFilterChip("out", "未上传"));

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

            // ---- 孤儿条目（仓库里有、库里没有）----
            repoOrphanHost = new StackPanel();
            var orphanStack = new StackPanel();
            orphanStack.Children.Add(VaultCharts.SectionTitle(p, "仓库里没有本机对应的条目"));
            orphanStack.Children.Add(VaultCharts.Muted(p,
                "这些归档在 NAS 上，但本地 Playnite 库里已经找不到对应游戏（换过机器、"
                + "或者游戏被从库里删掉了）。它们仍然占着仓库空间，可以直接删。"));
            orphanStack.Children.Add(repoOrphanHost);

            repoOrphanCard = VaultCharts.Card(p, orphanStack);
            repoOrphanCard.Margin = new Thickness(0, 16, 0, 0);
            repoOrphanCard.Visibility = Visibility.Collapsed;
            root.Children.Add(repoOrphanCard);

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
            repoOrphanHost.Children.Clear();
            repoOrphanCard.Visibility = Visibility.Collapsed;
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
            List<AppEntry> orphans;

            // 对账要遍历 Playnite 的库 —— 必须回 UI 线程做
            try
            {
                cards = plugin.BuildLocalAppCards(index);
                orphans = plugin.FindOrphanRepoApps(index, cards);
            }
            catch (Exception ex)
            {
                VaultLog.Error("仓库与归档：对账失败", ex);
                repoWallGrid.Children.Clear();
                repoSummary.Text = "读不出本机游戏列表。";
                repoEmpty.Text = "对账失败：" + ex.Message;
                repoEmpty.Visibility = Visibility.Visible;
                SetRepoStatus(string.Empty, null);
                return;
            }

            // ---- 汇总 ----
            var uploaded = cards.Count(c => c.InRepository);
            var bytes = cards.Where(c => c.InRepository).Sum(c => c.RepoBytes);
            repoSummary.Text = string.Format(
                "本机库 {0} 个游戏：{1} 个已上传、{2} 个还没传。仓库里这 {1} 个占 {3}。（索引来源：{4}）",
                cards.Count, uploaded, cards.Count - uploaded,
                VaultAdminWindow.FormatSize(bytes),
                string.IsNullOrEmpty(source) ? "未知" : source);

            SetRepoStatus(string.Empty, null);
            if (!string.IsNullOrEmpty(error))
            {
                SetRepoStatus("注意：" + error, null);
            }

            // ---- 卡片 ----
            repoCards.Clear();
            repoWallGrid.Children.Clear();

            var pendingCovers = new List<KeyValuePair<Image, string>>();
            foreach (var card in cards)
            {
                var visual = BuildAppCard(card, pendingCovers);
                repoCards.Add(visual);
                repoWallGrid.Children.Add(visual.Element);
            }

            ApplyRepoFilter();

            // ---- 孤儿 ----
            repoOrphanHost.Children.Clear();
            repoOrphanCard.Visibility = orphans.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            foreach (var app in orphans)
            {
                repoOrphanHost.Children.Add(BuildOrphanRow(app));
            }

            if (pendingCovers.Count > 0)
            {
                LoadCovers(pendingCovers);
            }
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

            // 状态徽章：绿=已上传、橙=该传了。**这两个都是实心色块**，
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
                    Text = card.InRepository ? "已上传" : "未上传",
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

            var size = new TextBlock
            {
                Text = card.InRepository
                    ? VaultAdminWindow.FormatSize(card.RepoBytes)
                    : (card.IsInstalled ? "待归档" : string.Empty),
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
                var remove = CardButton("删除", p.Danger);
                remove.ToolTip = "从 NAS 删除这条归档（要管理口令）；本地已下载的文件不动";
                remove.Click += (s, e) => ConfirmDeleteRepoApp(
                    captured.RepoAppId, captured.Name, captured.RepoBytes);
                actions.Children.Add(remove);
            }
            else if (card.IsInstalled)
            {
                var captured = card;
                var upload = CardButton("归档到 NAS", p.Accent);
                upload.ToolTip = "把这个游戏打包上传到 NAS（本地文件不会被删除）";
                upload.Click += (s, e) => plugin.ArchiveGameById(captured.GameId);
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

            return new RepoCardVisual { Card = card, Element = frame };
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
                          + "（" + VaultAdminWindow.FormatSize(card.RepoBytes) + "）");
            }

            if (!string.IsNullOrEmpty(card.InstallDir))
            {
                lines.Add("本机目录：" + card.InstallDir);
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
                var card = visual.Card;
                var pass = true;

                if (repoFilterMode == "in" && !card.InRepository)
                {
                    pass = false;
                }
                else if (repoFilterMode == "out" && card.InRepository)
                {
                    pass = false;
                }

                if (pass && repoFilterText.Length > 0)
                {
                    pass = (card.Name ?? string.Empty).IndexOf(
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
                repoEmpty.Text = "本机 Playnite 库里还没有游戏。";
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

        // ---------------------------------------------------------------- 孤儿条目

        private UIElement BuildOrphanRow(AppEntry app)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };

            var remove = CardButton("删除", p.Danger);
            remove.ToolTip = "从 NAS 删除 apps/" + app.Id + "/（本地已下载的文件不动）";
            remove.Click += (s, e) => ConfirmDeleteRepoApp(app.Id, app.Name, app.TotalBytes);
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);

            var size = new TextBlock
            {
                Text = VaultAdminWindow.FormatSize(app.TotalBytes),
                FontSize = 11.5,
                Foreground = p.TextMuted,
                Width = 80,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 10, 0)
            };
            DockPanel.SetDock(size, Dock.Right);
            row.Children.Add(size);

            var name = new TextBlock
            {
                Text = string.IsNullOrEmpty(app.Name) ? app.Id : app.Name,
                FontSize = 12,
                Foreground = p.Text,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "apps/" + app.Id + "/"
            };
            row.Children.Add(name);

            return row;
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
                Margin = new Thickness(0, 0, 0, 8)
            });
            root.Children.Add(VaultCharts.Muted(p,
                "和 Playnite 的「扩展设置」里是同一份界面（同一个设置模型和服务对象），改完点下面的保存。"));

            var host = new Border
            {
                Background = p.Surface,
                BorderBrush = p.Border,
                BorderThickness = new Thickness(2),
                Margin = new Thickness(0, 14, 0, 0),
                Padding = new Thickness(2)
            };

            try
            {
                host.Child = new VaultSettingsView(settingsVm, service);

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
        /// 落盘。注意 <c>SaveSettingsImmediately</c> 会触发 <see cref="RebuildForTheme"/>
        /// （换色需要重建视觉树），所以这里先把提示语写进字段，重建后新控件会自己带上它。
        /// </summary>
        private void SaveSettings()
        {
            try
            {
                plugin.SaveSettingsImmediately(settingsVm);
                saveStatusOk = true;
                saveStatusText = "已保存 " + DateTime.Now.ToString("HH:mm:ss");
            }
            catch (Exception ex)
            {
                VaultLog.Error("侧边栏页：保存设置失败", ex);
                saveStatusOk = false;
                saveStatusText = "保存失败：" + ex.Message;
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
            if (refreshButton != null)
            {
                refreshButton.IsEnabled = false;
            }

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
                    if (refreshButton != null)
                    {
                        refreshButton.IsEnabled = true;
                    }

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
