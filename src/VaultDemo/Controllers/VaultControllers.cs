using System;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using VaultDemo.Models;
using VaultDemo.Services;
using VaultDemo.UI;

namespace VaultDemo.Controllers
{
    /// <summary>
    /// 接管 Playnite 未安装应用的「安装」按钮。
    /// 读远端清单 → 空间预检 → 增量下载（断点续传 + 重试）→ 写本地索引 → 回写安装状态。
    /// </summary>
    public class VaultInstallController : InstallController
    {
        private readonly VaultService service;

        public VaultInstallController(Game game, VaultService service) : base(game)
        {
            this.service = service;
            this.Name = "Vault 安装";
        }

        public override void Install(InstallActionArgs args)
        {
            var api = VaultPlugin.Instance.PlayniteApi;

            // 落盘目录名不是 Id、也不是 Playnite 里的显示名：
            // 它来自随包元数据的 InstallDirName（上传前原始安装目录的最后一段）。
            // GetInstallDir(appId) 内部就是「缓存索引 → 远端清单」找这个字段，取不到才退回 Id。
            var targetDir = service.GetInstallDir(Game.GameId);

            api.Dialogs.ActivateGlobalProgress(progress =>
            {
                try
                {
                    VaultLog.Info("开始安装 " + Game.Name + " (" + Game.GameId + ")");

                    // 标题固定为「正在安装 xxx」，数值行随进度刷新；
                    // 进度条一开始就是确定态，避免中途从 indeterminate 切过来那一下跳变
                    var sink = new ProgressSink(progress, "正在安装 " + Game.Name);

                    var options = service.BuildSyncOptions();

                    var result = service.InstallApp(
                        Game.GameId,
                        targetDir,
                        options,
                        sink.Apply,
                        progress.CancelToken);

                    VaultLog.Info("安装完成：" + Game.Name + "，" + result.Describe());

                    // 必须传 GameInstallationData，否则安装目录与已安装状态都写不进游戏属性
                    InvokeOnInstalled(new GameInstalledEventArgs(new GameInstallationData
                    {
                        InstallDirectory = targetDir
                    }));
                }
                catch (OperationCanceledException)
                {
                    // Playnite 10.41 没有 InvokeOnInstallationCancelled，直接结束即可。
                    // 不清理目录：已下载的 .part 保留下来，下次点安装可以续传。
                    VaultLog.Warn("安装被取消：" + Game.Name);
                }
                catch (Exception ex)
                {
                    VaultLog.Error("安装失败：" + Game.Name, ex);
                    api.Dialogs.ShowErrorMessage(
                        "安装「" + Game.Name + "」失败：" + ex.Message +
                        "\n\n已下载的部分会保留，重新点击安装可从断点继续。",
                        "Vault Demo");
                }
            },
            new GlobalProgressOptions("从 Vault 安装「" + Game.Name + "」")
            {
                // 清单第一行就能拿到总字节数，所以没必要用 indeterminate
                IsIndeterminate = false,
                Cancelable = true
            });
        }
    }

    /// <summary>
    /// 卸载：删除本地目录与索引，条目回到「未安装」，NAS 上的归档不受影响。
    /// </summary>
    public class VaultUninstallController : UninstallController
    {
        private readonly VaultService service;

        public VaultUninstallController(Game game, VaultService service) : base(game)
        {
            this.service = service;
            this.Name = "Vault 卸载";
        }

        public override void Uninstall(UninstallActionArgs args)
        {
            var api = VaultPlugin.Instance.PlayniteApi;
            var entry = service.GetInstalledEntry(Game.GameId);
            var targetDir = entry != null ? entry.InstallDir : service.GetInstallDir(Game.GameId);

            var confirm = api.Dialogs.ShowMessage(
                "确定要从本地删除「" + Game.Name + "」吗？\n\n目录：" + targetDir +
                "\n\nNAS 上的归档不受影响，之后可以再次安装。",
                "Vault Demo",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning);

            if (confirm != System.Windows.MessageBoxResult.Yes)
            {
                // 用户放弃：必须给出一个结束信号，否则 Playnite 会卡在「正在卸载」
                InvokeOnUninstalled();
                return;
            }

            api.Dialogs.ActivateGlobalProgress(progress =>
            {
                try
                {
                    progress.Text = "正在删除本地文件...";
                    VaultLog.Info("卸载 " + Game.Name + "，目录 " + targetDir);

                    service.UninstallApp(Game.GameId, targetDir);

                    VaultLog.Info("卸载完成：" + Game.Name);
                    InvokeOnUninstalled();
                }
                catch (Exception ex)
                {
                    VaultLog.Error("卸载失败：" + Game.Name, ex);
                    api.Dialogs.ShowErrorMessage("卸载失败：" + ex.Message, "Vault Demo");
                    InvokeOnUninstalled();
                }
            },
            new GlobalProgressOptions("移除「" + Game.Name + "」")
            {
                IsIndeterminate = true,
                Cancelable = false
            });
        }
    }
}
