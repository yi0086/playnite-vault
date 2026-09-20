using System;
using System.Globalization;

namespace PlayniteVault.Services
{
    /// <summary>
    /// 宽松的版本号比较：吃得下 "1.4.0" / "v1.4.0" / "1.4" / "1.4.0-beta.1" / "PlayniteVault-1.4.0"。
    ///
    /// 为什么不直接用 <see cref="System.Version"/>：
    ///   · git tag 通常带 <c>v</c> 前缀，Version.Parse 会直接抛；
    ///   · 只有两段（1.4）时 Version 要求补成 1.4.0.0，否则部分重载会抛；
    ///   · 预发布后缀（-beta.1）Version 完全不认。
    /// 这里把「数字段」抽出来比，非数字部分只用来判断「正式版 &gt; 预发布版」。
    /// </summary>
    public struct SemVersion : IComparable<SemVersion>, IEquatable<SemVersion>
    {
        private int[] parts;     // 数字段，已去掉前导 v / 项目名前缀
        private string suffix;   // 预发布后缀（不含 '-'），正式版为 null

        public bool IsValid
        {
            get { return parts != null && parts.Length > 0; }
        }

        /// <summary>是否预发布（1.5.0-beta.1 这种）。正式版优先级更高。</summary>
        public bool IsPreRelease
        {
            get { return !string.IsNullOrEmpty(suffix); }
        }

        public int Major
        {
            get { return Segment(0); }
        }

        public int Minor
        {
            get { return Segment(1); }
        }

        public int Patch
        {
            get { return Segment(2); }
        }

        private int Segment(int index)
        {
            return parts != null && index < parts.Length ? parts[index] : 0;
        }

        public static SemVersion Parse(string text)
        {
            var result = new SemVersion();

            if (string.IsNullOrWhiteSpace(text))
            {
                return result;
            }

            var s = text.Trim();

            // "PlayniteVault-1.5.0.zip" 这种素材名也一并接受：先取最后一段 '-' 之后的数字串
            var dash = s.LastIndexOf('-');
            if (dash >= 0 && dash + 1 < s.Length)
            {
                var afterDash = s.Substring(dash + 1);
                // -beta.1 也含 '-'？不会：这里只在一段内没有数字的情况下才截断
                if (afterDash.Length > 0 && char.IsDigit(afterDash[0]))
                {
                    s = afterDash;
                }
            }

            var plus = s.IndexOf('+');
            if (plus >= 0)
            {
                s = s.Substring(0, plus);
            }

            var suffixIndex = s.IndexOf('-');
            if (suffixIndex >= 0)
            {
                result.suffix = s.Substring(suffixIndex + 1);
                s = s.Substring(0, suffixIndex);
            }

            // 前缀可能是 v / V / 任意项目名（PlayniteVault1.5.0）→ 从第一个数字开始
            var start = 0;
            while (start < s.Length && !char.IsDigit(s[start]))
            {
                start++;
            }
            s = s.Substring(start);

            var pieces = s.Split('.');
            var list = new System.Collections.Generic.List<int>();
            foreach (var piece in pieces)
            {
                var digits = 0;
                while (digits < piece.Length && char.IsDigit(piece[digits]))
                {
                    digits++;
                }

                if (digits == 0)
                {
                    break;   // 遇到 "0-rc1" 这类，后面的段不算
                }

                int value;
                if (!int.TryParse(piece.Substring(0, digits), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out value))
                {
                    break;
                }

                list.Add(value);
            }

            if (list.Count == 0)
            {
                return result;
            }

            result.parts = list.ToArray();
            return result;
        }

        /// <summary>把 "1.5.0" 这类规范串解出来；解析不了返回 null。</summary>
        public static string NormalizeText(string text)
        {
            var v = Parse(text);
            if (!v.IsValid)
            {
                return null;
            }

            var core = v.Major + "." + v.Minor + "." + v.Patch;
            return v.IsPreRelease ? core + "-" + v.suffix : core;
        }

        public int CompareTo(SemVersion other)
        {
            if (!IsValid && !other.IsValid) return 0;
            if (!IsValid) return -1;
            if (!other.IsValid) return 1;

            var length = Math.Max(parts.Length, other.parts.Length);
            for (var i = 0; i < length; i++)
            {
                var a = i < parts.Length ? parts[i] : 0;
                var b = i < other.parts.Length ? other.parts[i] : 0;
                if (a != b)
                {
                    return a < b ? -1 : 1;
                }
            }

            // 数字段完全一样时：正式版 &gt; 预发布版（1.5.0 &gt; 1.5.0-rc）
            if (IsPreRelease != other.IsPreRelease)
            {
                return IsPreRelease ? -1 : 1;
            }

            return string.Compare(suffix, other.suffix, StringComparison.OrdinalIgnoreCase);
        }

        public bool Equals(SemVersion other)
        {
            return CompareTo(other) == 0;
        }

        public override bool Equals(object obj)
        {
            return obj is SemVersion && Equals((SemVersion)obj);
        }

        public override int GetHashCode()
        {
            return ToString().GetHashCode();
        }

        public override string ToString()
        {
            if (!IsValid)
            {
                return string.Empty;
            }
            var core = Major + "." + Minor + "." + Patch;
            return IsPreRelease ? core + "-" + suffix : core;
        }

        /// <summary>a 是否比 b 新（严格大于）。</summary>
        public static bool IsNewer(string candidate, string current)
        {
            var a = Parse(candidate);
            var b = Parse(current);
            return a.IsValid && a.CompareTo(b) > 0;
        }
    }
}
