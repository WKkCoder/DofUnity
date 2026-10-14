using Dnf.Squirrel;

namespace Dnf.Squirrel.Tests;

/// <summary>按形参个数调整实参的调用(原版脚本里同一个回调有 3 个与 4 个形参的版本,见证据 E-035)。</summary>
public class AdaptedCallTests
{
    [Fact]
    public void ArityIsTheDeclaredParameterCountWithoutThis()
    {
        using var vm = new SqVm();
        vm.Run("t.nut", @"
            function none() { return 0 }
            function two(a, b) { return 0 }
            function four(a, b, c, d) { return 0 }
        ");
        Assert.Equal(0, vm.GetFunctionArity("none"));
        Assert.Equal(2, vm.GetFunctionArity("two"));
        Assert.Equal(4, vm.GetFunctionArity("four"));
        Assert.Equal(-1, vm.GetFunctionArity("missing"));
        Assert.Equal(0, vm.StackTop);
    }

    [Fact]
    public void ExtraArgumentsAreDroppedAndMissingOnesBecomeNull()
    {
        using var vm = new SqVm();
        vm.Run("t.nut", @"
            function two(a, b) { return a + "":"" + (b == null ? ""null"" : b) }
            function four(a, b, c, d) { return (c == null ? ""n"" : ""v"") + (d == null ? ""n"" : ""v"") }
        ");
        Assert.True(vm.TryCallAdapted("two", out object result, out string error, "x", "y", "extra", 9L));
        Assert.Equal("x:y", result);
        Assert.Null(error);

        Assert.True(vm.TryCallAdapted("two", out result, out error, "x"));
        Assert.Equal("x:null", result);

        Assert.True(vm.TryCallAdapted("four", out result, out error, 1L, 2L));
        Assert.Equal("nn", result);
        Assert.Equal(0, vm.StackTop);
    }

    [Fact]
    public void MissingFunctionReturnsFalseWithoutAnError()
    {
        using var vm = new SqVm();
        Assert.False(vm.TryCallAdapted("nope", out object result, out string error, 1L));
        Assert.Null(result);
        Assert.Null(error);
    }

    [Fact]
    public void ScriptErrorsAreReportedAsErrorText()
    {
        using var vm = new SqVm();
        vm.Run("t.nut", "function boom(a) { local x = null; return x.y(); }");
        Assert.False(vm.TryCallAdapted("boom", out _, out string error, 1L));
        Assert.False(string.IsNullOrEmpty(error));
        Assert.Equal(0, vm.StackTop);
    }

    [Fact]
    public void NativeFunctionsReceiveAllArguments()
    {
        using var vm = new SqVm();
        vm.RegisterGlobal("count", c => c.Return(c.ArgCount));
        Assert.True(vm.TryCallAdapted("count", out object result, out _, 1L, 2L, 3L));
        Assert.Equal(3L, result);
    }
}
