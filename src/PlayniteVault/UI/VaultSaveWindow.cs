using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Playnite.SDK;
using Playnite.SDK.Models;
using PlayniteVault.Models;
using PlayniteVault.Services;

namespace PlayniteVault.UI
{
    /// <summary>
    /// 存档管理窗口：左边选游戏，右边「路径定义 + 快照/分支」。
    ///
    /// <para>界面本身不碰 WebDAV —— 所有远端动作都丢到后台线程，回来只在
    /// Dispatcher 上重建控件。<c>WebDavClient</c> 是同步阻塞的，在 UI 线程上调它
    /// 直接就是「窗口假死」，而 SynchronizationContext 一旦被占住，连进度都刷不出来。</para>
    ///
    /// <para><b>路径定义的写回分两处</b>：来自 Playnite 的行写回 <c>Game.SavePaths</c>
    /// （那是用户自己填的，理应还在那儿），来自插件/嗅探的行写进插件自己的
    /// <c>save-paths.json</c>。界面上一行一个来源标签，免得用户搞不清某条是谁写的。</para>
    /// </summary>
    public class VaultSaveWindow : Window
    {
        private readonly VaultPlugin plugin;
        private readonly VaultSaveService saves;
        private readonly IPlayniteAPI api;

        private readonly ListBox gameList;
        private readonly TextBox filterBox;
        private readonly TextBlock titleText;
        private readonly TextBlock hintText;
        private readonly TextBlock statusText;
        private readonly StackPanel pathPanel;
        private readonly StackPanel snapshotPanel;
        private readonly ComboBox branchBox;
        private readonly ProgressBar progressBar;
        private readonly List<Button> buttons = new List<Button>();

        /// <summary>每次重建路径行时把「怎么把这一行读回来」塞进这里，保存时统一取。</summary>
        private readonly List<Func<SavePathSpec>> rowReaders = new List<Func<SavePathSpec>>();

        private List<Game> games = new List<Game>();
        private Game current;
        private SaveGameManifest manifest;
        private List<SaveSnapshot> snapshots = new List<SaveSnapshot>();
        private List<SaveBranch> branches = new List<SaveBranch>();
        private List<string> notes = new List<string>();
        private SaveSnapshot selectedSnapshot;
        private bool busy;
        private bool loading;
        private DateTime lastProgressAt = DateTime.MinValue;

        public VaultSaveWindow(VaultPlugin plugin, VaultSaveService saves, IPlayniteAPI api)
        {
            this.plugin = plugin;
            this.saves = saves;
            this.api = api;

            Title = "Vault 存档管理";
            Width = 1080;
            Height = 680;
            MinWidth = 860;
            MinHeight = 520;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // ---------------- 顶部 ----------------
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };

            titleText = new TextBlock
            {
                Text = "存档管理",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };

            var headerTools = new StackPanel { Orientation = Orientation.Horizontal };

            var refreshAll = MakeButton("刷新", () => ReloadGameList());
            headerTools.Children.Add(refreshAll);

            DockPanel.SetDock(headerTools, Dock.Right);
            header.Children.Add(headerTools);
            header.Children.Add(titleText);
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            // ---------------- 主体：左游戏 / 右详情 ----------------
            var body = new Grid();
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(280) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // ---- 左：游戏列表 ----
            var left = new DockPanel { Margin = new Thickness(0, 0, 10, 0) };

            filterBox = new TextBox { Margin = new Thickness(0, 0, 0, 6) };
            filterBox.TextChanged += (s, e) => ApplyGameFilter();
            DockPanel.SetDock(filterBox, Dock.Top);
            left.Children.Add(filterBox);

            gameList = new ListBox { DisplayMemberPath = "Name" };
            gameList.SelectionChanged += (s, e) => OnGameSelected();
            left.Children.Add(gameList);

            Grid.SetColumn(left, 0);
            body.Children.Add(left);

            // ---- 右：滚动区 ----
            var rightScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };

            var right = new StackPanel();

