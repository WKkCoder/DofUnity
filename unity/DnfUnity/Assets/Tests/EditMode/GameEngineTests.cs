using System;
using System.IO;
using System.Linq;
using Dnf.Data;
using Dnf.Game;
using Dnf.Script;
using NUnit.Framework;

namespace DnfUnity.Tests
{
    /// <summary>
    /// 在 Unity 编辑器(Mono)里跑 Dnf.Game 的最小世界:同样的原版脚本、同样的输入,结果要和 .NET 里的 `dnf game-check` 一致
    /// (跨运行时的确定性检查:整数运算、Squirrel 的 64 位整数、固定种子的随机数)。
    /// 完整的合成测试在 src/Dnf.Game.Tests(xunit,.NET)。真实数据测试只在设置了 DNF_DIR 时运行,数字来自 E-037 与 E-038。
    /// </summary>
    public class GameEngineTests
    {
        private const int Job = 8; // 男魔法师(E-036)

        private static PvfArchive OpenOrIgnore()
        {
            string dir = Environment.GetEnvironmentVariable("DNF_DIR");
            if (string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, "Script.pvf")))
            {
                Assert.Ignore("未设置 DNF_DIR(或目录里没有 Script.pvf),跳过真实数据的游戏世界测试");
            }
            return PvfArchive.Open(Path.Combine(dir, "Script.pvf"));
        }

        [Test]
        public void GameWorld_RealData_LoadsTheWholeScriptChain()
        {
            using (PvfArchive pvf = OpenOrIgnore())
            {
                var source = new PvfScriptSource(pvf);
                using (var world = new GameWorld(new GameData(source), new ScriptHost(source)))
                {
                    ScriptLoadSummary load = world.LoadScripts();
                    Assert.IsTrue(load.EntryOk);
                    Assert.AreEqual(169, load.LoadedScripts);
                    Assert.AreEqual(67, load.States);
                    Assert.AreEqual(104, load.PassiveObjects);
                    Assert.AreEqual(0, load.Errors);
                    Assert.AreEqual(58, world.AllStates.Count(s => s.IsSkill), "登记了技能的状态数与 `dnf game-check --sweep` 一致(E-038)");
                }
            }
        }

        [Test]
        public void GameWorld_RealData_IceCrashRunsToTheEndAsInDotNet()
        {
            using (PvfArchive pvf = OpenOrIgnore())
            {
                var source = new PvfScriptSource(pvf);
                using (var world = new GameWorld(new GameData(source), new ScriptHost(source)))
                {
                    world.LoadScripts();
                    Assert.IsTrue(world.Vm.TryGetGlobalInteger("SKILL_ICE_CRASH", out long skill));
                    Assert.IsTrue(world.Vm.TryGetGlobalInteger("STATE_ICE_CRASH", out long state));
                    SkillDef def = world.Data.GetSkill(Job, (int)skill);
                    GameObject hero = world.SpawnCharacter(Job, "hero", 200, 150, 0, Direction.Right);
                    hero.SkillLevels[(int)skill] = Math.Max(1, def != null && def.MaximumLevel > 0 ? Math.Min(def.MaximumLevel, 10) : 10);
                    world.SpawnDummy("dummy", 270, 150, 0, new Aabb(-20, 20, -10, 10, 0, 80));
                    world.RequestSkill(hero, (int)skill);
                    int returned = -1;
                    for (int t = 0; t < 6000; t += 10)
                    {
                        world.Step(10);
                        if (t > 50 && hero.State == 0 && hero.StateInfo == null)
                        {
                            returned = world.TimeMs;
                            break;
                        }
                    }
                    Assert.AreEqual(0, world.Errors.Count, string.Join("\n", world.Errors.Distinct().Take(5)));
                    Assert.AreEqual(1130, returned, "在 .NET 里 IceCrash 于 1130 ms 回到站立(E-037)");
                    Assert.AreEqual(19, world.Hits.Count, "命中次数与 .NET 一致");
                }
            }
        }

        // 三个覆盖了不同引擎语义的技能:FrozenLand(动画循环标记,E-039)、PandemoniumEx(时间事件次数 0)、Awakening(叠加动画与 GetAnimationMap);
        // 期望值是 .NET 里 `dnf game-check --sweep --only <后缀>` 的结果(每个技能开始前重置脚本随机数)。
        // 2026-10-16 刷新:E-046、E-048 改正了原生接口的几处实现后,.NET 里这两个技能的时长变了(PandemoniumEx 1820 → 1790,
        // Awakening 3690 → 3250),当时没有重跑 Unity 测试,常数一直是旧的;Unity 与 .NET 的结果现在重新核对为一致。
        [TestCase("FrozenLand", 4340)]
        [TestCase("PandemoniumEx", 1790)]
        [TestCase("Awakening", 3250)]
        public void GameWorld_RealData_SweepSkillsFinishAtTheSameTimeAsInDotNet(string suffix, int expectedMs)
        {
            using (PvfArchive pvf = OpenOrIgnore())
            {
                var source = new PvfScriptSource(pvf);
                using (var world = new GameWorld(new GameData(source), new ScriptHost(source)))
                {
                    world.LoadScripts();
                    StateInfo s = world.AllStates.Single(x => x.IsSkill && string.Equals(x.Suffix, suffix, StringComparison.OrdinalIgnoreCase));
                    world.Errors.Clear();
                    world.ResetScriptRandom();
                    GameObject hero = world.SpawnCharacter(s.Job, "hero", 200, 150, 0, Direction.Right, s.Module);
                    SkillDef def = world.Data.GetSkill(s.Job, s.Skill);
                    hero.SkillLevels[s.Skill] = Math.Max(1, def != null && def.MaximumLevel > 0 ? Math.Min(def.MaximumLevel, 10) : 10);
                    world.SpawnDummy("dummy", 270, 150, 0, new Aabb(-20, 20, -10, 10, 0, 80));
                    world.RequestSkill(hero, s.Skill);
                    bool entered = false;
                    int returned = -1;
                    int start = world.TimeMs;
                    for (int t = 0; t < 12000; t += 10)
                    {
                        world.Step(10);
                        if (hero.State == s.State)
                        {
                            entered = true;
                        }
                        if (entered && hero.State != s.State && hero.State == 0)
                        {
                            returned = world.TimeMs - start;
                            break;
                        }
                    }
                    Assert.AreEqual(0, world.Errors.Count, string.Join("\n", world.Errors.Distinct().Take(5)));
                    Assert.IsTrue(entered, suffix + " 没有进入状态");
                    Assert.AreEqual(expectedMs, returned, suffix + " 回到站立的时间与 .NET 不一致");
                }
            }
        }
    }
}
