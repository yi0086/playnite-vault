using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Playnite.SDK;
using VaultDemo.Net;
using VaultDemo.Services;

namespace VaultDemo.UI
{
    public class ObservableObjectBase : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged(string name)
        {
            var handler = PropertyChanged;
            if (handler != null)
            {
                handler(this, new PropertyChangedEventArgs(name));
            }
        }
    }

    /// <summary>
    /// 设置界面的 ViewModel，同时实现 ISettings 交给 Playnite 管理编辑/提交/取消的生命周期。
    /// BeginEdit 打快照，EndEdit 落盘，CancelEdit 回滚。
    /// </summary>
    public class VaultSettingsViewModel : ObservableObjectBase, ISettings
    {
        private readonly VaultService service;
        private VaultSettings editing;
        private VaultSettings snapshot;

        public VaultSettingsViewModel(VaultService service)
        {
            this.service = service;
            this.editing = service.Settings.Clone();
        }

        public string WebDavUrl
        {
            get { return editing.WebDavUrl; }
            set { editing.WebDavUrl = value; OnPropertyChanged("WebDavUrl"); }
        }

        public string Username
        {
            get { return editing.Username; }
            set { editing.Username = value; OnPropertyChanged("Username"); }
        }

        public string Password
        {
            get { return editing.Password; }
            set { editing.Password = value; OnPropertyChanged("Password"); }
        }

        public string LocalRoot
        {
            get { return editing.LocalRoot; }
            set { editing.LocalRoot = value; OnPropertyChanged("LocalRoot"); }
        }

        public int TimeoutSeconds
        {
            get { return editing.TimeoutSeconds; }
            set
            {
                editing.TimeoutSeconds = value <= 0 ? 60 : value;
                OnPropertyChanged("TimeoutSeconds");
            }
        }

        public bool UseSystemProxy
        {
            get { return editing.UseSystemProxy; }
            set { editing.UseSystemProxy = value; OnPropertyChanged("UseSystemProxy"); }
        }

        public int MaxRetries
        {
            get { return editing.MaxRetries; }
            set
            {
                editing.MaxRetries = value <= 0 ? 1 : value;
                OnPropertyChanged("MaxRetries");
            }
        }

        public bool ResumePartial
        {
            get { return editing.ResumePartial; }
            set { editing.ResumePartial = value; OnPropertyChanged("ResumePartial"); }
        }

        /// <summary>并发传输的分片数。见 VaultSettings.Concurrency 的说明。</summary>
        public int Concurrency
        {
            get { return editing.Concurrency; }
            set
            {
                editing.Concurrency = value <= 0 ? 1 : (value > 16 ? 16 : value);
                OnPropertyChanged("Concurrency");
            }
        }

        public void BeginEdit()
        {
            editing = service.Settings.Clone();
            snapshot = service.Settings.Clone();
            RaiseAll();
        }

        public void EndEdit()
        {
            service.SaveSettings(editing);
        }

        public void CancelEdit()
        {
            editing = snapshot != null ? snapshot.Clone() : service.Settings.Clone();
            RaiseAll();
        }

        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();

            var url = editing.WebDavUrl == null ? string.Empty : editing.WebDavUrl.Trim();
            if (url.Length > 0
                && !url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("WebDAV 地址需要以 http:// 或 https:// 开头。");
            }

            if (editing.TimeoutSeconds <= 0)
            {
                errors.Add("网络超时必须大于 0 秒。");
            }

            if (editing.MaxRetries <= 0)
            {
                errors.Add("失败重试次数至少为 1。");
            }

            if (editing.Concurrency <= 0 || editing.Concurrency > 16)
            {
                errors.Add("并发路数需要在 1~16 之间（HTTPS 建议 6）。");
            }

            return errors.Count == 0;
        }

        private void RaiseAll()
        {
            OnPropertyChanged("WebDavUrl");
            OnPropertyChanged("Username");
            OnPropertyChanged("Password");
            OnPropertyChanged("LocalRoot");
            OnPropertyChanged("TimeoutSeconds");
            OnPropertyChanged("UseSystemProxy");
            OnPropertyChanged("MaxRetries");
            OnPropertyChanged("ResumePartial");
            OnPropertyChanged("Concurrency");
        }
    }

    /// <summary>
    /// 纯代码构建的设置界面，避免 XAML 编译带来的额外约束。
    /// </summary>
    public class VaultSettingsView : UserControl
    {
        private readonly VaultSettingsViewModel vm;
        private readonly VaultService service;
        private PasswordBox passBox;
        private TextBlock statusText;
        private Button testButton;

        public VaultSettingsView(VaultSettingsViewModel vm, VaultService service)
        {
            this.vm = vm;
            this.service = service;
            Build();
        }

        private void Build()
        {
            var panel = new StackPanel { Margin = new Thickness(12) };

            panel.Children.Add(new TextBlock
            {
                Text = "Vault Demo —— 连接 NAS 上的 WebDAV 仓库",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 12)
            });

            var urlBox = new TextBox { MinWidth = 380 };
            urlBox.SetBinding(TextBox.TextProperty, Bind("WebDavUrl"));
            panel.Children.Add(Field("WebDAV 地址（以 / 结尾，例如 http://192.168.1.10:5005/vault/）", urlBox));

            var userBox = new TextBox();
            userBox.SetBinding(TextBox.TextProperty, Bind("Username"));
            panel.Children.Add(Field("用户名", userBox));

            passBox = new PasswordBox();
            passBox.Password = vm.Password ?? string.Empty;
            passBox.PasswordChanged += (s, e) => { vm.Password = passBox.Password; };
            vm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == "Password" && passBox.Password != (vm.Password ?? string.Empty))
                {
                    passBox.Password = vm.Password ?? string.Empty;
                }
            };
            panel.Children.Add(Field("密码", passBox));

            var rootRow = new DockPanel();
            var browseButton = new Button { Content = "浏览...", Padding = new Thickness(10, 2, 10, 2) };
            var rootBox = new TextBox();
            rootBox.SetBinding(TextBox.TextProperty, Bind("LocalRoot"));
            DockPanel.SetDock(browseButton, Dock.Right);
            browseButton.Click += (s, e) =>
            {
                var picked = VaultPlugin.Instance.PlayniteApi.Dialogs.SelectFolder();
                if (!string.IsNullOrEmpty(picked))
                {
                    vm.LocalRoot = picked;
                }
            };
            rootRow.Children.Add(browseButton);
            rootRow.Children.Add(rootBox);
            panel.Children.Add(Field("本地安装根目录", rootRow));

            var timeoutBox = new TextBox { Width = 100, HorizontalAlignment = HorizontalAlignment.Left };
            timeoutBox.SetBinding(TextBox.TextProperty, Bind("TimeoutSeconds"));
            panel.Children.Add(Field("网络超时（秒）", timeoutBox));

            var proxyBox = new CheckBox
            {
                Content = "走系统代理（访问内网 NAS 时不要勾选）",
                Margin = new Thickness(0, 0, 0, 10)
            };
            proxyBox.SetBinding(CheckBox.IsCheckedProperty, Bind("UseSystemProxy"));
            panel.Children.Add(proxyBox);

            var retryBox = new TextBox { Width = 100, HorizontalAlignment = HorizontalAlignment.Left };
            retryBox.SetBinding(TextBox.TextProperty, Bind("MaxRetries"));
            panel.Children.Add(Field("失败重试次数（每个文件）", retryBox));

            var resumeBox = new CheckBox
            {
                Content = "启用断点续传（中断后重试只传剩余部分）",
                Margin = new Thickness(0, 0, 0, 10)
            };
            resumeBox.SetBinding(CheckBox.IsCheckedProperty, Bind("ResumePartial"));
            panel.Children.Add(resumeBox);

            var connBox = new TextBox { Width = 100, HorizontalAlignment = HorizontalAlignment.Left };
            connBox.SetBinding(TextBox.TextProperty, Bind("Concurrency"));
            var connField = Field("并发下载路数（1~16）", connBox);
            connField.Children.Add(new TextBlock
            {
                Text = "单条 HTTPS 连接在 .NET Framework 下解密上限约 27 MB/s，所以千兆内网要开多路才能跑满。\n"
                     + "实测：3 路 72、4 路 93、6 路 96、8 路 86 MB/s —— 建议 6。走明文 HTTP 时 1~2 就够。\n"
                     + "注意峰值临时磁盘占用 ≈（并发数 + 2）× 分片大小。",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.6,
                FontSize = 11,
                Margin = new Thickness(0, 4, 0, 0)
            });
            panel.Children.Add(connField);

            testButton = new Button
            {
                Content = "测试连接",
                Padding = new Thickness(14, 4, 14, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 8, 0, 0)
            };
            testButton.Click += OnTestConnection;
            panel.Children.Add(testButton);

            statusText = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
                Foreground = Brushes.Gray
            };
            panel.Children.Add(statusText);

            Content = panel;
        }

        private static Binding Bind(string path)
        {
            return new Binding(path)
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            };
        }

        private static StackPanel Field(string label, UIElement control)
        {
            var box = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            box.Children.Add(new TextBlock
            {
                Text = label,
                Margin = new Thickness(0, 0, 0, 4),
                Opacity = 0.8
            });
            box.Children.Add(control);
            return box;
        }

        private void OnTestConnection(object sender, RoutedEventArgs e)
        {
            statusText.Foreground = Brushes.Gray;
            statusText.Text = "正在测试...";
            testButton.IsEnabled = false;

            var probe = vm.WebDavUrl;
            var user = vm.Username;
            var pass = vm.Password;
            var timeout = vm.TimeoutSeconds;
            var useProxy = vm.UseSystemProxy;

            System.Threading.Tasks.Task.Run(() =>
            {
                string message;
                bool ok;
                try
                {
                    var client = new WebDavClient(probe, user, pass, timeout, useProxy);
                    var count = client.TestConnection();
                    var hasIndex = client.Exists("index.json");
                    ok = true;
                    message = string.Format("连接成功。根目录可见 {0} 个条目，index.json {1}。",
                        count, hasIndex ? "存在" : "不存在（归档后会自动创建）");
                }
                catch (Exception ex)
                {
                    ok = false;
                    message = "连接失败：" + ex.Message;
                    VaultLog.Error("测试连接失败", ex);
                }

                Dispatcher.Invoke(() =>
                {
                    statusText.Text = message;
                    statusText.Foreground = ok ? Brushes.SeaGreen : Brushes.IndianRed;
                    testButton.IsEnabled = true;
                });
            });
        }
    }
}
