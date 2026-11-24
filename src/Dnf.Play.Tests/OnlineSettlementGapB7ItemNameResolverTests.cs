using System.Reflection;
using System.Text;
using Dnf.Data;
using Dnf.Data.Tests;
using Dnf.Game;
using Dnf.Render;
using Dnf.Script;

namespace Dnf.Play.Tests;

/// <summary>
/// 联网结算的物品名查询器(<see cref="OnlineSettlementHeroHelpers.CreateItemNameResolver"/>)在物品号为 0 与负数时的补测(区域 B 第七轮)。
/// 既有的胶水测试用的合成目录里没有 0 号与负号条目,所以"物品号 0 去查目录"与"不去查"读不出差别;
/// 这里自己拼一份迷你资源包(临时目录里的合成 Script.pvf,用完删除):可叠加物品列表里 0、-1、-4 都列了、脚本都写了名字、文字表也给了名字——
/// 守卫一旦放宽(例如物品号 0 也去查目录)就会拿到名字,而守卫正确时 0 与负数一律没有名字。
/// 另用反射读出查询器闭包里的缓存字典与目录对象,钉住"非正数不查缓存、不建目录;查过的号(含查不到的)进缓存;缓存先于目录被查"。
/// 数据全是合成的(物品号 1、9821 段)。
/// </summary>
public sealed class OnlineSettlementGapB7ItemNameResolverTests
{
    private const string ZeroName = "Gapb Seven Zero Item";
    private const string MinusName = "Gapb Seven Minus Item";
    private const string OneName = "Gapb Seven One Item";
    private const string NamedName = "Gapb Seven Named Item";

    /// <summary>
    /// 临时目录里的迷你资源包:Script.pvf(合成字符串表 + 令牌脚本)与空的 ImagePacks2。
    /// 可叠加物品列表七项:0、-1、-4 与 1、9821 的脚本都写了名字键;9822 的脚本没写名字;9823 列了但脚本文件不存在。(9999 故意不列。)
    /// </summary>
    private sealed class ItemAssets : IDisposable
    {
        private const byte TagType = (byte)TokenType.Tag;
        private const byte IntType = (byte)TokenType.Int;
        private const byte StrType = (byte)TokenType.String;
        private const byte KindType = (byte)TokenType.StringRefKind;
        private const byte KeyType = (byte)TokenType.StringRefKey;

        private readonly List<string> _pool = new List<string>();
        private readonly List<(string Name, byte[] Data)> _scripts = new List<(string, byte[])>();

