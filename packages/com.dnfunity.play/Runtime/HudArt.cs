using System;
using System.Collections.Generic;
using Dnf.Game;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>一个技能快捷槽此刻的状态(画底栏用)。</summary>
    public readonly struct HudSkillState
    {
        /// <summary>第几个槽(0 = A,1 = S,2 = D,3 = F,4 = G,5 = H)。</summary>
        public int Slot { get; }

        /// <summary>技能定义(取图标用);没有图标时这个槽只画空格。</summary>
        public SkillDef Def { get; }

        /// <summary>能放(冷却好了、魔法够)就画亮图标,否则画灰图标。</summary>
        public bool Usable { get; }

        public HudSkillState(int slot, SkillDef def, bool usable)
        {
            Slot = slot;
            Def = def;
            Usable = usable;
        }
    }

    /// <summary>
    /// 经验条此刻的数(证据 E-111):<see cref="Value"/> 是在本级里已经攒的经验,<see cref="Span"/> 是本级一共要攒的经验
    /// (客户端取这两个数的来源是推断的:读到的是一对成对存放的受保护整数,按数据流判断为"本级已攒、本级总量"),
    /// <see cref="Capped"/> 为真(等级 85,经验能涨到的上限)时在条上再盖一层满级图。
    /// </summary>
    public readonly struct HudExp
    {
        public int Value { get; }
        public int Span { get; }
        public bool Capped { get; }

        public HudExp(int value, int span, bool capped)
        {
            Value = value;
            Span = span;
            Capped = capped;
        }
    }

    /// <summary>
    /// 画面底部的原版界面底栏(证据 E-088):用户自备的图包里的 hud.img 画出来,和别的精灵走同一条绘制管线。
    /// 位置都是拿基线截图用 <c>dnf locate</c> 逐像素找出来的(不透明像素 100% 一致):
    /// 先画整条底栏(800×91,左右各有一个球的底座),再在左边画血球液体、右边画魔法球液体(液体按比例从下往上露出),
    /// 左下角的等级牌(Lv. 加数字),中间的菜单图标、菜单与商城按钮,右边的技能快捷槽(图标取 .skl 的 [icon],能放时亮、不能放时灰)与热键字母。
    /// 经验条(证据 E-111):<see cref="HudExp"/> 给了才画——hud.img 的 #4(625×4)放在 (88, 591),只露出前 <see cref="ExpBarWidth"/> 列;等级 85 再盖 #60。
    /// 没做:疲劳条的数据、状态图标、物品快捷栏(1 到 6)、技能等级小字与冷却遮罩、鼠标悬停与点击、球上的高光。
    /// 取不到 hud.img 时 <see cref="TryCreate"/> 返回 null,调用方退回占位的色块血条。
    /// </summary>
    public sealed class HudArt
    {
        public const string HudImage = "sprite/interface2/hud/hud.img";
        public const string LevelImage = "sprite/interface2/hud/level02.img";

        // ----- hud.img 的帧号(E-088)
        public const int BarFrame = 0;
        public const int HpFillFrame = 1;
        public const int MpFillFrame = 2;
        public const int KeyLabelStripFrame = 45;
        public const int InfoButtonFrame = 52;
        public const int MenuButtonFrame = 68;
        public const int ShopButtonFrame = 72;

        /// <summary>经验条(625×4)的帧号与满级覆盖层的帧号(客户端 0x004c0d60 里传给取图函数的下标 4 与 0x3c,证据 E-111)。</summary>
        public const int ExpBarFrame = 4;
        public const int ExpCapFrame = 60;

        /// <summary>经验条的像素宽度(满条)与位置(0x004c0d60 里画条的坐标 (0x58, 0x24f);这一帧自带偏移为 0,与基线截图逐像素对过,位置在 (88, 591))。</summary>
        public const int ExpBarMaxWidth = 625;
        public const int ExpBarX = 88;
        public const int ExpBarY = 591;

        /// <summary>中间五个菜单图标(24×26)的帧号:四个常态,最后一个是灰的(基线里就是这样)。</summary>
        public static readonly int[] MenuIconFrames = { 5, 8, 11, 14, 97 };

        // ----- 位置:像素块的左上角(屏幕 800×600)
        public const int BarX = 0;
        public const int BarY = 509;
        public const int HpOrbX = 13;
        public const int MpOrbX = 732;
        public const int OrbY = 524;
        public const int OrbSize = 56;
        public const int LevelLabelX = 19;
        public const int LevelDigitX = 37;
        public const int LevelDigitPitch = 12;
        public const int LevelY = 585;
        public const int MenuIconX = 283;
        public const int MenuIconPitch = 26;
        public const int MenuRowY = 560;
        public const int MenuButtonX = 413;
        public const int ShopButtonX = 466;
        public const int InfoButtonX = 519;
        public const int InfoButtonY = 559;
        public const int SkillSlotX = 538;
        public const int SkillSlotPitch = 30;
        public const int SkillSlotY = 558;
        public const int KeyLabelX = 560;
        public const int KeyLabelY = 579;
        public const int KeyLabelWidth = 6;
        public const int KeyLabelHeight = 7;

        /// <summary>热键字母(A S D F G H)在 <see cref="KeyLabelStripFrame"/> 那条图里的起始列。</summary>
        public static readonly int[] KeyLabelColumns = { 292, 322, 352, 383, 412, 442 };

        /// <summary>最多有几个技能槽(A S D F G H)。</summary>
        public const int SlotCount = 6;

        private readonly ISpriteSource _sprites;
        private readonly SpriteFrame _bar;
        private readonly SpriteFrame _hpFull;
        private readonly SpriteFrame _mpFull;
        private readonly SpriteFrame[] _labels = new SpriteFrame[SlotCount];
        private readonly Dictionary<int, SpriteFrame> _hpFills = new Dictionary<int, SpriteFrame>();
        private readonly Dictionary<int, SpriteFrame> _mpFills = new Dictionary<int, SpriteFrame>();
        private readonly Dictionary<int, SpriteFrame> _digits = new Dictionary<int, SpriteFrame>();
        private readonly Dictionary<int, SpriteFrame> _expFills = new Dictionary<int, SpriteFrame>();
        private readonly Dictionary<string, SpriteFrame> _frames = new Dictionary<string, SpriteFrame>(StringComparer.Ordinal);

        private HudArt(ISpriteSource sprites, SpriteFrame bar, SpriteFrame hpFull, SpriteFrame mpFull, SpriteFrame strip)
        {
            _sprites = sprites;
            _bar = bar;
            _hpFull = hpFull;
            _mpFull = mpFull;
            for (int i = 0; i < SlotCount; i++)
            {
                _labels[i] = Crop(strip, KeyLabelColumns[i], 0, KeyLabelWidth, KeyLabelHeight);
            }
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ISpriteSource, HudArt> Shared =
            new System.Runtime.CompilerServices.ConditionalWeakTable<ISpriteSource, HudArt>();

        /// <summary>
        /// 同一份图包共用一个底栏对象(副本每换一个房间就新建一个战斗会话,共用才不会每次重新裁液体、字母,
        /// GPU 端按帧对象缓存的贴图也不会越攒越多)。取不到底栏的图时返回 null。
        /// </summary>
        public static HudArt For(ISpriteSource sprites)
        {
            if (sprites == null)
            {
                return null;
            }
            if (Shared.TryGetValue(sprites, out HudArt hud))
            {
                return hud;
            }
            hud = TryCreate(sprites);
            if (hud != null)
            {
                Shared.Add(sprites, hud);
            }
            return hud;
        }

        /// <summary>
        /// 从图包里取底栏需要的图;底栏、两个球的液体与热键字母条缺任何一个就返回 null(例如合成数据的测试)。
        /// 菜单图标与按钮缺了只是不画。
        /// </summary>
        public static HudArt TryCreate(ISpriteSource sprites)
        {
            if (sprites == null
                || !sprites.TryGetFrame(HudImage, BarFrame, out SpriteFrame bar)
                || !sprites.TryGetFrame(HudImage, HpFillFrame, out SpriteFrame hp)
                || !sprites.TryGetFrame(HudImage, MpFillFrame, out SpriteFrame mp)
                || !sprites.TryGetFrame(HudImage, KeyLabelStripFrame, out SpriteFrame strip)
                || strip.Width < KeyLabelColumns[SlotCount - 1] + KeyLabelWidth || strip.Height < KeyLabelHeight)
            {
                return null;
            }
            return new HudArt(sprites, bar, hp, mp, strip);
        }

        /// <summary>
        /// 液体露出的行数:比例 ≤ 0 为 0;否则向上取整并至少 1 行(还有血就看得见一点),最多 <see cref="OrbSize"/> 行。
        /// 取整方式没有原版样本能校准(基线的液面只能数到整行),这是推断。
        /// </summary>
        public static int OrbRows(int value, int max)
        {
            if (value <= 0 || max <= 0)
            {
                return 0;
            }
            if (value >= max)
            {
                return OrbSize;
            }
            int rows = (int)Math.Ceiling((double)OrbSize * value / max);
            return rows < 1 ? 1 : rows > OrbSize ? OrbSize : rows;
        }

        /// <summary>
        /// 经验条露出的像素宽度(客户端 0x011b98c0 的算术,单精度,证据 E-111):<paramref name="value"/> 不小于 <paramref name="span"/> 时是满条
        /// (<see cref="ExpBarMaxWidth"/>,包括 <paramref name="span"/> 不大于 0 的情况);<paramref name="value"/> 不大于 0 时是 0;
        /// 其余是 <c>(int)((625 - 0) × value / span + 0)</c>,每一步都落成单精度,取整向零。
        /// </summary>
        public static int ExpBarWidth(int value, int span)
        {
            if (value >= span)
            {
                return ExpBarMaxWidth;
            }
            if (value <= 0)
            {
                return 0;
            }
            float width = (float)ExpBarMaxWidth - 0f;
            width = width * (float)value;
            width = width / (float)span;
            width = width + 0f;
            return (int)width;
        }

        /// <summary>
        /// 把底栏的全部精灵按从下到上的顺序加进 <paramref name="into"/>(<paramref name="order"/> 一路递增)。
        /// 顺序:底栏 → 血球液体 → 魔法球液体 → 经验条(给了才画)→ 等级牌 → 菜单图标 → 菜单、商城按钮 → "I" 按钮 → 技能图标 → 热键字母。
        /// </summary>
        public void Build(List<SceneSprite> into, ref int order, int level, int hp, int hpMax, int mp, int mpMax, IReadOnlyList<HudSkillState> skills,
            HudExp? exp = null)
        {
            PlaceBlock(into, ref order, _bar, BarX, BarY, "hud-bar");

            AddOrb(into, ref order, _hpFull, _hpFills, OrbRows(hp, hpMax), HpOrbX, "hud-hp");
            AddOrb(into, ref order, _mpFull, _mpFills, OrbRows(mp, mpMax), MpOrbX, "hud-mp");

            if (exp.HasValue)
            {
                AddExp(into, ref order, exp.Value);
            }

            AddLevel(into, ref order, level);

            for (int i = 0; i < MenuIconFrames.Length; i++)
            {
                if (Frame(HudImage, MenuIconFrames[i]) is SpriteFrame icon)
                {
                    PlaceBlock(into, ref order, icon, MenuIconX + MenuIconPitch * i, MenuRowY, "hud-menu-icon");
                }
            }
            if (Frame(HudImage, MenuButtonFrame) is SpriteFrame menu)
            {
                PlaceBlock(into, ref order, menu, MenuButtonX, MenuRowY, "hud-menu");
            }
            if (Frame(HudImage, ShopButtonFrame) is SpriteFrame shop)
            {
                PlaceBlock(into, ref order, shop, ShopButtonX, MenuRowY, "hud-shop");
            }
            if (Frame(HudImage, InfoButtonFrame) is SpriteFrame info)
            {
                PlaceBlock(into, ref order, info, InfoButtonX, InfoButtonY, "hud-info");
            }

            if (skills != null)
            {
                foreach (HudSkillState s in skills)
                {
                    if (s.Slot < 0 || s.Slot >= SlotCount || s.Def == null)
                    {
                        continue;
                    }
                    SpriteFrame normal = IconFrame(s.Def.IconImage, s.Def.IconFrame);
                    SpriteFrame gray = IconFrame(s.Def.IconDisabledImage, s.Def.IconDisabledFrame);
                    SpriteFrame pick = s.Usable ? normal ?? gray : gray ?? normal;
                    if (pick != null)
                    {
                        // 位置是亮图标的像素块位置;灰图标的偏移若不同,按各自偏移相对同一个原点摆
                        SpriteFrame anchor = normal ?? pick;
                        PlaceFrom(into, ref order, pick, anchor, SkillSlotX + SkillSlotPitch * s.Slot, SkillSlotY, "hud-skill");
                    }
                }
            }
            for (int i = 0; i < SlotCount; i++)
            {
                into.Add(OverlayArt.Place(_labels[i], KeyLabelX + SkillSlotPitch * i, KeyLabelY, order++, "hud-key"));
            }
        }

        private void AddOrb(List<SceneSprite> into, ref int order, SpriteFrame full, Dictionary<int, SpriteFrame> cache, int rows, int blockX, string tag)
        {
            if (rows <= 0)
            {
                return;
            }
            SpriteFrame fill = full;
            if (rows < full.Height)
            {
                if (!cache.TryGetValue(rows, out fill))
                {
                    fill = Crop(full, 0, full.Height - rows, full.Width, rows);
                    cache[rows] = fill;
                }
            }
            PlaceFrom(into, ref order, fill, full, blockX, OrbY, tag);
        }

        /// <summary>
        /// 经验条(证据 E-111):#4 放在 (88, 591),只露出前 <see cref="ExpBarWidth"/> 列(客户端是把裁剪框设成 x &lt; 88 + 宽度再整张画);
        /// 等级 85(<see cref="HudExp.Capped"/>)再在同一位置盖 #60。缺图就不画。
        /// </summary>
        private void AddExp(List<SceneSprite> into, ref int order, HudExp exp)
        {
            SpriteFrame full = Frame(HudImage, ExpBarFrame);
            if (full != null)
            {
                int width = Math.Min(ExpBarWidth(exp.Value, exp.Span), full.Width);
                if (width > 0)
                {
                    SpriteFrame fill = full;
                    if (width < full.Width && !_expFills.TryGetValue(width, out fill))
                    {
                        fill = Crop(full, 0, 0, width, full.Height);
                        _expFills[width] = fill;
                    }
                    PlaceFrom(into, ref order, fill, full, ExpBarX, ExpBarY, "hud-exp");
                }
            }
            if (exp.Capped && Frame(HudImage, ExpCapFrame) is SpriteFrame cap)
            {
                PlaceBlock(into, ref order, cap, ExpBarX, ExpBarY, "hud-exp-cap");
            }
        }

        private void AddLevel(List<SceneSprite> into, ref int order, int level)
        {
            if (!(Frame(LevelImage, 10) is SpriteFrame label))
            {
                return;
            }
            PlaceBlock(into, ref order, label, LevelLabelX, LevelY, "hud-level");
            string text = (level < 0 ? 0 : level).ToString(System.Globalization.CultureInfo.InvariantCulture);
            for (int i = 0; i < text.Length; i++)
            {
                if (Frame(LevelImage, text[i] - '0') is SpriteFrame digit)
                {
                    // 数字帧的原点按 12 像素一格排;"1" 那一帧自带偏移 2(窄一点),所以也要把偏移加回去
                    into.Add(OverlayArt.Place(digit, LevelDigitX + LevelDigitPitch * i + digit.OffsetX, LevelY + digit.OffsetY, order++, "hud-level"));
                }
            }
        }

        /// <summary>
        /// 按像素块的位置摆一个帧:<paramref name="blockX"/>、<paramref name="blockY"/> 是这个帧的像素块左上角(基线里量到的位置)。
        /// </summary>
        private static void PlaceBlock(List<SceneSprite> into, ref int order, SpriteFrame frame, int blockX, int blockY, string tag)
        {
            into.Add(OverlayArt.Place(frame, blockX, blockY, order++, tag));
        }

        /// <summary>
        /// 摆 <paramref name="frame"/>,位置以 <paramref name="anchor"/> 的像素块落在 (<paramref name="blockX"/>, <paramref name="blockY"/>) 为准:
        /// 两个帧共用同一个原点,像素块位置各加各的偏移(裁出来的液体帧、灰图标就是这样对齐的)。
        /// </summary>
        private static void PlaceFrom(List<SceneSprite> into, ref int order, SpriteFrame frame, SpriteFrame anchor, int blockX, int blockY, string tag)
        {
            int originX = blockX - anchor.OffsetX;
            int originY = blockY - anchor.OffsetY;
            into.Add(OverlayArt.Place(frame, originX + frame.OffsetX, originY + frame.OffsetY, order++, tag));
        }

        private SpriteFrame Frame(string image, int index)
        {
            if (image == null || index < 0)
            {
                return null;
            }
            string key = image + "#" + index;
            if (_frames.TryGetValue(key, out SpriteFrame cached))
            {
                return cached;
            }
            _sprites.TryGetFrame(image, index, out SpriteFrame sprite);
            _frames[key] = sprite;
            return sprite;
        }

        /// <summary>技能图标:.skl 里写的是 "Character/…/SkillIcon.img",图包里的名字是前面加 sprite/ 再全小写。</summary>
        private SpriteFrame IconFrame(string image, int index)
        {
            return image == null ? null : Frame("sprite/" + image.ToLowerInvariant(), index);
        }

        /// <summary>从 <paramref name="source"/> 里裁一块;新帧的偏移 = 原偏移加上裁掉的行与列,画布不变。</summary>
        private static SpriteFrame Crop(SpriteFrame source, int x, int y, int width, int height)
        {
            var rgba = new byte[width * height * 4];
            for (int row = 0; row < height; row++)
            {
                Buffer.BlockCopy(source.Rgba, ((y + row) * source.Width + x) * 4, rgba, row * width * 4, width * 4);
            }
            return new SpriteFrame(width, height, source.OffsetX + x, source.OffsetY + y, source.CanvasWidth, source.CanvasHeight, rgba);
        }
    }
}
