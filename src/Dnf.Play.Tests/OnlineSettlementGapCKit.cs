using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// 区域 C(联网结算画面构建器 <see cref="OnlineSettlementOverlay"/>)补测试共用的夹具,只给 OnlineSettlementGapC* 系列测试用:
/// 合成图包(结算菜单四行与按钮文字、评分字母)、"随字号与字符变化"的合成字体、结算摘要的逐字段构造器、几个精灵查询小工具。
/// 全部是合成数据,不含任何真实素材;既有测试文件里的辅助类原样不动,需要的部分在这里抄了一份。
/// </summary>
internal static class OnlineSettlementGapCKit
{
    /// <summary>评分字母帧的偏移(故意不为 0,好验证摆放时偏移被加上)。</summary>
    public const int LetterOffsetX = 3;

    public const int LetterOffsetY = 5;

    internal sealed class FakeSprites : ISpriteSource
    {
        public Dictionary<(string, int), SpriteFrame> Frames { get; } = new Dictionary<(string, int), SpriteFrame>();

        public bool TryGetFrame(string imageName, int frame, out SpriteFrame sprite) =>
            Frames.TryGetValue((imageName, frame), out sprite!);
    }

    /// <summary>整块同色的色块帧:蓝色通道 = <paramref name="tag"/>(用来认出是哪一帧),不透明。</summary>
    public static SpriteFrame Coded(int width, int height, byte tag, int offsetX = 0, int offsetY = 0)
    {
        var rgba = new byte[width * height * 4];
        for (int i = 0; i < width * height; i++)
        {
            rgba[i * 4 + 2] = tag;
            rgba[i * 4 + 3] = 255;
        }
        return new SpriteFrame(width, height, offsetX, offsetY, width, height, rgba);
    }

    /// <summary>
    /// 合成图包:结算面板的头、行、脚与 16 张按钮文字图(蓝色通道 = 100 + 帧号),
    /// 以及九张评分字母(蓝色通道 = 200 + 帧号,偏移 (<see cref="LetterOffsetX"/>, <see cref="LetterOffsetY"/>))。
    /// </summary>
    public static FakeSprites PanelSprites(bool withPanel = true, bool withRankLetters = true)
    {
        var f = new FakeSprites();
        if (withPanel)
        {
            f.Frames[(SettlementArt.Image, SettlementArt.HeaderFrame)] = Coded(268, 54, 1, 1, 0);
            f.Frames[(SettlementArt.Image, SettlementArt.RowFrame)] = Coded(270, 30, 2);
            f.Frames[(SettlementArt.Image, SettlementArt.FooterFrame)] = Coded(268, 16, 3, 1, 0);
            foreach (int first in new[] { SettlementArt.RetryLabelFrame, SettlementArt.SelectLabelFrame, SettlementArt.TownLabelFrame, SettlementArt.PartyLabelFrame })
            {
                for (int state = 0; state < 4; state++)
                {
                    f.Frames[(SettlementArt.Image, first + state)] = Coded(66, 19, (byte)(100 + first + state), 90, 2);
                }
            }
        }
        if (withRankLetters)
        {
            for (int i = 0; i < ResultArt.FrameCount; i++)
            {
                f.Frames[(ResultArt.RankLetterImage, i)] = Coded(48, 48, (byte)(200 + i), LetterOffsetX, LetterOffsetY);
            }
        }
        return f;
    }

    /// <summary>
    /// 随字号与字符变化的合成字体集:字号 px 的字体 PixelSize = px、Ascent = px、Descent = 0、LineHeight = px;
    /// 数字 d 是 (2 + d) 宽、px 高的实心块,前进 = 2 + d;其余窄字是 2 × px 的实心块、前进 3;
    /// 全角字(<see cref="GlyphText.IsWide"/>,含省略号)是 3 × px 的实心块、前进 4;空格没有墨迹、前进 2。
    /// 用它排一行字(不折行)得到的帧:宽 = 各字前进之和 + 2(描边两边各 1),高 = px + 2。
    /// 所以从精灵的高度能读出请求的字号,从宽度能读出文字内容(连数字取值都读得出)。<see cref="Requested"/> 记下被问过的字号。
    /// </summary>
    internal sealed class ProbeFonts : IGlyphFonts
    {
        public List<int> Requested { get; } = new List<int>();

        public IGlyphSource ForSize(int pixelSize)
        {
            Requested.Add(pixelSize);
            return new ProbeFont(pixelSize);
        }
    }

    internal sealed class ProbeFont : IGlyphSource
    {
        private readonly int _px;

        public ProbeFont(int px)
        {
            _px = px;
        }

        public int PixelSize => _px;

        public int Ascent => _px;

        public int Descent => 0;

        public int LineHeight => _px;

        public bool TryGetGlyph(int codePoint, out GlyphBitmap glyph)
        {
            if (codePoint == ' ')
            {
                glyph = GlyphBitmap.Blank(2);
                return true;
            }
            int width;
            int advance;
            if (codePoint >= '0' && codePoint <= '9')
            {
                width = 2 + (codePoint - '0');
                advance = width;
            }
            else if (GlyphText.IsWide(codePoint))
            {
                width = 3;
                advance = 4;
            }
            else
            {
                width = 2;
                advance = 3;
            }
            var alpha = new byte[width * _px];
            Array.Fill(alpha, (byte)255);
            glyph = new GlyphBitmap(width, _px, 0, _px, advance, alpha);
            return true;
        }
    }

