using Dnf.Data;
using Dnf.Game;
using Dnf.Script;

namespace Dnf.Game.Tests;

/// <summary>
/// 怪物的物品掉落(证据 E-117,服务端静态读码):物品脚本头部的读法、掉落池怎么建、怎么取、四类物品各掷一次的顺序与取值范围、背包。
/// 数值与名字全是合成的,只检验规则的形状,与原版数据无关。
/// </summary>
public class ItemDropTests
{
    /// <summary>按剧本给数的随机数:记录每次调用的参数,方便断言"问了什么、按什么顺序问"。</summary>
    private sealed class ScriptedRandom : IDropRandom
    {
        private readonly Queue<int> _values;

        public List<string> Calls { get; } = new();

        public ScriptedRandom(params int[] values)
        {
            _values = new Queue<int>(values);
        }

        public int NextInclusive(int max)
        {
            Calls.Add("N" + max);
            return _values.Dequeue();
        }

        public int Range(int lo, int hi)
        {
            Calls.Add("R" + lo + ":" + hi);
            return _values.Dequeue();
        }
    }

    // ------------------------------------------------------------------ 编号表

    [Theory]
    [InlineData("[hat avatar]", 0)]
    [InlineData("[aurora avatar]", 9)]
    [InlineData("[weapon]", 10)]
    [InlineData("[title name]", 11)]
    [InlineData("[coat]", 12)]
    [InlineData("[magic stone]", 21)]
    [InlineData("[creature]", 22)]
    [InlineData("[artifact red]", 23)]
    [InlineData("[artifact green]", 25)]
    [InlineData("[WEAPON]", 10)]
    [InlineData("[no such part]", EquipmentParts.Unknown)]
    [InlineData(null, EquipmentParts.Unknown)]
    public void EquipmentPartsFollowTheServerArrayOrder(string tag, int expected)
    {
        Assert.Equal(expected, EquipmentParts.FromTag(tag));
    }

    [Fact]
    public void EquipmentPartTagsRoundTrip()
    {
        for (int part = 0; part <= EquipmentParts.ArtifactGreen; part++)
        {
            Assert.Equal(part, EquipmentParts.FromTag(EquipmentParts.TagOf(part)));
        }
        Assert.Null(EquipmentParts.TagOf(EquipmentParts.Unknown));
        Assert.Null(EquipmentParts.TagOf(-1));
    }

    [Fact]
    public void EquipmentPartRangesMatchTheServerPredicates()
    {
        Assert.True(EquipmentParts.IsAvatar(0));
        Assert.True(EquipmentParts.IsAvatar(9));
        Assert.False(EquipmentParts.IsAvatar(10));
        Assert.False(EquipmentParts.IsAvatar(-1));
        Assert.True(EquipmentParts.IsEnchantable(10));
        Assert.True(EquipmentParts.IsEnchantable(21));
        Assert.False(EquipmentParts.IsEnchantable(22));
        Assert.False(EquipmentParts.IsEnchantable(9));
        Assert.True(EquipmentParts.IsCreature(22));
        Assert.True(EquipmentParts.IsCreature(25));
        Assert.False(EquipmentParts.IsCreature(21));
        Assert.False(EquipmentParts.IsCreature(26));
    }

    [Theory]
    [InlineData("[waste]", 0)]
    [InlineData("[material]", 1)]
    [InlineData("[recipe]", 3)]
    [InlineData("[throw]", 4)]
    [InlineData("[quest]", 8)]
    [InlineData("[etc]", 11)]
    [InlineData("[creature expitem]", 35)]
    [InlineData("[usable cera package]", 38)]
    [InlineData("[multi upgradable legacy]", 26)]
    [InlineData("[random upgradable legacy]", 26)]
    [InlineData("[RECIPE]", 3)]
    [InlineData("[nothing]", StackableTypes.Unknown)]
    [InlineData(null, StackableTypes.Unknown)]
    public void StackableTypesFollowTheServerChain(string tag, int expected)
    {
        Assert.Equal(expected, StackableTypes.FromTag(tag));
    }

    // ------------------------------------------------------------------ 物品脚本头部

    private static ItemInfo Parse(TokenStreamBuilder b, ItemKind kind, int code = 7, string path = "equipment/x.equ")
    {
        (ScriptToken[] _, PvfStringTable strings) = b.Build();
        return ItemScript.Parse(b.Bytes(), strings, kind, code, path);
    }

    [Fact]
    public void EquipmentHeaderIsRead()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[name]").Keyed(3, "name_9001");
        b.Tag("[grade]").Int(55);
        b.Tag("[attach type]").Str("[trade]");
        b.Tag("[creation rate]").Int(120);
        b.Tag("[rarity]").Int(3);
        b.Tag("[usable job]").Str("[swordman]").Str("[priest]");
        b.Tag("[/usable job]");
        b.Tag("[minimum level]").Int(40);
        b.Tag("[equipment type]").Str("[shoes]").Int(17);
        b.Tag("[field image]").Str("Item/FieldImage.img").Int(28);
        b.Tag("[icon]").Str("Item/Icon/Shoes.img").Int(5);
        b.Tag("[price]").Int(900);
        b.Tag("[move wav]").Str("SHOES_TOUCH");
        b.Tag("[physical attack]").Int(123);      // 不认识的标签原样跳过

        ItemInfo item = Parse(b, ItemKind.Equipment, 42, "equipment/shoes/a.equ");

