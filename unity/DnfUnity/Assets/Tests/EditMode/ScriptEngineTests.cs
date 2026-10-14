using System;
using System.IO;
using System.Linq;
using Dnf.Data;
using Dnf.Script;
using Dnf.Squirrel;
using NUnit.Framework;

namespace DnfUnity.Tests
{
    /// <summary>
    /// 在 Unity 编辑器(Mono)里跑 Squirrel 原生插件与脚本宿主:插件能被加载、P/Invoke 与回调往返可用、
    /// 脚本按原版的加载约定(证据 E-035)运行。完整的合成测试在 src/Dnf.Squirrel.Tests 与 src/Dnf.Script.Tests(xunit,.NET)。
    /// 真实数据测试只在设置了 DNF_DIR 时运行,数字与 `dnf sq-check` 一致。
    /// </summary>
    public class ScriptEngineTests
    {
        [Test]
        public void Squirrel_PluginLoadsAndReportsVersionAndIntegerSize()
        {
            Assert.AreEqual(320, SqVm.Version);
            using (var vm = new SqVm())
            {
                Assert.IsTrue(vm.TryRun("t", "BIG <- 1 << 40;", out string error), error);
                Assert.IsTrue(vm.TryGetGlobalInteger("BIG", out long big));
                Assert.AreEqual(1L << 40, big, "整数是 64 位(-D_SQ64)");
            }
        }

        [Test]
        public void Squirrel_ScriptsAndNativesCallEachOtherBothWays()
        {
            using (var vm = new SqVm())
            {
                long seen = 0;
                vm.RegisterGlobal("host_add", c => c.Return(c.Int(0) + c.Int(1)));
                vm.RegisterGlobal("host_note", c =>
                {
                    seen = c.Int(0);
                    return c.ReturnNothing();
                });
                Assert.IsTrue(vm.TryRun("t", "function twice(x) { host_note(host_add(x, x)); return host_add(x, x) * 2; }", out string error), error);
                Assert.IsTrue(vm.TryCall("twice", out object result, out error, 21L), error);
                Assert.AreEqual(42L, seen);
                Assert.AreEqual(84L, Convert.ToInt64(result));
            }
        }

        [Test]
        public void Squirrel_NativeExceptionsBecomeScriptErrorsAndTheVmStaysUsable()
        {
            using (var vm = new SqVm())
            {
                vm.RegisterGlobal("host_fail", c => { throw new InvalidOperationException("boom"); });
                Assert.IsFalse(vm.TryRun("t", "host_fail();", out string error));
                StringAssert.Contains("boom", error);
                Assert.IsTrue(vm.TryRun("t", "OK <- 1;", out error), error);
            }
        }

        [Test]
        public void ScriptHost_FollowsTheEngineLoadContract()
        {
            var source = new MemoryScriptSource()
                .Add("sqr/loadstate.nut", "IRDSQRCharacter.pushScriptFiles(\"h.nut\"); IRDSQRCharacter.pushState(8, \"s.nut\", \"S\", STATE_X, SKILL_X); IRDSQRCharacter.pushPassiveObj(\"po.nut\", 24100);")
                .Add("sqr/h.nut", "STATE_X <- 70; SKILL_X <- 12;")
                .Add("sqr/s.nut", "function onSetState_S(obj, state, datas, isResetTimer) { }")
                .Add("sqr/po.nut", "function setCustomData_po(obj, reciveData) { }");
            using (var host = new ScriptHost(source))
            {
                ScriptLoadSummary summary = host.LoadAll();
                Assert.IsTrue(summary.EntryOk);
                Assert.AreEqual(1, summary.States);
                Assert.AreEqual(1, summary.PassiveObjects);
                ScriptRegistration state = host.Registrations.Single(r => r.Kind == RegistrationKind.State);
                Assert.AreEqual(70, state.Id);
                Assert.AreEqual(12, state.Skill);
                Assert.AreEqual("sqr/s.nut", state.ResolvedPath);
            }
        }

        [Test]
        public void ScriptHost_RealData_LoadsTheWholeScriptChain()
        {
            string dir = Environment.GetEnvironmentVariable("DNF_DIR");
            if (string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, "Script.pvf")))
            {
                Assert.Ignore("未设置 DNF_DIR(或目录里没有 Script.pvf),跳过真实数据的脚本加载");
            }
            using (PvfArchive pvf = PvfArchive.Open(Path.Combine(dir, "Script.pvf")))
            using (var host = new ScriptHost(new PvfScriptSource(pvf)))
            {
                ScriptLoadSummary summary = host.LoadAll();
                Assert.IsTrue(summary.EntryOk, string.Join("\n", host.Errors));
                Assert.AreEqual(0, summary.Errors, string.Join("\n", host.Errors));
                Assert.AreEqual(0, summary.Missing);
                Assert.AreEqual(0, host.UnknownNames.Count, string.Join(",", host.UnknownNames.Keys));
                // 与 `dnf sq-check` 的输出一致(证据 E-035)
                Assert.AreEqual(169, summary.LoadedScripts);
                Assert.AreEqual(28, summary.ScriptFiles);
                Assert.AreEqual(67, summary.States);
                Assert.AreEqual(104, summary.PassiveObjects);
                Assert.IsTrue(host.Vm.HasFunction("sq_InitFrameIndices"));
            }
        }
    }
}
