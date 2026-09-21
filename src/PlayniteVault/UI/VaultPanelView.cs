using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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
    /// 配色策略：优先吃 Playnite 自己的画刷（<c>TextBrush</c> 等），解析不到再退回内置调色板；
    /// 明暗则靠探测窗口背景色的亮度判断。这样无论是在浅色还是深色主题里都不会出现
    /// 「黑字压黑底」这种一眼可见的破相。
    /// </summary>
    public class VaultPanelView : UserControl
    {
        private readonly VaultPlugin plugin;
        private readonly VaultService service;
        private readonly VaultSettingsViewModel settingsVm;
        private readonly string themesRoot;

        // ---- 配色 ----
        private bool isDark;
        private Brush cBg;          // 页面底色
        private Brush cCard;        // 卡片底色
        private Brush cInk;         // 主文字
        private Brush cSub;         // 次要文字
        private Brush cLine;        // 描边
        private static readonly Brush Pink = Frozen("#FF6B9D");
        private static readonly Brush Yellow = Frozen("#FFD93D");
        private static readonly Brush Blue = Frozen("#4D96FF");
        private static readonly Brush Green = Frozen("#6BCB77");
        private static readonly Brush Orange = Frozen("#FF9F45");

        // ---- 骨架 ----
        private ContentControl body;
        private readonly StackPanel navPanel = new StackPanel();
        private readonly Dictionary<string, Button> navButtons = new Dictionary<string, Button>();
        private readonly Dictionary<string, Func<UIElement>> sectionBuilders =
            new Dictionary<string, Func<UIElement>>();
        private readonly Dictionary<string, UIElement> sectionCache = new Dictionary<string, UIElement>();
        private string currentSection;

        // ---- 需要回写的控件 ----
        private TextBlock badgeText;
        private Border badgeBox;
        private TextBlock statApps;
        private TextBlock statRepoBytes;
        private TextBlock statThemes;
        private TextBlock statThemeBytes;
        private TextBlock statConflict;
        private TextBlock statLastSync;
        private TextBlock repoSource;
        private TextBlock syncResult;
        private TextBlock themeRootText;
        private RadioButton dirUpload;
        private RadioButton dirDownload;
        private RadioButton dirBoth;
        private CheckBox chkDryRun;
        private CheckBox chkForce;
        private Button syncButton;
        private StackPanel conflictList;
        private bool statsLoading;

        public VaultPanelView(VaultPlugin plugin, VaultService service,
                              VaultSettingsViewModel settingsVm, string themesRoot)
        {
            this.plugin = plugin;
            this.service = service;
            this.settingsVm = settingsVm;
            this.themesRoot = themesRoot;

            ResolvePalette();
            Build();

            Loaded += (s, e) =>
            {
                RefreshBadge();
                RefreshStats();
                if (syncResult != null && string.IsNullOrEmpty(syncResult.Text))
                {
                    syncResult.Text = "这一页直接驱动同步引擎，和命令行工具用的是同一段代码。"
                                      + Environment.NewLine
                                      + "· 两边都改过的主题按修改时间定胜负，输的那份原地留成 .conflict-* 副本，一个字节都不丢"
                                      + Environment.NewLine
                                      + "· 远端只增不减：本地删掉的主题不会连带删掉 NAS 上那份";
                }
            };
        }

        // ================================================================ 配色

        private static Brush Frozen(string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            return brush;
        }

        private static Brush AppBrush(string key)
        {
            var app = Application.Current;
            if (app == null)
            {
                return null;
            }

            try
            {
                return app.TryFindResource(key) as Brush;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 取 Playnite 主题里的画刷，取不到用兜底。
        /// 侧边栏图标也走这里 —— 在浅色主题里硬写白色笔画会直接看不见。
        /// </summary>
        public static Brush ThemedBrush(string key, Brush fallback)
        {
            return AppBrush(key) ?? fallback;
        }

        private static Brush WithAlpha(Brush source, double alpha)
        {
            var solid = source as SolidColorBrush;
            if (solid == null)
            {
                return source;
            }

            var c = solid.Color;
            var copy = new SolidColorBrush(Color.FromArgb((byte)(alpha * 255), c.R, c.G, c.B));
            copy.Freeze();
            return copy;
        }

        private void ResolvePalette()
        {
            var probe = AppBrush("WindowBackgroundBrush")
                        ?? AppBrush("BackgroundBrush")
                        ?? AppBrush("ControlBackgroundBrush");
            var solid = probe as SolidColorBrush;
            if (solid != null)
            {
                var c = solid.Color;
                isDark = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) < 132;
            }
            else
            {
                isDark = true;   // Playnite 桌面模式默认深色
            }

            cBg = isDark ? Frozen("#16181C") : Frozen("#F3F1EA");
            cCard = isDark ? Frozen("#212429") : Frozen("#FFFFFF");
            cLine = isDark ? Frozen("#3A3F47") : Frozen("#111111");
            cSub = isDark ? Frozen("#9AA3AE") : Frozen("#5C626B");

            // 正文字色优先用 Playnite 自己给的，对比度最稳
            var text = AppBrush("TextBrush") as SolidColorBrush;
            cInk = text != null && text.Color.A > 0
                ? text
                : (isDark ? Frozen("#E9ECF1") : Frozen("#14161A"));
        }

        // ================================================================ 骨架

        private void Build()
        {
            Background = cBg;

            // 分区是懒建的：点开哪个才构造哪个（设置那一页尤其重）
            sectionBuilders["overview"] = BuildOverview;
            sectionBuilders["themes"] = BuildThemeSync;
            sectionBuilders["repo"] = BuildRepo;
            sectionBuilders["settings"] = BuildSettings;
            sectionBuilders["about"] = BuildAbout;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(208) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var nav = new Border
            {
                Background = WithAlpha(cCard, 0.55),
                BorderBrush = cLine,
                BorderThickness = new Thickness(0, 0, 3, 0),
                Padding = new Thickness(14, 18, 14, 14)
            };

            navPanel.Children.Add(new TextBlock
            {
                Text = "仓库管家",
                FontSize = 19,
                FontWeight = FontWeights.Bold,
                Foreground = cInk,
                Margin = new Thickness(2, 0, 0, 2)
            });
            navPanel.Children.Add(new TextBlock
            {
                Text = "Playnite Vault",
                FontSize = 11.5,
                Foreground = cSub,
                Margin = new Thickness(2, 0, 0, 16)
            });

            AddSection("overview", "概览 · 统计");
            AddSection("themes", "主题同步");
            AddSection("repo", "仓库与归档");
            AddSection("settings", "插件设置");
            AddSection("about", "关于 · 依赖");

            navPanel.Children.Add(new Border
            {
                Height = 3,
                Background = cLine,
                Margin = new Thickness(0, 12, 0, 12),
                Opacity = 0.5
            });

            var logButton = FlatButton("打开日志文件", Orange);
            logButton.Click += (s, e) => OpenLog();
            navPanel.Children.Add(logButton);

            var navScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = navPanel
            };
            Grid.SetColumn(navScroll, 0);

            // 右侧：标题条 + 内容
            var right = new Grid();
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var header = new Border
            {
                BorderBrush = cLine,
                BorderThickness = new Thickness(0, 0, 0, 3),
                Padding = new Thickness(18, 14, 18, 14)
            };
            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleStack = new StackPanel();
            titleStack.Children.Add(new TextBlock
            {
                Text = "Playnite Vault",
                FontSize = 17,
                FontWeight = FontWeights.Bold,
                Foreground = cInk
            });
            var repoLine = new TextBlock
            {
                FontSize = 12,
                Foreground = cSub,
                Margin = new Thickness(0, 3, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 520
            };
            repoLine.Text = service.Settings.IsConfigured
                ? service.Settings.WebDavUrl
                : "还没配置 WebDAV 地址 —— 去「插件设置」里填一下";
            titleStack.Children.Add(repoLine);
            headerGrid.Children.Add(titleStack);

            badgeBox = new Border
            {
                BorderBrush = cLine,
                BorderThickness = new Thickness(3),
                Padding = new Thickness(10, 4, 10, 4),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0),
                Background = Yellow
            };
            badgeText = new TextBlock
            {
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                Foreground = Frozen("#111111")
            };
            badgeBox.Child = badgeText;
            Grid.SetColumn(badgeBox, 1);
            headerGrid.Children.Add(badgeBox);

            var refresh = FlatButton("刷新", Blue);
            refresh.Click += (s, e) =>
            {
                RefreshBadge();
                RefreshStats();
            };
            Grid.SetColumn(refresh, 2);
            headerGrid.Children.Add(refresh);

            header.Child = headerGrid;
            Grid.SetRow(header, 0);
            right.Children.Add(header);

            body = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
            var bodyScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Padding = new Thickness(18, 16, 18, 24),
                Content = body
            };
            Grid.SetRow(bodyScroll, 1);
            right.Children.Add(bodyScroll);

            Grid.SetColumn(right, 1);
            grid.Children.Add(nav);
            nav.Child = navScroll;
            grid.Children.Add(right);

            Content = grid;
            ShowSection("overview");
        }

        private void AddSection(string key, string title)
        {
            var button = new Button
            {
                Content = title,
                Tag = key,
                Margin = new Thickness(0, 0, 0, 7),
                Padding = new Thickness(11, 9, 11, 9),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                FontSize = 13.5,
                FontWeight = FontWeights.SemiBold,
                BorderThickness = new Thickness(3),
                BorderBrush = cLine,
                Background = cCard,
                Foreground = cInk,
                Cursor = Cursors.Hand
            };
            button.Click += (s, e) => ShowSection(key);
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
                pair.Value.Background = selected ? Yellow : cCard;
                pair.Value.Foreground = selected ? Frozen("#111111") : cInk;
            }

            if (key == "overview")
            {
                RefreshStats();
            }
        }

        // ================================================================ 小部件

        private Border Card(UIElement child, Brush accentBar)
        {
            var inner = new Border
            {
                Background = cCard,
                BorderBrush = cLine,
                BorderThickness = new Thickness(3),
                Padding = new Thickness(14, 12, 14, 13)
            };

            if (accentBar == null)
            {
                inner.Child = child;
                return inner;
            }

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var bar = new Border { Background = accentBar };
            Grid.SetColumn(bar, 0);
            grid.Children.Add(bar);

            var padded = new Border { Padding = new Thickness(11, 0, 0, 0), Child = child };
            Grid.SetColumn(padded, 1);
            grid.Children.Add(padded);

            inner.Padding = new Thickness(0, 0, 14, 0);
            inner.Child = grid;
            return inner;
        }

        private Button FlatButton(string text, Brush accent)
        {
            return new Button
            {
                Content = text,
                Padding = new Thickness(12, 7, 12, 7),
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                BorderThickness = new Thickness(3),
                BorderBrush = cLine,
                Background = accent,
                Foreground = Frozen("#111111"),
                Cursor = Cursors.Hand
            };
        }

        private TextBlock Head(string text, int size)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = size,
                FontWeight = FontWeights.Bold,
                Foreground = cInk,
                Margin = new Thickness(0, 0, 0, 9)
            };
        }

        private TextBlock Para(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 12.5,
                Foreground = cSub,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 19
            };
        }

        private Border StatCard(string caption, Brush accent, out TextBlock value)
        {
            var valueText = new TextBlock
            {
                Text = "—",
                FontSize = 22,
                FontWeight = FontWeights.Bold,
                Foreground = cInk
            };
            value = valueText;

            var stack = new StackPanel();
            stack.Children.Add(valueText);
            stack.Children.Add(new TextBlock
            {
                Text = caption,
                FontSize = 11.5,
                Foreground = cSub,
                Margin = new Thickness(0, 3, 0, 0)
            });

            var card = Card(stack, accent);
            card.Margin = new Thickness(0, 0, 10, 10);
            card.MinWidth = 138;
            return card;
        }

        private static StackPanel Row(params UIElement[] items)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var item in items)
            {
                row.Children.Add(item);
            }

            return row;
        }

        // ================================================================ 概览

        private UIElement BuildOverview()
        {
            var root = new StackPanel();
            root.Children.Add(Head("概览", 16));

            var cards = new WrapPanel();
            cards.Children.Add(StatCard("仓库里的应用", Blue, out statApps));
            cards.Children.Add(StatCard("仓库体积", Green, out statRepoBytes));
            cards.Children.Add(StatCard("本地主题", Pink, out statThemes));
            cards.Children.Add(StatCard("主题体积", Orange, out statThemeBytes));
            cards.Children.Add(StatCard("冲突留档", Yellow, out statConflict));
            root.Children.Add(cards);

            var meta = new StackPanel();
            meta.Children.Add(new TextBlock
            {
                Text = "统计看板的底子已经在这了：下面这行是索引来源，上面是几组现成的数字；"
                       + "以后要加图表（体积趋势、同步历史）直接往这块堆就行。",
                FontSize = 12.5,
                Foreground = cSub,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            });
            statLastSync = new TextBlock { FontSize = 12.5, Foreground = cInk, TextWrapping = TextWrapping.Wrap };
            meta.Children.Add(statLastSync);
            repoSource = new TextBlock
            {
                FontSize = 12,
                Foreground = cSub,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            };
            meta.Children.Add(repoSource);

            var cardWrap = Card(meta, Pink);
            cardWrap.Margin = new Thickness(0, 2, 0, 14);
            root.Children.Add(cardWrap);

            root.Children.Add(Head("常用操作", 14));
            var quick = new WrapPanel();

            var up = FlatButton("上传主题到 NAS", Blue);
            up.Margin = new Thickness(0, 0, 8, 8);
            up.Click += (s, e) => RunSync(ThemeSyncMode.Upload);
            quick.Children.Add(up);

            var down = FlatButton("从 NAS 拉主题", Green);
            down.Margin = new Thickness(0, 0, 8, 8);
            down.Click += (s, e) => RunSync(ThemeSyncMode.Download);
            quick.Children.Add(down);

            var browse = FlatButton("仓库管理…", Yellow);
            browse.Margin = new Thickness(0, 0, 8, 8);
            browse.Click += (s, e) => OpenAdmin();
            quick.Children.Add(browse);

            var check = FlatButton("检查插件更新", Orange);
            check.Margin = new Thickness(0, 0, 8, 8);
            check.Click += (s, e) => plugin.RunUpdateCheck(true);
            quick.Children.Add(check);

            var refreshLib = FlatButton("从 NAS 刷新库条目", Pink);
            refreshLib.Margin = new Thickness(0, 0, 8, 8);
            refreshLib.Click += (s, e) => plugin.RefreshLibraryEntries(true);
            quick.Children.Add(refreshLib);

            root.Children.Add(quick);
            return root;
        }

        // ================================================================ 主题同步

        private UIElement BuildThemeSync()
        {
            var root = new StackPanel();
            root.Children.Add(Head("主题同步", 16));
            root.Children.Add(Para("把 Playnite 的 Themes 目录和仓库的 themes/ 目录对齐。"
                                   + "远端是「普通文件镜像」——NAS 上就是一份能直接翻、能直接拷回来的主题目录树。"));

            var dir = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            dir.Children.Add(new TextBlock
            {
                Text = "方向",
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = cInk,
                Margin = new Thickness(0, 0, 0, 6)
            });

            dirUpload = DirOption("上传（本地 → NAS）：把本地主题推上去，适合刚下完主题", true);
            dirDownload = DirOption("下载（NAS → 本地）：从 NAS 取回主题，适合换机器", false);
            dirBoth = DirOption("双向：两边比对，缺哪补哪（默认只增不减，不会删东西）", false);
            dir.Children.Add(dirUpload);
            dir.Children.Add(dirDownload);
            dir.Children.Add(dirBoth);
            root.Children.Add(Card(dir, Blue));

            var opts = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            opts.Children.Add(new TextBlock
            {
                Text = "选项",
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = cInk,
                Margin = new Thickness(0, 0, 0, 6)
            });

            chkDryRun = new CheckBox
            {
                Content = "预演（只列计划，不落盘）",
                IsChecked = false,
                Foreground = cInk,
                FontSize = 12.5,
                Margin = new Thickness(0, 0, 0, 5)
            };
            opts.Children.Add(chkDryRun);

            chkForce = new CheckBox
            {
                Content = "强制（内容一样也重传一遍，慢；只有怀疑远端被改坏时才用）",
                IsChecked = false,
                Foreground = cInk,
                FontSize = 12.5
            };
            opts.Children.Add(chkForce);

            syncButton = FlatButton("开始同步", Green);
            syncButton.Margin = new Thickness(0, 12, 0, 0);
            syncButton.Click += (s, e) => RunSync(SelectedMode());
            opts.Children.Add(syncButton);

            themeRootText = new TextBlock
            {
                FontSize = 12,
                Foreground = cSub,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 0)
            };
            themeRootText.Text = "主题目录：" + themesRoot;
            opts.Children.Add(themeRootText);
            root.Children.Add(Card(opts, Green));

            var resultBox = new StackPanel();
            resultBox.Children.Add(new TextBlock
            {
                Text = "结果",
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = cInk,
                Margin = new Thickness(0, 0, 0, 6)
            });
            syncResult = new TextBlock
            {
                FontSize = 12.5,
                Foreground = cInk,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Consolas, Microsoft YaHei"),
                LineHeight = 19
            };
            resultBox.Children.Add(syncResult);

            conflictList = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            resultBox.Children.Add(conflictList);

            var resultCard = Card(resultBox, Yellow);
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
                Foreground = cInk,
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

        private void RunSync(ThemeSyncMode mode)
        {
            var dryRun = chkDryRun != null && chkDryRun.IsChecked == true;
            var force = chkForce != null && chkForce.IsChecked == true;

            var outcome = plugin.RunThemeSyncFromPanel(mode, dryRun, force);

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
                        FontSize = 12,
                        Foreground = cInk,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 0, 0, 4)
                    });
                    foreach (var line in copied)
                    {
                        conflictList.Children.Add(new TextBlock
                        {
                            Text = "· " + line,
                            FontSize = 11.5,
                            Foreground = cSub,
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

        private UIElement BuildRepo()
        {
            var root = new StackPanel();
            root.Children.Add(Head("仓库与归档", 16));
            root.Children.Add(Para("把本地大体积游戏打包塞进 NAS 腾出磁盘，需要时再解回本地；"
                                   + "游戏的元数据跟着包一起走，所以换台机器恢复出来的库是完整的。"));

            var actions = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            var browse = FlatButton("打开仓库管理…", Yellow);
            browse.Margin = new Thickness(0, 0, 8, 8);
            browse.Click += (s, e) => OpenAdmin();
            actions.Children.Add(browse);

            var refreshLib = FlatButton("从 NAS 刷新库条目", Blue);
            refreshLib.Margin = new Thickness(0, 0, 8, 8);
            refreshLib.Click += (s, e) => plugin.RefreshLibraryEntries(true);
            actions.Children.Add(refreshLib);

            var check = FlatButton("检查插件更新", Orange);
            check.Margin = new Thickness(0, 0, 8, 8);
            check.Click += (s, e) => plugin.RunUpdateCheck(true);
            actions.Children.Add(check);

            actions.Children.Add(Para("归档/解包的具体操作在「仓库管理」窗口里：列出仓库里的应用、"
                                      + "逐个删除、改管理口令。右键游戏菜单里还有归档/解包/元数据刷新。"));
            root.Children.Add(Card(actions, Yellow));
            return root;
        }

        // ================================================================ 设置

        private UIElement BuildSettings()
        {
            var root = new StackPanel();
            root.Children.Add(Head("插件设置", 16));
            root.Children.Add(Para("和 Playnite 的「扩展设置」里是同一份界面（同一个设置模型和服务对象），"
                                   + "改完记得点页面里的保存。"));

            var host = new Border
            {
                Background = cCard,
                BorderBrush = cLine,
                BorderThickness = new Thickness(3),
                Margin = new Thickness(0, 12, 0, 0),
                Padding = new Thickness(2)
            };

            try
            {
                host.Child = new VaultSettingsView(settingsVm, service);
            }
            catch (Exception ex)
            {
                VaultLog.Error("侧边栏页：内嵌设置界面失败", ex);
                host.Child = Para("内嵌设置界面失败：" + ex.Message);
            }

            root.Children.Add(host);
            return root;
        }

        // ================================================================ 关于

        private UIElement BuildAbout()
        {
            var root = new StackPanel();
            root.Children.Add(Head("关于与依赖", 16));

            var about = new StackPanel();
            about.Children.Add(new TextBlock
            {
                Text = "当前版本 " + VaultUpdater.CurrentVersion(),
                FontSize = 12.5,
                Foreground = cInk,
                Margin = new Thickness(0, 0, 0, 8)
            });
            about.Children.Add(Para("这个页面能跑起来靠的是 Playnite SDK 的两个口子："
                                    + "SidebarItem.Opened（Type = View）把这里的控件嵌进主窗口，"
                                    + "GetSettingsView 提供设置界面。"));

            var card1 = Card(about, Blue);
            card1.Margin = new Thickness(0, 12, 0, 12);
            root.Children.Add(card1);

            root.Children.Add(Head("关于主题兼容性（实测结论）", 14));
            var compat = new StackPanel();
            foreach (var line in new[]
            {
                "Playnite 主题不声明依赖：theme.yaml 只有 ThemeApiVersion / Id / Name / Author / Version / Mode 六个字段，",
                "官方 76 个主题里没有一个自带 extension.yaml，插件库里也没有任何依赖字段。",
                "",
                "观感差异来自两件事：",
                "1) 主题在描述里写着「支持」某些社区扩展 —— 76 个里有 17 个点名了：",
                "   DuplicateHider 14、SuccessStory 14、HowLongToBeat 11、GameActivity 10、ExtraMetadataLoader 7、CheckDLC 3。",
                "   这些扩展没装时，主题里对应区块是空的，看起来就像「主题没做全」。",
                "2) theme.yaml 声明的 ThemeApiVersion 从 2.0.0 铺到 2.10.0，与 Playnite 自身版本差得远时会走降级布局。",
                "",
                "所以「兼容差」基本不是装漏了插件，而是主题把可选扩展的区块留空 —— 想要那些区块，去装对应扩展即可。"
            })
            {
                compat.Children.Add(new TextBlock
                {
                    Text = line,
                    FontSize = 12.5,
                    Foreground = line.StartsWith("1)") || line.StartsWith("2)") ? cInk : cSub,
                    TextWrapping = TextWrapping.Wrap,
                    LineHeight = 19
                });
            }
            var card2 = Card(compat, Pink);
            card2.Margin = new Thickness(0, 0, 0, 12);
            root.Children.Add(card2);

            root.Children.Add(Head("关于视图配置", 14));
            var view = new StackPanel();
            view.Children.Add(Para("网格行列、封面尺寸、列表列这些设置全部在 Playnite 的 config.json 里，是全局的，"
                                   + "主题带不走（官方没有这个机制）。要「随主题走」，只能由插件接管："
                                   + "把当前这批键存成一份以主题为名的档案，切主题时套用。这一步还没做。"));
            root.Children.Add(Card(view, Green));

            return root;
        }

        // ================================================================ 数据

        private void RefreshBadge()
        {
            if (badgeText == null)
            {
                return;
            }

            if (!service.Settings.IsConfigured)
            {
                badgeText.Text = "未配置";
                badgeBox.Background = Yellow;
                return;
            }

            var pending = plugin.HasPendingUpdate;
            if (pending)
            {
                badgeText.Text = "有更新待重启";
                badgeBox.Background = Pink;
                return;
            }

            badgeText.Text = "已连接 " + VaultUpdater.CurrentVersion();
            badgeBox.Background = Green;
        }

        private void RefreshStats()
        {
            if (statsLoading)
            {
                return;
            }

            statsLoading = true;
            SetStatTexts("…");

            Task.Run(() =>
            {
                int apps = 0;
                long repoBytes = 0;
                string source = null;
                string error = null;
                ThemeLibraryStats themes = null;

                try
                {
                    var index = service.GetIndex(out source, out error);
                    if (index != null)
                    {
                        apps = index.Apps.Count;
                        repoBytes = index.Apps.Sum(a => a.TotalBytes);
                    }
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    VaultLog.Warn("侧边栏页：读仓库索引失败：" + ex.Message);
                }

                try
                {
                    themes = ScanThemes();
                }
                catch (Exception ex)
                {
                    VaultLog.Warn("侧边栏页：统计主题目录失败：" + ex.Message);
                }

                Dispatcher.Invoke(() =>
                {
                    statsLoading = false;
                    ApplyStats(apps, repoBytes, source, error, themes);
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

            // 冲突副本只扫两层，够用了
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

        private void SetStatTexts(string text)
        {
            if (statApps != null) statApps.Text = text;
            if (statRepoBytes != null) statRepoBytes.Text = text;
            if (statThemes != null) statThemes.Text = text;
            if (statThemeBytes != null) statThemeBytes.Text = text;
            if (statConflict != null) statConflict.Text = text;
        }

        private void ApplyStats(int apps, long repoBytes, string source, string error, ThemeLibraryStats themes)
        {
            if (statApps != null) statApps.Text = apps.ToString();
            if (statRepoBytes != null) statRepoBytes.Text = VaultAdminWindow.FormatSize(repoBytes);

            if (themes != null)
            {
                if (statThemes != null)
                {
                    statThemes.Text = themes.RootMissing
                        ? "0"
                        : themes.TotalThemes + "（桌 " + themes.DesktopThemes + " / 全 " + themes.FullscreenThemes + "）";
                }

                if (statThemeBytes != null) statThemeBytes.Text = VaultAdminWindow.FormatSize(themes.TotalBytes);

                if (statConflict != null)
                {
                    statConflict.Text = themes.ConflictCopies.ToString();
                }

                if (statLastSync != null)
                {
                    statLastSync.Text = themes.LastSyncUtc.HasValue
                        ? "上次主题同步：" + themes.LastSyncUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                        : "还没同步过主题（同步一次后这里会显示时间）";
                }
            }

            if (repoSource != null)
            {
                var text = "索引来源：" + (string.IsNullOrEmpty(source) ? "未知" : source);
                if (!string.IsNullOrEmpty(error))
                {
                    text += "　（注意：" + error + "）";
                }

                repoSource.Text = text;
            }
        }

        // ================================================================ 动作

        /// <summary>
        /// 打开仓库管理。**必须走插件那条公开入口**，不能直接 new 窗口：
        /// 那里有一道管理口令闸门，而仓库管理里有不可逆的删除。
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

        private void OpenLog()
        {
            try
            {
                VaultLog.Info("侧边栏页：用户打开了日志");
                System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + VaultLog.LogPath + "\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show("打开日志失败：" + ex.Message, "Playnite Vault");
            }
        }
    }
}
