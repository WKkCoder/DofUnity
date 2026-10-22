using System.Globalization;
using Dnf.Game;
using Dnf.Play;
using Dnf.Render;

namespace Dnf.Cli.Analysis;

/// <summary>
/// hero-damage:主角打一间房间里每种怪物的伤害分布(证据 E-110)。
/// 读房间的怪物与主角的 .chr、武器 .equ、技能 .skl,用 <see cref="RoomCombatSession.RollFormulaDamage"/> 对每一种招式
/// (每段普攻、奔跑攻击、空中攻击、带自己攻击信息的技能)各掷 --samples 次(默认 2000),打印最小/平均/最大与暴击占比,
/// 分"这只怪第一次挨打(算额外命中)"与"之后(不算)"两组。种子用 --seed 起、逐次加一,同一组参数得到同样的结果。
/// 面板与分布里的数值来自用户自己的数据,只打印在本机的终端上(不要把它们抄进入库的文件)。
/// 没有攻击面板或公式常量(缺 .chr 或 EtcParameter)时直接报错,不退回占位伤害。
/// </summary>
internal static class HeroDamageCheck
{
    public static int Run(DnfLocation loc, Options opts)
    {
        using DnfAssets assets = DnfAssets.Open(loc.DnfDir);
        RoomCombatSession session = DemoCombat.CreateSession(assets, opts.MapPath, opts.HeroWeapon ?? DemoHero.StarterWeapon, opts.Seed, true);
        HeroPanel? panel = session.Panel;
        if (panel == null || !session.UsesFormulaDamage)
        {
            throw new UserError("没读到主角的攻击面板或伤害公式常量(缺 .chr 或 EtcParameter.etc),无法按原版公式算伤害");
        }

        int samples = Math.Max(1, opts.Samples);
        Console.WriteLine("房间 " + opts.MapPath + ":怪物 " + session.Monsters.Count + " 个;主角等级 " + panel.Level + ",物理攻击 " + panel.PhysicalAttack
            + ",魔法攻击 " + panel.MagicalAttack + ";武器与装备 物攻 " + panel.Equipment.PhysicalAttack + " 魔攻 " + panel.Equipment.MagicalAttack
            + " 物暴 " + F(panel.Equipment.PhysicalCritical) + " 魔暴 " + F(panel.Equipment.MagicalCritical) + ";每种招式掷 " + samples + " 次,种子从 " + opts.Seed + " 起");

        var moves = new List<(string Name, HeroStrikeInfo Info)>();
        for (int i = 0; i < session.Strikes.Count; i++)
        {
            moves.Add(("普攻 " + (i + 1), session.Strikes[i]));
        }
        moves.Add(("奔跑攻击", session.DashStrike));
        moves.Add(("空中攻击", session.JumpStrike));
        if (session.Skills != null)
        {
            foreach (HeroSkillSlot slot in session.Skills.Slots)
            {
                if (slot.Strike.HasValue)
                {
                    moves.Add(("技能 " + slot.Key + "(编号 " + slot.SkillId + " 级 " + slot.Level + ")", slot.Strike.Value));
                }
            }
        }

        var seen = new HashSet<int>();
        foreach (MonsterActor monster in session.Monsters)
        {
            if (!seen.Add(monster.Code))
            {
                continue;
            }
            DefenderPanel defender = monster.Defense ?? new DefenderPanel { Level = monster.Level };
            Console.WriteLine("怪物 编号 " + monster.Code + (monster.IsBoss ? "(首领)" : string.Empty) + " 等级 " + defender.Level + " 物防 " + defender.PhysicalDefense
                + " 魔防 " + defender.MagicalDefense + " 生命 " + monster.HpMax
                + (HitDamage.BonusHitApplies(defender.Level, false) ? ";低于 " + HitDamage.BonusHitLevelLimit + " 级:第一击算额外命中" : ";不低于 " + HitDamage.BonusHitLevelLimit + " 级:没有额外命中"));
            foreach ((string name, HeroStrikeInfo info) in moves)
            {
                Console.WriteLine("  " + name + "  " + Describe(info));
                monster.LastAttackerId = 0;
                Console.WriteLine("    第一击 " + Distribution(session, info, monster, samples, opts.Seed));
                monster.LastAttackerId = RoomCombatSession.HeroAttackerId;
                Console.WriteLine("    之后   " + Distribution(session, info, monster, samples, opts.Seed));
            }
            monster.LastAttackerId = 0;
        }
        return 0;
    }

    private static string Distribution(RoomCombatSession session, HeroStrikeInfo info, MonsterActor monster, int samples, int seed)
    {
        long sum = 0;
        int min = int.MaxValue;
        int max = int.MinValue;
        int crits = 0;
        for (int i = 0; i < samples; i++)
        {
            int damage = session.RollFormulaDamage(info, monster, unchecked((uint)(seed + i)), out bool critical);
            sum += damage;
            min = Math.Min(min, damage);
            max = Math.Max(max, damage);
            if (critical)
            {
                crits++;
            }
        }
        return "最小 " + min + " 平均 " + ((double)sum / samples).ToString("F1", CultureInfo.InvariantCulture) + " 最大 " + max
            + " 暴击 " + (100.0 * crits / samples).ToString("F1", CultureInfo.InvariantCulture) + "%";
    }

    private static string Describe(HeroStrikeInfo info)
    {
        HitInput input = info.Input;
        return (input.Kind == AttackKind.Magic ? "魔法" : input.Kind == AttackKind.Physic ? "物理" : "无视防御")
            + (input.UseBonusBase ? ",带武器攻击力" : ",不带武器攻击力")
            + ",伤害系数 " + F(input.DamagePercent) + (input.Base > 0f ? ",基础伤害 " + F(input.Base) : string.Empty)
            + (input.AbsoluteDamage != 0 ? ",固定伤害 " + input.AbsoluteDamage : string.Empty)
            + (input.CriticalHit != 0f ? ",暴击加成 " + F(input.CriticalHit) : string.Empty)
            + (input.ElementMask != 0 ? ",带元素" : string.Empty)
            + (info.IsSkillDamage ? "(技能伤害)" : string.Empty);
    }

    private static string F(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}
