using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Playnite.SDK;
using VaultDemo.Models;
using VaultDemo.Services;

namespace VaultDemo.UI
{
    /// <summary>
    /// 仓库管理窗口：列出 NAS 上的应用，并提供删除。
    ///
    /// 进入这个窗口之前口令已经校验过一次，但**每次删除都会再校验一次** ——
    /// 窗口开着不动的这段时间里，口令可能在别处被换掉。
    /// </summary>
    public class VaultAdminWindow : Window
    {
        private readonly VaultService service;
        private readonly StackPanel listPanel;
        private readonly TextBlock statusText;
        private readonly TextBlock summaryText;
        private readonly Button refreshButton;
        private readonly Button changePasswordButton;

        private List<AppEntry> apps = new List<AppEntry>();

        public VaultAdminWindow(VaultService service)
        {
            this.service = service;

            Title = "Vault 仓库管理";
            Width = 820;
            Height = 560;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            MinWidth = 640;
            MinHeight = 380;

            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // ---- 标题 + 工具条 ----
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };

            var title = new TextBlock
            {
                Text = "仓库管理",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };

            var tools = new StackPanel { Orientation = Orientation.Horizontal };

            changePasswordButton = new Button
            {
                Content = "修改管理口令",
                Padding = new Thickness(12, 3, 12, 3),
                Margin = new Thickness(0, 0, 8, 0)
            };
            changePasswordButton.Click += OnChangePassword;

            refreshButton = new Button
            {
                Content = "刷新",
                Padding = new Thickness(12, 3, 12, 3)
            };
            refreshButton.Click += (s, e) => Load();

            tools.Children.Add(changePasswordButton);
            tools.Children.Add(refreshButton);

            DockPanel.SetDock(tools, Dock.Right);
            header.Children.Add(tools);
            header.Children.Add(title);
            Grid.SetRow(header, 0);
            root.Children.Add(header);

            // ---- 列头 ----
            var columnHeader = BuildRow("应用", "Id", "网络占用", "文件", "布局", null);
            columnHeader.Background = new SolidColorBrush(Color.FromArgb(24, 0, 0, 0));
            columnHeader.Margin = new Thickness(0, 0, 0, 2);
            Grid.SetRow(columnHeader, 1);
            root.Children.Add(columnHeader);

            // ---- 应用列表 ----
            listPanel = new StackPanel();
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = listPanel
            };
            Grid.SetRow(scroll, 2);
            root.Children.Add(scroll);

