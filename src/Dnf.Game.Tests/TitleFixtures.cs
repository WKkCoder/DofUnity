using Dnf.Data;
using Dnf.Script;

namespace Dnf.Game.Tests;

/// <summary>
/// 称号与成就各组测试共用的合成数据(编号全是自己编的:称号物品 901 起、成就 9001 起、任务 8001 起;数值是随手写的小整数,与原版数据无关)。
/// 一本"小图鉴":普通分类六格(第 3 格是空格)、特殊两格、绝望一格、活动两格;普通分类第一页(收前三格)带整页奖励,特殊分类第一页锁着。
/// </summary>
internal static class TitleFixtures
{
    public const int WeaponCode = 970;
    public const int StackableCode = 971;

    // 图鉴里的称号物品编号(格 → 编号)
    public const int General0 = 901;
    public const int General1 = 902;
    public const int General2 = 903;
    public const int General2Alt = 913;
    public const int General4 = 904;
    public const int General5 = 905;
    public const int Specific0 = 906;
    public const int Specific1 = 907;
    public const int Despair0 = 908;

    public static ScriptLine[] Lines(TokenStreamBuilder b)
    {
        (ScriptToken[] _, PvfStringTable strings) = b.Build();
        return ScriptDocument.Parse(b.Bytes(), strings);
    }

    /// <summary>"小图鉴"的脚本行(写法与真实的 titlebook.etc 一致:顶层标签、成组的格定义、一页一页的奖励)。</summary>
    public static ScriptLine[] BookLines()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[default reward]").Tag("[hp max]").Str("+").Int(100).Tag("[mp max]").Str("+").Int(50).Tag("[/default reward]");

        b.Tag("[title collection info]").Str("general").Int(6)
            .Ints(0, 0, 9001, 1, General0)
            .Ints(1, 0, 9002, 1, General1)
            .Ints(2, 0, 9003, 2, General2, General2Alt)
            .Ints(3, -1)
            .Ints(4, 0, 9004, 1, General4)
            .Ints(5, 0, -1, 1, General5)
            .Tag("[/title collection info]");
        b.Tag("[title collection info]").Str("specific").Int(2)
            .Ints(0, 0, 9005, 1, Specific0)
            .Ints(1, 0, 9006, 1, Specific1)
            .Tag("[/title collection info]");
        b.Tag("[title collection info]").Str("despair").Int(1)
            .Ints(0, 0, 9007, 1, Despair0)
            .Tag("[/title collection info]");
        b.Tag("[title collection info]").Str("event").Int(2)
            .Ints(0, 1, -1, 1, -1)
            .Ints(1, 1, -1, 1, -1)
            .Tag("[/title collection info]");

        b.Tag("[section]").Str("general");
        b.Tag("[section name]").Keyed(1, "sec_g0");
        b.Tag("[section icon]").Str("icon/g0.img").Int(3);
        b.Tag("[frame type]").Int(2);
        b.Tag("[name]").Keyed(1, "rew_g0");
        b.Tag("[image path]").Str("img/g0.img").Int(1);
        b.Tag("[effect path]").Str("eff/g0.ani");
        b.Tag("[hp max]").Str("+").Int(30);
        b.Tag("[all element]").Str("+").Int(5);
        b.Tag("[give emotion]").Int(7);
        b.Tag("[send postal]").Ints(950, 2).Keyed(1, "mail_title").Keyed(1, "mail_body");
        b.Tag("[/reward type]");
        b.Tag("[/section]");
        b.Tag("[section]").Str("specific");
        b.Tag("[section name]").Keyed(1, "sec_s0");
        b.Tag("[lock message]").Keyed(1, "locked_s0");
        b.Tag("[physical attack]").Str("+").Int(7);
        b.Tag("[/reward type]");
        b.Tag("[/section]");

