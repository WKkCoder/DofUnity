using System.Text;
using Dnf.Data;
using Dnf.Data.Tests;
using Dnf.Game;
using Dnf.Render;
using Kit = Dnf.Play.Tests.OnlineSettlementGapBKit;

namespace Dnf.Play.Tests;

/// <summary>
/// r18-settleui 联网结算在 Play 层的两个拼装帮助器(<see cref="OnlineSettlementHeroHelpers"/>)的补测(区域 B):
/// 本地评分(<c>ComputeLocalResult</c>)取副本脚本里的标准等级(<c>[basis level]</c>)、按单人队伍计算;
/// 物品名查询器(<c>CreateItemNameResolver</c>)只有资源与文字表都给了才造得出来,查询时按"物品列表 → 物品脚本 → 文字表"取名。
/// 资源包是测试里自己拼的迷你 PVF(合成脚本与合成文字表,落在临时目录,用完删除),不含任何真实数据。
/// </summary>
public sealed class OnlineSettlementGapBGlueTests
{
    /// <summary>
    /// 临时目录里的迷你资源包:Script.pvf(合成字符串表 + 几份令牌脚本)与空的 ImagePacks2。
    /// 副本脚本两份:leveled 写了 <c>[basis level]</c> 37,plain 存在但没写这个标签;
    /// 可叠加物品列表三项:9811 的脚本写了名字键,9812 的脚本没写名字,9813 列了但脚本文件不存在。
    /// </summary>
    private sealed class MiniAssets : IDisposable
    {
        public const string LeveledPath = "dungeon/test/gapbleveled.dgn";
        public const string PlainPath = "dungeon/test/gapbplain.dgn";

        private const byte TagType = (byte)TokenType.Tag;
        private const byte IntType = (byte)TokenType.Int;
        private const byte StrType = (byte)TokenType.String;
        private const byte KindType = (byte)TokenType.StringRefKind;
        private const byte KeyType = (byte)TokenType.StringRefKey;

        private readonly List<string> _pool = new List<string>();
        private readonly List<(string Name, byte[] Data)> _scripts = new List<(string, byte[])>();

        public MiniAssets()
        {
            Root = Path.Combine(Path.GetTempPath(), "dnf-gapb-glue-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Root, "ImagePacks2"));

            Add(LeveledPath, new List<(byte, int)> { (TagType, Intern("[basis level]")), (IntType, 37) });
            Add(PlainPath, new List<(byte, int)> { (TagType, Intern("[name]")), (StrType, Intern("gapb_plain")) });
            Add(ItemCatalog.StackableListPath, new List<(byte, int)>
            {
                (IntType, 9811), (StrType, Intern("gapb/named.stk")),
                (IntType, 9812), (StrType, Intern("gapb/unnamed.stk")),
                (IntType, 9813), (StrType, Intern("gapb/lost.stk")),
            });
            Add("stackable/gapb/named.stk", new List<(byte, int)>
            {
                (TagType, Intern("[name]")), (KindType, StrLibrary.KindStackable), (KeyType, Intern("gapb_item_named")),
            });
            Add("stackable/gapb/unnamed.stk", new List<(byte, int)> { (TagType, Intern("[grade]")), (IntType, 5) });

            var files = new List<TestData.PvfFileSpec>();
            var strings = new List<byte[]>();
            foreach (string s in _pool)
            {
                strings.Add(Encoding.ASCII.GetBytes(s));
            }
            files.Add(new TestData.PvfFileSpec("stringtable.bin", TestData.BuildStringTable(strings), 0));
            foreach ((string name, byte[] data) in _scripts)
            {
                files.Add(new TestData.PvfFileSpec(name, data, 0));
            }
            (byte[] pvf, int _) = TestData.BuildPvf(files, validChecksums: true);
            File.WriteAllBytes(Path.Combine(Root, "Script.pvf"), pvf);
            Assets = DnfAssets.Open(Root);
        }

        public string Root { get; }

        public DnfAssets Assets { get; }

        private int Intern(string text)
        {
            int at = _pool.IndexOf(text);
            if (at < 0)
            {
                _pool.Add(text);
                at = _pool.Count - 1;
            }
            return at;
        }

        private void Add(string path, List<(byte Type, int Value)> tokens)
        {
            _scripts.Add((path, TestData.BuildTokenScript(tokens)));
        }

        public void Dispose()
        {
            Assets.Dispose();
            try
            {
                Directory.Delete(Root, true);
            }
            catch (IOException)
            {
                // 临时目录清理失败不影响测试结论
            }
        }
    }

    /// <summary>内存文字表:可叠加物品种类里 gapb_item_named → "Gapb Named Item"。</summary>
    private static WorldTexts SyntheticTexts()
    {
        var map = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["stackable/stackable.kor.str"] = Encoding.ASCII.GetBytes("gapb_item_named>Gapb Named Item\r\n"),
        };
        return new WorldTexts(new StrLibrary(p => map.TryGetValue(p, out byte[] bytes) ? bytes : null));
    }

    // ---- 本地评分 ----

