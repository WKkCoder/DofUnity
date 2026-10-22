using System;
using System.Collections.Generic;
using Dnf.Game;
using Dnf.Play;
using Dnf.Render;
using NUnit.Framework;

namespace DnfUnity.Tests
{
    /// <summary>
    /// 房间战斗在 Unity(Mono)里的跨运行时核对(证据 E-069):占位伤害靠带种子的 System.Random 做概率进位,
    /// 序列必须与 .NET 一致(src/Dnf.Play.Tests/PlayArtTests.cs 里钉着同一组数,期望值由 .NET 的 System.Random(1) 算出),
    /// 否则命令行 combat-check 与播放器的战斗画面会出现不同的伤害数字;按键时间表(CombatScript)在两个运行时里读出同样的按键。
    /// 完整的合成单元测试在 src/Dnf.Play.Tests(xunit,.NET)。这里不需要游戏数据。
    /// </summary>
    public class CombatRuntimeTests
    {
        private static readonly int[] ReferenceBonuses = { 0, -15, 40, 0, -15, 40, 0, -15, 40, 0, -15, 40, 0, -15, 40, 0, -15, 40, 0, -15, 40, 0, -15, 40 };
        private static readonly int[] ReferenceRolls = { 26, 22, 35, 25, 21, 35, 25, 21, 36, 25, 22, 36, 25, 21, 35, 25, 22, 35, 25, 21, 35, 26, 22, 36 };

        [Test]
        public void SeededRandom_GivesTheSameDoublesAsDotNet()
        {
            var r = new Random(1);
            Assert.AreEqual(0.24866858415709278, r.NextDouble());
            Assert.AreEqual(0.11074397718102856, r.NextDouble());
            Assert.AreEqual(0.46701067987224587, r.NextDouble());
            Assert.AreEqual(0.7716041220219825, r.NextDouble());
            Assert.AreEqual(0.657518893786482, r.NextDouble());
        }

        [Test]
        public void PlaceholderDamage_FollowsTheSameSequenceAsDotNet()
        {
            var random = new Random(1);
            var rolls = new List<int>();
            foreach (int bonus in ReferenceBonuses)
            {
                rolls.Add(PlaceholderDamage.Roll(bonus, random));
            }
            CollectionAssert.AreEqual(ReferenceRolls, rolls);
        }

        private sealed class Recorder : IActionSession
        {
            public readonly List<string> Calls = new List<string>();

            public string MapPath { get { return "x"; } }
            public TownHero Hero { get { return null; } }
            public TownCamera Camera { get { return null; } }
            public int LayerCount { get { return 0; } }
            public int NpcCount { get { return 0; } }

            public void Update(double deltaMs, int dirX, int dirY)
            {
                Calls.Add(dirX + "," + dirY + ",-");
            }

            public void Update(double deltaMs, int dirX, int dirY, bool attackPressed, bool jumpPressed)
            {
                Calls.Add(dirX + "," + dirY + "," + (attackPressed ? "A" : "-") + (jumpPressed ? "J" : string.Empty));
            }

            public List<SceneSprite> Build(long timeMs)
            {
                return new List<SceneSprite>();
            }
        }

        [Test]
        public void CombatScript_StepsTheSessionWithTheScriptedKeys()
        {
            var rec = new Recorder();
            CombatScript script = CombatScript.Parse("0:right,0.05:right+attack,0.1:none");
            for (int i = 0; i < 8; i++)
            {
                script.Step(rec);
            }
            CollectionAssert.AreEqual(
                new[] { "1,0,-", "1,0,-", "1,0,-", "1,0,A", "1,0,-", "1,0,-", "0,0,-", "0,0,-" },
                rec.Calls);
            Assert.AreEqual(8 * (1000.0 / 60.0), script.TimeMs, 1e-9);
        }