        Assert.NotNull(item);
        Assert.Equal(42, item.Code);
        Assert.Equal("equipment/shoes/a.equ", item.Path);
        Assert.Equal(ItemKind.Equipment, item.Kind);
        Assert.True(item.IsEquipment);
        Assert.False(item.IsStackable);
        Assert.Equal("name_9001", item.NameKey);
        Assert.Equal(55, item.Grade);
        Assert.Equal(3, item.Rarity);
        Assert.Equal(120, item.CreationRate);
        Assert.False(item.SpecialMonsterDrop);
        Assert.Equal("[trade]", item.AttachType);
        Assert.Equal(new[] { "[swordman]", "[priest]" }, item.UsableJobs);
        Assert.Equal(40, item.MinimumLevel);
        Assert.Equal(EquipmentParts.Shoes, item.Part);
        Assert.Equal(17, item.TypeIndex);
        Assert.Equal(StackableTypes.Unknown, item.StackableType);
        Assert.Equal("Item/FieldImage.img", item.FieldImage);
        Assert.Equal(28, item.FieldFrame);
        Assert.Equal("Item/Icon/Shoes.img", item.Icon);
        Assert.Equal(5, item.IconFrame);
        Assert.Equal(900, item.Price);
        Assert.Equal("SHOES_TOUCH", item.MoveSound);
        Assert.False(item.IsCreature);
        Assert.False(item.IsAvatar);
    }

    [Fact]
    public void ImageKeysAreLowercaseUnderSprite()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[field image]").Str("Item/Field_Creature.img").Int(2);
        b.Tag("[icon]").Str("Item\\Creature\\Artifact_Green.img").Int(5);
        ItemInfo item = Parse(b, ItemKind.Equipment);
        Assert.Equal("sprite/item/field_creature.img", item.FieldImageKey);
        Assert.Equal("sprite/item/creature/artifact_green.img", item.IconKey);
        Assert.Null(Parse(new TokenStreamBuilder().Tag("[grade]").Int(1), ItemKind.Equipment).FieldImageKey);
        Assert.Null(Parse(new TokenStreamBuilder().Tag("[grade]").Int(1), ItemKind.Equipment).IconKey);
    }

    [Fact]
    public void StackableHeaderIsReadAndEquipmentTypeIsLeftUnknown()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[name]").Keyed(13, "name_77");
        b.Tag("[stackable type]").Str("[recipe]").Int(1);
        b.Tag("[grade]").Int(7);
        b.Tag("[creation rate]").Int(100);
        b.Tag("[special monster drop]").Int(1);
        b.Tag("[stack limit]").Int(99);

        ItemInfo item = Parse(b, ItemKind.Stackable, 5, "stackable/recipe/x.stk");

        Assert.True(item.IsStackable);
        Assert.Equal(StackableTypes.Recipe, item.StackableType);
        Assert.Equal(1, item.TypeIndex);
        Assert.Equal(-1, item.Part);
        Assert.True(item.SpecialMonsterDrop);
        Assert.Equal(99, item.StackLimit);
        Assert.False(item.IsCreature);
    }

    [Fact]
    public void EquipmentWithoutATypeHasTheUnknownPart()
    {
        ItemInfo item = Parse(new TokenStreamBuilder().Tag("[grade]").Int(1), ItemKind.Equipment);
        Assert.Equal(EquipmentParts.Unknown, item.Part);
        Assert.Equal(0, item.CreationRate);
        Assert.Equal(-1, item.FieldFrame);
        Assert.Null(item.MoveSound);
        Assert.Empty(item.UsableJobs);
    }

    [Fact]
    public void RepeatedTagsTakeTheLastValueAndOnlyTheFirstValueOfEachTag()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[rarity]").Int(1).Int(9);
        b.Tag("[rarity]").Int(4);
        b.Tag("[usable job]").Str("[a]");
        b.Tag("[usable job]").Str("[b]");
        ItemInfo item = Parse(b, ItemKind.Equipment);
        Assert.Equal(4, item.Rarity);
        Assert.Equal(new[] { "[b]" }, item.UsableJobs);
    }

    [Fact]
    public void CreatureAndAvatarPartsAreFlagged()
    {
        ItemInfo creature = Parse(new TokenStreamBuilder().Tag("[equipment type]").Str("[artifact blue]").Int(15), ItemKind.Equipment);
        Assert.True(creature.IsCreature);
        Assert.False(creature.IsAvatar);
        ItemInfo avatar = Parse(new TokenStreamBuilder().Tag("[equipment type]").Str("[skin avatar]").Int(0), ItemKind.Equipment);
        Assert.True(avatar.IsAvatar);
        Assert.False(avatar.IsCreature);
        // 可叠加物品的 Part 是 −1,不会被当成宠物
        ItemInfo stackable = Parse(new TokenStreamBuilder().Tag("[stackable type]").Str("[creature]").Int(0), ItemKind.Stackable);
        Assert.Equal(9, stackable.StackableType);
        Assert.False(stackable.IsCreature);
    }

    [Fact]
    public void NonTokenScriptsReadAsNull()
    {
        (ScriptToken[] _, PvfStringTable strings) = new TokenStreamBuilder().Tag("[x]").Build();
        Assert.Null(ItemScript.Parse(new byte[] { 1, 2, 3, 4 }, strings, ItemKind.Equipment, 1, "a"));
        Assert.Throws<ArgumentNullException>(() => ItemScript.Parse(new byte[] { 0xB0, 0xD0 }, null, ItemKind.Equipment, 1, "a"));
    }

    // ------------------------------------------------------------------ 目录(列表 + 按需读 + 整库扫描)

    [Fact]
    public void CatalogResolvesListsAndKeepsOnlyDroppableItems()
    {
        var pool = new List<string>();
        var listE = new TokenStreamBuilder(pool).Int(10).Str("w/a.equ").Int(11).Str("w/b.equ").Int(12).Str("w/missing.equ");
        var listS = new TokenStreamBuilder(pool).Int(500).Str("r/c.stk");
        var a = new TokenStreamBuilder(pool).Tag("[grade]").Int(5).Tag("[creation rate]").Int(100).Tag("[rarity]").Int(1)
            .Tag("[equipment type]").Str("[weapon]").Int(1);
        var b = new TokenStreamBuilder(pool).Tag("[grade]").Int(6).Tag("[rarity]").Int(2);       // 没写创建率:不进池
        var c = new TokenStreamBuilder(pool).Tag("[grade]").Int(7).Tag("[creation rate]").Int(50).Tag("[stackable type]").Str("[recipe]").Int(1);
        byte[] table = a.StringTableBytes();

        var source = new MemoryScriptSource()
            .AddBytes(GameData.StringTablePath, table)
            .AddBytes(ItemCatalog.EquipmentListPath, listE.Bytes())
            .AddBytes(ItemCatalog.StackableListPath, listS.Bytes())
            .AddBytes("equipment/w/a.equ", a.Bytes())
            .AddBytes("equipment/w/b.equ", b.Bytes())
            .AddBytes("stackable/r/c.stk", c.Bytes());
        var catalog = new ItemCatalog(new GameData(source));

        Assert.Equal(4, catalog.ListedCount);
        IReadOnlyList<ItemInfo> droppable = catalog.Droppable();
        Assert.Equal(new[] { 10, 500 }, droppable.Select(i => i.Code).ToArray());
        Assert.Equal(4, catalog.Scanned);
        Assert.Equal(1, catalog.Failed);                 // 12 号的脚本不存在
        Assert.Same(droppable, catalog.Droppable());      // 第二次用缓存,不再扫
        Assert.Equal(4, catalog.Scanned);

        Assert.Equal(EquipmentParts.Weapon, catalog.Get(10).Part);
        Assert.Equal(StackableTypes.Recipe, catalog.Get(500).StackableType);
        Assert.Equal(6, catalog.Get(11).Grade);           // 不在池里的也能按需读
        Assert.Null(catalog.Get(12));                     // 脚本缺失
        Assert.Null(catalog.Get(999));                    // 列表里没有
        Assert.Same(catalog.Get(10), catalog.Get(10));
    }

    [Fact]
    public void CatalogWithoutListsIsEmpty()
    {
        var catalog = new ItemCatalog(new GameData(new MemoryScriptSource()));
        Assert.Equal(0, catalog.ListedCount);
        Assert.Empty(catalog.Droppable());
        Assert.Null(catalog.Get(1));
        Assert.Throws<ArgumentNullException>(() => new ItemCatalog(null));
    }

    // ------------------------------------------------------------------ 掉落表的新读法

    private static readonly int[] DefaultBasis =
    {
        100000, 300000, 600000, 900000, 990000, 1000000,
        10, 20, 30, 40, 50, 60,
        200000, 400000, 600000, 800000, 900000, 950000,
        1, 2, 3, 4, 5, 6,
    };

    /// <summary>
    /// 合成的怪物掉落脚本:一行概率(副本等级 1..50;金币 0,类型 1..4 的基础概率由 <paramref name="values"/> 给),
    /// 稀有度分界(每类六个)、三张加成表(全取同一个值)、物品窗口(每个等级向下 <paramref name="before"/>、向上 <paramref name="after"/>)。
    /// </summary>
    private static MonsterDropTable DropTable(int[] values, int[] basis = null, float party = 1f, float role = 1f, float difficulty = 1f,
        int before = 2, int after = 3, bool partialWindows = false)
    {
        var b = new TokenStreamBuilder();
        b.Tag("[drop prob count]").Int(1);
        b.Tag("[drop prob]").Ints(1, 50).Ints(values);
        b.Tag("[basis of rarity dicision]").Ints(basis ?? DefaultBasis);
        b.Tag("[party member drop bonusrate]").Floats(Enumerable.Repeat(party, 20).ToArray());
        b.Tag("[dungeon difficulty drop bonusrate]").Floats(Enumerable.Repeat(difficulty, 25).ToArray());
        b.Tag("[monster type drop bonusrate]").Floats(Enumerable.Repeat(role, 20).ToArray());
        b.Tag("[item drop ref table]");
        for (int level = 1; level <= 200; level++)
        {
            // partialWindows:只有 10、20 两个等级有窗口,其余全 0
            bool has = !partialWindows || level == 10 || level == 20;
            b.Ints(level, has ? before : 0, has ? after : 0);
        }
        MonsterDropTable table = MonsterDropTable.Load(b.Cursor(), out string error);
        Assert.Null(error);
        Assert.NotNull(table);
        return table;
    }

    [Fact]
    public void WindowsAndRarityBasisAreKept()
    {
        MonsterDropTable table = DropTable(new[] { 1, 2, 3, 4 }.Prepend(0).ToArray(), before: 4, after: 9);
        table.ItemWindow(77, out int before, out int after);
        Assert.Equal((4, 9), (before, after));
        table.ItemWindow(0, out before, out after);       // 第 0 项:服务端启动时写 0
        Assert.Equal((0, 0), (before, after));
        table.ItemWindow(201, out before, out after);
        Assert.Equal((0, 0), (before, after));
        table.ItemWindow(-3, out before, out after);
        Assert.Equal((0, 0), (before, after));
    }

    [Fact]
    public void WindowEndsKeepOnlyTheLowEightBits()
    {
        MonsterDropTable table = DropTable(new[] { 0, 1, 2, 3, 4 }, before: 256 + 7, after: 512 + 9);
        table.ItemWindow(5, out int before, out int after);
        Assert.Equal((7, 9), (before, after));
    }

    [Fact]
    public void RarityIsTheFirstBandAtLeastTheRoll()
    {
        MonsterDropTable table = DropTable(new[] { 0, 1, 2, 3, 4 });
        // 类型 1 的分界:100000, 300000, 600000, 900000, 990000, 1000000
        Assert.Equal(0, table.ItemRarity(1, 1));
        Assert.Equal(0, table.ItemRarity(1, 100000));       // 不大于分界就算这一档
        Assert.Equal(1, table.ItemRarity(1, 100001));
        Assert.Equal(1, table.ItemRarity(1, 300000));
        Assert.Equal(2, table.ItemRarity(1, 600000));
        Assert.Equal(3, table.ItemRarity(1, 900000));
        Assert.Equal(4, table.ItemRarity(1, 990000));
        Assert.Equal(5, table.ItemRarity(1, 990001));
        Assert.Equal(5, table.ItemRarity(1, 1000000));
        // 一档也没满足(掷到的数比六个分界都大):回到 0 档(服务端的循环走完返回 0)
        Assert.Equal(0, table.ItemRarity(4, 7));            // 类型 4 的分界是 1..6,7 比谁都大
        Assert.Equal(2, table.ItemRarity(4, 3));
        Assert.Equal(0, table.ItemRarity(2, 5));            // 类型 2 的分界 10..60,5 ≤ 10 → 0 档
        Assert.Equal(3, table.ItemRarity(2, 40));
        Assert.Equal(0, table.ItemRarity(0, 1));            // 类型越界
        Assert.Equal(0, table.ItemRarity(5, 1));
    }

    [Fact]
    public void ItemProbabilityMultipliesBonusesThenRoundsToEvenThenAppliesTheDropRate()
    {
        // 队伍 2.0 × 种类 3.0 × 难度 1.5 × 基础 1000 = 9000
        MonsterDropTable table = DropTable(new[] { 0, 1000, 1000, 1000, 1000 }, party: 2f, role: 3f, difficulty: 1.5f);
        Assert.Equal(9000, table.ItemProbability(0, 1, 0, 0, 1));
        Assert.Equal(4500, table.ItemProbability(0, 2, 0, 0, 1, 50));      // 掉落率 50%
        Assert.Equal(13500, table.ItemProbability(0, 4, 0, 0, 1, 150));
        // 行号与类型越界
        Assert.Equal(0, table.ItemProbability(1, 1, 0, 0, 1));
        Assert.Equal(0, table.ItemProbability(0, 0, 0, 0, 1));              // 类型 0 是金币,不走这里
        Assert.Equal(0, table.ItemProbability(0, 5, 0, 0, 1));
    }

    [Theory]
    [InlineData(5, 2)]       // 2.5 → 2(平局取偶)
    [InlineData(7, 4)]       // 3.5 → 4
    [InlineData(3, 2)]       // 1.5 → 2
    [InlineData(1, 0)]       // 0.5 → 0
    public void ItemProbabilityRoundsHalvesToEven(int basis, int expected)
    {
        // 基础概率 basis,种类加成 0.5(浮点里能精确表示):乘积是 basis / 2,正好落在 .5 上
        MonsterDropTable table = DropTable(new[] { 0, basis, basis, basis, basis }, role: 0.5f);
        Assert.Equal(expected, table.ItemProbability(0, 1, 0, 0, 1));
    }

    [Fact]
    public void ItemProbabilityUsesTheMemberDifficultyAndRoleColumns()
    {
        static float[] Ones(int n) => Enumerable.Repeat(1f, n).ToArray();
        var b = new TokenStreamBuilder();
        b.Tag("[drop prob count]").Int(1);
        b.Tag("[drop prob]").Ints(1, 50, 0, 7, 5, 5, 5);
        // 加成表都是五行(金币、类型 1 到 4),这里只改类型 1 那一行(第 2 行),其余全 1
        b.Tag("[party member drop bonusrate]").Floats(Ones(4)).Floats(1f, 1.5f, 2f, 2.5f).Floats(Ones(12));
        b.Tag("[dungeon difficulty drop bonusrate]").Floats(Ones(5)).Floats(1f, 2f, 3f, 4f, 5f).Floats(Ones(15));
        b.Tag("[monster type drop bonusrate]").Floats(Ones(4)).Floats(1f, 10f, 100f, 1000f).Floats(Ones(12));
        MonsterDropTable table = MonsterDropTable.Load(b.Cursor(), out string error);
        Assert.Null(error);
        Assert.Equal(7, table.ItemProbability(0, 1, 0, 0, 1));
        Assert.Equal(7000, table.ItemProbability(0, 1, 2, 3, 4));                      // 7 × 2.5(4 人)× 100(种类 2)× 4(难度 3)
        Assert.Equal(5, table.ItemProbability(0, 2, 2, 3, 4));                         // 类型 2 那一行全是 1
        Assert.Equal(7, table.ItemProbability(0, 1, 4, 5, 5));                         // 越界的列按 1 算
    }

    // ------------------------------------------------------------------ 掉落池

    private static ItemInfo Equip(int code, int grade, int rarity, int rate, int part = EquipmentParts.Weapon)
    {
        return new ItemInfo(code, "equipment/" + code + ".equ", ItemKind.Equipment)
        {
            Grade = grade,
            Rarity = rarity,
            CreationRate = rate,
            Part = part,
        };
    }

    private static ItemInfo Stack(int code, int grade, int rarity, int rate, int type = StackableTypes.Material)
    {
        return new ItemInfo(code, "stackable/" + code + ".stk", ItemKind.Stackable)
        {
            Grade = grade,
            Rarity = rarity,
            CreationRate = rate,
            StackableType = type,
        };
    }

    [Theory]
    [InlineData(ItemKind.Equipment, EquipmentParts.Weapon, 1, false)]
    [InlineData(ItemKind.Equipment, EquipmentParts.Weapon, 2, true)]
    [InlineData(ItemKind.Equipment, EquipmentParts.Weapon, 3, false)]
    [InlineData(ItemKind.Equipment, EquipmentParts.Weapon, 4, false)]
    [InlineData(ItemKind.Equipment, EquipmentParts.Creature, 2, true)]       // 宠物也属于类型 2
    [InlineData(ItemKind.Equipment, EquipmentParts.Creature, 4, true)]       // 并且另外属于类型 4
    [InlineData(ItemKind.Equipment, EquipmentParts.ArtifactGreen, 4, true)]
    [InlineData(ItemKind.Equipment, EquipmentParts.HatAvatar, 4, false)]     // 时装不是宠物
    [InlineData(ItemKind.Equipment, EquipmentParts.Unknown, 2, true)]
    [InlineData(ItemKind.Stackable, 1, 1, true)]
    [InlineData(ItemKind.Stackable, 1, 2, false)]
    [InlineData(ItemKind.Stackable, 1, 3, false)]
    [InlineData(ItemKind.Stackable, 1, 4, false)]
    [InlineData(ItemKind.Stackable, StackableTypes.Recipe, 1, true)]         // 设计图也属于类型 1
    [InlineData(ItemKind.Stackable, StackableTypes.Recipe, 3, true)]         // 并且另外属于类型 3
    [InlineData(ItemKind.Stackable, StackableTypes.Throw, 3, false)]
    public void TypeAcceptsFollowsTheServerVerify(ItemKind kind, int partOrStackType, int type, bool expected)
    {
        ItemInfo item = kind == ItemKind.Equipment ? Equip(1, 1, 0, 1, partOrStackType) : Stack(1, 1, 0, 1, partOrStackType);
        Assert.Equal(expected, ItemPool.TypeAccepts(type, item));
    }

    [Fact]
    public void TypeAcceptsRejectsNull()
    {
        Assert.False(ItemPool.TypeAccepts(1, null));
    }

    [Fact]
    public void PoolTakesGradesInsideTheWindowOnly()
    {
        // 窗口:向下 2、向上 3 → 等级 10 的怪物收 [8, 12]
        MonsterDropTable table = DropTable(new[] { 0, 1, 1, 1, 1 }, before: 2, after: 3);
        var items = new[]
        {
            Equip(1, 7, 0, 10),    // 太低
            Equip(2, 8, 0, 10),    // 下边界
            Equip(3, 10, 0, 10),
            Equip(4, 12, 0, 10),   // 上边界(向上 3 → 到 L+2 = 12)
            Equip(5, 13, 0, 10),   // 太高
        };
        var pool = new ItemPool(items, table);
        Assert.Equal(3, pool.CandidateCount(2, 0, 10));
        Assert.Equal(30, pool.TotalWeight(2, 0, 10));
        // 等级 11 的窗口是 [9, 13]
        Assert.Equal(3, pool.CandidateCount(2, 0, 11));
        Assert.Equal(3, pool.Choose(2, 0, 11, new ScriptedRandom(0)).Code);     // 窗口里最低的是 10 级那件(编号 3)
        Assert.Equal(5, pool.Choose(2, 0, 11, new ScriptedRandom(30)).Code);    // 最高的是 13 级那件(编号 5)
    }

    [Fact]
    public void PoolFiltersByRarityTypeRateAndSpecialDrop()
    {
        MonsterDropTable table = DropTable(new[] { 0, 1, 1, 1, 1 }, before: 5, after: 5);
        var special = Equip(6, 10, 0, 10);
        special.SpecialMonsterDrop = true;
        var items = new List<ItemInfo>
        {
            Equip(1, 10, 0, 10),
            Equip(2, 10, 1, 10),                                  // 稀有度不同
            Equip(3, 10, 0, 0),                                   // 创建率 0
            Equip(4, 10, 0, -5),                                  // 创建率为负
            Stack(5, 10, 0, 10),                                  // 类型不同(可叠加物品)
            special,                                              // 特殊怪物才掉的
            null,
            Equip(7, 201, 0, 10),                                 // 等级超出 200
            Equip(8, -1, 0, 10),
        };
        var pool = new ItemPool(items, table);
        Assert.Equal(3, pool.Items);                              // 1、2、5 进了桶(其余在构造时就排除)
        Assert.Equal(1, pool.CandidateCount(2, 0, 10));
        Assert.Equal(1, pool.CandidateCount(2, 1, 10));
        Assert.Equal(1, pool.CandidateCount(1, 0, 10));
        Assert.Equal(0, pool.CandidateCount(3, 0, 10));
    }

    [Fact]
    public void PoolWindowIsClippedAtGrade200AndAtZero()
    {
        MonsterDropTable table = DropTable(new[] { 0, 1, 1, 1, 1 }, before: 3, after: 50);
        var pool = new ItemPool(new[] { Equip(1, 200, 0, 5), Equip(2, 198, 0, 5), Equip(3, 0, 0, 5), Equip(4, 1, 0, 5) }, table);
        // 等级 199:窗口 [196, 248] 截到 200 → 收 198 和 200
        Assert.Equal(2, pool.CandidateCount(2, 0, 199));
        // 等级 2:窗口 [−1, 51] → 收 0 和 1(等级 −1 不存在,跳过)
        Assert.Equal(2, pool.CandidateCount(2, 0, 2));
        // 等级越界或类型、稀有度越界:0 件
        Assert.Equal(0, pool.CandidateCount(2, 0, 0));
        Assert.Equal(0, pool.CandidateCount(2, 0, 201));
        Assert.Equal(0, pool.CandidateCount(2, 6, 10));
        Assert.Equal(0, pool.CandidateCount(0, 0, 10));
        Assert.Equal(0, pool.CandidateCount(5, 0, 10));
    }

    [Fact]
    public void ChooseWalksTheCumulativeWeightsWithLowerBound()
    {
        MonsterDropTable table = DropTable(new[] { 0, 1, 1, 1, 1 }, before: 0, after: 1);
        // 同一个等级 10 的三件,权重 10、20、30 → 累计键 10、30、60;总权重 60
        var items = new[] { Equip(1, 10, 0, 10), Equip(2, 10, 0, 20), Equip(3, 10, 0, 30) };
        var pool = new ItemPool(items, table);
        Assert.Equal(60, pool.TotalWeight(2, 0, 10));
        int Pick(int roll)
        {
            var random = new ScriptedRandom(roll);
            ItemInfo item = pool.Choose(2, 0, 10, random);
            Assert.Equal(new[] { "N60" }, random.Calls);          // 在 [0, 总权重] 里掷(含两端)
            return item.Code;
        }
        Assert.Equal(1, Pick(0));
        Assert.Equal(1, Pick(10));        // 键 ≥ 10 的第一个就是 10 本身
        Assert.Equal(2, Pick(11));
        Assert.Equal(2, Pick(30));
        Assert.Equal(3, Pick(31));
        Assert.Equal(3, Pick(60));
    }

    [Fact]
    public void ChooseOnAnEmptyTableDoesNotRoll()
    {
        MonsterDropTable table = DropTable(new[] { 0, 1, 1, 1, 1 });
        var pool = new ItemPool(new[] { Equip(1, 10, 0, 10) }, table);
        var random = new ScriptedRandom();
        Assert.Null(pool.Choose(2, 3, 10, random));                // 没有这个稀有度的
        Assert.Null(pool.Choose(2, 0, 100, random));               // 等级窗口里没有
        Assert.Null(pool.Choose(0, 0, 10, random));                // 参数越界
        Assert.Null(pool.Choose(2, 0, 10, null));
        Assert.Empty(random.Calls);
        Assert.Throws<ArgumentNullException>(() => new ItemPool(null, null));
        Assert.Equal(0, new ItemPool(null, table).Items);
    }

    [Fact]
    public void ChooseUsesTheSameTableOnRepeatedCalls()
    {
        MonsterDropTable table = DropTable(new[] { 0, 1, 1, 1, 1 }, before: 0, after: 1);
        var pool = new ItemPool(new[] { Equip(9, 10, 0, 1) }, table);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(9, pool.Choose(2, 0, 10, new ScriptedRandom(1)).Code);   // 总权重 1,掷 1 → 唯一的一件
        }
    }

    // ------------------------------------------------------------------ 掷骰

    /// <summary>一个把四类都放满的池:每一类在稀有度 0 与 1 各一件(等级 10)。</summary>
    private static ItemPool FullPool(MonsterDropTable table)
    {
        return new ItemPool(new[]
        {
            Stack(101, 10, 0, 10),                                        // 类型 1
            Stack(102, 10, 1, 10),
            Equip(201, 10, 0, 10),                                        // 类型 2
            Equip(202, 10, 1, 10),
            Stack(301, 10, 0, 10, StackableTypes.Recipe),                 // 类型 3(也在类型 1 里)
            Stack(302, 10, 1, 10, StackableTypes.Recipe),
            Equip(401, 10, 0, 10, EquipmentParts.Creature),               // 类型 4(也在类型 2 里)
            Equip(402, 10, 1, 10, EquipmentParts.ArtifactRed),
        }, table);
    }

    [Fact]
    public void RollAsksTheServerQuestionsInOrder()
    {
        // 四类的基础概率都是 10000(必掉)
        MonsterDropTable table = DropTable(new[] { 0, 10000, 10000, 10000, 10000 }, before: 0, after: 1);
        ItemPool pool = FullPool(table);
        // 每一类:概率骰 N10000、稀有度骰 R1:1000000、取物品 N<总权重>
        // 类型 1 的池里有 101(权重 10)与 301(10,设计图也算类型 1)→ 总权重 20;类型 2 的池里有 201 与 401(宠物也算类型 2);类型 3、4 各一件
        var random = new ScriptedRandom(
            0, 50000, 0,              // 类型 1:稀有度档 0(≤ 100000),取第一件
            0, 5, 20,                 // 类型 2:稀有度骰 5 ≤ 10 → 档 0,总权重 20,掷 20 → 第二件
            0, 1, 0,                  // 类型 3:稀有度档 0
            0, 1, 0);                 // 类型 4:稀有度档 0
        var context = new DropContext(10, 0, 10, 0);
        List<RolledItem> items = ItemDrop.Roll(table, pool, context, random);

        Assert.Equal(
            new[] { "N10000", "R1:1000000", "N20", "N10000", "R1:1000000", "N20", "N10000", "R1:1000000", "N10", "N10000", "R1:1000000", "N10" },
            random.Calls);
        Assert.Equal(new[] { 1, 2, 3, 4 }, items.Select(i => i.Type).ToArray());
        Assert.Equal(new[] { 101, 401, 301, 401 }, items.Select(i => i.Info.Code).ToArray());
    }

    [Fact]
    public void RollDropsWhenTheRollIsNotAboveTheProbability()
    {
        // 类型 1..4 的概率 100、100、100、100(1%);掷到 100 掉(不大于),101 不掉
        MonsterDropTable table = DropTable(new[] { 0, 100, 100, 100, 100 }, before: 0, after: 1);
        ItemPool pool = FullPool(table);
        // 数的顺序:类型 1 的概率骰 101(不掉);类型 2 的概率骰 100(掉)、稀有度骰 1、取物品骰 0;类型 3、4 的概率骰各 101(不掉)
        var random = new ScriptedRandom(101, 100, 1, 0, 101, 101);
        List<RolledItem> items = ItemDrop.Roll(table, pool, new DropContext(10, 0, 10, 0), random);
        Assert.Single(items);
        Assert.Equal(2, items[0].Type);
        Assert.Equal(new[] { "N10000", "N10000", "R1:1000000", "N20", "N10000", "N10000" }, random.Calls);
    }

    [Fact]
    public void RollConsumesTheRarityRollEvenWhenThePoolIsEmpty()
    {
        MonsterDropTable table = DropTable(new[] { 0, 10000, 0, 0, 0 }, before: 0, after: 1);
        var pool = new ItemPool(new[] { Stack(101, 10, 0, 10) }, table);
        // 稀有度骰 999999 → 类型 1 的分界里 990000 之后是档 5;档 5 没有物品 → 没有掉落,但稀有度骰已经掷了
        // (类型 2 到 4 的概率是 0,概率骰要掷到 1 以上才不掉)
        var random = new ScriptedRandom(0, 999999, 1, 1, 1);
        List<RolledItem> items = ItemDrop.Roll(table, pool, new DropContext(10, 0, 10, 0), random);
        Assert.Empty(items);
        Assert.Equal(new[] { "N10000", "R1:1000000", "N10000", "N10000", "N10000" }, random.Calls);
    }

    [Fact]
    public void RollPicksTheRarityFromTheRollAgainstTheTypeBands()
    {
        MonsterDropTable table = DropTable(new[] { 0, 10000, 0, 0, 0 }, before: 0, after: 1);
        var pool = new ItemPool(new[] { Stack(101, 10, 0, 10), Stack(102, 10, 3, 10) }, table);
        // 稀有度骰 600001 在类型 1 的分界(100000, 300000, 600000, 900000 …)里落到档 3
        var random = new ScriptedRandom(0, 600001, 0, 1, 1, 1);
        List<RolledItem> items = ItemDrop.Roll(table, pool, new DropContext(10, 0, 10, 0), random);
        Assert.Equal(102, Assert.Single(items).Info.Code);
    }

    [Fact]
    public void RollDoesNothingWithoutARowOrWithBadInputs()
    {
        MonsterDropTable table = DropTable(new[] { 0, 10000, 10000, 10000, 10000 });
        ItemPool pool = FullPool(table);
        var random = new ScriptedRandom();
        Assert.Empty(ItemDrop.Roll(table, pool, new DropContext(10, 0, 51, 0), random));      // 副本等级 51 不在行里
        Assert.Empty(ItemDrop.Roll(table, pool, new DropContext(10, 4, 10, 0), random));      // 种类 4 不掉
        Assert.Empty(ItemDrop.Roll(table, pool, new DropContext(10, -1, 10, 0), random));
        Assert.Empty(ItemDrop.Roll(null, pool, new DropContext(10, 0, 10, 0), random));
        Assert.Empty(ItemDrop.Roll(table, null, new DropContext(10, 0, 10, 0), random));
        Assert.Empty(ItemDrop.Roll(table, pool, new DropContext(10, 0, 10, 0), null));
        Assert.Empty(random.Calls);
    }

    [Fact]
    public void RollRepeatsForEachRoll()
    {
        MonsterDropTable table = DropTable(new[] { 0, 10000, 0, 0, 0 }, before: 0, after: 1);
        var pool = new ItemPool(new[] { Stack(101, 10, 0, 10) }, table);
        var random = new ScriptedRandom(0, 1, 5, 1, 1, 1, 0, 1, 5, 1, 1, 1);
        // 两次:每次 类型 1 的 [概率骰、稀有度骰、取物品骰](总权重 10),类型 2、3、4 的概率骰各一次(概率 0,骰 1 > 0 不掉)
        var context = new DropContext(10, 0, 10, 0, 1, 0, 100, 2);
        List<RolledItem> items = ItemDrop.Roll(table, pool, context, random);
        Assert.Equal(2, items.Count);
    }

    [Fact]
    public void ItemProbabilityUsesTheDropRateAndTheMembers()
    {
        // 类型 1 那一行的队伍加成:1、2、3、4 人 = 1、2、3、4;基础概率 1000
        var b = new TokenStreamBuilder();
        b.Tag("[drop prob count]").Int(1);
        b.Tag("[drop prob]").Ints(1, 50, 0, 1000, 0, 0, 0);
        b.Tag("[party member drop bonusrate]").Floats(Enumerable.Repeat(1f, 4).ToArray()).Floats(1f, 2f, 3f, 4f).Floats(Enumerable.Repeat(1f, 12).ToArray());
        MonsterDropTable table = MonsterDropTable.Load(b.Cursor(), out string error);
        Assert.Null(error);
        Assert.Equal(1000, table.ItemProbability(0, 1, 0, 0, 1));
        Assert.Equal(2000, table.ItemProbability(0, 1, 0, 0, 2));
        Assert.Equal(1000, table.ItemProbability(0, 1, 0, 0, 2, 50));
        Assert.Equal(4000, table.ItemProbability(0, 1, 0, 0, 4));
    }

    // ------------------------------------------------------------------ 背包

    [Fact]
    public void InventoryStacksStackablesAndSplitsAtTheLimit()
    {
        var bag = new Inventory(4);
        ItemInfo herb = Stack(1, 1, 0, 1, StackableTypes.Waste);
        herb.StackLimit = 10;
        Assert.Equal(7, bag.Add(herb, 7));
        Assert.Equal(7, bag.Add(herb, 7));                 // 先把第一格填满(再放 3),余下 4 个进新格
        Assert.Equal(14, bag.CountOf(1));
        IReadOnlyList<ItemStack> tab = bag.Tab(InventoryTab.Consumable);
        Assert.Equal(new[] { 10, 4 }, tab.Select(s => s.Count).ToArray());
        Assert.Equal(2, bag.StackCount);
        Assert.Equal(14, bag.TotalCount);
    }

    [Fact]
    public void InventoryEquipmentTakesOneSlotEach()
    {
        var bag = new Inventory(3);
        ItemInfo sword = Equip(5, 1, 0, 1);
        Assert.Equal(1, bag.Add(sword));
        Assert.Equal(1, bag.Add(sword));                   // 同编号的装备也不叠
        Assert.Equal(2, bag.Tab(InventoryTab.Equipment).Count);
        Assert.Equal(2, bag.CountOf(5));
        Assert.Equal(1, Inventory.StackLimitOf(sword));
    }

    [Fact]
    public void InventoryRefusesWhatDoesNotFit()
    {
        var bag = new Inventory(2);
        ItemInfo a = Equip(1, 1, 0, 1);
        ItemInfo b = Equip(2, 1, 0, 1);
        ItemInfo c = Equip(3, 1, 0, 1);
        Assert.Equal(1, bag.Add(a));
        Assert.Equal(1, bag.Add(b));
        Assert.False(bag.CanAdd(c));
        Assert.Equal(0, bag.Add(c));
        Assert.Equal(2, bag.StackCount);
        // 每一页各算各的格:消耗品页还是空的
        ItemInfo herb = Stack(9, 1, 0, 1, StackableTypes.Waste);
        Assert.True(bag.CanAdd(herb, 5));
        Assert.Equal(5, bag.Add(herb, 5));
    }

    [Fact]
    public void InventoryPartialAddReportsHowManyWentIn()
    {
        var bag = new Inventory(2);
        ItemInfo herb = Stack(9, 1, 0, 1, StackableTypes.Waste);
        herb.StackLimit = 5;
        Assert.Equal(10, bag.Add(herb, 12));               // 2 格 × 5 = 10,放不下 2 个
        Assert.False(bag.CanAdd(herb, 1));
        Assert.Equal(0, bag.Add(herb, 1));
    }

    [Fact]
    public void InventoryDefaultLimitAppliesWhenTheScriptHasNone()
    {
        ItemInfo plain = Stack(9, 1, 0, 1, StackableTypes.Waste);
        Assert.Equal(Inventory.DefaultStackLimit, Inventory.StackLimitOf(plain));
        plain.StackLimit = 20;
        Assert.Equal(20, Inventory.StackLimitOf(plain));
        Assert.Equal(1, Inventory.StackLimitOf(null));
    }

    [Fact]
    public void InventoryRemoveTakesFromTheNewestSlotsFirst()
    {
        var bag = new Inventory(8);
        ItemInfo herb = Stack(9, 1, 0, 1, StackableTypes.Waste);
        herb.StackLimit = 5;
        bag.Add(herb, 12);                                 // 5 + 5 + 2
        Assert.True(bag.Remove(9, 3));                     // 先扣最后一格的 2,再从第二格扣 1
        Assert.Equal(new[] { 5, 4 }, bag.Tab(InventoryTab.Consumable).Select(s => s.Count).ToArray());
        Assert.False(bag.Remove(9, 10));                   // 不够:一个也不拿
        Assert.Equal(9, bag.CountOf(9));
        Assert.False(bag.Remove(9, 0));
        Assert.False(bag.Remove(1234, 1));
        Assert.True(bag.Remove(9, 9));
        Assert.Equal(0, bag.StackCount);
    }

    [Theory]
    [InlineData(ItemKind.Equipment, EquipmentParts.Weapon, InventoryTab.Equipment)]
    [InlineData(ItemKind.Equipment, EquipmentParts.Creature, InventoryTab.Equipment)]
    [InlineData(ItemKind.Stackable, StackableTypes.Waste, InventoryTab.Consumable)]
    [InlineData(ItemKind.Stackable, StackableTypes.Throw, InventoryTab.Consumable)]
    [InlineData(ItemKind.Stackable, StackableTypes.Material, InventoryTab.Material)]
    [InlineData(ItemKind.Stackable, StackableTypes.Recipe, InventoryTab.Material)]
    [InlineData(ItemKind.Stackable, StackableTypes.Etc, InventoryTab.Material)]
    [InlineData(ItemKind.Stackable, StackableTypes.Quest, InventoryTab.Quest)]
    [InlineData(ItemKind.Stackable, 19, InventoryTab.Quest)]
    [InlineData(ItemKind.Stackable, StackableTypes.Unknown, InventoryTab.Consumable)]
    public void InventoryTabFollowsTheKind(ItemKind kind, int partOrType, InventoryTab expected)
    {
        ItemInfo item = kind == ItemKind.Equipment ? Equip(1, 1, 0, 1, partOrType) : Stack(1, 1, 0, 1, partOrType);
        Assert.Equal(expected, Inventory.TabOf(item));
    }

    [Fact]
    public void InventoryIgnoresNullAndNonPositiveCounts()
    {
        var bag = new Inventory();
        Assert.Equal(0, bag.Add(null));
        Assert.Equal(0, bag.Add(Stack(1, 1, 0, 1), 0));
        Assert.Equal(0, bag.Add(Stack(1, 1, 0, 1), -2));
        Assert.False(bag.CanAdd(null));
        Assert.False(bag.CanAdd(Stack(1, 1, 0, 1), 0));
        Assert.Equal(0, bag.TotalCount);
        Assert.Equal(Inventory.SlotsPerTab, bag.Capacity);
        Assert.Equal(1, new Inventory(0).Capacity);        // 格数至少 1
    }
}
