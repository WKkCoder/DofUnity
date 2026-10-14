using Dnf.Script;

namespace Dnf.Script.Tests;

/// <summary>
/// 脚本宿主的合成测试。里面的脚本都是测试自己写的,只模仿原版的加载约定(证据 E-035),不含任何原版内容。
/// </summary>
public sealed class ScriptHostTests
{
    private static MemoryScriptSource Sample()
    {
        return new MemoryScriptSource()
            .Add("sqr/loadstate.nut", @"
sq_RunScript(""common.nut"");
sq_RunScript(""Character/job_load_state.nut"");
sq_RunScript(""Character/av_load_state.nut"");
")
            .Add("sqr/common.nut", "DEBUG <- isDebugMode(); NULL <- null;")
            .Add("sqr/init_character.nut", "function sq_InitFrameIndices(obj) { return 0; }")
            .Add("sqr/character/job_load_state.nut", @"
IRDSQRCharacter.pushScriptFiles(""Job/header.nut"");
IRDSQRCharacter.pushState(JOB_TEST, ""Job/Skill/Foo.nut"", ""Foo"", STATE_FOO, SKILL_FOO);
IRDSQRCharacter.pushState(JOB_TEST, ""Job/Skill/Attack.nut"", ""Attack"", STATE_ATTACK, -1);
IRDSQRCharacter.pushPassiveObj(""PassiveObject/Job/po_bar.nut"", 24100);
")
            .Add("sqr/job/header.nut", "JOB_TEST <- 8; STATE_FOO <- 70; SKILL_FOO <- 12; STATE_ATTACK <- 8;")
            .Add("sqr/job/skill/foo.nut", @"
// function notAFunction_Foo(obj) is only in a comment
local text = ""function inString_Foo(obj)"";
function checkExecutableSkill_Foo(obj) { return true; }
function onSetState_Foo(obj, state, datas, isResetTimer) { }
function helper() { return 1; }
")
            .Add("sqr/job/skill/attack.nut", "function onSetState_Attack(obj, state, datas, isResetTimer) { }")
            .Add("sqr/passiveobject/job/po_bar.nut", "function setCustomData_po_Bar(obj, reciveData) { }")
            .Add("sqr/character/av_load_state.nut", @"
CNAvenger.pushScriptFiles(""Av/header.nut"");
CNAvenger.pushState(""Av/Spin.nut"", ""Spin"", STATE_SPIN, SKILL_SPIN);
CNAvenger.pushPassiveObj(""PassiveObject/Av/po_spin.nut"", 24200);
")
            .Add("sqr/av/header.nut", "STATE_SPIN <- 100; SKILL_SPIN <- 33;")
            .Add("sqr/av/spin.nut", "function onSetState_Spin(obj, state, datas, isResetTimer) { }")
            .Add("sqr/passiveobject/av/po_spin.nut", "function setCustomData_po_Spin(obj, reciveData) { }");
    }

    [Fact]
    public void LoadChainRunsScriptsInOrderAndRecordsRegistrations()
    {
        using var host = new ScriptHost(Sample());
        ScriptLoadSummary summary = host.LoadAll();

        Assert.True(summary.EntryOk);
        Assert.Empty(host.Errors);
        Assert.Empty(host.MissingScripts);
        Assert.Equal(new[]
        {
            "sqr/loadstate.nut",
            "sqr/common.nut",
            "sqr/character/job_load_state.nut",
            "sqr/job/header.nut",
            "sqr/job/skill/foo.nut",
            "sqr/job/skill/attack.nut",
            "sqr/passiveobject/job/po_bar.nut",
            "sqr/character/av_load_state.nut",
            "sqr/av/header.nut",
            "sqr/av/spin.nut",
            "sqr/passiveobject/av/po_spin.nut",
            "sqr/init_character.nut",
        }, host.LoadedScripts);
        Assert.Equal(2, summary.ScriptFiles);
        Assert.Equal(3, summary.States);
        Assert.Equal(2, summary.PassiveObjects);

        ScriptRegistration foo = host.Registrations.Single(r => r.Suffix == "Foo");
        Assert.Equal(RegistrationKind.State, foo.Kind);
        Assert.Equal("IRDSQRCharacter", foo.Table);
        Assert.Equal(8, foo.Job);
        Assert.Equal(70, foo.Id);
        Assert.Equal(12, foo.Skill);
        Assert.Equal("Job/Skill/Foo.nut", foo.Path);
        Assert.Equal("sqr/job/skill/foo.nut", foo.ResolvedPath);

        ScriptRegistration attack = host.Registrations.Single(r => r.Suffix == "Attack");
        Assert.Equal(-1, attack.Skill);

        ScriptRegistration spin = host.Registrations.Single(r => r.Suffix == "Spin");
        Assert.Equal("CNAvenger", spin.Table);
        Assert.Equal(ScriptHost.PriestJob, spin.Job);
        Assert.Equal(100, spin.Id);
        Assert.Equal(33, spin.Skill);

        ScriptRegistration po = host.Registrations.Single(r => r.Kind == RegistrationKind.PassiveObject && r.Table == "CNAvenger");
        Assert.Equal(24200, po.Id);
        Assert.Equal(ScriptHost.PriestJob, po.Job);

        Assert.True(host.Registrations.Select(r => r.Order).SequenceEqual(Enumerable.Range(0, host.Registrations.Count)));
    }

    [Fact]
    public void ScriptsRunAtRegistrationSoLaterRegistrationsCanUseHeaderConstants()
    {
        // pushState 的参数里用了头文件里才定义的常量:头文件必须在登记时立刻运行
        var source = new MemoryScriptSource()
            .Add("sqr/loadstate.nut", @"
IRDSQRCharacter.pushScriptFiles(""h.nut"");
IRDSQRCharacter.pushState(1, ""s.nut"", ""S"", STATE_FROM_HEADER, SKILL_FROM_HEADER);
")
            .Add("sqr/h.nut", "STATE_FROM_HEADER <- 5; SKILL_FROM_HEADER <- 6;")
            .Add("sqr/s.nut", "function onSetState_S(obj, state, datas, isResetTimer) { }");
        using var host = new ScriptHost(source);
        ScriptLoadSummary summary = host.LoadAll();
        Assert.True(summary.EntryOk);
        Assert.Equal(5, host.Registrations.Single(r => r.Kind == RegistrationKind.State).Id);

        // 反过来,头文件放在 pushState 之后就找不到常量
        var wrongOrder = new MemoryScriptSource()
            .Add("sqr/loadstate.nut", @"
IRDSQRCharacter.pushState(1, ""s.nut"", ""S"", STATE_FROM_HEADER, 0);
IRDSQRCharacter.pushScriptFiles(""h.nut"");
")
            .Add("sqr/h.nut", "STATE_FROM_HEADER <- 5;")
            .Add("sqr/s.nut", "");
        using var host2 = new ScriptHost(wrongOrder);
        Assert.False(host2.LoadAll().EntryOk);
        Assert.Contains("STATE_FROM_HEADER", host2.Errors[0]);
    }

    [Fact]
    public void DefinedFunctionsIgnoreCommentsAndStrings()
    {
        using var host = new ScriptHost(Sample());
        host.LoadAll();
        Assert.True(host.TryGetDefinedFunctions("sqr/job/skill/foo.nut", out List<ScriptFunction> functions));
        Assert.Equal(new[] { "checkExecutableSkill_Foo", "onSetState_Foo", "helper" }, functions.Select(f => f.Name));
        Assert.Equal(new[] { "obj", "state", "datas", "isResetTimer" }, functions[1].Parameters);
        Assert.Empty(functions[2].Parameters);
    }

    [Fact]
    public void MissingAndBrokenScriptsAreReportedAndLoadingContinues()
    {
        var source = new MemoryScriptSource()
            .Add("sqr/loadstate.nut", @"
IRDSQRCharacter.pushScriptFiles(""gone.nut"");
IRDSQRCharacter.pushScriptFiles(""broken.nut"");
IRDSQRCharacter.pushScriptFiles(""fine.nut"");
")
            .Add("sqr/broken.nut", "function (")
            .Add("sqr/fine.nut", "FINE <- 1;");
        using var host = new ScriptHost(source);
        ScriptLoadSummary summary = host.LoadAll();

        // 入口脚本本身没有出错:单个登记失败只记录,不打断后面的
        Assert.True(summary.EntryOk);
        Assert.Contains("sqr/gone.nut", host.MissingScripts);
        Assert.Equal(3, host.Registrations.Count);
        Assert.Contains(host.Errors, e => e.StartsWith("sqr/gone.nut"));
        Assert.Contains(host.Errors, e => e.StartsWith("sqr/broken.nut"));
        Assert.DoesNotContain(host.Errors, e => e.StartsWith("sqr/fine.nut"));
        Assert.True(host.Vm.TryGetGlobalInteger("FINE", out long fine));
        Assert.Equal(1, fine);
    }

    [Fact]
    public void UnknownFunctionsAreStubbedAndRecordedButUnknownConstantsAreNot()
    {
        var source = new MemoryScriptSource()
            .Add("sqr/loadstate.nut", @"
local r = some_native_not_implemented_yet(1, 2);
if (r != null) throw ""stub should return null"";
some_native_not_implemented_yet(3);
")
            .Add("sqr/init_character.nut", "");
        using var host = new ScriptHost(source);
        Assert.True(host.LoadAll().EntryOk);
        Assert.Equal(2, host.UnknownNames["some_native_not_implemented_yet"]);

        var constant = new MemoryScriptSource().Add("sqr/loadstate.nut", "local x = SOME_UNDEFINED_CONSTANT;");
        using var host2 = new ScriptHost(constant);
        Assert.False(host2.LoadAll().EntryOk);
        Assert.Contains("SOME_UNDEFINED_CONSTANT", host2.Errors[0]);

        using var strict = new ScriptHost(new MemoryScriptSource().Add("sqr/loadstate.nut", "other_function();")) { StubUnknownFunctions = false };
        Assert.False(strict.LoadAll().EntryOk);
    }

    [Fact]
    public void EnsureScriptRunsAScriptOnlyOnceAndPathsAreCaseInsensitive()
    {
        var source = new MemoryScriptSource()
            .Add("sqr/loadstate.nut", "COUNTER <- 0;")
            .Add("sqr/Appendage/Character/AP_Test.nut", "COUNTER = COUNTER + 1;");
        using var host = new ScriptHost(source);
        host.LoadAll();

        Assert.True(host.EnsureScript("appendage\\character\\ap_test.nut", out _));
        Assert.True(host.EnsureScript("Appendage/Character/ap_Test.nut", out _));
        Assert.True(host.Vm.TryGetGlobalInteger("COUNTER", out long counter));
        Assert.Equal(1, counter);

        Assert.False(host.EnsureScript("appendage/character/ap_none.nut", out string error));
        Assert.Contains("ap_none", error);
    }

    [Fact]
    public void DebugModeIsOffAndInitCharacterIsLoaded()
    {
        var source = Sample().Add("sqr/common.nut", "DEBUG <- isDebugMode(); DEBUG_FLAG <- DEBUG ? 1 : 0; NULL <- null;");
        using var host = new ScriptHost(source);
        host.LoadAll();
        Assert.True(host.Vm.TryGetGlobalInteger("DEBUG_FLAG", out long flag));
        Assert.Equal(0, flag);
        Assert.True(host.Vm.HasFunction("sq_InitFrameIndices"));
    }
}