    /// <summary>帧里所有完全不透明像素的 0xRRGGBB 颜色集合(用来看一段文字的填色、一块纯色的底色)。</summary>
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

    /// <summary>精灵列表里按标签挑出的子列表(保持原来的先后)。</summary>
    public static List<SceneSprite> Tagged(IEnumerable<SceneSprite> sprites, string tag)
    {
        return sprites.Where(s => s.AnimationPath == tag).ToList();
    }

    /// <summary>
    /// 结算摘要的逐字段构造器:每个字段默认"缺失"(<see cref="OnlineSettlementField{T}.Missing"/>),测试按需覆盖;
    /// 这样能直接造出"值有但 ShouldDisplay 为假"(把握程度 Unknown)或"ShouldDisplay 为真但值为 0"这类走构造器路径造不出的组合。
    /// </summary>
    internal sealed class SummarySpec
    {
        public int Difficulty { get; set; }
        public bool IsTimedOut { get; set; }
        public OnlineSettlementParseState ResultParse { get; set; } = OnlineSettlementParseState.NotApplicable;
        public OnlineSettlementParseState RewardParse { get; set; } = OnlineSettlementParseState.NotApplicable;
        public bool HasRank { get; set; }
        public int RankFrame { get; set; } = ResultArt.FrameCount - 1;
        public string RankLetter { get; set; } = "F";
        public OnlineSettlementField<int> ClearTime { get; set; } = OnlineSettlementField<int>.Missing();
        public OnlineSettlementField<int> PersonalBest { get; set; } = OnlineSettlementField<int>.Missing();
        public OnlineSettlementField<bool> IsNewRecord { get; set; } = OnlineSettlementField<bool>.Missing();
        public OnlineSettlementField<int> Kills { get; set; } = OnlineSettlementField<int>.Missing();
        public int DroppedKills { get; set; }
        public OnlineSettlementField<int> BaseExp { get; set; } = OnlineSettlementField<int>.Missing();
        public OnlineSettlementField<int> RankBonusExp { get; set; } = OnlineSettlementField<int>.Missing();
        public IReadOnlyList<OnlineSettlementExpLine> NamedBonus { get; set; } = Array.Empty<OnlineSettlementExpLine>();
        public OnlineSettlementField<int> RewardGold { get; set; } = OnlineSettlementField<int>.Missing();
        public IReadOnlyList<OnlineSettlementDropItem> Drops { get; set; } = Array.Empty<OnlineSettlementDropItem>();
        public OnlineSettlementField<bool> ReturnUser { get; set; } = OnlineSettlementField<bool>.Missing();
        public OnlineSettlementField<bool> DeadPenalty { get; set; } = OnlineSettlementField<bool>.Missing();

        public OnlineSettlementSummary Build()
        {
            return new OnlineSettlementSummary(
                dungeonId: 9101,
                dungeonPath: "dungeon/test/synth.dgn",
                difficulty: Difficulty,
                roomsEntered: 1,
                isTimedOut: IsTimedOut,
                resultParseState: ResultParse,
                rewardParseState: RewardParse,
                hasRank: HasRank,
                rankFrame: RankFrame,
                rankLetter: RankLetter,
                rankSource: HasRank ? SettlementFieldSource.Local : SettlementFieldSource.None,
                serverRankValue: OnlineSettlementField<byte>.Missing(),
                clearTimeMs: ClearTime,
                personalBestTimeMs: PersonalBest,
                isNewRecord: IsNewRecord,
                isQuestMaze: OnlineSettlementField<bool>.Missing(),
                kills: Kills,
                droppedKills: DroppedKills,
                baseExp: BaseExp,
                rankBonusExp: RankBonusExp,
                namedBonusExpLines: NamedBonus,
                rewardGold: RewardGold,
                drops: Drops,
                isReturnUserParty: ReturnUser,
                hasDeadUserPenalty: DeadPenalty,
                unknownExpSlot2: OnlineSettlementField<int>.Missing(),
                unknownReceiverValue: OnlineSettlementField<int>.Missing(),
                unknownPercent: OnlineSettlementField<int>.Missing(),
                unknownIsEventCharacParty: OnlineSettlementField<bool>.Missing(),
                unknownPremiumRewardActiveCount: OnlineSettlementField<int>.Missing(),
                diffFields: Array.Empty<string>(),
                confidentExpGrant: 0);
        }
    }

    /// <summary>什么字段都没有的摘要(战果行只剩"通关用时 --:--"、"掉落物品 --"与固定的未同步提示三行)。</summary>
    public static OnlineSettlementSummary BareSummary() => new SummarySpec().Build();

    /// <summary>
    /// 一份"够用"的摘要:通关用时 65000 毫秒(01:05)、基础经验 1800、奖励解析成功且没有掉落;没有评分字母。
    /// 无字体时战果行共 4 条:CLEAR TIME 01:05 / CLEAR EXP +1800 / DROPS: NONE / NOTE: ITEMS/GOLD UNSYNCED。
    /// </summary>
    public static OnlineSettlementSummary SmallSummary(bool hasRank = false, int rankFrame = 2, string rankLetter = "A")
    {
        var spec = new SummarySpec
        {
            ClearTime = OnlineSettlementField<int>.Observed(65000, SettlementFieldSource.Local),
            BaseExp = OnlineSettlementField<int>.Inferred(1800, SettlementFieldSource.Server),
            RewardParse = OnlineSettlementParseState.Succeeded,
            HasRank = hasRank,
            RankFrame = rankFrame,
            RankLetter = rankLetter,
        };
        return spec.Build();
    }
}
