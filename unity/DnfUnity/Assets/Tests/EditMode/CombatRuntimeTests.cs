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
    }
}
