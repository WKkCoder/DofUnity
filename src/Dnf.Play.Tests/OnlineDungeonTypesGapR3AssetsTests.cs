using System.Text;
using Dnf.Data;
using Dnf.Data.Tests;
using Dnf.Game;
using Dnf.Play;
using Dnf.Render;

namespace Dnf.Play.Tests;

/// <summary>
/// r18-dungeonflow 联网副本选择策略在"有资源包(DnfAssets)"时的第三轮缺口测试(<see cref="OnlineDungeonSelectPolicy.Create"/> 的资源分支):
/// 无卡片条目的副本编号经 dungeon/dungeon.lst 反查、本地数据是否存在由脚本读得到与否决定、显示名取脚本 [name]、
/// "有没有难度 3"由 etc/ultimatedungeonlist.etc 决定(文件缺失时一个都没有)、地区显示名取世界地图脚本 [name],
/// 以及显式委托(编号解析/本地数据/有无难度 3)优先于资源分支。
/// 资源包用测试里自己拼的迷你 PVF(合成脚本与合成文字表,落在临时目录,用完删除),不含任何真实数据。
/// </summary>
public class OnlineDungeonTypesGapR3AssetsTests
{
    private const string AlphaPath = "dungeon/test/alpha.dgn";
    private const string BetaPath = "dungeon/test/beta.dgn";
    private const string GammaPath = "dungeon/test/gamma.dgn";
    private const string MissingPath = "dungeon/test/missing.dgn";
    private const string RegionPath = "worldmap/test/region.wld";
    private const string PlainRegionPath = "worldmap/test/regionplain.wld";

    private static KeyValuePair<int, byte> Tier(int dungeonId, byte tier) => new KeyValuePair<int, byte>(dungeonId, tier);

    /// <summary>
    /// 临时目录里的迷你资源包:Script.pvf(含合成的字符串表与几份令牌脚本)与空的 ImagePacks2。
    /// 副本列表:31 → alpha、32 → beta、33 → gamma、34 → missing(列表里有,脚本文件没有);
    /// alpha 的 [name] 写成普通字符串键,beta 写成"种类 + 键",gamma 没有 [name](只有 [summary],里面的字符串若被误当名字就能查到文字);
    /// 世界地图脚本两份:region 的 [name] 写成"种类 + 键"(种类自带),regionplain 写成普通字符串键(文字种类只能靠默认值)。
    /// </summary>
    private sealed class MiniAssets : IDisposable
    {
        private const byte TagType = (byte)TokenType.Tag;
        private const byte IntType = (byte)TokenType.Int;
        private const byte StrType = (byte)TokenType.String;
        private const byte KindType = (byte)TokenType.StringRefKind;
        private const byte KeyType = (byte)TokenType.StringRefKey;

        private readonly List<string> _pool = new List<string>();
        private readonly List<(string Name, byte[] Data)> _scripts = new List<(string, byte[])>();

