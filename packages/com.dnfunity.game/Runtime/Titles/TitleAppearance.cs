using System;
using System.Collections.Generic;
using Dnf.Data;

// 证据:本机全库统计(观察,不是读码)——称号物品是 equipment/character/common/title/ 下的 .equ,装备类型都是 [title name](部位 11);
// 外观数据是脚本里深度 0 的 [custom animation](一个相对称号目录的 .ani 路径)、文字键 [name] 与 [name2]、可选的 [usable period](天数);
// 称号气泡在 etc 目录下的 titleballooninfo 脚本里(每 6 个值一条:称号编号、第二个编号或 −1、静止动画、移动动画、横偏移、纵偏移)。
// 服务端读码:限时规则——CItem::getUsablePeriod 0x08110c60 读物品对象 +0x6c;_checkTimeoutItem 0x0867cf8d 对"有期限且已计时"的物品,
// 当物品实例里记的到期日(ushort 天数)乘 86400 再加 0x44a54a80 不大于当前秒数时判过期;CTitleBook::deleteTimeoutItem 0x0864125a 对图鉴里每个过期的称号清空那一格。
// 这个项目的物品实例没有到期日字段(背包只存物品定义与数量),所以限时只登记(数据与换算),不在背包与图鉴里执行。
namespace Dnf.Game
{
    /// <summary>一件称号物品的外观与时限数据(<see cref="TitleAppearanceScript.Parse(ReadOnlySpan{byte}, PvfStringTable, int, string)"/> 读 .equ 得到)。不接渲染:只给路径与键。</summary>
    public sealed class TitleAppearance
    {
        public TitleAppearance(int code, string path)
        {
            Code = code;
            Path = path;
            TextKind = -1;
            PartTag = null;
        }

        public int Code { get; }

        public string Path { get; }

        /// <summary><c>[name]</c> 的字符串键;没写为 null。</summary>
        public string NameKey { get; internal set; }

        /// <summary><c>[name]</c> 的文字种类号(决定去哪份 *.kor.str 查)。</summary>
        public int TextKind { get; internal set; }

        /// <summary><c>[name2]</c> 的字符串键;没写为 null(数据里绝大多数称号有)。</summary>
        public string Name2Key { get; internal set; }

        /// <summary><c>[equipment type]</c> 的关键字(称号是 <c>[title name]</c>)。</summary>
        public string PartTag { get; internal set; }

        /// <summary>是称号物品(装备类型 <c>[title name]</c>)。</summary>
        public bool IsTitle => EquipmentParts.FromTag(PartTag) == EquipmentParts.TitleName;

        /// <summary><c>[custom animation]</c> 里写的路径(原样,相对称号目录);没写或写了空为 null。</summary>
        public string AnimationPath { get; internal set; }

        public bool HasAnimation => !string.IsNullOrEmpty(AnimationPath);

        /// <summary>动画在 PVF 里的规范路径(相对 .equ 所在目录解析,全库的称号都在同一个目录里);没有动画返回 null。</summary>
        public string ResolvedAnimationPath => HasAnimation ? PvfPath.ResolveRelativeTo(Path, AnimationPath) : null;

        /// <summary><c>[usable period]</c>:可用天数;没写为 0(不限时)。数据里的取值种类很少(分布见笔记)。</summary>
        public int UsablePeriodDays { get; internal set; }

        public bool IsTimeLimited => UsablePeriodDays > 0;

        /// <summary><c>[icon]</c>:图标图包与帧号。</summary>
        public string IconPath { get; internal set; }

        public int IconFrame { get; internal set; } = -1;
    }

    /// <summary>读称号 .equ 里的外观与时限字段。只认深度 0 的标签(条件块 <c>[if]</c> 里的同名标签不算,块规则见 <see cref="ScriptBlocks"/>)。</summary>
    public static class TitleAppearanceScript
    {
        public static TitleAppearance Parse(ReadOnlySpan<byte> data, PvfStringTable strings, int code, string path)
        {
            if (strings == null)
            {
                throw new ArgumentNullException(nameof(strings));
            }
            ScriptToken[] tokens = TokenScript.Parse(data);
            int[] depths = ScriptBlocks.Depths(tokens, strings);
            ScriptLine[] lines = ScriptDocument.Parse(data, strings);

            var info = new TitleAppearance(code, path);
            int tagIndex = 0;
            var tagDepths = new List<int>();
            for (int i = 0; i < tokens.Length; i++)
            {
                if (tokens[i].Type == (byte)TokenType.Tag)
                {
                    tagDepths.Add(depths[i]);
                }
            }
            foreach (ScriptLine line in lines)
            {
                if (line.Tag == null)
                {
                    continue;
                }
                int depth = tagIndex < tagDepths.Count ? tagDepths[tagIndex] : 0;
                tagIndex++;
                if (depth != 0)
                {
                    continue;
                }
                Apply(info, line);
            }
            return info;
        }

