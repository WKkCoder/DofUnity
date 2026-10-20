using System;
using System.Collections.Generic;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>屏幕上一个矩形(800×600 画布坐标,左上角起,右、下边不含),点击测试用。</summary>
    public readonly struct HitRect
    {
        public int X { get; }
        public int Y { get; }
        public int Width { get; }
        public int Height { get; }

        public HitRect(int x, int y, int width, int height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        public bool Contains(int px, int py)
        {
            return px >= X && px < X + Width && py >= Y && py < Y + Height;
        }
    }

    /// <summary>
    /// 副本选择界面上一张卡片(世界地图上的一个副本入口)的显示数据(证据 E-094):入口标记的位置与气泡样式取自世界地图的界面脚本
    /// (.ui 里的 [balloon] 控件),卡片的画取自副本定义(.dgn 的 [worldmap pattern info]),最低等级取自 [minimum required level]。
    /// </summary>
    public sealed class DungeonCard
    {
        /// <summary>dungeon/dungeon.lst 里的副本编号。</summary>
        public int DungeonId { get; }

        /// <summary>[balloon] 控件的位置:入口标记的原点。</summary>
        public int MarkerX { get; }

        public int MarkerY { get; }

        /// <summary>[balloon] 的气泡样式号(气泡图的帧号 = <see cref="WorldMapArt.BubbleFrameBase"/> + 样式号)。</summary>
        public int BubbleStyle { get; }

        /// <summary>卡片的画所在的图包名与帧号;没有时为 null(画占位卡片)。</summary>
        public string SlotImage { get; }

        public int SlotFrame { get; }

        public int MinimumLevel { get; }

        public DungeonCard(int dungeonId, int markerX, int markerY, int bubbleStyle, string slotImage, int slotFrame, int minimumLevel)
        {
            DungeonId = dungeonId;
            MarkerX = markerX;
            MarkerY = markerY;
            BubbleStyle = bubbleStyle;
            SlotImage = slotImage;
            SlotFrame = slotFrame;
            MinimumLevel = minimumLevel;
        }
    }

    /// <summary>一张世界地图的画面数据:要画的底图、地图名(占位字库画得出来的名字)、取图用的图包。</summary>
    public sealed class WorldMapScene
    {
        public ISpriteSource Sprites { get; }

        /// <summary>底图的图包名(例如 sprite/worldmap/lorien.img)与帧号;没有时画纯色底。</summary>
        public string MapImage { get; }

        public int MapFrame { get; }

        /// <summary>地图名:原版画在右下角的难度条上的是中文字符串,这里用占位字库画路径主干的大写。</summary>
        public string RegionName { get; }

        public WorldMapScene(ISpriteSource sprites, string mapImage, int mapFrame, string regionName)
        {
            Sprites = sprites ?? throw new ArgumentNullException(nameof(sprites));
            MapImage = mapImage;
            MapFrame = mapFrame;
            RegionName = regionName ?? string.Empty;
        }
    }

    /// <summary>副本选择界面右下角的按钮。</summary>
    public enum WorldMapButton
    {
        None = 0,

        /// <summary>快速组队(要组队系统,单机里没有,点了没反应)。</summary>
        QuickParty = 1,

        /// <summary>深渊派对(灰的,用不了)。</summary>
        Abyss = 2,

        /// <summary>返回城镇。</summary>
        Town = 3,
    }

    /// <summary>
    /// 副本选择界面(世界地图)的画法(证据 E-094,基线 S05):整屏底图上摆着每个副本的入口标记与卡片,选中的卡片大、带边框,
    /// 没选中的缩成 0.7 倍;上下边有装饰条,底部有按键提示(两条横幅轮流出现)、地图名的难度条与三个按钮。
    /// 所有图都来自用户自备的图包(<c>sprite/worldmap/selectdungeon02.img</c>、<c>sprite/worldmap/selectdungeonslot/*.img</c>、
    /// <c>sprite/interface/windowcommon.img</c> 等),位置是拿基线帧用 <c>dnf locate</c> / <c>dnf fit-sprite</c> 量出来的(整屏合成与基线逐像素对过,
    /// 除文字外差异在重采样的噪声量级)。副本名、按键提示、横幅文字都画在图里;图里没有的字(等级、按钮文字、地图名)是中文系统字体,
    /// 这里用自带的像素字(<see cref="OverlayArt"/>)写英文占位,位置与大小是近似。
    /// 取不到的帧就不画(合成数据的测试只放要测的帧)。
    /// </summary>
    public sealed class WorldMapArt
    {
        public const string UiImage = "sprite/worldmap/selectdungeon02.img";
        public const string ButtonImage = "sprite/interface/windowcommon.img";
        public const string PartyImage = "sprite/interface/newstyle/windows/party/party.img";

        // ----- selectdungeon02.img 的帧号
        /// <summary>选中卡片的边框(174×79)。</summary>
        public const int CardBorderFrame = 0;

        /// <summary>气泡图 30 到 47 共 18 张(尖角朝向不同),[balloon] 的样式号加 30 是帧号。</summary>
        public const int BubbleFrameBase = 30;

        /// <summary>只量过样式 13 的位置;别的样式暂时按它的几何画(见 E-094 的"没做"一节)。</summary>
        public const int MeasuredBubbleStyle = 13;

        public const int ProperChannelBannerFrame = 64;
        public const int KeyGuideBannerFrame = 76;
        public const int MarkerFrame = 77;
        public const int TopBottomBannerFrame = 79;
        public const int MapSelectBannerFrame = 80;
        public const int DifficultyBarFrame = 90;

        /// <summary>卡片右上角的小徽章(含义没确认,每张卡片都画一个)。</summary>
        public const int BadgeFrame = 106;

        // ----- windowcommon.img 与 party.img 的帧号
        public const int ButtonNormalFrame = 66;
        public const int ButtonDisabledFrame = 69;
        public const int PartyIconFrame = 23;

        // ----- 位置(800×600 画布;各帧自带的偏移在摆放时再加上)
        /// <summary>气泡的原点 = 标记原点加这个偏移。</summary>
        public const int BubbleOffsetX = -36;

        public const int BubbleOffsetY = -77;

        /// <summary>选中卡片的画、边框、徽章相对气泡原点的位置。</summary>
        public const int SelectedArtX = -27;

        public const int SelectedArtY = -20;
        public const int SelectedBorderX = -30;
        public const int SelectedBorderY = -23;
        public const int SelectedBadgeX = 120;
        public const int SelectedBadgeY = -40;

        /// <summary>没选中的卡片:画与徽章缩到 0.7 倍,画的左上角就在气泡原点,徽章在它的右上方。</summary>
        public const double UnselectedScale = 0.7;

        public const int UnselectedBadgeX = 100;
        public const int UnselectedBadgeY = -17;

        /// <summary>卡片画的像素尺寸(168×73);没有画时占位卡片也用这个尺寸。</summary>
        public const int CardWidth = 168;

        public const int CardHeight = 73;

        /// <summary>选中卡片上等级文字的左上角,相对卡片画的左上角(近似)。</summary>
        public const int LevelTextX = 6;

        public const int LevelTextY = 26;

        public const int KeyGuideX = 200;
        public const int ChannelBannerX = 0;
        public const int BottomBannerY = 572;
        public const int MapSelectBannerX = 0;
        public const int MapSelectBannerY = 250;

        public const int DifficultyBarX = 604;
        public const int DifficultyBarY = 476;
        public const int PartyIconX = 745;
        public const int PartyIconY = 479;

        /// <summary>地图名文字的右端(右对齐);放不下时挪到图标左边。</summary>
        public const int RegionTextRight = 797;

        public const int RegionTextY = 482;

        public const int QuickPartyButtonX = 700;
        public const int QuickPartyButtonY = 500;
        public const int TownButtonX = 700;
        public const int TownButtonY = 541;
        public const int AbyssButtonX = 600;
        public const int AbyssButtonY = 541;
        public const int ButtonWidth = 97;
        public const int ButtonHeight = 23;

        // ----- 时间线(毫秒;从界面打开算起)
        /// <summary>开场的地图名横幅:0.3 秒淡入、停 0.7 秒、0.3 秒淡出;这段时间里卡片还没出现。</summary>
        public const long IntroFadeMs = 300;

        public const long IntroHoldMs = 700;

        /// <summary>入口标记与卡片在横幅退场之后(1.3 秒)淡入,淡入用 0.3 秒。</summary>
        public const long BalloonDelayMs = IntroFadeMs + IntroHoldMs + IntroFadeMs;

        public const long BalloonFadeMs = 300;

        /// <summary>底部横幅的轮换周期:按键提示停 3 秒、1 秒换成"适当频道"横幅、停 3 秒、1 秒换回来。</summary>
        public const long BannerCycleMs = 8000;

        public const long BannerHoldMs = 3000;
        public const long BannerFadeMs = 1000;

        public const uint TextColor = OverlayArt.White;
        public const uint DisabledTextColor = 0x9A9A9Au;
        public const uint FallbackBackdrop = 0x0C1018u;
        public const uint FallbackCardColor = 0x20283Cu;

        private readonly ISpriteSource _sprites;
        private readonly ScaledSpriteCache _scaled = new ScaledSpriteCache();

        public WorldMapArt(ISpriteSource sprites)
        {
            _sprites = sprites ?? throw new ArgumentNullException(nameof(sprites));
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ISpriteSource, WorldMapArt> Shared =
            new System.Runtime.CompilerServices.ConditionalWeakTable<ISpriteSource, WorldMapArt>();

        /// <summary>同一份图包共用一个对象(缩放缓存跟着它,界面反复打开不会重算);<paramref name="sprites"/> 为 null 返回 null。</summary>
        public static WorldMapArt For(ISpriteSource sprites)
        {
            if (sprites == null)
            {
                return null;
            }
            if (!Shared.TryGetValue(sprites, out WorldMapArt art))
            {
                art = new WorldMapArt(sprites);
                Shared.Add(sprites, art);
            }
            return art;
        }

        // ------------------------------------------------------------------ 时间线

        /// <summary>入口标记与卡片的不透明度(0 到 255):开场横幅退场之前看不见,之后 0.3 秒淡入。</summary>
        public static int BalloonAlpha(long ageMs)
        {
            return Ramp(ageMs - BalloonDelayMs, BalloonFadeMs);
        }

        /// <summary>开场的地图名横幅的不透明度:0 → 255(0.3 秒)、停住(0.7 秒)、255 → 0(0.3 秒),之后一直是 0。</summary>
        public static int MapSelectBannerAlpha(long ageMs)
        {
            if (ageMs < 0 || ageMs >= BalloonDelayMs)
            {
                return 0;
            }
            if (ageMs < IntroFadeMs)
            {
                return Ramp(ageMs, IntroFadeMs);
            }
            if (ageMs < IntroFadeMs + IntroHoldMs)
            {
                return 255;
            }
            return 255 - Ramp(ageMs - IntroFadeMs - IntroHoldMs, IntroFadeMs);
        }

        /// <summary>按键提示横幅的不透明度:每 8 秒一个周期,先停 3 秒(255)、1 秒淡出、停 3 秒(0)、1 秒淡入。</summary>
        public static int KeyGuideAlpha(long ageMs)
        {
            long t = ageMs < 0 ? 0 : ageMs % BannerCycleMs;
            if (t < BannerHoldMs)
            {
                return 255;
            }
            if (t < BannerHoldMs + BannerFadeMs)
            {
                return 255 - Ramp(t - BannerHoldMs, BannerFadeMs);
            }
            if (t < BannerHoldMs + BannerFadeMs + BannerHoldMs)
            {
                return 0;
            }
            return Ramp(t - (BannerHoldMs + BannerFadeMs + BannerHoldMs), BannerFadeMs);
        }

        /// <summary>"适当频道"横幅的不透明度:和按键提示横幅此消彼长(两者相加是 255)。</summary>
        public static int ProperChannelAlpha(long ageMs)
        {
            return 255 - KeyGuideAlpha(ageMs);
        }

        /// <summary>从 0 线性升到 255,用 <paramref name="durationMs"/> 毫秒;负数是 0,超过是 255。</summary>
        private static int Ramp(long elapsedMs, long durationMs)
        {
            if (elapsedMs <= 0)
            {
                return 0;
            }
            if (elapsedMs >= durationMs)
            {
                return 255;
            }
            return (int)Math.Round(255.0 * elapsedMs / durationMs, MidpointRounding.AwayFromZero);
        }

        // ------------------------------------------------------------------ 文字

        /// <summary>选中卡片上的等级文字:Lv. 最低等级~最低等级 + 1(三位数,不足补零)。区间的上限规则只有一个样本,是推断。</summary>
        public static string LevelText(int minimumLevel)
        {
            int low = Math.Max(0, minimumLevel);
            return "Lv. " + low.ToString("D3") + "~" + (low + 1).ToString("D3");
        }

        // ------------------------------------------------------------------ 点击测试

        /// <summary>画布坐标落在哪个按钮上;不在任何按钮上返回 <see cref="WorldMapButton.None"/>。</summary>
        public static WorldMapButton HitButton(int x, int y)
        {
            if (new HitRect(TownButtonX, TownButtonY, ButtonWidth, ButtonHeight).Contains(x, y))
            {
                return WorldMapButton.Town;
            }
            if (new HitRect(QuickPartyButtonX, QuickPartyButtonY, ButtonWidth, ButtonHeight).Contains(x, y))
            {
                return WorldMapButton.QuickParty;
            }
            if (new HitRect(AbyssButtonX, AbyssButtonY, ButtonWidth, ButtonHeight).Contains(x, y))
            {
                return WorldMapButton.Abyss;
            }
            return WorldMapButton.None;
        }

        /// <summary>某张卡片占的矩形:选中的是 168×73 的整张画,没选中的是缩到 0.7 倍之后的(118×52),左上角分别按各自的偏移从气泡原点算。</summary>
        public static HitRect CardRect(DungeonCard card, bool selected)
        {
            int bubbleX = card.MarkerX + BubbleOffsetX;
            int bubbleY = card.MarkerY + BubbleOffsetY;
            if (selected)
            {
                return new HitRect(bubbleX + SelectedArtX, bubbleY + SelectedArtY, CardWidth, CardHeight);
            }
            return new HitRect(bubbleX, bubbleY, ScaledSize(CardWidth), ScaledSize(CardHeight));
        }

        private static int ScaledSize(int size)
        {
            return Math.Max(1, (int)Math.Ceiling(size * UnselectedScale - 1e-9));
        }

        /// <summary>
        /// 画布坐标落在哪个条目的卡片上(没有卡片的条目不参与);选中的卡片画在最上面,所以先判它。没有返回 -1。
        /// </summary>
        public static int HitCard(IReadOnlyList<DungeonEntry> entries, int selected, int x, int y)
        {
            if (entries == null)
            {
                return -1;
            }
            if (selected >= 0 && selected < entries.Count && entries[selected].Card != null
                && CardRect(entries[selected].Card, true).Contains(x, y))
            {
                return selected;
            }
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                if (i != selected && entries[i].Card != null && CardRect(entries[i].Card, false).Contains(x, y))
                {
                    return i;
                }
            }
            return -1;
        }

        // ------------------------------------------------------------------ 画

        /// <summary>
        /// 把界面的精灵加进 <paramref name="into"/>(按底图、入口标记与气泡、没选中的卡片、选中的卡片、上下边装饰、底部横幅、难度条、按钮、开场横幅的顺序,
        /// <paramref name="order"/> 接着往下编号)。<paramref name="selected"/> 是选中条目的序号(-1 = 没有),<paramref name="ageMs"/> 是界面打开以来的毫秒数。
        /// </summary>
        public void Build(List<SceneSprite> into, ref int order, OverlayArt font, WorldMapScene scene, IReadOnlyList<DungeonEntry> entries, int selected, long ageMs)
        {
            AddMap(into, ref order, font, scene);

            int alpha = BalloonAlpha(ageMs);
            if (alpha > 0)
            {
                AddMarkersAndBubbles(into, ref order, entries, alpha);
                for (int i = 0; i < entries.Count; i++)
                {
                    if (i != selected && entries[i].Card != null)
                    {
                        AddUnselectedCard(into, ref order, font, entries[i], alpha);
                    }
                }
                if (selected >= 0 && selected < entries.Count && entries[selected].Card != null)
                {
                    AddSelectedCard(into, ref order, font, entries[selected], alpha);
                }
            }

            AddFrame(into, ref order, UiImage, TopBottomBannerFrame, 0, 0, "map-edges", 255);
            AddFrame(into, ref order, UiImage, KeyGuideBannerFrame, KeyGuideX, BottomBannerY, "map-key-guide", KeyGuideAlpha(ageMs));
            AddFrame(into, ref order, UiImage, ProperChannelBannerFrame, ChannelBannerX, BottomBannerY, "map-channel", ProperChannelAlpha(ageMs));

            AddDifficultyBar(into, ref order, font, scene);
            AddButtons(into, ref order, font);
            AddFrame(into, ref order, UiImage, MapSelectBannerFrame, MapSelectBannerX, MapSelectBannerY, "map-intro", MapSelectBannerAlpha(ageMs));
        }

        private void AddMap(List<SceneSprite> into, ref int order, OverlayArt font, WorldMapScene scene)
        {
            if (scene.MapImage != null && _sprites.TryGetFrame(scene.MapImage, scene.MapFrame, out SpriteFrame map))
            {
                into.Add(PlaceAt(map, 0, 0, order++, "map-background", 255));
            }
            else
            {
                into.Add(OverlayArt.Place(font.Solid(DungeonSelectScreen.ScreenWidth, DungeonSelectScreen.ScreenHeight, FallbackBackdrop), 0, 0, order++, "map-backdrop"));
            }
        }

        private void AddMarkersAndBubbles(List<SceneSprite> into, ref int order, IReadOnlyList<DungeonEntry> entries, int alpha)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                DungeonCard card = entries[i].Card;
                if (card == null)
                {
                    continue;
                }
                // 只量过样式 13 的位置;别的样式的气泡图换上去,几何还按它的画(限制,见 E-094)
                AddFrame(into, ref order, UiImage, BubbleFrameBase + card.BubbleStyle, card.MarkerX + BubbleOffsetX, card.MarkerY + BubbleOffsetY, "map-bubble", alpha);
            }
            for (int i = 0; i < entries.Count; i++)
            {
                DungeonCard card = entries[i].Card;
                if (card != null)
                {
                    AddFrame(into, ref order, UiImage, MarkerFrame, card.MarkerX, card.MarkerY, "map-marker", alpha);
                }
            }
        }

        private void AddUnselectedCard(List<SceneSprite> into, ref int order, OverlayArt font, DungeonEntry entry, int alpha)
        {
            DungeonCard card = entry.Card;
            int bubbleX = card.MarkerX + BubbleOffsetX;
            int bubbleY = card.MarkerY + BubbleOffsetY;
            if (card.SlotImage != null && _sprites.TryGetFrame(card.SlotImage, card.SlotFrame, out SpriteFrame art))
            {
                SpriteFrame small = _scaled.Get(art, UnselectedScale);
                into.Add(PlaceAt(small, bubbleX, bubbleY, order++, "map-card", alpha));
            }
            else
            {
                AddFallbackCard(into, ref order, font, entry, bubbleX, bubbleY, UnselectedScale, alpha, false);
            }
            if (_sprites.TryGetFrame(UiImage, BadgeFrame, out SpriteFrame badge))
            {
                into.Add(PlaceAt(_scaled.Get(badge, UnselectedScale), bubbleX + UnselectedBadgeX, bubbleY + UnselectedBadgeY, order++, "map-badge", alpha));
            }
        }

        private void AddSelectedCard(List<SceneSprite> into, ref int order, OverlayArt font, DungeonEntry entry, int alpha)
        {
            DungeonCard card = entry.Card;
            int bubbleX = card.MarkerX + BubbleOffsetX;
            int bubbleY = card.MarkerY + BubbleOffsetY;
            int artX = bubbleX + SelectedArtX;
            int artY = bubbleY + SelectedArtY;
            if (card.SlotImage != null && _sprites.TryGetFrame(card.SlotImage, card.SlotFrame, out SpriteFrame art))
            {
                into.Add(PlaceAt(art, artX, artY, order++, "map-card-selected", alpha));
            }
            else
            {
                AddFallbackCard(into, ref order, font, entry, artX, artY, 1.0, alpha, true);
            }
            AddFrame(into, ref order, UiImage, CardBorderFrame, bubbleX + SelectedBorderX, bubbleY + SelectedBorderY, "map-card-border", alpha);
            AddFrame(into, ref order, UiImage, BadgeFrame, bubbleX + SelectedBadgeX, bubbleY + SelectedBadgeY, "map-badge-selected", alpha);
            SpriteFrame level = font.Text(LevelText(card.MinimumLevel), 1, TextColor);
            into.Add(OverlayArt.Place(level, artX + LevelTextX, artY + LevelTextY, order++, "map-level", alpha));
        }

        /// <summary>取不到卡片的画时的占位:纯色块 + 副本名(像素字)。</summary>
        private static void AddFallbackCard(List<SceneSprite> into, ref int order, OverlayArt font, DungeonEntry entry, int x, int y, double scale, int alpha, bool selected)
        {
            int w = Math.Max(1, (int)Math.Ceiling(CardWidth * scale - 1e-9));
            int h = Math.Max(1, (int)Math.Ceiling(CardHeight * scale - 1e-9));
            into.Add(OverlayArt.Place(font.Solid(w, h, FallbackCardColor), x, y, order++, selected ? "map-card-selected" : "map-card", alpha));
            SpriteFrame name = font.Text(entry.Name, selected ? 2 : 1, TextColor);
            if (name != null)
            {
                into.Add(OverlayArt.Place(name, x + 6, y + (h - name.Height) / 2, order++, "map-card-name", alpha));
            }
        }

        private void AddDifficultyBar(List<SceneSprite> into, ref int order, OverlayArt font, WorldMapScene scene)
        {
            AddFrame(into, ref order, UiImage, DifficultyBarFrame, DifficultyBarX, DifficultyBarY, "map-difficulty-bar", 255);
            if (_sprites.TryGetFrame(PartyImage, PartyIconFrame, out SpriteFrame icon))
            {
                into.Add(PlaceAt(icon, PartyIconX, PartyIconY, order++, "map-party-icon", 255));
            }
            SpriteFrame name = font.Text(scene.RegionName, 1, TextColor);
            if (name != null)
            {
                int x = RegionTextRight - name.Width;
                if (x < PartyIconX + 17)
                {
                    x = PartyIconX - 3 - name.Width;       // 名字太长:挪到图标左边
                }
                into.Add(OverlayArt.Place(name, x, RegionTextY, order++, "map-region-name", 255));
            }
        }

        private void AddButtons(List<SceneSprite> into, ref int order, OverlayArt font)
        {
            AddButton(into, ref order, font, QuickPartyButtonX, QuickPartyButtonY, ButtonNormalFrame, "QUICK PARTY", TextColor);
            AddButton(into, ref order, font, TownButtonX, TownButtonY, ButtonNormalFrame, "TOWN", TextColor);
            AddButton(into, ref order, font, AbyssButtonX, AbyssButtonY, ButtonDisabledFrame, "ABYSS PARTY", DisabledTextColor);
        }

        private void AddButton(List<SceneSprite> into, ref int order, OverlayArt font, int x, int y, int frame, string caption, uint color)
        {
            if (_sprites.TryGetFrame(ButtonImage, frame, out SpriteFrame button))
            {
                into.Add(PlaceAt(button, x, y, order++, "map-button", 255));
            }
            else
            {
                into.Add(OverlayArt.Place(font.Solid(ButtonWidth, ButtonHeight, FallbackCardColor), x, y, order++, "map-button"));
            }
            SpriteFrame text = font.Text(caption, 1, color);
            into.Add(OverlayArt.Place(text, x + (ButtonWidth - text.Width) / 2, y + (ButtonHeight - text.Height) / 2, order++, "map-button-text", 255));
        }

        private void AddFrame(List<SceneSprite> into, ref int order, string image, int frame, int x, int y, string tag, int alpha)
        {
            if (alpha > 0 && _sprites.TryGetFrame(image, frame, out SpriteFrame sprite))
            {
                into.Add(PlaceAt(sprite, x, y, order++, tag, alpha));
            }
        }

        /// <summary>按控件原点摆一张图:屏幕左上角 = 原点 + 帧自带的偏移。</summary>
        private static SceneSprite PlaceAt(SpriteFrame frame, int x, int y, int order, string tag, int alpha)
        {
            return OverlayArt.Place(frame, x + frame.OffsetX, y + frame.OffsetY, order, tag, alpha);
        }
    }
}
