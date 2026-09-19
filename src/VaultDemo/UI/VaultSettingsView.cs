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

        /// <summary>建连 / 等响应头的上限（秒）。</summary>
        public int TimeoutSeconds
        {
            get { return editing.TimeoutSeconds; }
            set
            {
                editing.TimeoutSeconds = value <= 0 ? 15 : value;
                OnPropertyChanged("TimeoutSeconds");
            }
        }

        /// <summary>
        /// 停滞超时（秒）：连续这么久没有字节流动才断开。
        /// 这是「速度从 20MB/s 掉到 0」的真正判据，不要和建连超时混为一谈。
        /// </summary>
        public int StallTimeoutSeconds
        {
            get { return editing.StallTimeoutSeconds; }
            set
            {
                editing.StallTimeoutSeconds = value <= 0 ? 30 : (value > 600 ? 600 : value);
                OnPropertyChanged("StallTimeoutSeconds");
            }
        }

        /// <summary>body 发完后等服务端落盘回包的上限（秒）。大区块必须给足。</summary>
        public int ResponseTimeoutSeconds
        {
            get { return editing.ResponseTimeoutSeconds; }
            set
            {
                editing.ResponseTimeoutSeconds = value <= 0 ? 180 : (value > 3600 ? 3600 : value);
                OnPropertyChanged("ResponseTimeoutSeconds");
            }
        }

        /// <summary>区块大小（MB）。32 是实测最优：单块传完快、重试代价低。</summary>
        public int ChunkSizeMB
        {
            get { return editing.ChunkSizeMB; }
            set
            {
                editing.ChunkSizeMB = value < 1 ? 32 : (value > 512 ? 512 : value);
                OnPropertyChanged("ChunkSizeMB");
            }
        }

        /// <summary>上传并发。0 表示沿用下载并发。</summary>
        public int UploadConcurrency
        {
            get { return editing.UploadConcurrency; }
            set
            {
                editing.UploadConcurrency = value < 0 ? 0 : (value > 16 ? 16 : value);
                OnPropertyChanged("UploadConcurrency");
            }
        }

        /// <summary>归档时是否压缩。游戏资源多为已压缩格式，默认关闭。</summary>
        public bool CompressOnArchive
        {
            get { return editing.CompressOnArchive; }
            set { editing.CompressOnArchive = value; OnPropertyChanged("CompressOnArchive"); }
        }

        /// <summary>下载时是否边下边解（下载区块与解包并行）。</summary>
        public bool PipelineExtract
        {
            get { return editing.PipelineExtract; }
            set { editing.PipelineExtract = value; OnPropertyChanged("PipelineExtract"); }
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
                errors.Add("建连超时必须大于 0 秒。");
            }

            if (editing.StallTimeoutSeconds <= 0)
            {
                errors.Add("停滞超时必须大于 0 秒。");
            }

            if (editing.ResponseTimeoutSeconds <= 0)
            {
                errors.Add("应答超时必须大于 0 秒。");
            }

            if (editing.ChunkSizeMB < 1 || editing.ChunkSizeMB > 512)
            {
                errors.Add("区块大小需要在 1~512 MB 之间（推荐 32）。");
            }

            if (editing.UploadConcurrency < 0 || editing.UploadConcurrency > 16)
            {
                errors.Add("上传并发需要在 1~16 之间（填 0 表示沿用下载并发）。");
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
            OnPropertyChanged("StallTimeoutSeconds");
            OnPropertyChanged("ResponseTimeoutSeconds");
            OnPropertyChanged("ChunkSizeMB");
            OnPropertyChanged("UploadConcurrency");
            OnPropertyChanged("CompressOnArchive");
            OnPropertyChanged("PipelineExtract");
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

            panel.Children.Add(Section("传输超时"));
            panel.Children.Add(NumberField("建连超时（秒）", "TimeoutSeconds",
                "建立连接并等到响应头的上限。内网 15 秒足够。"));
            panel.Children.Add(NumberField("停滞超时（秒）", "StallTimeoutSeconds",
                "连续这么久没有任何字节流动才断开重试。**这才是「速度从 20MB/s 掉到 0」对应的判据**；\n"
                + "它管的是「每次读写等多久」，不是「整个传输总共多久」，所以大文件也能安全通过。"));
            panel.Children.Add(NumberField("应答超时（秒）", "ResponseTimeoutSeconds",
                "body 发完之后等服务端合并/落盘回包的上限。NAS 收到大区块还要落盘，这里要给足（默认 180）。"));

            panel.Children.Add(Section("切块与并发"));
            panel.Children.Add(NumberField("区块大小（MB）", "ChunkSizeMB",
                "一个文件会被切成若干这样大小的区块，**可以跨块**。\n"
                + "256MB 时 Dead Cells 的 res.pak（1.93GB）会变成一个巨型分片，单个 PUT 上百秒必然超时；\n"
                + "32MB 是实测跑满带宽的档位，单块传得快、失败了重传代价也小。"));
            panel.Children.Add(NumberField("并发下载路数（1~16）", "Concurrency",
                "单条 HTTPS 连接在 .NET Framework 下解密上限约 27 MB/s，所以千兆内网要开多路才能跑满。\n"
                + "实测：3 路 72、4 路 93、6 路 96、8 路 86 MB/s —— 建议 6。走明文 HTTP 时 1~2 就够。\n"
                + "注意峰值临时磁盘占用 ≈（并发数 + 2）× 区块大小。"));
            panel.Children.Add(NumberField("并发上传路数（0 = 沿用下载）", "UploadConcurrency",
                "瓶颈通常在 NAS 写入侧：实测下载 88 MB/s 而上传只有 20~40 MB/s，并发越高越容易停顿。\n"
                + "所以上传默认比下载保守一档（4 路），填 0 表示跟下载并发一致。"));

            panel.Children.Add(Section("行为开关"));

            var resumeBox = new CheckBox
            {
                Content = "启用断点续传（中断后重试只补没传完的区块）",
                Margin = new Thickness(0, 0, 0, 6)
            };
            resumeBox.SetBinding(CheckBox.IsCheckedProperty, Bind("ResumePartial"));
            panel.Children.Add(resumeBox);

            var pipelineBox = new CheckBox
            {
                Content = "下载时边下边解（区块下完立刻解包并删掉临时文件）",
                Margin = new Thickness(0, 0, 0, 6)
            };
            pipelineBox.SetBinding(CheckBox.IsCheckedProperty, Bind("PipelineExtract"));
            panel.Children.Add(pipelineBox);

            var compressBox = new CheckBox
            {
                Content = "归档时压缩内容（游戏资源多为已压缩格式，一般省不下空间）",
                Margin = new Thickness(0, 0, 0, 6)
            };
            compressBox.SetBinding(CheckBox.IsCheckedProperty, Bind("CompressOnArchive"));
            panel.Children.Add(compressBox);

            var proxyBox = new CheckBox
            {
                Content = "走系统代理（访问内网 NAS 时不要勾选）",
                Margin = new Thickness(0, 0, 0, 10)
            };
            proxyBox.SetBinding(CheckBox.IsCheckedProperty, Bind("UseSystemProxy"));
            panel.Children.Add(proxyBox);

            panel.Children.Add(NumberField("失败重试次数（每个区块）", "MaxRetries",
                "只对可重试的错误生效：超时 / 连接中断 / 429 / 5xx。\n"
                + "4xx（比如 401 密码错、403 无权限）不重试 —— 重试也不会变对，只会白等。"));

            panel.Children.Add(Section("仓库管理口令"));
            var adminHint = new TextBlock
            {
                Text = "删除仓库应用前需要输入管理口令。口令单独设置、保存在仓库根的 "
                     + "vault-admin.json，本机只缓存一份派生值。\n"
                     + "入口：游戏列表右键 → Vault → 管理仓库应用（删除，需管理口令）。\n"
                     + "提醒：它是防误触闸门，不是权限系统 —— 真正拦住外人的是 WebDAV 账号。",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.6,
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 10)
            };
            panel.Children.Add(adminHint);

            testButton = new Button
            {
                Content = "测试连接",
                Padding = new Thickness(14, 4, 14, 4),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 4, 0, 0)
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

            // 设置项越来越多，宿主窗口未必给滚动条，这里自己兜一层
            Content = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = panel
            };
        }

        private static TextBlock Section(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 6, 0, 8),
                Opacity = 0.9
            };
        }

        /// <summary>数字输入 + 灰色说明文字的一体化字段。</summary>
        private static StackPanel NumberField(string label, string path, string hint)
        {
            var box = new TextBox { Width = 100, HorizontalAlignment = HorizontalAlignment.Left };
            box.SetBinding(TextBox.TextProperty, Bind(path));

            var field = Field(label, box);
            if (!string.IsNullOrEmpty(hint))
            {
                field.Children.Add(new TextBlock
                {
                    Text = hint,
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.6,
                    FontSize = 11,
                    Margin = new Thickness(0, 4, 0, 0)
                });
            }

            return field;
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
            var stall = vm.StallTimeoutSeconds;
            var response = vm.ResponseTimeoutSeconds;
            var useProxy = vm.UseSystemProxy;

            System.Threading.Tasks.Task.Run(() =>
            {
                string message;
                bool ok;
                try
                {
                    var client = new WebDavClient(probe, user, pass,
                        WebDavTimeouts.FromSeconds(timeout, stall, response), useProxy);
                    var count = client.TestConnection();
                    var hasIndex = client.Exists("index.json");
                    ok = true;
                    message = string.Format("连接成功。根目录可见 {0} 个条目，index.json {1}。",
                        count, hasIndex ? "存在" : "不存在（归档后会自动创建）");
                }
                catch (Exception ex)
                {
                    ok = false;
                    // 401 之类的错误光看状态码分不清「密码错」还是「服务端认证坏了」，
                    // 这里给出可照做的排查步骤
                    message = "连接失败：\n\n" + WebDavDiagnostics.Describe(ex, probe, user);
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
