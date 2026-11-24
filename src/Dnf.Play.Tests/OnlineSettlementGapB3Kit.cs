using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// r18-settleui 联网结算覆盖层文字补测(区域 B,第三轮)共用的合成辅助:
/// ① "把码点编码进像素"的合成字体集(<see cref="OnlineSettlementGapB3CodecFonts"/>)与对应的解码器 <see cref="Decode"/>,
///    让覆盖层经 <c>ShellPaint.Line</c> 画出来的文字精灵能被读回成字符串——同样长度的错字也会读出不同的字符;
/// ② 极简的合成摘要工厂 <see cref="Summary"/> 与只放评分字母帧的合成图包 <see cref="LetterSprites"/>。
/// 全是合成数据,不含任何原版内容;既有测试里的私有辅助不改动,需要的部分在这里自己写一份。
/// </summary>
internal static class OnlineSettlementGapB3Kit
{
    /// <summary>解码用:一个字形占的列数(横向 2 列各 8 位)、行数、前进量(2 列墨迹 + 1 列空隙)。</summary>
    public const int GlyphColumns = 2;

    public const int GlyphRows = 8;
    public const int GlyphAdvance = 3;

    /// <summary>描边宽度(与 <see cref="GlyphText.OutlinePad"/> 一致:精灵四周各留 1 像素)。</summary>
    public const int Pad = 1;

    /// <summary>装着编码字体集的覆盖层画布;<c>Localize = false</c> 让后备面板的占位英文留在点阵字库里,不去问字体。</summary>
    public static OverlayArt FontArt(out OnlineSettlementGapB3CodecFonts fonts)
    {
        fonts = new OnlineSettlementGapB3CodecFonts();
        return new OverlayArt { Fonts = fonts, Localize = false };
    }

    /// <summary>
    /// 把一张用编码字体画出的文字精灵读回字符串:第 i 个字符的字形占 x = Pad + 3i 起的两列、y = Pad 起的八行,
    /// 第 (列 × 8 + 行) 位为 1 当且仅当该像素是不透明的填色像素(描边是另一种颜色,读不进去)。
    /// 帧宽不是"3 的整数倍 + 2"说明排版与预期不一致,直接断言失败。
    /// </summary>
    public static string Decode(SpriteFrame frame, uint fill)
    {
        Assert.NotNull(frame);
        Assert.Equal(0, (frame.Width - 2 * Pad) % GlyphAdvance);
        int count = (frame.Width - 2 * Pad) / GlyphAdvance;
        var chars = new char[count];
        for (int i = 0; i < count; i++)
        {
            int code = 0;
            for (int col = 0; col < GlyphColumns; col++)
            {
                for (int row = 0; row < GlyphRows; row++)
                {
                    if (IsFill(frame, Pad + i * GlyphAdvance + col, Pad + row, fill))
                    {
                        code |= 1 << (col * 8 + row);
                    }
                }
            }
            chars[i] = (char)code;
        }
        return new string(chars);
    }

    private static bool IsFill(SpriteFrame frame, int x, int y, uint fill)
    {
        if (x < 0 || y < 0 || x >= frame.Width || y >= frame.Height)
        {
            return false;
        }
        int o = (y * frame.Width + x) * 4;
        uint rgb = ((uint)frame.Rgba[o] << 16) | ((uint)frame.Rgba[o + 1] << 8) | frame.Rgba[o + 2];
        return frame.Rgba[o + 3] == 255 && rgb == fill;
    }

    /// <summary>帧里所有完全不透明像素的 0xRRGGBB 颜色集合(文字精灵里就是"填色 + 描边色")。</summary>
    public static HashSet<uint> OpaqueColors(SpriteFrame frame)
    {
        var colors = new HashSet<uint>();
        for (int i = 0; i < frame.Width * frame.Height; i++)
        {
            if (frame.Rgba[i * 4 + 3] == 255)
            {
                colors.Add(((uint)frame.Rgba[i * 4] << 16) | ((uint)frame.Rgba[i * 4 + 1] << 8) | frame.Rgba[i * 4 + 2]);
            }
        }
        return colors;
    }

    /// <summary>一块纯色块第 0 个像素的 0xAARRGGBB(用来核对底色块的颜色)。</summary>
    public static uint FirstPixelArgb(SpriteFrame frame)
    {
        return ((uint)frame.Rgba[3] << 24) | ((uint)frame.Rgba[0] << 16) | ((uint)frame.Rgba[1] << 8) | frame.Rgba[2];
    }

