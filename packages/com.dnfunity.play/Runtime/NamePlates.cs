using System;
using System.Collections.Generic;
using System.Globalization;
using Dnf.Render;

// 证据:E-122。版式取自基线城镇帧与副本帧上的量(只记几何与颜色,不记画面上的任何名字):
//   · 名字是 12 像素高的汉字,1 像素描边(描边色 0x333333);NPC 名字的字色 0xE6C89B,主角名牌是白色;
//   · 主角名牌 "Lv.等级 名字":水平居中在主角脚下,最下面一行墨迹在脚底(世界 y + 地面 y)上方 122 行;
//   · NPC 名字:水平居中在 NPC 的世界 x(再往左 1 像素),最下面一行墨迹在精灵上沿上方 5 行;
//   · 怪物目标血条窗口:名字紧接在 "Lv 等级" 后面 3 像素,最下面一行墨迹在牌子行下沿下方 14 行(普通窗口是基线量的,精英与首领窗口按同一规则推断)。
// 没量过、没做:NPC 名字旁的任务/活动标记与"可入场次数"一类的附加牌、主角名牌右端的小图标、怪物头顶的名字(基线里只有首领与特殊怪有)、被选中或鼠标悬停时的变色。
namespace Dnf.Play
{
    /// <summary>
    /// 头顶的名牌(M30):主角的 "Lv.N 名字" 与城镇 NPC 的名字。只在有汉字字体的运行里画(<see cref="OverlayArt.HasFonts"/>),
    /// 没有字体时一律不画(5×7 点阵字库写不出名字)——离屏出图与对拍因此不受影响。
    /// </summary>
    public static class NamePlates
    {
        /// <summary>NPC 名字的字色(RGB)。</summary>
        public const uint NpcFill = 0xE6C89Bu;

        /// <summary>主角名牌的字色(RGB)。</summary>
        public const uint HeroFill = 0xFFFFFFu;

        /// <summary>名字的描边色(RGB)。</summary>
        public const uint Outline = 0x333333u;

        /// <summary>字号:点阵字库放大倍数 1 对应 12 像素(<see cref="OverlayArt.GlyphPixelSize"/>),与基线名字的字高一致。</summary>
        public const int Scale = 1;

        /// <summary>主角名牌最下面一行墨迹比脚底高几行。</summary>
        public const int HeroInkAboveFeet = 122;

        /// <summary>NPC 名字最下面一行墨迹比精灵上沿高几行。</summary>
        public const int NpcInkAboveSprite = 5;

        /// <summary>NPC 名字相对世界 x 的水平偏移(量出来是往左 1 像素)。</summary>
        public const int NpcCenterShift = -1;

        /// <summary>怪物窗口里名字离 "Lv 等级" 文字右端的空隙。</summary>
        public const int MonsterNameGap = 3;

        /// <summary>怪物窗口里名字最下面一行墨迹比牌子行(类别牌的上沿)低几行。</summary>
        public const int MonsterInkBelowTagRow = 14;

        /// <summary>名牌上的文字:<c>Lv.等级 名字</c>。名字为空时只有等级。</summary>
        public static string HeroText(int level, string name)
        {
            string lv = "Lv." + level.ToString(CultureInfo.InvariantCulture);
            return string.IsNullOrEmpty(name) ? lv : lv + " " + name;
        }

        /// <summary>一段名字的精灵帧(带描边);没有字体或文字为空返回 null。同样的参数总是同一个对象。</summary>
        public static SpriteFrame TextFrame(OverlayArt art, string text, uint fill)
        {
            if (art == null || !art.HasFonts || string.IsNullOrEmpty(text))
            {
                return null;
            }
            return art.Wrapped(text, Scale, fill, 0, 0, Outline);
        }

        /// <summary>
        /// 帧里最下面一行墨迹(不含描边)的行号:从下往上找第一行有像素的,再去掉 1 像素描边;没有像素的空帧返回最后一行。
        /// 用来按"墨迹的位置"而不是"帧的边"摆名字,换了字体行高不同也对得齐。
        /// </summary>
        public static int InkBottom(SpriteFrame frame)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }
            for (int y = frame.Height - 1; y >= 0; y--)
            {
                for (int x = 0; x < frame.Width; x++)
                {
                    if (frame.Rgba[(y * frame.Width + x) * 4 + 3] != 0)
                    {
                        return Math.Max(0, y - GlyphText.OutlinePad);
                    }
                }
            }
            return frame.Height - 1;
        }

        /// <summary>
        /// 加主角名牌:脚在屏幕 (<paramref name="feetX"/>, <paramref name="feetY"/>) 的主角,文字水平居中在脚下、最下面一行墨迹在脚底上方 <see cref="HeroInkAboveFeet"/> 行。
        /// 没有字体(或名字为空)时什么也不加,返回 false。
        /// </summary>
        public static bool AddHero(List<SceneSprite> into, ref int order, OverlayArt art, int level, string name, int feetX, int feetY)
        {
            SpriteFrame frame = TextFrame(art, HeroText(level, name), HeroFill);
            if (frame == null)
            {
                return false;
            }
            into.Add(OverlayArt.Place(frame, feetX - frame.Width / 2, feetY - HeroInkAboveFeet - InkBottom(frame), order++, "plate-hero"));
            return true;
        }

        /// <summary>
        /// 加城镇里所有 NPC 的名字:<paramref name="npcs"/> 给 NPC 的编号与世界位置(水平方向照世界 x 减镜头 x 居中),
        /// <paramref name="scene"/> 是这一帧已经排好的场景精灵(找每个 NPC 当前帧的精灵上沿)。没有名字的 NPC、画面里没有精灵的 NPC 跳过。
        /// 返回加了几个。
        /// </summary>
        public static int AddNpcs(List<SceneSprite> into, ref int order, OverlayArt art, NpcNames names, IReadOnlyList<SceneSprite> scene,
            IReadOnlyList<TownNpc> npcs, int cameraX)
        {
            if (art == null || !art.HasFonts || names == null || scene == null || npcs == null)
            {
                return 0;
            }
            int added = 0;
            foreach (TownNpc npc in npcs)
            {
                SceneSprite sprite = SpriteOf(scene, npc, cameraX);
                if (sprite == null)
                {
                    continue;
                }
                SpriteFrame frame = TextFrame(art, names.PlateOf(npc.Id), NpcFill);
                if (frame == null)
                {
                    continue;
                }
                int anchorX = npc.X - cameraX + NpcCenterShift;
                into.Add(OverlayArt.Place(frame, anchorX - frame.Width / 2, sprite.ScreenY - NpcInkAboveSprite - InkBottom(frame), order++, "plate-npc"));
                added++;
            }
            return added;
        }

        /// <summary>
        /// 找某个 NPC 当前帧的精灵:编号相同的精灵里挑水平范围盖住它世界 x 的那个(同一个编号摆了几个时用来区分),都不盖就取第一个同编号的。
        /// </summary>
        public static SceneSprite SpriteOf(IReadOnlyList<SceneSprite> scene, TownNpc npc, int cameraX)
        {
            int anchorX = npc.X - cameraX;
            SceneSprite first = null;
            for (int i = 0; i < scene.Count; i++)
            {
                SceneSprite s = scene[i];
                if (s.NpcId != npc.Id || npc.Id <= 0)
                {
                    continue;
                }
                if (first == null)
                {
                    first = s;
                }
                if (anchorX >= s.ScreenX && anchorX <= s.ScreenX + s.Frame.Width)
                {
                    return s;
                }
            }
            return first;
        }
    }
}