        public ItemAssets()
        {
            Root = Path.Combine(Path.GetTempPath(), "dnf-gapb7-item-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Root, "ImagePacks2"));

            Add(ItemCatalog.StackableListPath, new List<(byte, int)>
            {
                (IntType, 0), (StrType, Intern("gapb7/zero.stk")),
                (IntType, -1), (StrType, Intern("gapb7/minus.stk")),
                (IntType, -4), (StrType, Intern("gapb7/minus.stk")),
                (IntType, 1), (StrType, Intern("gapb7/one.stk")),
                (IntType, 9821), (StrType, Intern("gapb7/named.stk")),
                (IntType, 9822), (StrType, Intern("gapb7/unnamed.stk")),
                (IntType, 9823), (StrType, Intern("gapb7/lost.stk")),
            });
            AddNamed("stackable/gapb7/zero.stk", "gapb7_item_zero");
            AddNamed("stackable/gapb7/minus.stk", "gapb7_item_minus");
            AddNamed("stackable/gapb7/one.stk", "gapb7_item_one");
            AddNamed("stackable/gapb7/named.stk", "gapb7_item_named");
            Add("stackable/gapb7/unnamed.stk", new List<(byte, int)> { (TagType, Intern("[grade]")), (IntType, 5) });

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

        /// <summary>写了 <c>[name]</c>(可叠加物品种类 + 名字键)的物品脚本。</summary>
        private void AddNamed(string path, string nameKey)
        {
            Add(path, new List<(byte, int)>
            {
                (TagType, Intern("[name]")), (KindType, StrLibrary.KindStackable), (KeyType, Intern(nameKey)),
            });
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

    /// <summary>内存文字表:可叠加物品种类里四个名字键 → 四个合成名字。</summary>
    private static WorldTexts SyntheticTexts()
    {
        var map = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["stackable/stackable.kor.str"] = Encoding.ASCII.GetBytes(
                "gapb7_item_zero>" + ZeroName + "\r\n"
                + "gapb7_item_minus>" + MinusName + "\r\n"
                + "gapb7_item_one>" + OneName + "\r\n"
                + "gapb7_item_named>" + NamedName + "\r\n"),
        };
        return new WorldTexts(new StrLibrary(p => map.TryGetValue(p, out byte[] bytes) ? bytes : null));
    }

    private static Func<int, string> Create(ItemAssets fx)
    {
        Func<int, string> resolve = OnlineSettlementHeroHelpers.CreateItemNameResolver(fx.Assets, SyntheticTexts());
        Assert.NotNull(resolve);
        return resolve;
    }

    // 保护的行为:物品号 ≤ 0 的查询一律没有名字(返回 null),哪怕目录里恰好列了 0 号与负号条目、文字表也给了它们名字——
    // 0 不是合法的物品号,不能去查目录;查过正数物品(缓存与目录都已建好)之后再问也一样。
    // 期望值手算:目录里 0 → 脚本 zero.stk → 名字键 gapb7_item_zero → ZeroName;-1、-4 → minus.stk → MinusName;
    // 先用目录与文字表直接问一遍确认"0 与负数确实有名字"(前提),查询器对这三个号仍给 null。
    [Fact]
    public void ZeroAndNegativeItemIdsGetNoNameEvenWhenTheCatalogListsAndNamesThem()
    {
        using var fx = new ItemAssets();
        WorldTexts texts = SyntheticTexts();

        // 前提:这份合成目录里 0、-1、-4 都有名字(绕过查询器直接问目录与文字表)
        var catalog = new ItemCatalog(new GameData(new PvfScriptSource(fx.Assets.Pvf)));
        Assert.Equal(ZeroName, texts.ItemName(catalog.Get(0)));
        Assert.Equal(MinusName, texts.ItemName(catalog.Get(-1)));
        Assert.Equal(MinusName, texts.ItemName(catalog.Get(-4)));

        Func<int, string> resolve = OnlineSettlementHeroHelpers.CreateItemNameResolver(fx.Assets, texts);
        Assert.NotNull(resolve);

        Assert.Null(resolve(0));
        Assert.Null(resolve(-1));
        Assert.Null(resolve(-4));
        Assert.Null(resolve(int.MinValue));

        // 先查一个正数(目录建好、缓存有了内容)之后,0 与负数仍然没有名字
        Assert.Equal(OneName, resolve(1));
        Assert.Null(resolve(0));
        Assert.Null(resolve(-1));
        Assert.Null(resolve(-4));
    }

    // 保护的行为:正数物品号按"列表 → 脚本 → 文字表"取名——最小的正数 1 能查到(守卫另一侧的边界);脚本没写名字键、列表里有但脚本缺失、
    // 根本不在列表里(含最大的 int)的号返回 null、不抛异常;同一个号第二次查询(走缓存)与第一次结果相同,对查不到的号也一样。
    [Fact]
    public void PositiveItemIdsResolveThroughListScriptAndTextTableAndAskingAgainGivesTheSameAnswer()
    {
        using var fx = new ItemAssets();
        Func<int, string> resolve = Create(fx);

        for (int pass = 0; pass < 2; pass++)
        {
            Assert.Equal(OneName, resolve(1));              // 最小的正数
            Assert.Equal(NamedName, resolve(9821));         // 列表 + 脚本 + 文字表齐全
            Assert.Null(resolve(9822));                     // 脚本没写名字键
            Assert.Null(resolve(9823));                     // 列表里有,脚本文件缺失
            Assert.Null(resolve(9999));                     // 不在列表里
            Assert.Null(resolve(int.MaxValue));             // 最大的 int 也不在
        }
    }

    /// <summary>查询器闭包里捕获的缓存字典与目录字段(按类型找,不依赖编译器生成的名字)。</summary>
    private static (object Closure, Dictionary<int, string> Cache, FieldInfo CatalogField) Inspect(Func<int, string> resolve)
    {
        object closure = resolve.Target;
        Assert.NotNull(closure);
        FieldInfo[] fields = closure.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        FieldInfo cacheField = fields.Single(f => f.FieldType == typeof(Dictionary<int, string>));
        FieldInfo catalogField = fields.Single(f => f.FieldType == typeof(ItemCatalog));
        return (closure, (Dictionary<int, string>)cacheField.GetValue(closure), catalogField);
    }

    // 保护的行为:缓存与目录的使用次序——
    //   (1) 物品号 ≤ 0 在查缓存、建目录之前就返回:查完 0、-1、-4 后缓存仍是空的、目录对象还没建(惰性);
    //   (2) 第一次查正数号才建目录(只建一次),查过的号都进缓存,查不到的号也进缓存(值为 null);
    //   (3) 再查同一个号直接取缓存:往缓存里塞一条目录里并不存在的"伪名字",再查原样取回;
    //   (4) 非正数的守卫排在缓存之前:缓存里塞了 0 号的名字,查 0 仍是 null。
    // 期望值手算:查 9821(有名字)与 9999(不在列表)后缓存恰两项:{9821: NamedName, 9999: null}。
    [Fact]
    public void ResolverCachesQueriedIdsInOrderButNeverCachesOrBuildsTheCatalogForNonPositiveIds()
    {
        using var fx = new ItemAssets();
        Func<int, string> resolve = Create(fx);
        (object closure, Dictionary<int, string> cache, FieldInfo catalogField) = Inspect(resolve);

        // 刚造出来:缓存为空,目录还没建
        Assert.Empty(cache);
        Assert.Null(catalogField.GetValue(closure));

        // (1) 非正数:不进缓存、不建目录
        Assert.Null(resolve(0));
        Assert.Null(resolve(-1));
        Assert.Null(resolve(-4));
        Assert.Empty(cache);
        Assert.Null(catalogField.GetValue(closure));

        // (2) 正数:建一次目录;有名字的与查不到的都进缓存
        Assert.Equal(NamedName, resolve(9821));
        object catalog = catalogField.GetValue(closure);
        Assert.NotNull(catalog);
        Assert.Null(resolve(9999));
        Assert.Equal(2, cache.Count);
        Assert.Equal(NamedName, cache[9821]);
        Assert.Contains(9999, cache.Keys);
        Assert.Null(cache[9999]);
        Assert.Same(catalog, catalogField.GetValue(closure));

        // (3) 再查直接取缓存:目录里不存在的号 7777,缓存里放了名字,查询器原样给出
        cache[7777] = "Cached Only";
        Assert.Equal("Cached Only", resolve(7777));
        Assert.Equal(NamedName, resolve(9821));
        Assert.Null(resolve(9999));
        Assert.Same(catalog, catalogField.GetValue(closure));

        // (4) 缓存里放了 0 号的名字也不理:非正数的守卫在查缓存之前
        cache[0] = "Cached Zero";
        cache[-4] = "Cached Minus";
        Assert.Null(resolve(0));
        Assert.Null(resolve(-4));
    }
}
