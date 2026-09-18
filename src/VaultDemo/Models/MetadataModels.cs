using System.Collections.Generic;

namespace VaultDemo.Models
{
    /// <summary>
    /// 随包携带的应用元数据。
    /// 归档时从 Playnite 的 Game 对象抓取，安装 / 库导入时直接回填，
    /// 因此换机器重装后不需要再联网刮削一遍。
    /// </summary>
    public class AppMetadata
    {
        public string Name { get; set; }

        /// <summary>排序名（Playnite 的 SortingName）。</summary>
        public string SortingName { get; set; }

        public string Description { get; set; }
        public string Version { get; set; }

        /// <summary>
        /// 归档时该应用在 Playnite 里的安装目录名（路径最后一级文件夹名）。
        /// 例如安装目录是 D:\Games\Brotato，这里就是 "Brotato"。
        ///
        /// 用途：解包 / 重装时用它当本地子目录名，保持和上传前一致，
        /// 免得同一个游戏在不同机器上落到名字不一样的文件夹里
        /// （仓库里的目录名 apps/{id}/ 是 slug，不适合当本地目录名）。
        /// 旧归档没有这个字段，读取端要回退成 app id。
        /// </summary>
        public string InstallDirName { get; set; }

        /// <summary>发行日期，格式 yyyy-MM-dd。</summary>
        public string ReleaseDate { get; set; }

        /// <summary>来源商店，例如 Steam / Epic / 手动添加。</summary>
        public string Source { get; set; }

        public List<string> Developers { get; set; }
        public List<string> Publishers { get; set; }
        public List<string> Genres { get; set; }
        public List<string> Categories { get; set; }
        public List<string> Tags { get; set; }
        public List<string> Features { get; set; }
        public List<string> Series { get; set; }
        public List<string> Platforms { get; set; }
        public List<string> Regions { get; set; }
        public List<string> AgeRatings { get; set; }
        public List<LinkEntry> Links { get; set; }

        public int? CommunityScore { get; set; }
        public int? CriticScore { get; set; }
        public int? UserScore { get; set; }

        /// <summary>安装后占用字节数（用于 Playnite 显示体积）。</summary>
        public long? InstallSize { get; set; }

        /// <summary>启动参数与工作目录（相对于安装目录；空表示安装根目录）。</summary>
        public string LaunchArguments { get; set; }
        public string LaunchWorkingDir { get; set; }

        public bool? Hidden { get; set; }
        public bool? Favorite { get; set; }

        /// <summary>
        /// 随包存放的图片。键固定为 icon / cover / background，
        /// 值是相对于 apps/{id}/ 的仓库路径，例如 meta/cover.png。
        /// </summary>
        public Dictionary<string, string> Images { get; set; }

        public bool IsEmpty()
        {
            return string.IsNullOrWhiteSpace(Name)
                && string.IsNullOrWhiteSpace(Description)
                && (Developers == null || Developers.Count == 0)
                && (Genres == null || Genres.Count == 0)
                && (Tags == null || Tags.Count == 0)
                && (Images == null || Images.Count == 0);
        }
    }

    /// <summary>外链（官网 / 商店页 / 攻略等）。</summary>
    public class LinkEntry
    {
        public string Name { get; set; }
        public string Url { get; set; }
    }
}