        private static void Apply(TitleAppearance info, ScriptLine line)
        {
            switch (line.Tag.ToLowerInvariant())
            {
                case "[name]":
                    foreach (ScriptValue v in line.Values)
                    {
                        if (v.Kind == ScriptValueKind.Keyed)
                        {
                            info.NameKey = v.Text;
                            info.TextKind = v.Int;
                            break;
                        }
                    }
                    break;
                case "[name2]":
                    foreach (ScriptValue v in line.Values)
                    {
                        if (v.Kind == ScriptValueKind.Keyed)
                        {
                            info.Name2Key = v.Text;
                            break;
                        }
                    }
                    break;
                case "[equipment type]":
                    info.PartTag = line.FirstString();
                    break;
                case "[custom animation]":
                {
                    string path = line.FirstString();
                    info.AnimationPath = string.IsNullOrEmpty(path) ? null : path;
                    break;
                }
                case "[usable period]":
                {
                    List<int> v = line.Ints();
                    info.UsablePeriodDays = v.Count > 0 ? v[0] : 0;
                    break;
                }
                case "[icon]":
                {
                    info.IconPath = line.FirstString();
                    List<int> v = line.Ints();
                    info.IconFrame = v.Count > 0 ? v[0] : -1;
                    break;
                }
            }
        }
    }

    /// <summary>称号气泡(气泡脚本 titleballooninfo 的一条):称号戴上时头顶冒出的小动画,只给数据。</summary>
    public readonly struct TitleBalloon
    {
        public TitleBalloon(int titleCode, int secondCode, string stayAnimation, string moveAnimation, int offsetX, int offsetY)
        {
            TitleCode = titleCode;
            SecondCode = secondCode;
            StayAnimation = stayAnimation;
            MoveAnimation = moveAnimation;
            OffsetX = offsetX;
            OffsetY = offsetY;
        }

        public int TitleCode { get; }

        /// <summary>第二个编号(−1 表示没有);含义没有查明(有的像是另一件物品的编号)。</summary>
        public int SecondCode { get; }

        public string StayAnimation { get; }

        public string MoveAnimation { get; }

        public int OffsetX { get; }

        public int OffsetY { get; }
    }

    /// <summary>读气泡脚本 titleballooninfo:标签 <c>[title balloon info]</c> 后面每 6 个值一条,不足一条的尾巴丢掉。</summary>
    public static class TitleBalloonScript
    {
        public static List<TitleBalloon> Parse(ScriptLine[] lines)
        {
            var list = new List<TitleBalloon>();
            if (lines == null)
            {
                return list;
            }
            foreach (ScriptLine line in lines)
            {
                if (!line.TagIs("[title balloon info]"))
                {
                    continue;
                }
                ScriptValue[] v = line.Values;
                for (int at = 0; at + 6 <= v.Length; at += 6)
                {
                    if (!v[at].IsNumber || !v[at + 1].IsNumber || v[at + 2].Kind != ScriptValueKind.String || v[at + 3].Kind != ScriptValueKind.String
                        || !v[at + 4].IsNumber || !v[at + 5].IsNumber)
                    {
                        break;
                    }
                    list.Add(new TitleBalloon(v[at].Int, v[at + 1].Int, v[at + 2].Text, v[at + 3].Text, v[at + 4].Int, v[at + 5].Int));
                }
            }
            return list;
        }
    }

    /// <summary>
    /// 称号的限时换算(只是算术,没有接线):到期日是"天数"(服务端存成 16 位整数),天数 × 86400 + 起点秒数就是到期的秒数;当前秒数不小于它就算过期。
    /// 常数来自服务端 <c>_checkTimeoutItem</c> 0x0867cf8d。
    /// </summary>
    public static class TitleExpiry
    {
        /// <summary>天数的起点(Unix 秒,0x44a54a80)。</summary>
        public const long BaseSeconds = 0x44A54A80L;

        public const long SecondsPerDay = 86400L;

        /// <summary>某个到期日(天数)对应的到期秒数。</summary>
        public static long ExpirySeconds(int expiryDay)
        {
            return expiryDay * SecondsPerDay + BaseSeconds;
        }

