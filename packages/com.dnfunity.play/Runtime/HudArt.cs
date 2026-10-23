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

        /// <summary>能放(冷却好了、魔法够、主角此刻腾得出手)就画亮图标,否则画灰图标。</summary>
        public bool Usable { get; }

        /// <summary>
        /// 冷却还剩的比例(0 到 1,0 表示没在冷却,证据 E-112):图标上方盖一层半透明的暗色,盖住的行数是
        /// <see cref="HudArt.CooldownRows"/>(28 乘比例,向零取整),冷却越到后面盖得越少,亮着的部分从图标底下一行一行长上来。
        /// </summary>
        public double CooldownFraction { get; }

        /// <summary>技能等级;大于 0 时在图标右上角画 "Lv.N" 小字(证据 E-112),0 或负数不画。</summary>
        public int Level { get; }

        /// <summary>
        /// 小字变灰:主角此刻放不了这个技能(正在挥击、放别的技能、受伤、腾空、魔法不够或已倒下)。
        /// 只是冷却中不算——基线里冷却的技能小字照旧是金色,只被上面那层暗色压暗(证据 E-112)。
        /// </summary>
        public bool LabelGray { get; }

        public HudSkillState(int slot, SkillDef def, bool usable, double cooldownFraction = 0.0, int level = 0, bool labelGray = false)
        {
            Slot = slot;
            Def = def;
            Usable = usable;
            CooldownFraction = cooldownFraction;
            Level = level;
            LabelGray = labelGray;
        }
    }

    /// <summary>一个物品快捷槽此刻的状态(画底栏用,证据 E-120):槽号、图标(图包键与帧号,取自物品脚本的 [icon])、背包里这种物品的件数、冷却还剩的比例。</summary>
    public readonly struct HudItemState
    {
        /// <summary>第几个槽(0 到 5,对应数字键 1 到 6)。</summary>
        public int Slot { get; }

        /// <summary>图标所在的图包键(<c>sprite/item/….img</c>)与帧号;没有图标时只画件数。</summary>
        public string IconKey { get; }

        public int IconFrame { get; }

        /// <summary>背包里这种物品一共几件(右上角的小字);不大于 0 不画。</summary>
        public int Count { get; }

        /// <summary>冷却还剩的比例(0 到 1,0 表示没在冷却),遮罩的画法同技能槽(<see cref="HudArt.CooldownRows"/>)。</summary>
        public double CooldownFraction { get; }

        public HudItemState(int slot, string iconKey, int iconFrame, int count, double cooldownFraction = 0.0)
        {
            Slot = slot;
            IconKey = iconKey;
            IconFrame = iconFrame;
            Count = count;
            CooldownFraction = cooldownFraction;
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
    /// 技能槽(证据 E-112):图标之上是 "Lv.N" 小字(自己的 3×5 点阵,金色加黑边,不是原版字体)、冷却遮罩(图标自上而下盖一层 (8,8,8) 半透明,盖住行数随冷却减少),
    /// 热键字母最后画、不受遮罩影响;右球下方的 SP 框里画剩余技能点(<c>sp.img</c> 的数字,居中)。
    /// 物品快捷槽(证据 E-120):左边 6 个 28×28 的槽(<see cref="HudItemState"/>),图标取物品脚本的 [icon],右上角画件数(自己的点阵字),冷却遮罩同技能槽,
    /// 右下角的数字标签 1 到 6 来自同一张 hud.img 的 #45 条(位置与基线逐像素吻合;件数小字的位置是量出来的近似)。
    /// 没做:疲劳条的数据、状态图标、鼠标悬停与点击、球上的高光。
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

        /// <summary>
        /// SP 数字(证据 E-112):<c>sprite/common/digit/sp/sp.img</c> 的帧 0 是负号,帧 1 到 10 是数字 0 到 9(12×11,"1" 是 8 宽、自带偏移 2);
        /// 基线里一个两位数的两个数字块落在 x = 755 与 765(逐像素 100% 一致)、y = 585,数字之间间隔 10 像素;
        /// 底栏里 SP 框的内部是 x = 739 到 791,中心 765,两位数的整体宽度按 10 像素一个数字算正好居中,所以数字串按"总宽 = 位数 × 10"居中(推断,只有一个两位数的样本)。
        /// </summary>
        public const string SpDigitImage = "sprite/common/digit/sp/sp.img";
        public const int SpDigitFirstFrame = 1;
        public const int SpCenterX = 765;
        public const int SpDigitPitch = 10;
        public const int SpDigitY = 585;

        /// <summary>技能图标是 28×28;冷却遮罩是 (8,8,8)、整体不透明度 128/255(基线里被盖住的像素 = 灰图标 / 2 + 4,逐像素吻合)。</summary>
        public const int SkillIconSize = 28;
        public const uint CooldownShadeRgb = 0x080808u;
        public const int CooldownShadeAlpha = 128;

        /// <summary>
        /// "Lv.N" 小字的颜色(基线里量到的):金色填充加黑边;放不了(忙、没魔法)时填充灰 (132,132,132)、描边 (16,16,16)。
        /// 位置:"Lv." 三个字的填充从图标内 (10, 1) 起,数字串的填充右端落在图标内 x = 26,五行高(第 1 到 5 行),四周一圈描边。
        /// </summary>
        public const uint LabelGoldRgb = 0xFFCE31u;
        public const uint LabelGoldOutlineRgb = 0x000000u;
        public const uint LabelGrayRgb = 0x848484u;
        public const uint LabelGrayOutlineRgb = 0x101010u;
        public const int LabelPrefixX = 10;
        public const int LabelRowY = 1;
        public const int LabelNumberRight = 26;

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

        // ----- 物品快捷槽(1 到 6;证据 E-120):图标 28×28,在技能槽的左边,同一行、同样的间距;数字标签在每个槽的右下角
        /// <summary>物品快捷槽的个数(服务端快捷栏是 6 格)。</summary>
        public const int ItemSlotCount = 6;

        public const int ItemSlotX = 85;
        public const int ItemSlotPitch = 30;
        public const int ItemSlotY = 558;

        /// <summary>数字键标签(1 到 6)取自 <see cref="KeyLabelStripFrame"/> 那条图的最前面:每个数字的起始列与宽度(基线里逐像素吻合,"1" 窄一点)。</summary>
        public static readonly int[] ItemLabelColumns = { 0, 28, 58, 88, 118, 148 };
        public static readonly int[] ItemLabelWidths = { 4, 6, 6, 6, 6, 6 };

        /// <summary>数字标签画在 (<see cref="ItemLabelOriginX"/> + 起始列, <see cref="ItemLabelY"/>)。</summary>
        public const int ItemLabelOriginX = 109;
        public const int ItemLabelY = 579;

        /// <summary>件数小字:填充的右端落在槽内 x = 26(与技能等级小字同一条线),帧的最上一行在槽内 y = 2(基线里的数字占 2 到 8 行,这里的点阵字矮一点,按上沿对齐);白色加黑边。</summary>
        public const int ItemCountTop = 2;
        public const uint CountRgb = 0xFFFFFFu;
        public const uint CountOutlineRgb = 0x000000u;

        private readonly ISpriteSource _sprites;
        private readonly SpriteFrame _bar;
        private readonly SpriteFrame _hpFull;
        private readonly SpriteFrame _mpFull;
        private readonly SpriteFrame[] _labels = new SpriteFrame[SlotCount];
        private readonly SpriteFrame[] _itemLabels = new SpriteFrame[ItemSlotCount];
        private readonly Dictionary<int, SpriteFrame> _hpFills = new Dictionary<int, SpriteFrame>();
        private readonly Dictionary<int, SpriteFrame> _mpFills = new Dictionary<int, SpriteFrame>();
        private readonly Dictionary<int, SpriteFrame> _digits = new Dictionary<int, SpriteFrame>();
        private readonly Dictionary<int, SpriteFrame> _expFills = new Dictionary<int, SpriteFrame>();
        private readonly Dictionary<string, SpriteFrame> _frames = new Dictionary<string, SpriteFrame>(StringComparer.Ordinal);
        private readonly Dictionary<string, SpriteFrame> _labelTexts = new Dictionary<string, SpriteFrame>(StringComparer.Ordinal);
        private readonly Dictionary<int, SpriteFrame> _shades = new Dictionary<int, SpriteFrame>();

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
            for (int i = 0; i < ItemSlotCount; i++)
            {
                _itemLabels[i] = Crop(strip, ItemLabelColumns[i], 0, ItemLabelWidths[i], KeyLabelHeight);
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
        /// 冷却遮罩盖住的行数(证据 E-112):<paramref name="fraction"/>(冷却还剩的比例)不大于 0 为 0,不小于 1 为整个图标的 <see cref="SkillIconSize"/> 行,
        /// 其余是 28 乘比例向零取整(基线里一次冷却过程的 8 个采样点,亮着的行数 = 28 减去它;施放的时刻按 100 毫秒的采样间隔拟合,8 点全部吻合)。
        /// </summary>
        public static int CooldownRows(double fraction)
        {
            if (!(fraction > 0.0))
            {
                return 0;
            }
            if (fraction >= 1.0)
            {
                return SkillIconSize;
            }
            return (int)(SkillIconSize * fraction);
        }

        /// <summary>
        /// 把底栏的全部精灵按从下到上的顺序加进 <paramref name="into"/>(<paramref name="order"/> 一路递增)。
        /// 顺序:底栏 → 血球液体 → 魔法球液体 → 经验条(给了才画)→ 等级牌 → SP 数字(给了才画)→ 菜单图标 → 菜单、商城按钮 → "I" 按钮
        /// → 每个技能槽的图标、"Lv.N" 小字、冷却遮罩 → 热键字母。
        /// </summary>
        public void Build(List<SceneSprite> into, ref int order, int level, int hp, int hpMax, int mp, int mpMax, IReadOnlyList<HudSkillState> skills,
            HudExp? exp = null, int? skillPoints = null, IReadOnlyList<HudItemState> items = null)
        {
            PlaceBlock(into, ref order, _bar, BarX, BarY, "hud-bar");

            AddOrb(into, ref order, _hpFull, _hpFills, OrbRows(hp, hpMax), HpOrbX, "hud-hp");
            AddOrb(into, ref order, _mpFull, _mpFills, OrbRows(mp, mpMax), MpOrbX, "hud-mp");

            if (exp.HasValue)
            {
                AddExp(into, ref order, exp.Value);
            }

            AddLevel(into, ref order, level);
            if (skillPoints.HasValue)
            {
                AddSp(into, ref order, skillPoints.Value);
            }

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
                        int slotX = SkillSlotX + SkillSlotPitch * s.Slot;
                        PlaceFrom(into, ref order, pick, anchor, slotX, SkillSlotY, "hud-skill");
                        AddSkillLevel(into, ref order, s, slotX, SkillSlotY);
                        AddCooldownShade(into, ref order, s.CooldownFraction, slotX, SkillSlotY);
                    }
                }
            }
            if (items != null)
            {
                foreach (HudItemState item in items)
                {
                    AddItem(into, ref order, item);
                }
            }
            for (int i = 0; i < ItemSlotCount; i++)
            {
                into.Add(OverlayArt.Place(_itemLabels[i], ItemLabelOriginX + ItemLabelColumns[i], ItemLabelY, order++, "hud-item-key"));
            }
            for (int i = 0; i < SlotCount; i++)
            {
                into.Add(OverlayArt.Place(_labels[i], KeyLabelX + SkillSlotPitch * i, KeyLabelY, order++, "hud-key"));
            }
        }

        /// <summary>
        /// 一个物品快捷槽(证据 E-120):图标(取物品脚本 [icon] 那一帧,居中摆进 28×28 的槽)、右上角的件数(自己的点阵字,白色加黑边,不是原版字体)、
        /// 冷却遮罩(与技能槽同一套,盖在图标与件数上;数字键标签在遮罩之后画,不被压暗)。图标取不到就只画件数。
        /// </summary>
        private void AddItem(List<SceneSprite> into, ref int order, HudItemState item)
        {
            if (item.Slot < 0 || item.Slot >= ItemSlotCount)
            {
                return;
            }
            int slotX = ItemSlotX + ItemSlotPitch * item.Slot;
            if (Frame(item.IconKey, item.IconFrame) is SpriteFrame icon && icon.Width > 0 && icon.Height > 0)
            {
                into.Add(OverlayArt.Place(icon, slotX + (SkillIconSize - icon.CanvasWidth) / 2 + icon.OffsetX, ItemSlotY + (SkillIconSize - icon.CanvasHeight) / 2 + icon.OffsetY,
                    order++, "hud-item"));
            }
            if (item.Count > 0)
            {
                SpriteFrame count = LabelText(item.Count.ToString(System.Globalization.CultureInfo.InvariantCulture), "c:", CountRgb, CountOutlineRgb);
                into.Add(OverlayArt.Place(count, slotX + LabelNumberRight + 2 - count.Width, ItemSlotY + ItemCountTop, order++, "hud-item-count"));
            }
            AddCooldownShade(into, ref order, item.CooldownFraction, slotX, ItemSlotY, "hud-item-cooldown");
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
        /// SP 数字(证据 E-112):<c>sp.img</c> 的数字帧,每个数字按 <see cref="SpDigitPitch"/> 像素一格,整串居中在 <see cref="SpCenterX"/>;
        /// 数字帧自带偏移("1" 窄一点,偏移 2)要加回去;缺图就不画。
        /// </summary>
        private void AddSp(List<SceneSprite> into, ref int order, int skillPoints)
        {
            string text = (skillPoints < 0 ? 0 : skillPoints).ToString(System.Globalization.CultureInfo.InvariantCulture);
            int left = SpCenterX - SpDigitPitch * text.Length / 2;
            for (int i = 0; i < text.Length; i++)
            {
                if (Frame(SpDigitImage, SpDigitFirstFrame + (text[i] - '0')) is SpriteFrame digit)
                {
                    into.Add(OverlayArt.Place(digit, left + SpDigitPitch * i + digit.OffsetX, SpDigitY + digit.OffsetY, order++, "hud-sp"));
                }
            }
        }

        /// <summary>技能图标右上角的 "Lv.N":"Lv." 三个字的填充从图标内 (10, 1) 起,数字串的填充右端落在图标内 x = 26;帧比填充多一圈描边。</summary>
        private void AddSkillLevel(List<SceneSprite> into, ref int order, HudSkillState s, int slotX, int slotY)
        {
            if (s.Level <= 0)
            {
                return;
            }
            SpriteFrame prefix = LabelText("Lv.", s.LabelGray);
            SpriteFrame number = LabelText(s.Level.ToString(System.Globalization.CultureInfo.InvariantCulture), s.LabelGray);
            into.Add(OverlayArt.Place(prefix, slotX + LabelPrefixX - 1, slotY + LabelRowY - 1, order++, "hud-skill-level"));
            into.Add(OverlayArt.Place(number, slotX + LabelNumberRight - (number.Width - 2), slotY + LabelRowY - 1, order++, "hud-skill-level"));
        }

        /// <summary>
        /// 冷却遮罩:图标自上而下盖 <see cref="CooldownRows"/> 行 (8,8,8)、整体不透明度 <see cref="CooldownShadeAlpha"/>/255 的暗色,
        /// 压在图标与小字上、在热键字母下面(基线里字母不被压暗)。
        /// </summary>
        private void AddCooldownShade(List<SceneSprite> into, ref int order, double fraction, int slotX, int slotY, string tag = "hud-skill-cooldown")
        {
            int rows = CooldownRows(fraction);
            if (rows <= 0)
            {
                return;
            }
            if (!_shades.TryGetValue(rows, out SpriteFrame shade))
            {
                var rgba = new byte[SkillIconSize * rows * 4];
                for (int i = 0; i < SkillIconSize * rows; i++)
                {
                    rgba[i * 4] = (byte)((CooldownShadeRgb >> 16) & 0xFF);
                    rgba[i * 4 + 1] = (byte)((CooldownShadeRgb >> 8) & 0xFF);
                    rgba[i * 4 + 2] = (byte)(CooldownShadeRgb & 0xFF);
                    rgba[i * 4 + 3] = 255;
                }
                shade = new SpriteFrame(SkillIconSize, rows, 0, 0, SkillIconSize, rows, rgba);
                _shades[rows] = shade;
            }
            into.Add(OverlayArt.Place(shade, slotX, slotY, order++, tag, CooldownShadeAlpha));
        }

        /// <summary>小字的点阵高度(行)。</summary>
        public const int LabelGlyphHeight = 5;

        /// <summary>
        /// "Lv.N" 小字用的点阵(我们自己画的,不是原版字体;原版这行字是系统字体渲染的,量到的形状:字高 5 行、笔画 1 像素、字间 1 列):
        /// "L"、"v"、"." 与数字 "1" 取自基线里量到的笔画,其余数字是照常见的 3×5 点阵自己排的(没有基线样本)。
        /// </summary>
        private static readonly Dictionary<char, string[]> LabelGlyphs = new Dictionary<char, string[]>
        {
            ['L'] = new[] { "#..", "#..", "#..", "#..", "###" },
            ['v'] = new[] { "...", "...", "#.#", "#.#", ".#." },
            ['.'] = new[] { ".", ".", ".", ".", "#" },
            ['0'] = new[] { "###", "#.#", "#.#", "#.#", "###" },
            ['1'] = new[] { ".#", "##", ".#", ".#", ".#" },
            ['2'] = new[] { "###", "..#", "###", "#..", "###" },
            ['3'] = new[] { "###", "..#", "###", "..#", "###" },
            ['4'] = new[] { "#.#", "#.#", "###", "..#", "..#" },
            ['5'] = new[] { "###", "#..", "###", "..#", "###" },
            ['6'] = new[] { "###", "#..", "###", "#.#", "###" },
            ['7'] = new[] { "###", "..#", "..#", "..#", "..#" },
            ['8'] = new[] { "###", "#.#", "###", "#.#", "###" },
            ['9'] = new[] { "###", "#.#", "###", "..#", "###" },
        };

        /// <summary>
        /// 排出一段小字(<paramref name="gray"/> 为真用灰色),四周一圈 8 邻域描边;帧的宽 = 填充宽 + 2,高 = <see cref="LabelGlyphHeight"/> + 2。
        /// 字库里没有的字符跳过。同样的参数总是返回同一个对象。
        /// </summary>
        private SpriteFrame LabelText(string text, bool gray)
        {
            return LabelText(text, gray ? "g:" : "n:", gray ? LabelGrayRgb : LabelGoldRgb, gray ? LabelGrayOutlineRgb : LabelGoldOutlineRgb);
        }

        /// <summary>同上,颜色自己给;<paramref name="cachePrefix"/> 区分不同颜色的缓存。</summary>
        private SpriteFrame LabelText(string text, string cachePrefix, uint fillRgb, uint outlineRgb)
        {
            string key = cachePrefix + text;
            if (_labelTexts.TryGetValue(key, out SpriteFrame cached))
            {
                return cached;
            }
            var glyphs = new List<string[]>();
            int fillWidth = 0;
            foreach (char c in text)
            {
                if (LabelGlyphs.TryGetValue(c, out string[] g))
                {
                    glyphs.Add(g);
                    fillWidth += g[0].Length + (glyphs.Count > 1 ? 1 : 0);
                }
            }
            int width = fillWidth + 2;
            int height = LabelGlyphHeight + 2;
            var fill = new bool[fillWidth, LabelGlyphHeight];
            int left = 0;
            foreach (string[] g in glyphs)
            {
                for (int y = 0; y < LabelGlyphHeight; y++)
                {
                    for (int x = 0; x < g[y].Length; x++)
                    {
                        fill[left + x, y] = g[y][x] == '#';
                    }
                }
                left += g[0].Length + 1;
            }
            var rgba = new byte[width * height * 4];
            for (int pass = 0; pass < 2; pass++)
            {
                for (int y = 0; y < LabelGlyphHeight; y++)
                {
                    for (int x = 0; x < fillWidth; x++)
                    {
                        if (!fill[x, y])
                        {
                            continue;
                        }
                        // 第一遍铺描边(8 邻域),第二遍铺填充盖在上面
                        int from = pass == 0 ? -1 : 0;
                        int to = pass == 0 ? 1 : 0;
                        for (int dy = from; dy <= to; dy++)
                        {
                            for (int dx = from; dx <= to; dx++)
                            {
                                int o = ((y + 1 + dy) * width + x + 1 + dx) * 4;
                                uint rgb = pass == 0 ? outlineRgb : fillRgb;
                                rgba[o] = (byte)((rgb >> 16) & 0xFF);
                                rgba[o + 1] = (byte)((rgb >> 8) & 0xFF);
                                rgba[o + 2] = (byte)(rgb & 0xFF);
                                rgba[o + 3] = 255;
                            }
                        }
                    }
                }
            }
            var frame = new SpriteFrame(width, height, 0, 0, width, height, rgba);
            _labelTexts[key] = frame;
            return frame;
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