    /// <summary>
    /// 保护的行为:本地评分的标准等级取副本脚本里 <c>[basis level]</c> 的数(37);脚本存在但没写这个标签时是 0(不是缺省值 1);
    /// 没给路径或路径读不到脚本时才取缺省值 1;不管哪种,队伍人数都按 1 个人算;副本编号、难度、战绩原样带进结果;没有资源包时没有结果。
    /// </summary>
    [Fact]
    public void Local_result_takes_the_dungeon_standard_level_and_scores_for_a_party_of_one()
    {
        using var fx = new MiniAssets();
        var stats = new RunStats(clearMs: 90000, kills: 12, comboKills: 7, maxCombo: 9, hitsTaken: 2, revivesUsed: 1);

        // 脚本里写了 [basis level] 37 → 标准等级 37
        DungeonResult leveled = OnlineSettlementHeroHelpers.ComputeLocalResult(fx.Assets, MiniAssets.LeveledPath, 9701, 2, stats, null);
        Assert.NotNull(leveled);
        Assert.Equal(37, leveled.StandardLevel);
        Assert.Equal(1, leveled.Members);
        Assert.Equal(9701, leveled.DungeonId);
        Assert.Equal(2, leveled.Difficulty);
        Assert.Equal((90000, 12, 7, 9, 2, 1),
            (leveled.Stats.ClearMs, leveled.Stats.Kills, leveled.Stats.ComboKills, leveled.Stats.MaxCombo, leveled.Stats.HitsTaken, leveled.Stats.RevivesUsed));
        Assert.False(leveled.HasRank);           // 资源包里没有评分表
        Assert.Equal(0L, leveled.ExpBase);       // 没给主角进度:不算经验

        // 脚本存在但没写 [basis level] → 0
        DungeonResult plain = OnlineSettlementHeroHelpers.ComputeLocalResult(fx.Assets, MiniAssets.PlainPath, 9702, 0, stats, null);
        Assert.NotNull(plain);
        Assert.Equal(0, plain.StandardLevel);
        Assert.Equal(1, plain.Members);
        Assert.Equal(9702, plain.DungeonId);
        Assert.Equal(0, plain.Difficulty);

        // 没给路径、空路径、路径读不到脚本 → 缺省 1
        foreach (string path in new[] { null, string.Empty, "dungeon/test/gapbabsent.dgn" })
        {
            DungeonResult fallback = OnlineSettlementHeroHelpers.ComputeLocalResult(fx.Assets, path, 9703, 1, stats, null);
            Assert.NotNull(fallback);
            Assert.Equal(1, fallback.StandardLevel);
            Assert.Equal(1, fallback.Members);
            Assert.Equal(9703, fallback.DungeonId);
        }

        // 没有资源包 → 没有结果
        Assert.Null(OnlineSettlementHeroHelpers.ComputeLocalResult(null, MiniAssets.LeveledPath, 9701, 2, stats, null));
    }

    // ---- 物品名查询器 ----

    /// <summary>
    /// 保护的行为:资源包与文字表缺任何一个都造不出查询器(返回 null),都给了才有;
    /// 查询时物品号 ≤ 0 返回 null,查得到的返回文字表里的名字(再次查询结果一致),
    /// 脚本里没写名字、列表里有但脚本缺失、不在列表里的物品都返回 null(不抛异常)。
    /// </summary>
    [Fact]
    public void Item_name_resolver_needs_both_assets_and_texts_and_resolves_names_through_list_script_and_text_table()
    {
        using var fx = new MiniAssets();
        WorldTexts texts = SyntheticTexts();

        Assert.Null(OnlineSettlementHeroHelpers.CreateItemNameResolver(null, texts));
        Assert.Null(OnlineSettlementHeroHelpers.CreateItemNameResolver(fx.Assets, null));
        Assert.Null(OnlineSettlementHeroHelpers.CreateItemNameResolver(null, null));

        Func<int, string> resolve = OnlineSettlementHeroHelpers.CreateItemNameResolver(fx.Assets, texts);
        Assert.NotNull(resolve);
        Assert.Equal("Gapb Named Item", resolve(9811));
        Assert.Null(resolve(9812));      // 脚本里没写名字
        Assert.Null(resolve(9813));      // 列表里有,脚本文件缺失
        Assert.Null(resolve(9999));      // 不在列表里
        Assert.Null(resolve(0));
        Assert.Null(resolve(-4));

        // 再问一遍(走缓存)结果不变
        Assert.Equal("Gapb Named Item", resolve(9811));
        Assert.Null(resolve(9812));
        Assert.Null(resolve(9813));
        Assert.Null(resolve(9999));
    }

    /// <summary>保护的行为:查询器接进结算摘要构造器后,掉落条目的名字来自它(带数量的显示串包含名字);查不到的条目名字为空且标记为未解析。</summary>
    [Fact]
    public void The_resolver_feeds_the_drop_names_of_the_settlement_summary()
    {
        using var fx = new MiniAssets();
        Func<int, string> resolve = OnlineSettlementHeroHelpers.CreateItemNameResolver(fx.Assets, SyntheticTexts());
        Assert.NotNull(resolve);

        OnlineSettlementSummary summary = OnlineSettlementSummaryBuilder.Build(
            Kit.Data(Kit.Reward(
                questItems: new[] { Kit.Quest(9811, 2), Kit.Quest(9812, 1) },
                rewards: new[] { Kit.Slot(2, 0, itemId: 9811, itemData: 3) })),
            null,
            resolve);

        Assert.Equal(3, summary.Drops.Count);
        Assert.Equal((9811, 2, true, "Gapb Named Item"), (summary.Drops[0].ItemId, summary.Drops[0].Count, summary.Drops[0].IsQuestItem, summary.Drops[0].Name));
        Assert.Equal((9812, 1, true), (summary.Drops[1].ItemId, summary.Drops[1].Count, summary.Drops[1].IsQuestItem));
        Assert.Null(summary.Drops[1].Name);
        Assert.False(summary.Drops[1].NameResolved);
        Assert.Equal((9811, 3, false, "Gapb Named Item"), (summary.Drops[2].ItemId, summary.Drops[2].Count, summary.Drops[2].IsQuestItem, summary.Drops[2].Name));
        Assert.Equal("Gapb Named Item x2", summary.Drops[0].DisplayLabel(true));
        Assert.Equal("Gapb Named Item x3", summary.Drops[2].DisplayLabel(true));
    }
}