        /// <summary>当前秒数 <paramref name="nowSeconds"/> 时这个到期日是否已过期(到期秒数 ≤ 当前秒数)。</summary>
        public static bool IsExpired(int expiryDay, long nowSeconds)
        {
            return ExpirySeconds(expiryDay) <= nowSeconds;
        }

        /// <summary>
        /// 在 <paramref name="nowSeconds"/> 取得一件可用 <paramref name="periodDays"/> 天的物品,到期日(天数)是多少:现在已过去的整天数加上可用天数。
        /// (服务端算到期日的函数没有读,这是按"天数 + 可用天数"的自然写法;只用来给宿主一个起点,不是读码结论。)
        /// </summary>
        public static int ExpiryDayFor(long nowSeconds, int periodDays)
        {
            return (int)((nowSeconds - BaseSeconds) / SecondsPerDay) + periodDays;
        }
    }

    /// <summary>
    /// 称号目录:称号物品清单(<c>equipment.lst</c> 里放在称号目录下的)、外观数据、气泡与图鉴定义。读不到文件时返回空表或 null,不抛异常。
    /// </summary>
    public sealed class TitleCatalog
    {
        public const string TitleDirectory = "equipment/character/common/title/";
        private const string EtcDirectory = "etc/";
        public const string BookPath = EtcDirectory + "titlebook.etc";
        public const string BalloonPath = EtcDirectory + "titleballooninfo.etc";

        private readonly GameData _data;
        private readonly Dictionary<int, TitleAppearance> _cache = new Dictionary<int, TitleAppearance>();
        private Dictionary<int, string> _paths;
        private TitleBookDef _book;
        private List<TitleBalloon> _balloons;

        public TitleCatalog(GameData data)
        {
            _data = data ?? throw new ArgumentNullException(nameof(data));
        }

        private Dictionary<int, string> Paths()
        {
            if (_paths != null)
            {
                return _paths;
            }
            _paths = new Dictionary<int, string>();
            Dictionary<int, string> raw = _data.ReadList(ItemCatalog.EquipmentListPath);
            if (raw != null)
            {
                foreach (KeyValuePair<int, string> pair in raw)
                {
                    string path = PvfPath.ResolveRelativeTo(ItemCatalog.EquipmentListPath, pair.Value);
                    if (path.StartsWith(TitleDirectory, StringComparison.OrdinalIgnoreCase))
                    {
                        _paths[pair.Key] = path;
                    }
                }
            }
            return _paths;
        }

        /// <summary>放在称号目录下的物品编号(升序)。</summary>
        public IReadOnlyList<int> Codes()
        {
            var codes = new List<int>(Paths().Keys);
            codes.Sort();
            return codes;
        }

        /// <summary>称号物品的外观与时限数据;编号不是称号目录下的物品或脚本读不到返回 null(缓存)。</summary>
        public TitleAppearance AppearanceOf(int code)
        {
            if (_cache.TryGetValue(code, out TitleAppearance cached))
            {
                return cached;
            }
            TitleAppearance info = null;
            if (Paths().TryGetValue(code, out string path) && _data.Strings != null && _data.TryReadBytes(path, out byte[] bytes))
            {
                try
                {
                    info = TitleAppearanceScript.Parse(bytes, _data.Strings, code, path);
                }
                catch (DnfFormatException)
                {
                    info = null;
                }
            }
            _cache[code] = info;
            return info;
        }

        /// <summary>称号气泡表(没有文件返回空表)。</summary>
        public IReadOnlyList<TitleBalloon> Balloons()
        {
            if (_balloons == null)
            {
                _balloons = TitleBalloonScript.Parse(SafeScript(BalloonPath));
            }
            return _balloons;
        }

        /// <summary>某件称号的气泡;没有返回 false。</summary>
        public bool TryGetBalloon(int titleCode, out TitleBalloon balloon)
        {
            foreach (TitleBalloon b in Balloons())
            {
                if (b.TitleCode == titleCode)
                {
                    balloon = b;
                    return true;
                }
            }
            balloon = default;
            return false;
        }

        /// <summary>称号图鉴定义(没有文件得到一份空定义)。</summary>
        public TitleBookDef Book()
        {
            return _book ?? (_book = TitleBookScript.Parse(SafeScript(BookPath)));
        }

        private ScriptLine[] SafeScript(string path)
        {
            try
            {
                return _data.ReadScript(path);
            }
            catch (DnfFormatException)
            {
                return null;
            }
        }
    }
}