        b.Tag("[custom condition message]").Str("general").Int(1).Keyed(1, "cond_g1").Str("specific").Int(0).Keyed(1, "cond_s0");
        b.Tag("[adjust list]").Ints(8001, General0, 9001, 8004, General4, 9004);
        b.Tag("[view titlebook eventmark]").Int(1);
        b.Tag("[titlebook event minlevel]").Int(20);
        return Lines(b);
    }

    public static TitleBookDef Book() => TitleBookScript.Parse(BookLines());

    /// <summary>
    /// 十二格的普通分类(前 11 格有成就、第 12 格空),两页:第一页写满 10 格(终点 10),第二页的起点 10、被第一个空格截到 11——
    /// 用来钉服务端"一页写满时下一页的第一格会被判给这一页"的写法。成就 9100 起、称号 9200 起。
    /// </summary>
    public static TitleBookDef QuirkBook()
    {
        var b = new TokenStreamBuilder();
        b.Tag("[title collection info]").Str("general").Int(12);
        for (int i = 0; i < 11; i++)
        {
            b.Ints(i, 0, 9100 + i, 1, 9200 + i);
        }
        b.Ints(11, -1);
        b.Tag("[/title collection info]");
        b.Tag("[section]").Str("general");
        b.Tag("[section name]").Keyed(1, "p0");
        b.Tag("[send postal]").Ints(951, 1).Keyed(1, "t0").Keyed(1, "b0");
        b.Tag("[/reward type]");
        b.Tag("[section name]").Keyed(1, "p1");
        b.Tag("[/reward type]");
        b.Tag("[/section]");
        return TitleBookScript.Parse(Lines(b));
    }

    // ------------------------------------------------------------------ 物品

    public static ItemInfo TitleItem(int code, EquipStats stats = null, int level = 1)
    {
        var item = new ItemInfo(code, "equipment/character/common/title/t" + code + ".equ", ItemKind.Equipment)
        {
            Part = EquipmentParts.TitleName,
            MinimumLevel = level,
            Stats = stats,
        };
        item.UsableJobs.Add("[all]");
        return item;
    }

    public static Dictionary<int, ItemInfo> Items()
    {
        var items = new Dictionary<int, ItemInfo>();
        foreach ((int code, EquipStat stat, double value) in new[]
        {
            (General0, EquipStat.HpMax, 10.0), (General1, EquipStat.PhysicalAttack, 5.0), (General2, EquipStat.MpMax, 4.0), (General2Alt, EquipStat.MpMax, 40.0),
            (General4, EquipStat.PhysicalDefense, 3.0), (General5, EquipStat.MagicalAttack, 6.0), (Specific0, EquipStat.HpMax, 20.0),
            (Specific1, EquipStat.MpMax, 8.0), (Despair0, EquipStat.PhysicalAttack, 9.0),
        })
        {
            items[code] = TitleItem(code, EquipStats.Empty.With(stat, value));
        }
        var weapon = new ItemInfo(WeaponCode, "equipment/character/swordman/synthw970.equ", ItemKind.Equipment) { Part = EquipmentParts.Weapon, MinimumLevel = 1 };
        weapon.UsableJobs.Add("[all]");
        items[WeaponCode] = weapon;
        items[StackableCode] = new ItemInfo(StackableCode, "stackable/p971.stk", ItemKind.Stackable) { StackableType = StackableTypes.Waste };
        return items;
    }

    public static Func<int, ItemInfo> Lookup(Dictionary<int, ItemInfo> items = null)
    {
        Dictionary<int, ItemInfo> map = items ?? Items();
        return code => map.TryGetValue(code, out ItemInfo item) ? item : null;
    }

    // ------------------------------------------------------------------ 成就

    public static AchievementDef Achievement(int id, QuestKind kind, int minLevel, int titleCode, int[] counts = null, int[] intData = null, AchievementPostal postal = default)
    {
        var quest = new QuestDef(id, "n_quest/a" + id + ".qst")
        {
            Grade = QuestGrade.Title,
            Kind = kind,
            MinLevel = minLevel,
            IntData = intData ?? Array.Empty<int>(),
        };
        if (titleCode != 0)
        {
            quest.Rewards.Add(new QuestItem(titleCode, 1));
        }
        return new AchievementDef(quest, counts ?? Array.Empty<int>(), postal);
    }

    /// <summary>
    /// 9001:完成任务 8001(默认计数 1,0,0;带任务邮件)→ 称号 901;9002:猎杀,等级 ≥ 10,计数 3 → 902;9003:完成任务 8003,计数 2,1,3 → 903;
    /// 9004:完成任务 8004 → 904;9005:使用物品,计数 1 → 906;9006:完成任务 8006 → 907;9007:完成任务 8007 → 908(绝望);
    /// 9008:取得分数,计数 1,没有称号奖励;9009:猎杀敌人,计数 1,称号编号 999(物品库里没有)。
    /// 另有一个分类不是 [title] 的任务(8999),不应进目录。
    /// </summary>
    public static AchievementCatalog Achievements()
    {
        return new AchievementCatalog(new[]
        {
            Achievement(9001, QuestKind.ClearQuest, 1, General0, null, new[] { 8001 }, new AchievementPostal(960, 1, new TitleText(1, "ach_mail_t"), new TitleText(1, "ach_mail_b"))),
            Achievement(9002, QuestKind.HuntMonster, 10, General1, new[] { 3 }),
            Achievement(9003, QuestKind.ClearQuest, 1, General2, new[] { 2, 1, 3 }, new[] { 8003 }),
            Achievement(9004, QuestKind.ClearQuest, 1, General4, null, new[] { 8004 }),
            Achievement(9005, QuestKind.UseItem, 1, Specific0, new[] { 1 }),
            Achievement(9006, QuestKind.ClearQuest, 1, Specific1, null, new[] { 8006 }),
            Achievement(9007, QuestKind.ClearQuest, 1, Despair0, null, new[] { 8007 }),
            Achievement(9008, QuestKind.GetScore, 1, 0, new[] { 1 }),
            Achievement(9009, QuestKind.HuntEnemy, 1, 999, new[] { 1 }),
            new AchievementDef(new QuestDef(8999, "n_quest/q8999.qst") { Grade = QuestGrade.Epic, Kind = QuestKind.ClearQuest }, Array.Empty<int>(), default),
        });
    }

    public static TitleSystem NewSystem(Dictionary<int, ItemInfo> items = null)
    {
        return new TitleSystem(Book(), Achievements(), Lookup(items));
    }
}