            hintText = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.75,
                Margin = new Thickness(0, 0, 0, 8),
                Text = "选择左边的游戏。存档路径可以先从 Playnite 读，也可以在这里补一份、或者让嗅探器去猜。"
            };
            right.Children.Add(hintText);

            // 路径定义区
            right.Children.Add(SectionHeader("存档路径定义"));

            var pathTools = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 4)
            };
            pathTools.Children.Add(MakeButton("添加一行", AddRow));
            pathTools.Children.Add(MakeButton("自动嗅探", SniffPaths));
            pathTools.Children.Add(MakeButton("保存路径改动", SavePathEdits));
            right.Children.Add(pathTools);

            pathPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            right.Children.Add(pathPanel);

            // 快照区
            right.Children.Add(SectionHeader("快照与分支"));

            var branchRow = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 4)
            };
            branchRow.Children.Add(new TextBlock
            {
                Text = "分支：",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 4, 0)
            });

            branchBox = new ComboBox { Width = 160, Margin = new Thickness(0, 0, 8, 0) };
            branchBox.SelectionChanged += (s, e) => RefreshSnapshotsFromBranch();
            branchRow.Children.Add(branchBox);

            branchRow.Children.Add(MakeButton("新建分支", CreateBranch));
            branchRow.Children.Add(MakeButton("刷新", () => ReloadCurrent()));
            right.Children.Add(branchRow);

            var snapshotTools = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 4)
            };
            snapshotTools.Children.Add(MakeButton("上传当前状态", UploadNow));
            snapshotTools.Children.Add(MakeButton("恢复选中快照", RestoreSelected));
            snapshotTools.Children.Add(MakeButton("标星 / 取消", TogglePin));
            snapshotTools.Children.Add(MakeButton("删除选中", DeleteSelected));
            snapshotTools.Children.Add(MakeButton("按保留策略裁剪", PruneNow));
            right.Children.Add(snapshotTools);

            snapshotPanel = new StackPanel();
            right.Children.Add(snapshotPanel);

            rightScroll.Content = right;
            Grid.SetColumn(rightScroll, 1);
            body.Children.Add(rightScroll);

            Grid.SetRow(body, 1);
            root.Children.Add(body);

            // ---------------- 底部 ----------------
            var footer = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };

            progressBar = new ProgressBar
            {
                Height = 6,
                Minimum = 0,
                Maximum = 100,
                Visibility = Visibility.Collapsed,
                Margin = new Thickness(0, 0, 0, 4)
            };

            statusText = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.Gray
            };

            footer.Children.Add(progressBar);
            footer.Children.Add(statusText);
            Grid.SetRow(footer, 2);
            root.Children.Add(footer);

            Content = root;

            ReloadGameList();
        }

        // ==================================================================
        //  小工具
        // ==================================================================

        /// <summary>
        /// 打开窗口时预选一个游戏（从右键菜单进来时用）。窗口构造完、列表已加载，
        /// 所以这里直接选就行 —— 选中会触发 <see cref="OnGameSelected"/> 去读远端。
        /// </summary>
        public void Preselect(Game game)
        {
            if (game == null || gameList.ItemsSource == null)
            {
                return;
            }

            var match = ((IEnumerable<Game>)gameList.ItemsSource)
                .FirstOrDefault(g => g.Id == game.Id);
            if (match != null)
            {
                gameList.SelectedItem = match;
                gameList.ScrollIntoView(match);
            }
        }

        private static TextBlock SectionHeader(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 6, 0, 4)
            };
        }

        private Button MakeButton(string text, Action action)
        {
            var button = new Button
            {
                Content = text,
                Padding = new Thickness(10, 3, 10, 3),
                Margin = new Thickness(0, 0, 6, 0)
            };
            button.Click += (s, e) => action();
            buttons.Add(button);
            return button;
        }

        /// <summary>后台动作的统一入口：忙就拒绝，回来只在 Dispatcher 上更新界面。</summary>
        private void Run(string label, Action work, Action after = null)
        {
            if (busy)
            {
                SetStatus("上一个操作还没结束，稍等一下。");
                return;
            }

            busy = true;
            SetBusy(true);
            SetStatus(label + "…");
            ShowProgress(true, 0);

            Task.Run(() =>
            {
                string error = null;
                try
                {
                    work();
                }
                catch (OperationCanceledException)
                {
                    error = "已取消";
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    VaultLog.Error("存档操作失败：" + label, ex);
                }

                Dispatcher.Invoke(() =>
                {
                    busy = false;
                    SetBusy(false);
                    ShowProgress(false, 0);

                    if (error != null)
                    {
                        SetStatus(label + " 失败：" + error);
                        return;
                    }

                    if (after != null)
                    {
                        try
                        {
                            after();
                            return;
                        }
                        catch (Exception ex)
                        {
                            // after 里通常还要拉远端（比如重载列表）——它自己也会失败，
                            // 这时候要把它的话说出来，而不是卡在「操作成功」上
                            VaultLog.Error("存档操作收尾失败：" + label, ex);
                            SetStatus(label + " 已完成，但刷新界面时出错：" + ex.Message);
                            return;
                        }
                    }

                    SetStatus(label + " 完成。");
                });
            });
        }

        private void SetBusy(bool value)
        {
            foreach (var button in buttons)
            {
                button.IsEnabled = !value;
            }

            gameList.IsEnabled = !value;
            branchBox.IsEnabled = !value;
        }

        private void ShowProgress(bool visible, double percent)
        {
            progressBar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (visible)
            {
                progressBar.IsIndeterminate = percent <= 0;
                if (percent > 0)
                {
                    progressBar.Value = Math.Min(100, percent);
                }
            }
        }

        private void SetStatus(string text)
        {
            statusText.Text = text ?? string.Empty;
        }

        /// <summary>进度回调（可能来自后台线程）。刷太快会把 UI 线程打满，所以限流。</summary>
        internal void ReportProgress(SyncProgress progress)
        {
            if (progress == null)
            {
                return;
            }

            var now = DateTime.UtcNow;
            if ((now - lastProgressAt).TotalMilliseconds < 120)
            {
                return;
            }

            lastProgressAt = now;
            var text = progress.Describe();
            var percent = progress.BytesTotal > 0
                ? progress.BytesDone * 100.0 / progress.BytesTotal
                : 0;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                SetStatus(text);
                ShowProgress(true, percent);
            }));
        }

        internal void ReportStage(string text)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                SetStatus(text);
                ShowProgress(true, 0);
            }));
        }

        // ==================================================================
        //  游戏列表
        // ==================================================================

        private void ReloadGameList()
        {
            var all = api.Database != null
                ? api.Database.Games.OrderBy(g => g.Name, StringComparer.CurrentCulture).ToList()
                : new List<Game>();

            games = all;
            ApplyGameFilter();
            SetStatus("共 " + all.Count + " 个游戏。左边选一个开始。");
        }

        private void ApplyGameFilter()
        {
            var filter = (filterBox.Text ?? string.Empty).Trim();
            var shown = string.IsNullOrEmpty(filter)
                ? games
                : games.Where(g => (g.Name ?? string.Empty)
                    .IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) >= 0).ToList();

            var previous = current;
            gameList.ItemsSource = shown;
            if (previous != null && shown.Contains(previous))
            {
                gameList.SelectedItem = previous;
            }
        }

        private void OnGameSelected()
        {
            if (busy)
            {
                return;
            }

            var picked = gameList.SelectedItem as Game;
            if (picked == null || ReferenceEquals(picked, current))
            {
                return;
            }

            current = picked;
            titleText.Text = "存档管理 —— " + picked.Name;
            ReloadCurrent();
        }

        /// <summary>重新读当前游戏的远端清单并重建右侧。</summary>
        private void ReloadCurrent()
        {
            var game = current;
            if (game == null)
            {
                return;
            }

            loading = true;
            pathPanel.Children.Clear();
            snapshotPanel.Children.Clear();
            rowReaders.Clear();
            SetStatus("正在读取「" + game.Name + "」的远端存档…");

            Task.Run(() =>
            {
                SaveGameManifest loaded = null;
                var localNotes = new List<string>();
                List<SaveSnapshot> remote = new List<SaveSnapshot>();
                List<SaveBranch> remoteBranches = new List<SaveBranch>();
                string error = null;

                try
                {
                    var engine = saves.CreateEngine(CancellationToken.None, new NullSaveReporter());
                    loaded = saves.GetManifest(engine, game, false, out localNotes);
                    if (loaded != null)
                    {
                        remoteBranches = loaded.Branches.ToList();
                        var branch = PickBranch(loaded);
                        remote = loaded.SnapshotsIn(branch);
                    }
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    VaultLog.Error("读远端存档清单失败：" + game.Name, ex);
                }

                Dispatcher.Invoke(() =>
                {
                    loading = false;

                    if (!ReferenceEquals(game, current))
                    {
                        // 用户已经切走了，这次结果就丢掉
                        return;
                    }

                    manifest = loaded;
                    notes = localNotes;
                    branches = remoteBranches.Count > 0
                        ? remoteBranches
                        : new List<SaveBranch> { new SaveBranch { Name = DefaultBranch } };

                    RenderBranches();
                    RenderPaths();
                    snapshots = remote;
                    RenderSnapshots();

                    if (error != null)
                    {
                        SetStatus("读远端失败：" + error + "（下面的路径定义仍然可以编辑）");
                    }
                    else if (loaded == null)
                    {
                        SetStatus("远端还没有这个游戏的存档。填好路径后点「上传当前状态」就把第一份放上去。");
                    }
                    else
                    {
                        SetStatus("远端有 " + loaded.Snapshots.Count + " 份快照，分属 "
                                  + loaded.Branches.Count + " 条分支。");
                    }
                });
            });
        }

        private string DefaultBranch
        {
            get
            {
                var name = (saves.Settings.SaveDefaultBranch ?? string.Empty).Trim();
                return name.Length == 0 ? SaveBranch.Default : name;
            }
        }

        private string PickBranch(SaveGameManifest forManifest)
        {
            var wanted = branchBox.SelectedItem as string;
            if (!string.IsNullOrWhiteSpace(wanted)
                && forManifest.Branches.Any(b => string.Equals(b.Name, wanted, StringComparison.OrdinalIgnoreCase)))
            {
                return wanted;
            }

            return DefaultBranch;
        }

        // ==================================================================
        //  路径定义
        // ==================================================================

        private void RenderBranches()
        {
            var keep = branchBox.SelectedItem as string;
            branchBox.ItemsSource = null;
            branchBox.ItemsSource = branches.Select(b => b.Name).ToList();

            var wanted = !string.IsNullOrWhiteSpace(keep)
                ? keep
                : (branches.Any(b => string.Equals(b.Name, DefaultBranch, StringComparison.OrdinalIgnoreCase))
                    ? DefaultBranch
                    : (branches.Count > 0 ? branches[0].Name : SaveBranch.Default));

            branchBox.SelectedItem = wanted;
        }

        private void RefreshSnapshotsFromBranch()
        {
            if (busy || loading || manifest == null || current == null)
            {
                return;
            }

            var branch = branchBox.SelectedItem as string;
            snapshots = manifest.SnapshotsIn(branch);
            RenderSnapshots();
        }

        /// <summary>把「Playnite 那份 + 插件那份」摊成可编辑的行。</summary>
        private void RenderPaths()
        {
            pathPanel.Children.Clear();
            rowReaders.Clear();

            if (current == null)
            {
                return;
            }

            var rows = manifest != null && manifest.Paths.Count > 0
                ? manifest.Paths
                : saves.MergedPaths(current, out notes);

            if (rows.Count == 0)
            {
                pathRowsCache = new List<SavePathSpec>();
                pathPanel.Children.Add(new TextBlock
                {
                    Text = "还没有任何路径定义。点「添加一行」，或者点「自动嗅探」让它去找。",
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.7,
                    Margin = new Thickness(0, 2, 0, 2)
                });
            }
            else
            {
                pathRowsCache = rows.Select(r => r.GetCopy()).ToList();
            }

            for (var i = 0; i < pathRowsCache.Count; i++)
            {
                pathPanel.Children.Add(BuildPathRow(i));
            }

            if (notes.Count > 0)
            {
                var noteText = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.7,
                    Margin = new Thickness(0, 4, 0, 0),
                    Foreground = Brushes.DarkOrange,
                    Text = "· " + string.Join(Environment.NewLine + "· ", notes.ToArray())
                };
                pathPanel.Children.Add(noteText);
            }
        }

        private List<SavePathSpec> pathRowsCache = new List<SavePathSpec>();

        private FrameworkElement BuildPathRow(int index)
        {
            var spec = pathRowsCache[index];

            var row = new Grid { Margin = new Thickness(0, 0, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(78) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });

            var title = new TextBox
            {
                Text = spec.Title ?? string.Empty,
                Margin = new Thickness(0, 0, 4, 0),
                ToolTip = "跨机器的对齐键。两台机器上同名的路径会互相对齐。"
            };

            var typeBox = new ComboBox { Margin = new Thickness(0, 0, 4, 0) };
            typeBox.Items.Add("目录");
            typeBox.Items.Add("文件");
            typeBox.SelectedIndex = spec.Type == SaveElementType.File ? 1 : 0;

            var path = new TextBox
            {
                Text = spec.Path ?? string.Empty,
                Margin = new Thickness(0, 0, 4, 0),
                ToolTip = "可以写 {LocalAppData}、{GameDir} 这类占位符（勾上「自适应」才会展开）。"
            };

            var adaptive = new CheckBox
            {
                IsChecked = spec.AutoAdaptive,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 4, 0),
                ToolTip = "按当前机器展开路径里的占位符。换机器还能用的关键就是它。"
            };

            var source = new TextBlock
            {
                Text = SourceLabel(spec),
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.7,
                Margin = new Thickness(2, 0, 4, 0),
                ToolTip = spec.Source == SaveSource.Playnite
                    ? "来自 Playnite 的游戏编辑；在这里改会写回 Playnite。"
                    : "来自本插件（或嗅探）；改这里只影响插件自己那份。"
            };

            var ignore = new TextBox
            {
                Text = spec.Ignore == null ? string.Empty : string.Join(", ", spec.Ignore.ToArray()),
                Margin = new Thickness(0, 0, 4, 0),
                ToolTip = "要忽略的文件，逗号分隔。例如 *.log, cache.dat"
            };

            var remove = new Button
            {
                Content = "×",
                Padding = new Thickness(0),
                ToolTip = "从这一行删掉（保存改动后才生效）"
            };

            var captured = index;
            remove.Click += (s, e) =>
            {
                if (captured < pathRowsCache.Count)
                {
                    pathRowsCache.RemoveAt(captured);
                    RenderPathRowsOnly();
                }
            };

            Grid.SetColumn(title, 0);
            Grid.SetColumn(typeBox, 1);
            Grid.SetColumn(path, 2);
            Grid.SetColumn(adaptive, 3);
            Grid.SetColumn(source, 4);
            Grid.SetColumn(ignore, 5);
            Grid.SetColumn(remove, 6);
            row.Children.Add(title);
            row.Children.Add(typeBox);
            row.Children.Add(path);
            row.Children.Add(adaptive);
            row.Children.Add(source);
            row.Children.Add(ignore);
            row.Children.Add(remove);

            rowReaders.Add(() => new SavePathSpec
            {
                Title = (title.Text ?? string.Empty).Trim(),
                Type = typeBox.SelectedIndex == 1 ? SaveElementType.File : SaveElementType.Directory,
                Path = (path.Text ?? string.Empty).Trim(),
                AutoAdaptive = adaptive.IsChecked == true,
                Enabled = true,
                Source = spec.Source,
                Comment = spec.Comment,
                Ignore = ParseIgnore(ignore.Text)
            });

            return row;
        }

        private static List<string> ParseIgnore(string text)
        {
            var list = new List<string>();
            foreach (var piece in (text ?? string.Empty).Split(new[] { ',', '，', ';', '；' }))
            {
                var trimmed = piece.Trim();
                if (trimmed.Length > 0 && !list.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                {
                    list.Add(trimmed);
                }
            }

            return list.Count == 0 ? null : list;
        }

        private static string SourceLabel(SavePathSpec spec)
        {
            if (spec == null)
            {
                return string.Empty;
            }

            switch (spec.Source)
            {
                case SaveSource.Playnite:
                    return "Playnite";
                case SaveSource.Sniffed:
                    return "嗅探";
                default:
                    return "插件";
            }
        }

        /// <summary>只重建路径区（删行之后用，免得把整页连同滚动位置一起重置）。</summary>
        private void RenderPathRowsOnly()
        {
            pathPanel.Children.Clear();
            rowReaders.Clear();

            for (var i = 0; i < pathRowsCache.Count; i++)
            {
                pathPanel.Children.Add(BuildPathRow(i));
            }
        }

        private void AddRow()
        {
            if (current == null)
            {
                SetStatus("先选一个游戏。");
                return;
            }

            pathRowsCache.Add(new SavePathSpec
            {
                Title = string.Empty,
                Type = SaveElementType.Directory,
                Path = string.Empty,
                AutoAdaptive = true,
                Source = SaveSource.Plugin
            });

            RenderPathRowsOnly();
            SetStatus("加了一行。填好路径再点「保存路径改动」。");
        }

        /// <summary>把界面上的编辑按来源写回两处。</summary>
        private void SavePathEdits()
        {
            var game = current;
            if (game == null)
            {
                SetStatus("先选一个游戏。");
                return;
            }

            var edited = rowReaders.Select(r => r()).ToList();

            var unnamed = edited.FirstOrDefault(s => string.IsNullOrWhiteSpace(s.Path));
            if (unnamed != null)
            {
                SetStatus("有一行的路径是空的，先补上或删掉它。");
                return;
            }

            var duplicated = edited
                .GroupBy(s => (s.Title ?? string.Empty).Trim(), StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(g => g.Count() > 1 && g.Key.Length > 0);
            if (duplicated != null)
            {
                // Title 是跨机器的对齐键，重名会让「恢复哪一条」变得含糊
                SetStatus("有两行的名字都是「" + duplicated.Key + "」，改掉一个再保存。");
                return;
            }

            var toPlaynite = edited.Where(s => s.Source == SaveSource.Playnite).ToList();
            var toPlugin = edited.Where(s => s.Source != SaveSource.Playnite).ToList();

            try
            {
                // ---- 写回 Playnite（那是用户自己填的那份，理应还在那儿） ----
                if (game.SavePaths == null)
                {
                    game.SavePaths = new ObservableCollection<SavePath>();
                }

                game.SavePaths.Clear();
                foreach (var spec in toPlaynite)
                {
                    var path = new SavePath(
                        spec.Type == SaveElementType.File ? GameSaveType.File : GameSaveType.Directory,
                        spec.Path,
                        spec.AutoAdaptive)
                    {
                        Title = spec.Title
                    };
                    game.SavePaths.Add(path);
                }

                api.Database.Games.Update(game);

                // ---- 写回插件这份 ----
                saves.SetPluginPaths(game.Id.ToString(), toPlugin);
            }
            catch (Exception ex)
            {
                VaultLog.Error("保存存档路径失败", ex);
                SetStatus("保存失败：" + ex.Message);
                return;
            }

            SetStatus("已保存：" + toPlaynite.Count + " 条写回 Playnite，"
                      + toPlugin.Count + " 条存在插件里。");

            // 路径变了，清单里的定义也要跟着变
            if (manifest != null)
            {
                manifest.Paths = edited;
            }

            RenderPaths();
        }

        // ==================================================================
        //  嗅探
        // ==================================================================

        private void SniffPaths()
        {
            var game = current;
            if (game == null)
            {
                SetStatus("先选一个游戏。");
                return;
            }

            var ctx = BuildSniffContext(game, out var watchRoots);

            Task.Run(() =>
            {
                var all = new List<SaveSniffCandidate>();
                var messages = new List<string>();

                try
                {
                    all.AddRange(SaveSniffer.Heuristic(ctx));
                }
                catch (Exception ex)
                {
                    messages.Add("常见位置嗅探失败：" + ex.Message);
                    VaultLog.Error("启发式嗅探失败", ex);
                }

                // 会话差分：只有存在「上次启动前」的指纹时才有意义
                try
                {
                    var before = saves.LoadFingerprint(game.Id.ToString());
                    if (before.Count > 0)
                    {
                        var limits = SaveSniffLimits.Default;
                        foreach (var pair in before)
                        {
                            var after = SaveSniffer.Capture(pair.Key, limits);
                            all.AddRange(SaveSniffer.Diff(pair.Value, after, ctx));
                        }
                    }
                }
                catch (Exception ex)
                {
                    messages.Add("会话差分失败：" + ex.Message);
                    VaultLog.Error("会话差分失败", ex);
                }

                // PCGamingWiki：抓不到就只是少一档，不报错
                try
                {
                    string wikiError;
                    var wiki = PcgamingWikiClient.TrySniff(game.Name, ctx, out wikiError);
                    if (wiki.Count > 0)
                    {
                        all.AddRange(wiki);
                    }
                    else if (!string.IsNullOrEmpty(wikiError))
                    {
                        messages.Add("PCGamingWiki：" + wikiError);
                    }
                }
                catch (Exception ex)
                {
                    messages.Add("PCGamingWiki 抓取失败：" + ex.Message);
                }

                var merged = PcgamingWikiClient.Merge(all);

                Dispatcher.Invoke(() =>
                {
                    if (!ReferenceEquals(game, current))
                    {
                        return;
                    }

                    if (merged.Count == 0)
                    {
                        SetStatus("没嗅到候选项。"
                                  + (messages.Count > 0
                                      ? "（" + string.Join("；", messages.ToArray()) + "）"
                                      : "可以手动添加路径，或者先玩一次游戏再回来用会话差分。"));
                        return;
                    }

                    var dialog = new SaveSniffDialog(merged, messages) { Owner = this };
                    if (dialog.ShowDialog() != true)
                    {
                        SetStatus("嗅探结果没有采纳。");
                        return;
                    }

                    foreach (var candidate in dialog.Selected)
                    {
                        pathRowsCache.Add(candidate.ToSpec());
                    }

                    RenderPathRowsOnly();
                    SetStatus("采纳了 " + dialog.Selected.Count
                              + " 条候选，确认无误后点「保存路径改动」。");
                });
            });
        }

        private SaveSniffContext BuildSniffContext(Game game, out List<string> watchRoots)
        {
            watchRoots = SaveSniffer.WatchRoots(game.InstallDirectory);

            var known = new List<string>();
            if (game.SavePaths != null)
            {
                known.AddRange(game.SavePaths.Select(p => p == null ? null : p.Path));
            }

            known.AddRange(saves.PluginPaths(game.Id.ToString())
                .Where(s => s != null)
                .Select(s => s.Path));

            return new SaveSniffContext
            {
                GameName = game.Name,
                GameInstallDir = game.InstallDirectory,
                ExecutableNames = new List<string>(),
                KnownPaths = known.Where(k => !string.IsNullOrWhiteSpace(k)).ToList(),
                Budget = TimeSpan.FromSeconds(8),
                MinScore = 30,
                MaxPerTier = 20
            };
        }

        // ==================================================================
        //  快照
        // ==================================================================

        private void RenderSnapshots()
        {
            snapshotPanel.Children.Clear();
            selectedSnapshot = null;

            if (snapshots.Count == 0)
            {
                snapshotPanel.Children.Add(new TextBlock
                {
                    Text = manifest == null
                        ? "远端还没有这个游戏的快照。"
                        : "这条分支上还没有快照。",
                    Opacity = 0.7,
                    TextWrapping = TextWrapping.Wrap
                });
                return;
            }

            foreach (var snapshot in snapshots)
            {
                snapshotPanel.Children.Add(BuildSnapshotRow(snapshot));
            }
        }

        private FrameworkElement BuildSnapshotRow(SaveSnapshot snapshot)
        {
            var border = new Border
            {
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)),
                Margin = new Thickness(0, 0, 0, 3),
                Padding = new Thickness(6, 3, 6, 3),
                Background = Brushes.Transparent
            };

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new TextBlock
            {
                Text = snapshot.Describe(),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };

            var pick = new Button
            {
                Content = "选中",
                Padding = new Thickness(8, 1, 8, 1),
                VerticalAlignment = VerticalAlignment.Center
            };

            var captured = snapshot;
            pick.Click += (s, e) =>
            {
                selectedSnapshot = captured;
                foreach (var child in snapshotPanel.Children)
                {
                    var item = child as Border;
                    if (item != null)
                    {
                        item.Background = Brushes.Transparent;
                    }
                }

                border.Background = new SolidColorBrush(Color.FromArgb(28, 77, 150, 255));
                SetStatus("已选中：" + captured.Describe());
            };

            Grid.SetColumn(text, 0);
            Grid.SetColumn(pick, 1);
            row.Children.Add(text);
            row.Children.Add(pick);
            border.Child = row;

            return border;
        }

        private ISaveSyncReporter MakeReporter()
        {
            return new WindowReporter(this);
        }

        // ---- 上传 ----

        private void UploadNow()
        {
            var game = current;
            if (game == null || busy)
            {
                return;
            }

            // 先确保路径改动落地 —— 否则会出现「界面改了但传的是旧的」这种最难查的问题
            if (rowReaders.Count > 0)
            {
                SavePathEdits();
                if (current == null)
                {
                    return;
                }
            }

            var branch = (branchBox.SelectedItem as string) ?? DefaultBranch;
            SaveSyncOutcome outcome = null;

            Run("上传存档", () =>
            {
                var engine = saves.CreateEngine(CancellationToken.None, MakeReporter());
                List<string> localNotes;
                var loaded = saves.GetManifest(engine, game, true, out localNotes);

                outcome = engine.Upload(game.Id.ToString(), game.Name, game.InstallDirectory,
                    loaded, new SaveSyncOptions
                    {
                        Branch = branch,
                        KeepPerBranch = saves.Settings.SaveKeepPerBranch,
                        Origin = SaveSnapshotOrigin.Manual
                    });
            },
            () =>
            {
                if (outcome == null)
                {
                    return;
                }

                SetStatus(outcome.Ok
                    ? "上传完成：" + outcome.Counters.Describe()
                    : "上传失败：" + outcome.Failure);
                ReloadCurrent();
            });
        }

        // ---- 恢复 ----

        private void RestoreSelected()
        {
            var game = current;
            if (game == null || busy)
            {
                return;
            }

            if (selectedSnapshot == null)
            {
                SetStatus("先在上面点一条快照的「选中」。");
                return;
            }

            var target = selectedSnapshot;
            var confirm = MessageBox.Show(
                "要把存档恢复成这份快照吗？" + Environment.NewLine + Environment.NewLine
                + target.Describe() + Environment.NewLine + Environment.NewLine
                + "恢复前会先把当前状态留一份底（本地 + 可选地推一份快照到远端），"
                + "而且只覆盖快照里有的文件，本地多出来的文件不会被删。",
                "Vault 存档管理", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes)
            {
                SetStatus("已取消恢复。");
                return;
            }

            var branch = (branchBox.SelectedItem as string) ?? DefaultBranch;
            SaveSyncOutcome outcome = null;

            Run("恢复存档", () =>
            {
                var engine = saves.CreateEngine(CancellationToken.None, MakeReporter());
                List<string> localNotes;
                var loaded = saves.GetManifest(engine, game, true, out localNotes);

                outcome = engine.Download(game.Id.ToString(), game.Name, game.InstallDirectory,
                    loaded, new SaveSyncOptions
                    {
                        Branch = branch,
                        SnapshotId = target.Id,
                        SafetyBackup = true,
                        SnapshotBeforeRestore = saves.Settings.SaveBackupBeforeRestore,
                        KeepLocalBackups = saves.Settings.SaveKeepLocalBackups,
                        KeepPerBranch = saves.Settings.SaveKeepPerBranch
                    });
            },
            () =>
            {
                if (outcome == null)
                {
                    return;
                }

                SetStatus(outcome.Ok
                    ? "恢复完成：" + outcome.Counters.Describe()
                    : "恢复失败：" + outcome.Failure);
                ReloadCurrent();
            });
        }

        // ---- 标星 ----

        private void TogglePin()
        {
            var game = current;
            if (game == null || busy)
            {
                return;
            }

            var target = selectedSnapshot;
            if (target == null)
            {
                SetStatus("先选中一条快照。");
                return;
            }

            var wanted = !target.Pinned;
            Run("修改标星", () =>
            {
                var engine = saves.CreateEngine(CancellationToken.None, new NullSaveReporter());
                List<string> localNotes;
                var loaded = saves.GetManifest(engine, game, true, out localNotes);
                var summary = loaded.FindSnapshot(target.Id);
                if (summary == null)
                {
                    throw new InvalidOperationException("远端清单里找不到这份快照了，可能刚被别的机器删掉。");
                }

                summary.Pinned = wanted;
                engine.SaveManifest(SaveSyncEngine.SafeGameKey(game.Id.ToString()), loaded);
            },
            () =>
            {
                SetStatus(wanted ? "已标星：保留策略不会删它。" : "已取消标星。");
                ReloadCurrent();
            });
        }

        // ---- 删除 ----

        private void DeleteSelected()
        {
            var game = current;
            if (game == null || busy)
            {
                return;
            }

            var target = selectedSnapshot;
            if (target == null)
            {
                SetStatus("先选中一条快照。");
                return;
            }

            var confirm = MessageBox.Show(
                "确定要从 NAS 上删掉这份快照吗？" + Environment.NewLine + Environment.NewLine
                + target.Describe() + Environment.NewLine + Environment.NewLine
                + "这是不可逆的。没有别的快照引用到的文件内容会被一并回收。",
                "Vault 存档管理", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes)
            {
                SetStatus("已取消删除。");
                return;
            }

            var branch = (branchBox.SelectedItem as string) ?? DefaultBranch;
            SaveSyncOutcome outcome = null;

            Run("删除快照", () =>
            {
                var engine = saves.CreateEngine(CancellationToken.None, MakeReporter());
                List<string> localNotes;
                var loaded = saves.GetManifest(engine, game, true, out localNotes);
                outcome = engine.DeleteSnapshot(SaveSyncEngine.SafeGameKey(game.Id.ToString()),
                    loaded, branch, target.Id, new SaveSyncOptions { AllowDelete = true });
            },
            () =>
            {
                SetStatus(outcome != null && outcome.Ok
                    ? "已删除：" + outcome.Counters.Describe()
                    : "删除失败：" + (outcome == null ? "未知原因" : outcome.Failure));
                ReloadCurrent();
            });
        }

        // ---- 裁剪 ----

        private void PruneNow()
        {
            var game = current;
            if (game == null || busy)
            {
                return;
            }

            var keep = saves.Settings.SaveKeepPerBranch;
            var confirm = MessageBox.Show(
                "按保留策略裁剪吗？每个分支最多留 "
                + (keep <= 0 ? "不限（等于什么都不删）" : keep + " 份") + "。" + Environment.NewLine
                + "标星的快照不会被删，每条分支至少留最新一份。",
                "Vault 存档管理", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes)
            {
                SetStatus("已取消裁剪。");
                return;
            }

            SaveSyncOutcome outcome = null;

            Run("裁剪旧快照", () =>
            {
                var engine = saves.CreateEngine(CancellationToken.None, MakeReporter());
                List<string> localNotes;
                var loaded = saves.GetManifest(engine, game, true, out localNotes);
                outcome = engine.Prune(SaveSyncEngine.SafeGameKey(game.Id.ToString()), loaded,
                    new SaveSyncOptions
                    {
                        KeepPerBranch = keep,
                        AllowDelete = true,
                        Branch = (branchBox.SelectedItem as string) ?? DefaultBranch
                    });
            },
            () =>
            {
                SetStatus(outcome != null && outcome.Ok
                    ? "裁剪完成：" + outcome.Counters.Describe()
                    : "裁剪失败：" + (outcome == null ? "未知原因" : outcome.Failure));
                ReloadCurrent();
            });
        }

        // ---- 分支 ----

        private void CreateBranch()
        {
            var game = current;
            if (game == null || busy)
            {
                return;
            }

            var name = PromptForText("新分支叫什么名字？（例如 ng+、mod、二周目）", "新建分支");
            name = (name ?? string.Empty).Trim();
            if (name.Length == 0)
            {
                SetStatus("新分支没起名字，已取消。");
                return;
            }

            var from = selectedSnapshot == null ? null : selectedSnapshot.Id;

            Run("新建分支", () =>
            {
                var engine = saves.CreateEngine(CancellationToken.None, new NullSaveReporter());
                List<string> localNotes;
                var loaded = saves.GetManifest(engine, game, true, out localNotes);
                engine.CreateBranch(SaveSyncEngine.SafeGameKey(game.Id.ToString()), loaded, name,
                    from, from == null ? "空分支" : "从快照 " + from + " 分叉");
            },
            () =>
            {
                SetStatus("分支「" + name + "」建好了。切到它再上传，就得到一条独立的存档历史。");
                ReloadCurrent();
            });
        }

        /// <summary>
        /// 一个小输入框。自己写而不是用 SDK 的 <c>SelectString</c>，是因为它的返回值
        /// 是个包装类型（<c>StringSelectionDialogResult</c>），拿字符串还得再拆一层，
        /// 而且不同 SDK 版本上字段名不一样 —— 为一个输入框去猜别人的 API 不划算。
        /// </summary>
        private string PromptForText(string message, string title)
        {
            var dialog = new Window
            {
                Title = title,
                Width = 420,
                Height = 170,
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize
            };

            var panel = new StackPanel { Margin = new Thickness(14) };

            panel.Children.Add(new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            });

            var input = new TextBox();
            panel.Children.Add(input);

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };

            var ok = new Button { Content = "确定", Padding = new Thickness(14, 3, 14, 3), Margin = new Thickness(0, 0, 8, 0) };
            ok.Click += (s, e) =>
            {
                dialog.DialogResult = true;
            };

            var cancel = new Button { Content = "取消", Padding = new Thickness(14, 3, 14, 3) };
            cancel.Click += (s, e) =>
            {
                dialog.DialogResult = false;
            };

            row.Children.Add(ok);
            row.Children.Add(cancel);
            panel.Children.Add(row);

            dialog.Content = panel;
            input.Focus();

            return dialog.ShowDialog() == true ? input.Text : null;
        }

        // ==================================================================
        //  进度回调
        // ==================================================================
        private class WindowReporter : ISaveSyncReporter
        {
            private readonly VaultSaveWindow owner;

            public WindowReporter(VaultSaveWindow owner)
            {
                this.owner = owner;
            }

            public void Stage(string text)
            {
                owner.ReportStage(text);
            }

            public void Progress(SyncProgress progress)
            {
                owner.ReportProgress(progress);
            }

            public void Log(string line)
            {
                VaultLog.Info("存档同步：" + line);
            }
        }
    }

    /// <summary>
    /// 嗅探结果的勾选窗。**候选的唯一出口是「用户勾了哪几条」** ——
    /// 嗅探永远不自己往路径定义里写东西，猜错的代价（把 Cache 当存档上传、
    /// 恢复时覆盖真存档）太大。
    /// </summary>
    internal class SaveSniffDialog : Window
    {
        private readonly List<CheckBox> boxes = new List<CheckBox>();
        private readonly List<SaveSniffCandidate> candidates;

        public SaveSniffDialog(List<SaveSniffCandidate> candidates, List<string> messages)
        {
            this.candidates = candidates;

            Title = "嗅探结果 —— 勾选要采纳的路径";
            Width = 780;
            Height = 560;
            MinWidth = 620;
            MinHeight = 400;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var root = new DockPanel { Margin = new Thickness(12) };

            var header = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
                Text = "勾选你认得的存档位置。默认只勾了分数最高的前两条，别的要你自己确认。"
                       + "分数只是排序用的，不代表「一定对」。"
            };
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);

            if (messages != null && messages.Count > 0)
            {
                var warn = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 8),
                    Foreground = Brushes.DarkOrange,
                    Text = "（" + string.Join("；", messages.ToArray()) + "）"
                };
                DockPanel.SetDock(warn, Dock.Top);
                root.Children.Add(warn);
            }

            var footer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 8, 0, 0)
            };

            var ok = new Button { Content = "采纳勾选项", Padding = new Thickness(14, 3, 14, 3), Margin = new Thickness(0, 0, 8, 0) };
            ok.Click += (s, e) =>
            {
                DialogResult = true;
            };

            var cancel = new Button { Content = "取消", Padding = new Thickness(14, 3, 14, 3) };
            cancel.Click += (s, e) =>
            {
                DialogResult = false;
            };

            footer.Children.Add(ok);
            footer.Children.Add(cancel);
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);

            var list = new StackPanel();

            for (var i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                var box = new CheckBox
                {
                    IsChecked = i < 2,
                    Margin = new Thickness(0, 0, 0, 2),
                    Content = candidate.Describe(),
                    Tag = candidate
                };
                boxes.Add(box);
                list.Children.Add(box);
            }

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = list
            };
            root.Children.Add(scroll);

            Content = root;
        }

        /// <summary>用户勾选的候选。</summary>
        public List<SaveSniffCandidate> Selected
        {
            get
            {
                return boxes
                    .Where(b => b.IsChecked == true)
                    .Select(b => b.Tag as SaveSniffCandidate)
                    .Where(c => c != null)
                    .ToList();
            }
        }
    }
}