            // ---- 底部状态 ----
            var footer = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };

            summaryText = new TextBlock { Opacity = 0.75, TextWrapping = TextWrapping.Wrap };

            statusText = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
                Foreground = Brushes.Gray
            };

            footer.Children.Add(summaryText);
            footer.Children.Add(statusText);
            Grid.SetRow(footer, 3);
            root.Children.Add(footer);

            Content = root;

            Loaded += (s, e) => Load();
        }

        // ---------------------------------------------------------------- 加载

        /// <summary>拉取远端索引并重建列表。网络走后台线程，UI 只在 Dispatcher 上重建。</summary>
        private void Load()
        {
            refreshButton.IsEnabled = false;
            changePasswordButton.IsEnabled = false;
            SetStatus("正在读取仓库索引...", false, Brushes.Gray);
            listPanel.Children.Clear();
            summaryText.Text = string.Empty;

            Task.Run(() =>
            {
                List<AppEntry> loaded = null;
                string source = null;
                string error = null;
                try
                {
                    var index = service.GetIndex(out source, out error);
                    loaded = index == null
                        ? new List<AppEntry>()
                        : index.Apps.OrderBy(a => a.Name, StringComparer.CurrentCulture).ToList();
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    VaultLog.Error("仓库管理：读取索引失败", ex);
                }

                Dispatcher.Invoke(() =>
                {
                    refreshButton.IsEnabled = true;
                    changePasswordButton.IsEnabled = true;

                    if (loaded == null)
                    {
                        SetStatus("读取失败：" + (error ?? "未知错误"), false, Brushes.IndianRed);
                        return;
                    }

                    apps = loaded;
                    Render();

                    var total = apps.Sum(a => a.TotalBytes);
                    summaryText.Text = string.Format(
                        "共 {0} 个应用，{1}（索引来源：{2}）",
                        apps.Count, FormatSize(total), string.IsNullOrEmpty(source) ? "未知" : source);

                    if (!string.IsNullOrEmpty(error))
                    {
                        SetStatus("注意：" + error, false, Brushes.DarkOrange);
                    }
                    else
                    {
                        SetStatus(string.Empty, false, Brushes.Gray);
                    }
                });
            });
        }

        private void Render()
        {
            listPanel.Children.Clear();

            if (apps.Count == 0)
            {
                listPanel.Children.Add(new TextBlock
                {
                    Text = "仓库里还没有归档的应用。",
                    Opacity = 0.6,
                    Margin = new Thickness(4, 12, 0, 0)
                });
                return;
            }

            foreach (var app in apps)
            {
                var captured = app;
                var deleteButton = new Button
                {
                    Content = "删除",
                    Padding = new Thickness(12, 2, 12, 2),
                    Foreground = Brushes.IndianRed,
                    ToolTip = "从 NAS 彻底删除 apps/" + app.Id + "/（本地已下载的文件不动）"
                };
                deleteButton.Click += (s, e) => DeleteApp(captured);

                var row = BuildRow(
                    app.Name,
                    app.Id,
                    FormatSize(app.TotalBytes),
                    app.FileCount > 0 ? app.FileCount.ToString() : "-",
                    LayoutName(app),
                    deleteButton);

                row.ToolTip = "apps/" + app.Id + "/";
                listPanel.Children.Add(row);
            }
        }

        private static string LayoutName(AppEntry app)
        {
            if (app.ChunkCount > 0)
            {
                return "v3 区块";
            }
            if (app.PartCount > 0)
            {
                return "v2 分片";
            }
            return "v1 直传";
        }

        // ---------------------------------------------------------------- 删除

        private void DeleteApp(AppEntry app)
        {
            var confirm = MessageBox.Show(
                "确定要从 NAS 删除「" + app.Name + "」吗？\n\n"
                + "目录：apps/" + app.Id + "/\n"
                + "占用：" + FormatSize(app.TotalBytes) + "\n\n"
                + "远端归档会被彻底删除（含全部区块文件），之后无法再安装。\n"
                + "本地已下载的游戏文件不受影响。\n\n此操作不可撤销。",
                "删除仓库应用",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }

            // 二次校验口令：窗口可能开了很久，口令在别处被换掉也不该放行
            var password = PasswordPrompt.Ask(this, "输入管理口令", "删除「" + app.Name + "」需要管理口令：", false);
            if (password == null)
            {
                return;
            }

            SetStatus("正在校验口令...", false, Brushes.Gray);
            refreshButton.IsEnabled = false;

            Task.Run(() =>
            {
                bool isSet;
                bool ok;
                try
                {
                    ok = service.VerifyAdminPassword(password, out isSet);
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() =>
                    {
                        refreshButton.IsEnabled = true;
                        SetStatus("校验口令失败：" + ex.Message, false, Brushes.IndianRed);
                    });
                    return;
                }

                if (!ok)
                {
                    Dispatcher.Invoke(() =>
                    {
                        refreshButton.IsEnabled = true;
                        SetStatus(
                            isSet ? "口令不正确，删除已取消。" : "仓库还没有设置管理口令，请先点「修改管理口令」设置。",
                            false,
                            Brushes.IndianRed);
                    });
                    return;
                }

                var removed = 0;
                try
                {
                    removed = service.RemoveApp(app.Id);
                }
                catch (Exception ex)
                {
                    VaultLog.Error("删除仓库应用失败：" + app.Id, ex);
                    Dispatcher.Invoke(() =>
                    {
                        refreshButton.IsEnabled = true;
                        SetStatus("删除失败：" + ex.Message, false, Brushes.IndianRed);
                    });
                    return;
                }

                Dispatcher.Invoke(() =>
                {
                    refreshButton.IsEnabled = true;
                    SetStatus(string.Format("已删除「{0}」，清理了 {1} 个远端文件。", app.Name, removed),
                        false, Brushes.SeaGreen);
                    Load();
                });
            });
        }

        // ---------------------------------------------------------------- 改口令

        private void OnChangePassword(object sender, RoutedEventArgs e)
        {
            var isNew = false;

            // 已设置过就先验旧口令；没设置过（或验不过又坚持设置）走「设置新口令」
            var existing = PasswordPrompt.Ask(this, "修改管理口令",
                "先输入当前口令（还没设置过就直接点「确定」跳过）：", false);
            if (existing == null)
            {
                return;
            }

            if (existing.Length == 0)
            {
                isNew = true;
            }
            else
            {
                bool isSet;
                bool ok;
                try
                {
                    ok = service.VerifyAdminPassword(existing, out isSet);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("校验失败：" + ex.Message, "Vault 仓库管理",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                if (!isSet)
                {
                    isNew = true;
                }
                else if (!ok)
                {
                    MessageBox.Show("当前口令不正确。", "Vault 仓库管理",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            var fresh = PasswordPrompt.Ask(this, "设置管理口令",
                isNew ? "设置新的管理口令（留空则取消）：" : "设置新的管理口令：", true);
            if (fresh == null)
            {
                return;
            }

            if (fresh.Length < 4)
            {
                MessageBox.Show("口令至少 4 位。", "Vault 仓库管理",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                service.SetAdminPassword(fresh);
                SetStatus("管理口令已更新（保存在仓库根的 " + VaultAdmin.FileName + "）。", false, Brushes.SeaGreen);
            }
            catch (Exception ex)
            {
                VaultLog.Error("设置管理口令失败", ex);
                MessageBox.Show("设置失败：" + ex.Message, "Vault 仓库管理",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ---------------------------------------------------------------- 小工具

        private void SetStatus(string text, bool unused, Brush color)
        {
            statusText.Text = text ?? string.Empty;
            statusText.Foreground = color;
        }

        /// <summary>一行五列：应用 / Id / 网络占用 / 文件 / 布局，最后一列放操作按钮。</summary>
        private static DockPanel BuildRow(string name, string id, string size, string files,
            string layout, UIElement action)
        {
            var row = new DockPanel { Margin = new Thickness(4, 3, 4, 3) };

            if (action != null)
            {
                DockPanel.SetDock(action, Dock.Right);
                row.Children.Add(action);
            }

            row.Children.Add(Cell(layout, 80, 0.6));
            row.Children.Add(Cell(files, 60, 0.6));
            row.Children.Add(Cell(size, 90, 0.7));
            row.Children.Add(Cell(id, 170, 0.55));
            row.Children.Add(Cell(name, double.NaN, 1.0, true));
            return row;
        }

        private static TextBlock Cell(string text, double width, double opacity, bool bold = false)
        {
            var block = new TextBlock
            {
                Text = text ?? string.Empty,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = opacity,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                Margin = new Thickness(0, 0, 8, 0)
            };

            if (!double.IsNaN(width))
            {
                block.Width = width;
            }

            return block;
        }

        /// <summary>人类可读体积。统一用 1024 进制，跟仓库里的字节数对齐。</summary>
        public static string FormatSize(long bytes)
        {
            if (bytes <= 0)
            {
                return "0 B";
            }

            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            var unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return unit == 0
                ? bytes + " B"
                : value.ToString(value >= 100 ? "0" : "0.0") + " " + units[unit];
        }
    }

    /// <summary>
    /// 极简口令输入框。<c>confirm = true</c> 时要求输入两遍且一致。
    /// 取消返回 null —— 调用方必须把 null 当作「用户放弃」而不是「空口令」。
    /// </summary>
    internal static class PasswordPrompt
    {
        public static string Ask(Window owner, string title, string message, bool confirm)
        {
            var window = new Window
            {
                Title = title,
                Width = 400,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = owner,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false
            };

            var panel = new StackPanel { Margin = new Thickness(16) };

            panel.Children.Add(new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            });

            var first = new PasswordBox { Margin = new Thickness(0, 0, 0, 8) };
            panel.Children.Add(first);

            PasswordBox second = null;
            if (confirm)
            {
                second = new PasswordBox { Margin = new Thickness(0, 0, 0, 8) };
                panel.Children.Add(new TextBlock
                {
                    Text = "再输一遍",
                    Opacity = 0.75,
                    Margin = new Thickness(0, 0, 0, 4)
                });
                panel.Children.Add(second);
            }

            var error = new TextBlock
            {
                Foreground = Brushes.IndianRed,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            };
            panel.Children.Add(error);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };

            var okButton = new Button { Content = "确定", Padding = new Thickness(16, 3, 16, 3), IsDefault = true };
            var cancelButton = new Button
            {
                Content = "取消",
                Padding = new Thickness(16, 3, 16, 3),
                Margin = new Thickness(8, 0, 0, 0),
                IsCancel = true
            };

            buttons.Children.Add(okButton);
            buttons.Children.Add(cancelButton);
            panel.Children.Add(buttons);

            window.Content = panel;
            window.Loaded += (s, e) => first.Focus();

            string result = null;

            okButton.Click += (s, e) =>
            {
                var value = first.Password ?? string.Empty;

                if (confirm)
                {
                    if (value.Length == 0)
                    {
                        error.Text = "口令不能为空。";
                        return;
                    }

                    if (value != (second.Password ?? string.Empty))
                    {
                        error.Text = "两次输入不一致。";
                        second.Clear();
                        second.Focus();
                        return;
                    }
                }

                result = value;
                window.DialogResult = true;
            };

            window.ShowDialog();
            return result;
        }
    }
}