        [Test]
        public void CombatScript_RejectsMalformedItems()
        {
            Assert.Throws<FormatException>(() => CombatScript.Parse("1"));
            Assert.Throws<FormatException>(() => CombatScript.Parse("1:dance"));
            Assert.IsTrue(CombatScript.Parse("").IsEmpty);
        }

        [Test]
        public void CombatScript_PassesTheJumpEdgeToTheSession()
        {
            var rec = new Recorder();
            CombatScript script = CombatScript.Parse("0.05:jump");
            for (int i = 0; i < 5; i++)
            {
                script.Step(rec);
            }
            CollectionAssert.AreEqual(new[] { "0,0,-", "0,0,-", "0,0,-", "0,0,-J", "0,0,-" }, rec.Calls);
        }

        /// <summary>
        /// 主角打怪物的原版公式(证据 E-110)在 Mono 里的核对:与 src/Dnf.Game.Tests/HitDamageTests.cs 同一组合成面板,期望值是手算的整数
        /// (浮点步骤逐步落地成单精度,两个运行时必须算出同样的数)。随机幅度的底与系数为 0,结果与种子无关。
        /// </summary>
        [Test]
        public void HeroFormulaDamage_GivesTheHandComputedValues()
        {
            var k = new DamageConstants
            {
                PhysicalDamageRate = 100f,
                MagicalDamageRate = 100f,
                PhysicalReduceRate = 20f,
                MagicalReduceRate = 20f,
                CommonElementBase = 100f,
            };
            var attacker = new AttackerPanel { Level = 10, PhysicalAttack = 50, MagicalAttack = 100, PhysicalBonusBase = 30, MagicalBonusBase = 20 };
            var defender = new DefenderPanel { Level = 5 };
            var plain = new HitInput(0f, true, AttackKind.Physic, 0f, 0, -1000f);
            bool critical;

            // (0 + 装备物攻 30) × (物攻 50 / 100 + 1) = 45
            Assert.AreEqual(45, HitDamage.Roll(plain, attacker, defender, false, k, 1u, out critical));
            Assert.IsFalse(critical);
            // 额外命中:45 × 1.1 = 49.5 → 49
            Assert.AreEqual(49, HitDamage.Roll(plain, attacker, defender, true, k, 1u, out critical));
            // 防御 200、等级 10、系数 20:200 / (10 × 20 + 200) = 0.5 → 22.5 → 22
            Assert.AreEqual(22, HitDamage.Roll(plain, attacker, new DefenderPanel { Level = 5, PhysicalDefense = 200 }, false, k, 1u, out critical));
            // 必暴击:45 × 1.5 = 67.5 → 67
            var crit = new HitInput(0f, true, AttackKind.Physic, 0f, 0, 10000f);
            Assert.AreEqual(67, HitDamage.Roll(crit, attacker, defender, false, k, 1u, out critical));
            Assert.IsTrue(critical);
            // 魔法:装备魔攻 20 × 2(加成 100)× (魔攻 100 / 100 + 1) = 80
            var magic = new HitInput(0f, true, AttackKind.Magic, 100f, 0, -1000f);
            Assert.AreEqual(80, HitDamage.Roll(magic, attacker, defender, false, k, 1u, out critical));
            // 技能:倍率 160、固定值 40 → (25 + 装备魔攻 10)× 1.6 = 56
            attacker.MagicalAttack = 0;
            attacker.MagicalBonusBase = 10;
            Assert.AreEqual(56, HitDamage.Roll(magic.WithSkillDamage(160, 40), attacker, defender, false, k, 1u, out critical));
        }