    /// <summary>
    /// 极简合成摘要:只摆评分与通关用时,其余字段全部"缺失"。奖励段"不适用",所以结果行最短是
    /// [评分(没画成图时)] + 用时占位/用时 + 掉落占位 + 说明。
    /// </summary>
    public static OnlineSettlementSummary Summary(
        bool hasRank = false,
        string rankLetter = "S",
        int rankFrame = 2,
        OnlineSettlementField<int> clearTime = default)
    {
        return new OnlineSettlementSummary(
            dungeonId: 9741,
            dungeonPath: "dungeon/test/b3overlay.dgn",
            difficulty: 0,
            roomsEntered: 1,
            isTimedOut: false,
            resultParseState: OnlineSettlementParseState.NotApplicable,
            rewardParseState: OnlineSettlementParseState.NotApplicable,
            hasRank: hasRank,
            rankFrame: rankFrame,
            rankLetter: rankLetter,
            rankSource: hasRank ? SettlementFieldSource.Local : SettlementFieldSource.None,
            serverRankValue: default,
            clearTimeMs: clearTime,
            personalBestTimeMs: default,
            isNewRecord: default,
            isQuestMaze: default,
            kills: default,
            droppedKills: 0,
            baseExp: default,
            rankBonusExp: default,
            namedBonusExpLines: null,
            rewardGold: default,
            drops: null,
            isReturnUserParty: default,
            hasDeadUserPenalty: default,
            unknownExpSlot2: default,
            unknownReceiverValue: default,
            unknownPercent: default,
            unknownIsEventCharacParty: default,
            unknownPremiumRewardActiveCount: default,
            diffFields: null,
            confidentExpGrant: 0);
    }

    /// <summary>只含评分字母帧的合成图包(没有结算面板的头/行/脚,所以面板走后备占位)。</summary>
    public sealed class LetterSprites : ISpriteSource
    {
        private readonly Dictionary<(string, int), SpriteFrame> _frames = new Dictionary<(string, int), SpriteFrame>();

        public void Add(string imageName, int frame, SpriteFrame sprite)
        {
            _frames[(imageName, frame)] = sprite;
        }

        public bool TryGetFrame(string imageName, int frame, out SpriteFrame sprite)
        {
            return _frames.TryGetValue((imageName, frame), out sprite);
        }
    }
}

/// <summary>
/// 把码点编码进像素的合成字体集:每个字号都给一个 <see cref="OnlineSettlementGapB3CodecFont"/>,<see cref="Requested"/> 记下被问过的字号。
/// </summary>
internal sealed class OnlineSettlementGapB3CodecFonts : IGlyphFonts
{
    public List<int> Requested { get; } = new List<int>();

    public IGlyphSource ForSize(int pixelSize)
    {
        Requested.Add(pixelSize);
        return new OnlineSettlementGapB3CodecFont(pixelSize);
    }
}

/// <summary>
/// 编码字体:字号 px 的字体 PixelSize = Ascent = LineHeight = px、Descent = 0,所以用它排一行字(不折行)得到的精灵高 = px + 2(从高度能读出字号);
/// 每个码点(16 位)画成 2 列 × 8 行的实心点阵——第 (列 × 8 + 行) 位为 1 的像素是墨迹,前进 3(两列墨迹加一列空隙),
/// 所以精灵宽 = 3 × 字符数 + 2。任何两个不同的字符画出的像素都不同,可以由 <see cref="OnlineSettlementGapB3Kit.Decode"/> 读回。
/// </summary>
internal sealed class OnlineSettlementGapB3CodecFont : IGlyphSource
{
    private readonly int _px;

    public OnlineSettlementGapB3CodecFont(int px)
    {
        _px = px;
    }

    public int PixelSize => _px;

    public int Ascent => _px;

    public int Descent => 0;

    public int LineHeight => _px;

    public bool TryGetGlyph(int codePoint, out GlyphBitmap glyph)
    {
        const int Columns = OnlineSettlementGapB3Kit.GlyphColumns;
        const int Rows = OnlineSettlementGapB3Kit.GlyphRows;
        var alpha = new byte[Columns * Rows];
        for (int bit = 0; bit < Columns * Rows; bit++)
        {
            if (((codePoint >> bit) & 1) != 0)
            {
                alpha[(bit % Rows) * Columns + bit / Rows] = 255;
            }
        }
        // 位图上沿在基线之上 px 像素:落在行盒的最上面;前进 3
        glyph = new GlyphBitmap(Columns, Rows, 0, _px, OnlineSettlementGapB3Kit.GlyphAdvance, alpha);
        return true;
    }
}