        public MiniAssets(bool withUltimateList, bool withEmptyNamedScript = false)
        {
            Root = Path.Combine(Path.GetTempPath(), "dnf-gapr3-assets-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Root, "ImagePacks2"));

            Add("dungeon/dungeon.lst", new List<(byte, int)>
            {
                (IntType, 31), (StrType, Intern("Test/Alpha.dgn")),
                (IntType, 32), (StrType, Intern("Test/Beta.dgn")),
                (IntType, 33), (StrType, Intern("Test/Gamma.dgn")),
                (IntType, 34), (StrType, Intern("Test/Missing.dgn")),
            });
            Add(AlphaPath, new List<(byte, int)> { (TagType, Intern("[name]")), (StrType, Intern("name_alpha")) });
            Add(BetaPath, new List<(byte, int)>
            {
                (TagType, Intern("[name]")), (KindType, StrLibrary.KindDungeon), (KeyType, Intern("name_beta")),
            });
            Add(GammaPath, new List<(byte, int)> { (TagType, Intern("[summary]")), (StrType, Intern("name_alpha")) });
            Add(RegionPath, new List<(byte, int)>
            {
                (TagType, Intern("[name]")), (KindType, StrLibrary.KindWorldMap), (KeyType, Intern("region_key")),
            });
            Add(PlainRegionPath, new List<(byte, int)> { (TagType, Intern("[name]")), (StrType, Intern("region_key")) });
            if (withEmptyNamedScript)
            {
                // 退化档案:里面有一份文件名为空串的脚本(带能查到文字的 [name]),用来证明"空路径"这道门是策略自己守的
                Add(string.Empty, new List<(byte, int)> { (TagType, Intern("[name]")), (StrType, Intern("name_alpha")) });
            }
            if (withUltimateList)
            {
                Add(UltimateDungeonList.Path, new List<(byte, int)>
                {
                    (TagType, Intern("[apply ultimate]")), (IntType, 31), (IntType, 33), (TagType, Intern("[/apply ultimate]")),
                });
            }

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

    /// <summary>内存文字表:副本种类里 name_alpha → AlphaName、name_beta → BetaName;世界地图种类里 region_key → RegionName。</summary>
    private static WorldTexts SyntheticTexts()
    {
        var map = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["dungeon/dungeon.kor.str"] = Encoding.ASCII.GetBytes("name_alpha>AlphaName\r\nname_beta>BetaName\r\n"),
            ["worldmap/worldmap.kor.str"] = Encoding.ASCII.GetBytes("region_key>RegionName\r\n"),
        };
        return new WorldTexts(new StrLibrary(p => map.TryGetValue(p, out byte[] bytes) ? bytes : null));
    }

    private static OnlineDungeonPermissionTable SampleTable()
    {
        var table = new OnlineDungeonPermissionTable();
        // 31 alpha:档位 3(在难度 3 名单里);32 beta:档位 3(不在名单里);33 gamma:档位 255(在名单里);34 missing:档位 2
        table.LoadInitial(new[] { Tier(31, 3), Tier(32, 3), Tier(33, 255), Tier(34, 2), Tier(9, 1) }, hasSnapshot: true);
        return table;
    }

    private static DungeonEntry PathOnly(string path) => new DungeonEntry(path, "E");

    // ------------------------------------------------------------------------------------------
    // 编号解析与入口状态
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// 保护:没有卡片编号时,编号由 dungeon.lst 按路径反查(路径不分大小写、反斜杠等价);列表里没有的路径、空路径、空条目为 -1;
    /// 卡片编号 >= 0(含 0)时优先于资源反查,卡片编号为负视为没有;显式编号委托优先于资源反查(但仍排在卡片之后);
    /// 没有资源包时有路径也反查不了(-1),卡片编号照常。
    /// </summary>
    [Fact]
    public void DungeonIdComesFromTheDungeonListByPathUnlessCardOrExplicitResolverTakesOver()
    {
        using var fx = new MiniAssets(withUltimateList: true);
        OnlineDungeonSelectPolicy policy = OnlineDungeonSelectPolicy.Create(SampleTable(), assets: fx.Assets);

        Assert.Equal(31, policy.ResolveDungeonId(PathOnly(AlphaPath)));
        Assert.Equal(32, policy.ResolveDungeonId(PathOnly(BetaPath)));
        Assert.Equal(33, policy.ResolveDungeonId(PathOnly(GammaPath)));
        Assert.Equal(34, policy.ResolveDungeonId(PathOnly(MissingPath)));
        Assert.Equal(31, policy.ResolveDungeonId(PathOnly("Dungeon/Test/ALPHA.dgn"))); // 大小写不敏感
        Assert.Equal(31, policy.ResolveDungeonId(PathOnly("dungeon\\test\\alpha.dgn"))); // 反斜杠等价
        Assert.Equal(31, policy.ResolveDungeonId(PathOnly(AlphaPath))); // 缓存命中也是同一结果
        Assert.Equal(-1, policy.ResolveDungeonId(PathOnly("dungeon/test/delta.dgn"))); // 不在列表里
        Assert.Equal(-1, policy.ResolveDungeonId(PathOnly(string.Empty)));
        Assert.Equal(-1, policy.ResolveDungeonId(null));

        var withCard = new DungeonEntry(AlphaPath, "ALPHA", new DungeonCard(9, 10, 10, 0, null, 0, 1));
        Assert.Equal(9, policy.ResolveDungeonId(withCard)); // 卡片编号优先于资源反查
        var cardZero = new DungeonEntry(AlphaPath, "ZERO", new DungeonCard(0, 10, 10, 0, null, 0, 1));
        Assert.Equal(0, policy.ResolveDungeonId(cardZero)); // 编号 0 是有效编号(下限含 0),不退回资源反查
        var cardNegative = new DungeonEntry(AlphaPath, "NEG", new DungeonCard(-1, 10, 10, 0, null, 0, 1));
        Assert.Equal(31, policy.ResolveDungeonId(cardNegative)); // 卡片编号为负视为没有,退回资源反查

        OnlineDungeonSelectPolicy withResolver = OnlineDungeonSelectPolicy.Create(SampleTable(), assets: fx.Assets, idResolver: _ => 900);
        Assert.Equal(900, withResolver.ResolveDungeonId(PathOnly(AlphaPath))); // 显式委托优先于资源反查
        Assert.Equal(9, withResolver.ResolveDungeonId(withCard)); // 卡片仍排在委托之前
        Assert.Equal(900, withResolver.ResolveDungeonId(PathOnly(string.Empty))); // 委托说了算,不再看路径是否为空

        // 没有资源包(也没有委托)时没法反查:有路径也是 -1,不会去碰资源包
        OnlineDungeonSelectPolicy noAssets = OnlineDungeonSelectPolicy.Create(SampleTable());
        Assert.Equal(-1, noAssets.ResolveDungeonId(PathOnly(AlphaPath)));
        Assert.Equal(-1, noAssets.ResolveDungeonId(PathOnly(string.Empty)));
        Assert.Equal(9, noAssets.ResolveDungeonId(withCard)); // 卡片编号照常
    }

    /// <summary>
    /// 保护:入口状态的判定顺序"编号无效 → 缺本地数据 → 服务端拒绝":本地数据是否存在由脚本读得到与否决定
    /// (列表里有编号但脚本文件缺失 → 缺本地数据;卡片给了编号但脚本缺失同样缺本地数据;空路径视为缺);
    /// 路径不在列表里 → 缺编号;显式本地数据委托优先于脚本读取;服务端拒绝在前两关通过后才轮到(并带上服务端给的拒绝码);
    /// 没有资源包时有路径就当本地数据在、空路径当缺。
    /// </summary>
    [Fact]
    public void EntryStatusUsesScriptPresenceAndTheDungeonListUnlessExplicitDelegatesTakeOver()
    {
        using var fx = new MiniAssets(withUltimateList: true);
        OnlineDungeonPermissionTable table = SampleTable();
        OnlineDungeonSelectPolicy policy = OnlineDungeonSelectPolicy.Create(table, assets: fx.Assets);

        Assert.Equal(DungeonEntryBlockKind.None, policy.EntryStatus(PathOnly(AlphaPath)).Kind);
        Assert.Equal(DungeonEntryBlockKind.None, policy.EntryStatus(PathOnly(BetaPath)).Kind);
        Assert.Equal(DungeonEntryBlockKind.None, policy.EntryStatus(PathOnly(GammaPath)).Kind); // 没有 [name] 也算有本地数据
        Assert.Equal(DungeonEntryBlockKind.None, policy.EntryStatus(PathOnly("Dungeon/Test/ALPHA.dgn")).Kind);
        Assert.Equal(DungeonEntryBlockKind.MissingLocalData, policy.EntryStatus(PathOnly(MissingPath)).Kind); // 编号 34 有,脚本没有
        Assert.Equal(DungeonEntryBlockKind.MissingDungeonId, policy.EntryStatus(PathOnly("dungeon/test/delta.dgn")).Kind);
        Assert.Equal(DungeonEntryBlockKind.MissingDungeonId, policy.EntryStatus(PathOnly(string.Empty)).Kind); // 编号先于本地数据检查
        Assert.Equal(DungeonEntryBlockKind.MissingDungeonId, policy.EntryStatus(null).Kind);

        var cardNoScript = new DungeonEntry("dungeon/test/nope.dgn", "NOPE", new DungeonCard(9, 10, 10, 0, null, 0, 1));
        Assert.Equal(DungeonEntryBlockKind.MissingLocalData, policy.EntryStatus(cardNoScript).Kind);
        var cardEmptyPath = new DungeonEntry(string.Empty, "EMPTY", new DungeonCard(9, 10, 10, 0, null, 0, 1));
        Assert.Equal(DungeonEntryBlockKind.MissingLocalData, policy.EntryStatus(cardEmptyPath).Kind);

        // 服务端拒绝:前两关通过的条目才会显示拒绝;缺本地数据的条目即便被标记拒绝也仍显示缺本地数据
        table.MarkServerRejected(31, 6);
        table.MarkServerRejected(34, 7);
        DungeonEntryStatus rejected = policy.EntryStatus(PathOnly(AlphaPath));
        Assert.Equal(DungeonEntryBlockKind.ServerRejected, rejected.Kind);
        Assert.Equal((byte)6, rejected.Code);
        Assert.Equal(DungeonEntryBlockKind.MissingLocalData, policy.EntryStatus(PathOnly(MissingPath)).Kind);

        // 显式本地数据委托优先于脚本读取:全说"有"时连脚本缺失的条目也放行(拒绝标记仍然生效),全说"没有"时连有脚本的也拦下
        OnlineDungeonSelectPolicy allPresent = OnlineDungeonSelectPolicy.Create(table, assets: fx.Assets, hasLocalData: _ => true);
        Assert.Equal(DungeonEntryBlockKind.ServerRejected, allPresent.EntryStatus(PathOnly(MissingPath)).Kind);
        Assert.Equal(DungeonEntryBlockKind.None, allPresent.EntryStatus(PathOnly(BetaPath)).Kind);
        OnlineDungeonSelectPolicy nonePresent = OnlineDungeonSelectPolicy.Create(table, assets: fx.Assets, hasLocalData: _ => false);
        Assert.Equal(DungeonEntryBlockKind.MissingLocalData, nonePresent.EntryStatus(PathOnly(BetaPath)).Kind);
        Assert.Equal(DungeonEntryBlockKind.MissingDungeonId, nonePresent.EntryStatus(PathOnly("dungeon/test/delta.dgn")).Kind);

        // 没有资源包(也没有委托)时:有路径就当本地数据在,空路径当缺;没有卡片编号时编号反查不了,算缺编号
        OnlineDungeonSelectPolicy noAssets = OnlineDungeonSelectPolicy.Create(table);
        Assert.Equal(DungeonEntryBlockKind.None, noAssets.EntryStatus(cardNoScript).Kind);
        Assert.Equal(DungeonEntryBlockKind.MissingLocalData, noAssets.EntryStatus(cardEmptyPath).Kind);
        Assert.Equal(DungeonEntryBlockKind.MissingDungeonId, noAssets.EntryStatus(PathOnly(AlphaPath)).Kind);
    }

    // ------------------------------------------------------------------------------------------
    // 显示名
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// 保护:副本显示名取该副本脚本里 [name] 的文字(普通字符串键与"种类 + 键"两种写法各一例);脚本没有 [name]
    /// (哪怕别的标签里有能查到文字的字符串)、脚本文件不存在、空路径、空条目都没有名字(null);重复读取结果不变。
    /// 地区显示名取世界地图脚本的 [name];缺文字表、缺资源包、地图路径为空/为空串、地图脚本不存在时都为 null。
    /// </summary>
    [Fact]
    public void DisplayNamesComeFromTheNameTagOfTheScriptsAndAreNullWhenAnyInputIsMissing()
    {
        using var fx = new MiniAssets(withUltimateList: true);
        WorldTexts texts = SyntheticTexts();
        OnlineDungeonSelectPolicy policy = OnlineDungeonSelectPolicy.Create(
            SampleTable(), assets: fx.Assets, texts: texts, worldMapPath: RegionPath);

        Assert.Equal("AlphaName", policy.EntryDisplayName(PathOnly(AlphaPath)));
        Assert.Equal("BetaName", policy.EntryDisplayName(PathOnly(BetaPath)));
        Assert.Equal("AlphaName", policy.EntryDisplayName(PathOnly(AlphaPath))); // 第二次(缓存)结果不变
        Assert.Equal("BetaName", policy.EntryDisplayName(PathOnly("Dungeon/Test/BETA.dgn"))); // 路径不分大小写
        Assert.Null(policy.EntryDisplayName(PathOnly(GammaPath))); // 只有 [summary],没有 [name]
        Assert.Null(policy.EntryDisplayName(PathOnly(MissingPath))); // 脚本不存在
        Assert.Null(policy.EntryDisplayName(PathOnly(string.Empty)));
        Assert.Null(policy.EntryDisplayName(null));
        Assert.Equal("RegionName", policy.RegionDisplayName);

        // 地区名写成普通字符串键时,文字种类取"世界地图"(不是副本):键只登记在世界地图文字表里
        OnlineDungeonSelectPolicy plainRegion = OnlineDungeonSelectPolicy.Create(
            SampleTable(), assets: fx.Assets, texts: texts, worldMapPath: PlainRegionPath);
        Assert.Equal("RegionName", plainRegion.RegionDisplayName);

        // 缺文字表:名字与地区名都取不到
        OnlineDungeonSelectPolicy noTexts = OnlineDungeonSelectPolicy.Create(
            SampleTable(), assets: fx.Assets, texts: null, worldMapPath: RegionPath);
        Assert.Null(noTexts.EntryDisplayName(PathOnly(AlphaPath)));
        Assert.Null(noTexts.RegionDisplayName);

        // 缺资源包(有文字表):同样取不到
        OnlineDungeonSelectPolicy noAssets = OnlineDungeonSelectPolicy.Create(
            SampleTable(), assets: null, texts: texts, worldMapPath: RegionPath);
        Assert.Null(noAssets.EntryDisplayName(PathOnly(AlphaPath)));
        Assert.Null(noAssets.RegionDisplayName);

        // 地图路径为空 / 空串 / 脚本不存在:地区名为 null,但副本名不受影响
        foreach (string worldMapPath in new[] { null, string.Empty, "worldmap/test/nothing.wld" })
        {
            OnlineDungeonSelectPolicy noRegion = OnlineDungeonSelectPolicy.Create(
                SampleTable(), assets: fx.Assets, texts: texts, worldMapPath: worldMapPath);
            Assert.Null(noRegion.RegionDisplayName);
            Assert.Equal("AlphaName", noRegion.EntryDisplayName(PathOnly(AlphaPath)));
        }
    }

    /// <summary>
    /// 保护:空路径的条目无论资源包里有什么都取不到显示名——策略自己先拦下空路径,而不是指望"读不到脚本"。
    /// 用一份文件名为空串的退化档案(里面那份脚本带能查到文字的 [name])证明:若策略放行空路径,就会把它的名字当成该条目的名字。
    /// </summary>
    [Fact]
    public void EntryWithEmptyPathNeverGetsADisplayNameEvenIfTheArchiveHoldsAnUnnamedScript()
    {
        using var fx = new MiniAssets(withUltimateList: true, withEmptyNamedScript: true);
        Assert.NotNull(fx.Assets.ReadScript(string.Empty)); // 前提:档案里确有这份"无名脚本",否则本用例什么也证明不了
        OnlineDungeonSelectPolicy policy = OnlineDungeonSelectPolicy.Create(
            SampleTable(), assets: fx.Assets, texts: SyntheticTexts(), worldMapPath: RegionPath);

        Assert.Null(policy.EntryDisplayName(PathOnly(string.Empty)));
        Assert.Equal("AlphaName", policy.EntryDisplayName(PathOnly(AlphaPath))); // 正常条目不受影响
        Assert.Equal("RegionName", policy.RegionDisplayName);
    }

    // ------------------------------------------------------------------------------------------
    // "有没有难度 3"
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// 保护:有资源包且没给"有没有难度 3"委托时,由 etc/ultimatedungeonlist.etc 里 [apply ultimate] 的编号决定——
    /// 在名单里的副本档位 3/255 可选到 3,不在名单里的被夹到 2;名单文件缺失时一个副本都没有难度 3(全部夹到 2),
    /// 与"没有资源包时默认都有"相反;显式委托优先于名单(说"没有"时名单里的也夹到 2,说"有"时名单外的也到 3)。
    /// 推导:31/33 在名单里,32 不在;档位 31→3、32→3、33→255、34→2;上限 = min(档位, 有难度 3 ? 3 : 2)。
    /// </summary>
    [Fact]
    public void UltimateAvailabilityComesFromTheUltimateListWhenAssetsArePresent()
    {
        using var withList = new MiniAssets(withUltimateList: true);
        OnlineDungeonSelectPolicy policy = OnlineDungeonSelectPolicy.Create(SampleTable(), assets: withList.Assets);

        Assert.Equal(3, policy.DifficultyLimit(PathOnly(AlphaPath))); // 31:在名单里,档位 3
        Assert.Equal(2, policy.DifficultyLimit(PathOnly(BetaPath))); // 32:不在名单里,档位 3 被夹到 2
        Assert.Equal(3, policy.DifficultyLimit(PathOnly(GammaPath))); // 33:在名单里,档位 255
        Assert.Equal(2, policy.DifficultyLimit(PathOnly(MissingPath))); // 34:档位 2
        Assert.Equal(0, policy.DifficultyLimit(PathOnly("dungeon/test/delta.dgn"))); // 编号无效

        using var withoutList = new MiniAssets(withUltimateList: false);
        OnlineDungeonSelectPolicy noList = OnlineDungeonSelectPolicy.Create(SampleTable(), assets: withoutList.Assets);
        Assert.Equal(2, noList.DifficultyLimit(PathOnly(AlphaPath))); // 名单文件缺失:谁都没有难度 3
        Assert.Equal(2, noList.DifficultyLimit(PathOnly(GammaPath)));
        Assert.Equal(2, noList.DifficultyLimit(PathOnly(MissingPath)));

        OnlineDungeonSelectPolicy denyAll = OnlineDungeonSelectPolicy.Create(SampleTable(), assets: withList.Assets, hasUltimate: _ => false);
        Assert.Equal(2, denyAll.DifficultyLimit(PathOnly(AlphaPath)));
        OnlineDungeonSelectPolicy allowAll = OnlineDungeonSelectPolicy.Create(SampleTable(), assets: withoutList.Assets, hasUltimate: _ => true);
        Assert.Equal(3, allowAll.DifficultyLimit(PathOnly(BetaPath)));
    }
}