        /// <summary>
        /// 击杀经验与升级(证据 E-111)在 Mono 里的核对:与 src/Dnf.Game.Tests/ProgressTests.cs 同一组合成数字,期望值是手算的整数。
        /// x87 的 80 位中间结果与 64 位截断在两个运行时里必须一致(落成单精度会得到不同的整数)。
        /// </summary>
        [Test]
        public void KillExpAndLeveling_GiveTheHandComputedValues()
        {
            ExpRates neutral = ExpRates.Neutral;
            Assert.AreEqual(50u, KillExp.MonsterTotalExp(100, 0, 0, 1f, 1f, false, neutral));
            Assert.AreEqual(1u, KillExp.MonsterTotalExp(3, 0, 0, 1f, 1f, false, neutral));              // 1.5 → 1
            Assert.AreEqual(6u, KillExp.MonsterTotalExp(20, 0, 0, 1f, 0.7f, false, neutral));           // 10 × 0.7f 留在 80 位里是 6.99999988 → 6(落成单精度会得 7)
            Assert.AreEqual(60u, KillExp.MonsterTotalExp(100, 0, 0, 1.2f, 1f, false, neutral));         // 50 × 1.2f = 60.0000024 → 60
            Assert.AreEqual(112u, KillExp.MemberExp(100, 1.12f, 1));                                   // 112.0000005 → 112
            Assert.AreEqual(5u, KillExp.MemberExp(100, 0.05f, 1));                                     // 5.00000007 → 5
            Assert.AreEqual(25u, KillExp.MemberExp(101, 0.5f, 2));                                     // 25.25 → 25
            // 等级差倍率与客户端的单精度立即数逐位一致
            Assert.AreEqual(0x3f8f5c29, BitConverter.SingleToInt32Bits(KillExp.LevelGapRate(10, 12)));
            Assert.AreEqual(0x3d4ccccd, BitConverter.SingleToInt32Bits(KillExp.LevelGapRate(20, 10)));

            // 合成经验表:升到 L 级的累计经验 = 100 × (L - 1) × L / 2;合成技能点表:升到 L 级给 L 点
            var thresholds = new List<int>();
            for (int level = 2; level <= 91; level++)
            {
                thresholds.Add(100 * (level - 1) * level / 2);
            }
            var skillPoints = new List<int>();
            for (int level = 0; level <= 95; level++)
            {
                skillPoints.Add(level);
            }
            ExpGainResult r = Leveling.Gain(new ExpTable(thresholds), new SpTable(skillPoints), 1, 0, 1000);   // 正好到 5 级的门槛
            Assert.AreEqual(5, r.Level);
            Assert.AreEqual(1000, r.Exp);
            Assert.AreEqual(4, r.LevelsGained);
            Assert.AreEqual(2 + 3 + 4 + 5, r.SkillPoints);
        }

        /// <summary>
        /// 底栏技能槽的数(证据 E-112)在 Mono 里的核对:冷却遮罩盖住的行数 = 28 乘剩余比例向零取整(夹在 0 到 28),
        /// 与 src/Dnf.Play.Tests/HudArtTests.cs 同一组数;技能槽的显示状态结构按构造参数原样读出。
        /// </summary>
        [Test]
        public void HudSkillSlot_CooldownRowsAreHandComputed()
        {
            Assert.AreEqual(0, HudArt.CooldownRows(0.0));
            Assert.AreEqual(0, HudArt.CooldownRows(double.NaN));
            Assert.AreEqual(0, HudArt.CooldownRows(0.03));              // 0.84 → 0
            Assert.AreEqual(2, HudArt.CooldownRows(0.08));              // 2.24 → 2
            Assert.AreEqual(14, HudArt.CooldownRows(0.5));
            Assert.AreEqual(25, HudArt.CooldownRows(0.9));              // 25.2 → 25
            Assert.AreEqual(27, HudArt.CooldownRows(0.999));
            Assert.AreEqual(28, HudArt.CooldownRows(1.0));
            Assert.AreEqual(28, HudArt.CooldownRows(3.0));

            var s = new HudSkillState(1, null, false, 0.25, 3, true);
            Assert.AreEqual((1, false, 0.25, 3, true), (s.Slot, s.Usable, s.CooldownFraction, s.Level, s.LabelGray));
        }
    }
}
