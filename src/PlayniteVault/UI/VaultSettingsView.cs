using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Playnite.SDK;
using PlayniteVault.Models;
using PlayniteVault.Net;
using PlayniteVault.Services;

namespace PlayniteVault.UI
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

        // ---------- 自动更新 ----------

        /// <summary>启动时自动检查插件更新（GitHub / Gitee）。</summary>
        public bool AutoUpdateEnabled
        {
            get { return editing.AutoUpdateEnabled; }
            set { editing.AutoUpdateEnabled = value; OnPropertyChanged("AutoUpdateEnabled"); }
        }

        /// <summary>更新前先问一声。关掉 = 下载完直接重启应用。</summary>
        public bool AutoUpdatePrompt
        {
            get { return editing.AutoUpdatePrompt; }
            set { editing.AutoUpdatePrompt = value; OnPropertyChanged("AutoUpdatePrompt"); }
        }

        /// <summary>0=自动，1=仅 GitHub，2=仅 Gitee。ComboBox 用下标绑。</summary>
        public int UpdateMirrorIndex
        {
            get
            {
                if (string.Equals(editing.UpdateMirror, "github", StringComparison.OrdinalIgnoreCase)) return 1;
                if (string.Equals(editing.UpdateMirror, "gitee", StringComparison.OrdinalIgnoreCase)) return 2;
                return 0;
            }
            set
            {
                editing.UpdateMirror = value == 1 ? "github" : (value == 2 ? "gitee" : "auto");
                OnPropertyChanged("UpdateMirrorIndex");
            }
        }

        /// <summary>被「跳过」的版本号，只读展示。</summary>
        public string SkippedVersion
        {
            get { return editing.SkippedVersion ?? string.Empty; }
            set { editing.SkippedVersion = value ?? string.Empty; OnPropertyChanged("SkippedVersion"); }
        }

        // ---------- 自动刷新远端库 ----------

        /// <summary>定时把远端索引同步进 Playnite 库。</summary>
        public bool AutoRefreshEnabled
        {
            get { return editing.AutoRefreshEnabled; }
            set { editing.AutoRefreshEnabled = value; OnPropertyChanged("AutoRefreshEnabled"); }
        }

        /// <summary>自动刷新间隔（分钟），5 ~ 1440。</summary>
        public int AutoRefreshMinutes
        {
            get { return editing.AutoRefreshMinutes; }
            set
            {
                editing.AutoRefreshMinutes = LibraryAutoRefresh.Clamp(value);
                OnPropertyChanged("AutoRefreshMinutes");
            }
        }

        /// <summary>启动后先刷新一次。</summary>
        public bool AutoRefreshOnStartup
        {
            get { return editing.AutoRefreshOnStartup; }
            set { editing.AutoRefreshOnStartup = value; OnPropertyChanged("AutoRefreshOnStartup"); }
        }

        // ---------- 云存档 ----------

        /// <summary>云存档总开关。</summary>
        public bool SaveSyncEnabled
        {
            get { return editing.SaveSyncEnabled; }
            set { editing.SaveSyncEnabled = value; OnPropertyChanged("SaveSyncEnabled"); }
        }

        /// <summary>
        /// 触发方式的界面索引。选项顺序就是 <see cref="SaveTriggerMode"/> 的枚举值顺序，
        /// 所以下标可以直接当枚举用 —— 这样「界面上第几个」与「存进去的值」
        /// 只有一个对应关系，不会出现两边各写一份而默默错位的情况。
        /// </summary>
        public int SaveTriggerIndex
        {
            get
            {
                var value = (int)editing.SaveTrigger;
                return value < 0 || value > 2 ? 0 : value;
            }
            set
            {
                var clamped = value < 0 ? 0 : (value > 2 ? 2 : value);
                editing.SaveTrigger = (SaveTriggerMode)clamped;
                OnPropertyChanged("SaveTriggerIndex");
            }
        }

        /// <summary>每个分支最多留几份；0 = 无限。</summary>
        public int SaveKeepPerBranch
        {
            get { return editing.SaveKeepPerBranch; }
            set
            {
                editing.SaveKeepPerBranch = value < 0 ? 0 : value;
                OnPropertyChanged("SaveKeepPerBranch");
            }
        }

        public string SaveDefaultBranch
        {
            get { return editing.SaveDefaultBranch ?? "main"; }
            set
            {
                var name = (value ?? string.Empty).Trim();
                editing.SaveDefaultBranch = name.Length == 0 ? "main" : name;
                OnPropertyChanged("SaveDefaultBranch");
            }
        }

        public bool SaveBackupBeforeRestore
        {
            get { return editing.SaveBackupBeforeRestore; }
            set { editing.SaveBackupBeforeRestore = value; OnPropertyChanged("SaveBackupBeforeRestore"); }
        }

        public int SaveKeepLocalBackups
        {
            get { return editing.SaveKeepLocalBackups; }
            set
            {
                editing.SaveKeepLocalBackups = value < 0 ? 0 : value;
                OnPropertyChanged("SaveKeepLocalBackups");
            }
        }

        public bool SaveSessionSniff
        {
            get { return editing.SaveSessionSniff; }
            set { editing.SaveSessionSniff = value; OnPropertyChanged("SaveSessionSniff"); }
        }

        /// <summary>设置落盘之后触发，插件据此重新定时 / 重新校验。</summary>
        public event Action SettingsSaved;

        // ---------- 界面（v1.8.0） ----------

        /// <summary>
        /// 明暗模式下拉框的下标：0 = 跟随 Playnite，1 = 浅色，2 = 深色。
        /// 和 <see cref="SaveTriggerIndex"/> 一样，界面顺序就是唯一的对应关系，
        /// 不让「界面上第几个」和「存进去的字符串」各写一份。
        /// </summary>
        public int UiThemeIndex
        {
            get
            {
                switch (VaultSettings.NormalizeUiTheme(editing.UiTheme))
                {
                    case VaultSettings.UiThemeLight:
                        return 1;
                    case VaultSettings.UiThemeDark:
                        return 2;
                    default:
                        return 0;
                }
            }
            set
            {
                editing.UiTheme = value == 1 ? VaultSettings.UiThemeLight
                                : value == 2 ? VaultSettings.UiThemeDark
                                : VaultSettings.UiThemeAuto;
                OnPropertyChanged("UiThemeIndex");
                OnPropertyChanged("UiThemeHint");
            }
        }

        /// <summary>
        /// 顺带把「自动档实际判成了什么」写出来。用户遇到「自动适配失效」时，
        /// 最需要知道的就是插件到底以为现在是明还是暗 —— 否则他只能靠猜。
        /// </summary>
        public string UiThemeHint
        {
            get
            {
                var mode = VaultPalette.ParseMode(editing.UiTheme);
                if (mode == VaultUiThemeMode.Light)
                {
                    return "固定浅色（不跟随 Playnite）。";
                }

                if (mode == VaultUiThemeMode.Dark)
                {
                    return "固定深色（不跟随 Playnite）。";
                }

                var detected = VaultPalette.Resolve(VaultUiThemeMode.Auto).IsDark ? "深色" : "浅色";
                return "跟随 Playnite —— 按主窗口底色自动判断，现在判成「" + detected + "」。"
                       + "要是实际观感正好相反（有些主题把窗口底色做了特殊处理），就在上面手动选一档。";
            }
        }

        public void BeginEdit()
        {
            editing = service.Settings.Clone();
            snapshot = service.Settings.Clone();
            RaiseAll();
        }

        /// <summary>
        /// 落盘。落完之后把**落下去的那份**推回界面。
        ///
        /// <para>为什么要 RaiseAll：各字段的 setter 会做范围钳制（并发路数只能 1~16、
        /// 区块大小 1~512 …）。不推回来的话，用户看到的是自己打的字，
        /// 而真正生效的是被钳过的值 —— 那看起来就是「保存没生效」。</para>
        /// </summary>
        public void EndEdit()
        {
            service.SaveSettings(editing);
            RaiseAll();

            var handler = SettingsSaved;
            if (handler != null)
            {
                handler();
            }
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

            if (editing.AutoRefreshEnabled
                && (editing.AutoRefreshMinutes < LibraryAutoRefresh.MinMinutes
                    || editing.AutoRefreshMinutes > LibraryAutoRefresh.MaxMinutes))
            {
                errors.Add("自动刷新间隔需要在 " + LibraryAutoRefresh.MinMinutes + "~"
                           + LibraryAutoRefresh.MaxMinutes + " 分钟之间。");
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
            OnPropertyChanged("AutoUpdateEnabled");
            OnPropertyChanged("AutoUpdatePrompt");
            OnPropertyChanged("UpdateMirrorIndex");
            OnPropertyChanged("SkippedVersion");
            OnPropertyChanged("AutoRefreshEnabled");
            OnPropertyChanged("AutoRefreshMinutes");
            OnPropertyChanged("AutoRefreshOnStartup");
            OnPropertyChanged("SaveSyncEnabled");
            OnPropertyChanged("SaveTriggerIndex");
            OnPropertyChanged("SaveKeepPerBranch");
            OnPropertyChanged("SaveDefaultBranch");
            OnPropertyChanged("SaveBackupBeforeRestore");
            OnPropertyChanged("SaveKeepLocalBackups");
            OnPropertyChanged("SaveSessionSniff");
            OnPropertyChanged("UiThemeIndex");
            OnPropertyChanged("UiThemeHint");
        }
    }

    /// <summary>
    /// 纯代码构建的设置界面，避免 XAML 编译带来的额外约束。
    ///
    /// <para><b>v1.8 的三处改动</b>：</para>
    /// <list type="number">
    /// <item><b>配色不再靠 Opacity 蒙</b>。以前说明文字一律用 <c>Opacity = 0.6</c>，
    /// 那是拿「当前前景色」去兑透明度 —— 在浅色主题下前景是黑的，兑完变灰还行；
    /// 可一旦控件不在主题的样式作用域里（比如被嵌进侧边栏页），前景会退回系统默认色，
    /// 于是深色主题里就出现「灰字压黑底」近乎看不见。现在所有文字走
    /// <see cref="VaultPalette"/> 的显式令牌。</item>
    /// <item><b>长说明收进小 ⓘ 图标</b>。原来每个选项下面都挂一段两三行的解释，
    /// 一屏只能放三四个设置项。现在解释挂在标题右侧的小圆点上，悬停才出来。</item>
    /// <item><b>输入框自带水印，而不是在标签右边写「当前 xxx」</b>。右上角那行小字既占位置
    /// 又容易被当成两个字段；现在改成：框里有值就显示值（那就是当前值），
    /// 框被清空时水印把原值顶上来，一个字符也不会丢。</item>
    /// <item><b>按「用的时候在找什么」分模块</b>：连接 NAS / 传输 / 自动化 / 云存档。
    /// 四个模块体量刻意不一样大，视觉层级差让入口级设置与调优项一眼分得开。</item>
    /// </list>
    /// </summary>
    public class VaultSettingsView : UserControl
    {
        private readonly VaultSettingsViewModel vm;
        private readonly VaultService service;
        private readonly VaultPalette p;

        /// <summary>
        /// 这一份视图是**嵌在侧边栏页里**的吗？
        ///
        /// <para>嵌进去时不能自带 <see cref="ScrollViewer"/>：外层（侧边栏页的 bodyScroll）
        /// 已经有一个了。两层套着时，滚轮事件被里层吃掉、而里层内容不溢出所以不滚动，
        /// 表现出来的就是「滚轮没反应，只能拖旁边的滚动条」。</para>
        /// </summary>
        private readonly bool embedded;

        private PasswordBox passBox;
        private TextBlock statusText;
        private Button testButton;
        private TextBlock updateStatusText;
        private TextBlock refreshStatusText;
        private TextBlock skippedText;

        public VaultSettingsView(VaultSettingsViewModel vm, VaultService service,
            bool embedded = false)
        {
            this.vm = vm;
            this.service = service;
            this.embedded = embedded;
            this.p = VaultPalette.Resolve(VaultPalette.ParseMode(service.Settings.UiTheme));

            Build();

            // 状态区在打开设置页时才读一次（没必要挂计时器）
            Loaded += (s, e) => RefreshStatusLines();
        }

        // ================================================================ 配色

        /// <summary>已保存的值——输入框水印用。</summary>
        private static string DescribeSaved(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "(空)" : value;
        }

        /// <summary>把已保存的触发方式说成一句人话，给输入框水印用。</summary>
        private static string DescribeSaveTrigger(SaveTriggerMode mode)
        {
            switch (mode)
            {
                case SaveTriggerMode.UploadOnStop:
                    return "退出后自动上传";
                case SaveTriggerMode.UploadOnStopAskOnStart:
                    return "退出上传 + 启动前询问";
                default:
                    return "只在我点的时候";
            }
        }

        // ================================================================ 小部件

        /// <summary>把一段说明做成悬停浮窗。每次调用都新建 —— 同一个 UIElement 不能挂两处。</summary>
        private Border Tooltip(string text)
        {
            return new Border
            {
                Background = p.Surface,
                BorderBrush = p.Border,
                BorderThickness = new Thickness(2),
                Padding = new Thickness(12, 10, 12, 10),
                MaxWidth = 430,
                Child = new TextBlock
                {
                    Text = text,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 11.5,
                    Foreground = p.Text,
                    LineHeight = 18
                }
            };
        }

        /// <summary>
        /// 小 ⓘ 图标：一个圆圈里一个 i。
        /// 自己画而不是用字体里的 U+24D8，是因为那个码位在部分中文字体里缺字
        /// （会显示成方框），而这里只需要一个圆和一根竖线。
        /// </summary>
        private Border InfoIcon(string help)
        {
            var icon = new Border
            {
                Width = 15,
                Height = 15,
                CornerRadius = new CornerRadius(7.5),
                BorderThickness = new Thickness(1.2),
                BorderBrush = p.TextMuted,
                Background = Brushes.Transparent,
                Cursor = Cursors.Help,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
                Child = new TextBlock
                {
                    Text = "i",
                    FontSize = 9.5,
                    FontWeight = FontWeights.Bold,
                    Foreground = p.TextMuted,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };

            icon.ToolTip = Tooltip(help);
            ToolTipService.SetInitialShowDelay(icon, 150);
            ToolTipService.SetShowDuration(icon, 60000);
            return icon;
        }

        /// <summary>小节标题（+ 可选 ⓘ）。</summary>
        private UIElement Section(string text, string help = null)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 16, 0, 10)
            };
            row.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 13.5,
                FontWeight = FontWeights.Bold,
                Foreground = p.Text,
                VerticalAlignment = VerticalAlignment.Center
            });

            if (!string.IsNullOrEmpty(help))
            {
                row.Children.Add(InfoIcon(help));
            }

            return row;
        }

        /// <summary>
        /// 字段：标签行（标签 + 可选 ⓘ）+ 控件。
        ///
        /// <para><paramref name="saved"/> 是**已落盘**的值。它不再占一行小字挂在右上角，
        /// 而是变成输入框里的**水印**：框空着的时候把原值顶上来，一输入就消失。
        /// 输入框里本来就是当前值，再在角上写一遍只是噪音（还会把标签行挤窄）。</para>
        /// </summary>
        private StackPanel Field(string label, UIElement control, string help = null, string saved = null)
        {
            var box = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };

            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = p.Text,
                VerticalAlignment = VerticalAlignment.Center
            });
            if (!string.IsNullOrEmpty(help))
            {
                header.Children.Add(InfoIcon(help));
            }

            box.Children.Add(header);
            box.Children.Add(WithWatermark(control, saved));
            return box;
        }

        /// <summary>
        /// 给输入框加水印：**框里没内容时**显示它原来（已保存）的值，一有输入就藏起来。
        ///
        /// <para>为什么不直接把提示写进 <c>Text</c>：那样绑定就分不清「用户想看这个名字」
        /// 与「用户什么都没输入」，保存时会把提示语当成真值写进 settings.json。</para>
        ///
        /// <para>不是输入框的（下拉框、带浏览按钮的目录行）没有水印可加 ——
        /// 当前值本来就直接显示在控件上，这里只把原来的小字改为挂到提示里。</para>
        /// </summary>
        private UIElement WithWatermark(UIElement control, string saved)
        {
            if (control == null || string.IsNullOrEmpty(saved))
            {
                return control;
            }

            var textBox = control as TextBox;
            var passwordBox = control as PasswordBox;

            if (textBox == null && passwordBox == null)
            {
                // ToolTip 定义在 FrameworkElement 上，UIElement 没有 → 先降一档再挂
                var fe = control as FrameworkElement;
                if (fe != null && fe.ToolTip == null)
                {
                    fe.ToolTip = "当前：" + saved;
                }

                return control;
            }

            var host = new Grid();

            // 不吃点击，否则鼠标落在水印上就选不中那个输入框了
            var hint = new TextBlock
            {
                Text = saved,
                FontSize = 12,
                Foreground = p.TextMuted,
                Margin = new Thickness(7, 0, 7, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            host.Children.Add(control);
            host.Children.Add(hint);

            Action sync = () =>
            {
                var empty = textBox != null
                    ? string.IsNullOrEmpty(textBox.Text)
                    : string.IsNullOrEmpty(passwordBox.Password);
                hint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            };

            if (textBox != null)
            {
                textBox.TextChanged += (s, e) => sync();
            }
            else
            {
                passwordBox.PasswordChanged += (s, e) => sync();
            }

            // 先按「现在就空」摆一次：绑定是在加载时把源值写进控件的，
            // 那一刻会触发一次 TextChanged，水印会自己收起来。
            sync();
            return host;
        }

        /// <summary>
        /// 数字输入 + 可选 ⓘ。
        /// 空框时把当前生效值当水印顶上来，用户清空重填时不会忘记原来是多少。
        /// </summary>
        private StackPanel NumberField(string label, string path, string help, string saved)
        {
            var box = new TextBox { Width = 96, HorizontalAlignment = HorizontalAlignment.Left };
            box.SetBinding(TextBox.TextProperty, Bind(path));
            return Field(label, box, help, saved);
        }

        private static Binding Bind(string path)
        {
            return new Binding(path)
            {
                Mode = BindingMode.TwoWay,
                UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged
            };
        }

        private CheckBox Check(string text, string path, string help = null)
        {
            var box = new CheckBox
            {
                Content = text,
                Foreground = p.Text,
                Margin = new Thickness(0, 0, 0, 8)
            };
            box.SetBinding(CheckBox.IsCheckedProperty, Bind(path));
            if (!string.IsNullOrEmpty(help))
            {
                box.ToolTip = Tooltip(help);
            }

            return box;
        }

        private Button Action(string text)
        {
            return new Button
            {
                Content = text,
                Padding = new Thickness(14, 5, 14, 5),
                Foreground = p.Text,
                Margin = new Thickness(0, 0, 8, 0)
            };
        }

        // ================================================================ 构建

        private void Build()
        {
            var panel = new StackPanel { Margin = new Thickness(14, 12, 14, 16) };

            panel.Children.Add(new TextBlock
            {
                Text = "Playnite Vault",
                FontSize = 16.5,
                FontWeight = FontWeights.Bold,
                Foreground = p.Text
            });
            panel.Children.Add(new TextBlock
            {
                Text = "连上 NAS 上的 WebDAV 仓库。改完记得保存。",
                FontSize = 12,
                Foreground = p.TextMuted,
                Margin = new Thickness(0, 4, 0, 16)
            });

            // 模块按「用的时候在找什么」分，而不是按对象分。
            // 四块体量刻意不一样大 —— 视觉层级差能让「连接」这种入口级设置
            // 一眼就和「传输超时」这种调优项分开。
            panel.Children.Add(ModuleCard("连接 NAS",
                "仓库地址与账号。这一块填不对，其他模块都不会生效。",
                BuildInterfaceSection, BuildConnectionSection));

            panel.Children.Add(ModuleCard("传输",
                "超时、切块与并发、行为开关。传输卡住或速度不理想时来这里。",
                BuildTransferSection));

            panel.Children.Add(ModuleCard("自动化",
                "插件自更新与远端库自动刷新。都是后台定时跑的事。",
                BuildUpdateSection, BuildAutoRefreshSection));

            panel.Children.Add(ModuleCard("云存档",
                "存档快照、分支与保留策略。",
                BuildSaveSection));

            UIElement content = panel;

            // 嵌进侧边栏时不套这层 ScrollViewer（外面已经有一个）——
            // 两层套着时滚轮会被里层吃掉，而里层内容不溢出所以一动不动，
            // 表现就是「滚轮没反应，只能拖旁边的滚动条」。
            // Playnite 自己的「扩展设置」页没有外层滚动容器，那边仍然需要它。
            if (!embedded)
            {
                content = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Content = panel
                };
            }

            Content = content;
        }

        /// <summary>
        /// 一个设置模块：带标题的卡片，内容由传入的 section 构建器按顺序填进去。
        ///
        /// <para>左侧那条强调色竖条是关键 —— 没有它，几个模块之间的边界只能靠空白去猜，
        /// 而空白在设置页里到处都是。有了一条实心竖线，一眼就知道「这几个字段是一组的」。</para>
        /// </summary>
        private UIElement ModuleCard(string title, string help,
            params Action<StackPanel>[] sections)
        {
            var body = new StackPanel();

            var head = new StackPanel { Orientation = Orientation.Horizontal };
            head.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                Foreground = p.Text,
                VerticalAlignment = VerticalAlignment.Center
            });
            if (!string.IsNullOrEmpty(help))
            {
                head.Children.Add(InfoIcon(help));
            }

            body.Children.Add(head);

            var inner = new StackPanel();
            foreach (var section in sections)
            {
                section(inner);
            }

            body.Children.Add(new Border
            {
                BorderBrush = p.Accent,
                BorderThickness = new Thickness(3, 0, 0, 0),
                Background = p.SurfaceAlt,
                Margin = new Thickness(0, 10, 0, 0),
                Padding = new Thickness(13, 6, 10, 2),
                Child = inner
            });

            return new Border
            {
                Background = p.Surface,
                BorderBrush = p.Border,
                BorderThickness = new Thickness(2),
                Padding = new Thickness(14, 12, 14, 12),
                Margin = new Thickness(0, 0, 0, 16),
                Child = body
            };
        }

        // ---------- 界面 ----------

        private void BuildInterfaceSection(StackPanel panel)
        {
            panel.Children.Add(Section("界面",
                "侧边栏「仓库管家」那一页的明暗配色。默认跟随 Playnite 自动判断；\n"
                + "判断依据是主窗口底色，所以某些把窗口底色做了特殊处理的主题可能判反 —— 那时手动选一档即可。"));

            var themeBox = new ComboBox
            {
                Width = 220,
                HorizontalAlignment = HorizontalAlignment.Left,
                ItemsSource = new[] { "跟随 Playnite（自动）", "浅色", "深色" }
            };
            themeBox.SetBinding(ComboBox.SelectedIndexProperty, Bind("UiThemeIndex"));
            panel.Children.Add(Field("明暗模式", themeBox, null, VaultSettings.DescribeUiTheme(service.Settings.UiTheme)));

            var hint = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Foreground = p.TextMuted,
                Margin = new Thickness(0, -4, 0, 4)
            };
            hint.SetBinding(TextBlock.TextProperty, new Binding("UiThemeHint") { Mode = BindingMode.OneWay });
            panel.Children.Add(hint);
        }

        // ---------- 连接 ----------

        private void BuildConnectionSection(StackPanel panel)
        {
            panel.Children.Add(Section("连接",
                "仓库地址必须以 / 结尾，例如 http://192.168.1.10:5005/vault/ 。\n"
                + "用户名/密码是 NAS 上 WebDAV 服务的账号 —— 有些 NAS 需要单独生成「应用密码」，"
                + "并不是管理后台的登录密码。"));

            var urlBox = new TextBox { MinWidth = 380 };
            urlBox.SetBinding(TextBox.TextProperty, Bind("WebDavUrl"));
            panel.Children.Add(Field("WebDAV 地址", urlBox, null, DescribeSaved(service.Settings.WebDavUrl)));

            var userBox = new TextBox { MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Left };
            userBox.SetBinding(TextBox.TextProperty, Bind("Username"));
            panel.Children.Add(Field("用户名", userBox, null, DescribeSaved(service.Settings.Username)));

            passBox = new PasswordBox { MinWidth = 220, HorizontalAlignment = HorizontalAlignment.Left };
            passBox.Password = vm.Password ?? string.Empty;
            passBox.PasswordChanged += (s, e) => { vm.Password = passBox.Password; };
            vm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == "Password" && passBox.Password != (vm.Password ?? string.Empty))
                {
                    passBox.Password = vm.Password ?? string.Empty;
                }
            };
            panel.Children.Add(Field("密码", passBox, "明文保存在插件私有数据目录的 settings.json 里，"
                + "不会上传到仓库。\n要注意的是：它跟仓库的「管理口令」不是一回事 —— "
                + "管理口令只用来拦删除这类不可逆操作。",
                string.IsNullOrEmpty(service.Settings.Password) ? "未设置" : "已设置"));

            var rootRow = new DockPanel();
            var browseButton = new Button
            {
                Content = "浏览…",
                Padding = new Thickness(10, 2, 10, 2),
                Foreground = p.Text
            };
            browseButton.Click += (s, e) =>
            {
                var picked = VaultPlugin.Instance.PlayniteApi.Dialogs.SelectFolder();
                if (!string.IsNullOrEmpty(picked))
                {
                    vm.LocalRoot = picked;
                }
            };
            var rootBox = new TextBox();
            rootBox.SetBinding(TextBox.TextProperty, Bind("LocalRoot"));
            DockPanel.SetDock(browseButton, Dock.Right);
            rootRow.Children.Add(browseButton);
            rootRow.Children.Add(rootBox);
            panel.Children.Add(Field("本地安装根目录", rootRow,
                "游戏解包后落在哪里。留空则用默认值（用户目录下的 VaultApps）。",
                DescribeSaved(service.Settings.LocalRoot)));

            testButton = Action("测试连接");
            testButton.HorizontalAlignment = HorizontalAlignment.Left;
            testButton.Click += OnTestConnection;
            panel.Children.Add(testButton);

            statusText = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
                FontSize = 11.5,
                Foreground = p.TextMuted
            };
            panel.Children.Add(statusText);
        }

        // ---------- 传输 ----------

        private void BuildTransferSection(StackPanel panel)
        {
            panel.Children.Add(Section("传输超时",
                "这三条超时各管一段，互不重叠。搞混它们是排查「传输卡住」时最常见的弯路。\n\n"
                + "· 建连超时：从发出请求到收到响应头。\n"
                + "· 停滞超时：连续多久没有**任何字节流动**才算死。\n"
                + "· 应答超时：body 发完之后等服务端落盘回包。"));

            var timeouts = new WrapPanel();
            timeouts.Children.Add(NumberField("建连超时（秒）", "TimeoutSeconds",
                "建立连接并等到响应头的上限。内网 15 秒足够。", service.Settings.TimeoutSeconds.ToString()));
            timeouts.Children.Add(NumberField("停滞超时（秒）", "StallTimeoutSeconds",
                "连续这么久没有任何字节流动才断开重试。\n"
                + "**这才是「速度从 20MB/s 掉到 0」对应的判据**；它管的是「每次读写等多久」，"
                + "不是「整个传输总共多久」，所以大文件也能安全通过。", service.Settings.StallTimeoutSeconds.ToString()));
            timeouts.Children.Add(NumberField("应答超时（秒）", "ResponseTimeoutSeconds",
                "body 发完之后等服务端合并/落盘回包的上限。NAS 收到大区块还要落盘，这里要给足（默认 180）。",
                service.Settings.ResponseTimeoutSeconds.ToString()));
            panel.Children.Add(timeouts);

            panel.Children.Add(Section("切块与并发",
                "区块是传输的最小单位：失败了只需要重传一个区块，而不是整个文件。"));

            var chunks = new WrapPanel();
            chunks.Children.Add(NumberField("区块大小（MB）", "ChunkSizeMB",
                "一个文件会被切成若干这样大小的区块，**可以跨块**。\n"
                + "256MB 时 Dead Cells 的 res.pak（1.93GB）会变成一个巨型分片，单个 PUT 上百秒必然超时；\n"
                + "32MB 是实测跑满带宽的档位，单块传得快、失败了重传代价也小。", service.Settings.ChunkSizeMB.ToString()));
            chunks.Children.Add(NumberField("并发下载路数（1~16）", "Concurrency",
                "单条 HTTPS 连接在 .NET Framework 下解密上限约 27 MB/s，所以千兆内网要开多路才能跑满。\n"
                + "实测：3 路 72、4 路 93、6 路 96、8 路 86 MB/s —— 建议 6。走明文 HTTP 时 1~2 就够。\n"
                + "注意峰值临时磁盘占用 ≈（并发数 + 2）× 区块大小。", service.Settings.Concurrency.ToString()));
            chunks.Children.Add(NumberField("并发上传路数（0 = 沿用下载）", "UploadConcurrency",
                "瓶颈通常在 NAS 写入侧：实测下载 88 MB/s 而上传只有 20~40 MB/s，并发越高越容易停顿。\n"
                + "所以上传默认比下载保守一档（4 路），填 0 表示跟下载并发一致。",
                service.Settings.UploadConcurrency.ToString()));
            chunks.Children.Add(NumberField("失败重试次数（每个区块）", "MaxRetries",
                "只对可重试的错误生效：超时 / 连接中断 / 429 / 5xx。\n"
                + "4xx（比如 401 密码错、403 无权限）不重试 —— 重试也不会变对，只会白等。",
                service.Settings.MaxRetries.ToString()));
            panel.Children.Add(chunks);

            panel.Children.Add(Section("行为开关"));

            panel.Children.Add(Check("启用断点续传（中断后重试只补没传完的区块）", "ResumePartial",
                "关了的话每次重试都从头传整个区块。只有在排查「续传本身有问题」时才建议关掉。"
                + "默认开。"));
            panel.Children.Add(Check("下载时边下边解（区块下完立刻解包并删掉临时文件）", "PipelineExtract"));
            panel.Children.Add(Check("归档时压缩内容（游戏资源多为已压缩格式，一般省不下空间）", "CompressOnArchive",
                "开着会让打包变慢、CPU 占用升高，而多数游戏资源（贴图/音频/视频）已经是压缩格式，"
                + "Deflate 再压一遍基本省不下空间。除非你确认要归档的是大量纯文本/未压缩资源，否则关着。"));
            panel.Children.Add(Check("走系统代理（访问内网 NAS 时不要勾选）", "UseSystemProxy",
                "开着的话请求会被本机代理（Clash 之类）截走，内网 NAS 会直接连不上。"
                + "只有在 NAS 挂在公网、必须经代理访问时才需要勾。"));
        }

        // ---------- 自动更新 ----------

        private void BuildUpdateSection(StackPanel panel)
        {
            panel.Children.Add(Section("插件自动更新",
                "插件从 GitHub / Gitee 的 release 上取新版本。两个平台的包内容完全一致，"
                + "所以「自动探测」只是挑当时连得快的那个，换哪个都不影响功能。"));

            panel.Children.Add(Check("启动 Playnite 时检查 GitHub / Gitee 上的新版本", "AutoUpdateEnabled"));
            panel.Children.Add(Check("更新前先询问（不勾选＝下载完自动替换并重启 Playnite）", "AutoUpdatePrompt"));

            var mirrorBox = new ComboBox
            {
                Width = 240,
                HorizontalAlignment = HorizontalAlignment.Left,
                ItemsSource = new[]
                {
                    "自动（探测两个源，谁快用谁）",
                    "只用 GitHub",
                    "只用 Gitee"
                }
            };
            mirrorBox.SetBinding(ComboBox.SelectedIndexProperty, Bind("UpdateMirrorIndex"));
            panel.Children.Add(Field("下载镜像", mirrorBox,
                "「自动」会并发探测两个源，响应快的当主源；下载过程中实测速度低于 "
                + VaultUpdater.SpeedFloorKb + " KB/s 会换另一个源重来"
                + "（如果两个源都低于这条线，最后会拿主源不限速再跑一次，不会因为慢就装不上）。",
                service.Settings.UpdateMirror));

            skippedText = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Foreground = p.TextMuted,
                Margin = new Thickness(0, 0, 0, 8)
            };
            panel.Children.Add(skippedText);

            var row = new WrapPanel();
            var checkButton = Action("立即检查更新");
            checkButton.Click += (s, e) =>
            {
                var plugin = VaultPlugin.Instance;
                if (plugin != null)
                {
                    plugin.RunUpdateCheck(true);
                }
                RefreshStatusLines();
            };
            row.Children.Add(checkButton);

            var clearButton = Action("清除「跳过版本」");
            clearButton.Click += (s, e) =>
            {
                vm.SkippedVersion = string.Empty;
                var plugin = VaultPlugin.Instance;
                if (plugin != null)
                {
                    plugin.SaveSettingsImmediately(vm);
                }
                RefreshStatusLines();
            };
            row.Children.Add(clearButton);

            // 「打开日志文件」从侧边栏左栏底部搬到这里（v1.8）
            var logButton = Action("打开日志文件");
            logButton.Click += (s, e) =>
            {
                var plugin = VaultPlugin.Instance;
                if (plugin != null)
                {
                    plugin.OpenLogFile();
                }
            };
            row.Children.Add(logButton);

            panel.Children.Add(row);

            updateStatusText = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
                FontSize = 11,
                Foreground = p.TextMuted
            };
            panel.Children.Add(updateStatusText);
        }

        // ---------- 自动刷新 ----------

        private void BuildAutoRefreshSection(StackPanel panel)
        {
            panel.Children.Add(Section("自动刷新远端库",
                "定时拉一次 NAS 上的 index.json（几十 KB），把远端新增的游戏同步进 Playnite 库。\n"
                + "**索引没有变化时完全不碰本地库**，所以放着也不会拖慢 Playnite。\n"
                + "正在运行游戏时会自动暂停；NAS 连续不可达时，间隔会逐级放宽（最多 8 倍），不会一直白白去敲。"));

            panel.Children.Add(Check("按下面的间隔自动把 NAS 上的索引同步进 Playnite 库", "AutoRefreshEnabled"));
            panel.Children.Add(NumberField("刷新间隔（分钟）", "AutoRefreshMinutes",
                "最少 " + LibraryAutoRefresh.MinMinutes + " 分钟。",
                service.Settings.AutoRefreshMinutes.ToString()));
            panel.Children.Add(Check("启动 Playnite 后先刷新一次", "AutoRefreshOnStartup"));

            var button = Action("立即刷新远端库");
            button.HorizontalAlignment = HorizontalAlignment.Left;
            button.Click += (s, e) =>
            {
                var plugin = VaultPlugin.Instance;
                if (plugin != null)
                {
                    plugin.RefreshLibraryEntries(true);
                }
                RefreshStatusLines();
            };
            panel.Children.Add(button);

            refreshStatusText = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
                FontSize = 11,
                Foreground = p.TextMuted
            };
            panel.Children.Add(refreshStatusText);
        }

        // ---------- 云存档 ----------

        private void BuildSaveSection(StackPanel panel)
        {
            panel.Children.Add(Section("云存档",
                "把存档也存到 NAS 上（与游戏仓库共用同一个 WebDAV）。每个游戏的存档目录按内容去重后"
                + "存成快照，换机器 / 回滚都能用。\n"
                + "路径定义先读 Playnite 自己那份，插件再叠加一份；两边靠「名字」对齐。"));

            panel.Children.Add(Check("启用云存档（关闭后连菜单里的存档项也不出现）", "SaveSyncEnabled"));

            var triggerBox = new ComboBox
            {
                Width = 380,
                HorizontalAlignment = HorizontalAlignment.Left,
                ItemsSource = new[]
                {
                    "只在我点的时候（最安全，默认）",
                    "游戏退出后自动上传一份",
                    "退出后自动上传 + 启动前问一句要不要拉远端的"
                }
            };
            triggerBox.SetBinding(ComboBox.SelectedIndexProperty, Bind("SaveTriggerIndex"));
            panel.Children.Add(Field("什么时候自动动存档", triggerBox,
                "「启动前问一句」**不会静默覆盖**：它会先把当前本地存档留一份底（本地 + 远端各一份），"
                + "再按你的回答决定要不要拉。\n"
                + "自动上传只在内容真的变了才造快照 —— 内容没变就不产生新快照。",
                DescribeSaveTrigger(service.Settings.SaveTrigger)));

            panel.Children.Add(Field("默认分支名", BuildBranchBox(),
                "新游戏的存档都往这条分支上推。常见的有 main（主线）、ng+（二周目）、mod（装过 mod 的档）。",
                service.Settings.SaveDefaultBranch));

            panel.Children.Add(NumberField("每个分支最多保留几份（0 = 无限）", "SaveKeepPerBranch",
                "超出部分从最旧的开始删。**标星的快照永远不删**，每条分支至少留最新一份；\n"
                + "删完会顺手回收没人引用到的文件内容（只在该游戏自己的目录里做）。\n"
                + "没开删除许可的操作一次 DELETE 都不发。",
                service.Settings.SaveKeepPerBranch.ToString()));

            panel.Children.Add(Check("恢复前先把当前状态也推一份快照到远端（除了本地留底以外的第二层保险）",
                "SaveBackupBeforeRestore"));

            panel.Children.Add(NumberField("本地「恢复前留底」最多留几份（0 = 不限）", "SaveKeepLocalBackups",
                "远端连不上的时候，本地这份就是唯一的保险，所以留底这一步永远先做、且不受网络影响。",
                service.Settings.SaveKeepLocalBackups.ToString()));

            panel.Children.Add(Check("游戏运行时记一份目录指纹，退出后做「会话差分」找存档位置", "SaveSessionSniff",
                "指纹只记「文件名 + 大小 + 修改时间」，不读内容，而且限深度 / 限文件数 / 限时间，"
                + "不会把游戏拖慢。差分出来的候选会存下来，等你打开存档管理时确认 —— "
                + "**嗅探从不自己写路径定义**。"));

            var openButton = Action("打开存档管理");
            openButton.HorizontalAlignment = HorizontalAlignment.Left;
            openButton.Click += (s, e) =>
            {
                var plugin = VaultPlugin.Instance;
                if (plugin != null)
                {
                    plugin.OpenSaveManager(null);
                }
            };
            panel.Children.Add(openButton);

            panel.Children.Add(new TextBlock
            {
                Text = SavePathAdapter.Describe(),
                TextWrapping = TextWrapping.Wrap,
                FontSize = 11,
                Foreground = p.TextMuted,
                Margin = new Thickness(0, 10, 0, 0)
            });
        }

        /// <summary>默认分支名：可编辑下拉，常见分支给了几个现成的，也允许自己写。</summary>
        private static ComboBox BuildBranchBox()
        {
            var combo = new ComboBox
            {
                Width = 200,
                HorizontalAlignment = HorizontalAlignment.Left,
                IsEditable = true,
                ItemsSource = new[] { "main", "ng+", "mod" }
            };
            combo.SetBinding(ComboBox.TextProperty, Bind("SaveDefaultBranch"));
            return combo;
        }

        // ================================================================ 状态

        /// <summary>把几个状态区重新读一遍。</summary>
        private void RefreshStatusLines()
        {
            var plugin = VaultPlugin.Instance;

            if (updateStatusText != null && plugin != null)
            {
                updateStatusText.Text = plugin.DescribeUpdateStatus();
            }

            if (refreshStatusText != null && plugin != null)
            {
                refreshStatusText.Text = plugin.DescribeAutoRefreshStatus();
            }

            if (skippedText != null)
            {
                var version = service.Settings.SkippedVersion;
                skippedText.Text = string.IsNullOrWhiteSpace(version)
                    ? "当前没有跳过任何版本。"
                    : "已跳过：" + version + "（改成新版本后会自动重新提示）";
            }
        }

        private void OnTestConnection(object sender, RoutedEventArgs e)
        {
            statusText.Foreground = p.TextMuted;
            statusText.Text = "正在测试…";
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
                    statusText.Foreground = ok ? p.Success : p.Danger;
                    testButton.IsEnabled = true;
                });
            });
        }
    }
}
