using System.Text;
using Dnf.Squirrel;

namespace Dnf.Squirrel.Tests;

/// <summary>Squirrel 虚拟机封装的单元测试:全部用自己写的合成脚本,不含任何原版内容。</summary>
public class SqVmTests
{
    private static byte[] Src(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void NativeLibraryIsSquirrel32WithSixtyFourBitIntegers()
    {
        Assert.Equal(320, SqVm.Version);
        using var vm = new SqVm();
        Assert.Equal(0, vm.StackTop);
    }

    [Fact]
    public void ScriptFunctionsCanBeCalledWithAndReturnEveryBasicType()
    {
        using var vm = new SqVm();
        vm.Run("t.nut", @"
            function add(a, b) { return a + b }
            function half(x) { return x / 2.0 }
            function greet(name) { return ""hi "" + name }
            function isBig(n) { return n > 100 }
            function nothing() { }
        ");
        Assert.Equal(5L, vm.Call("add", 2, 3));
        Assert.Equal(1.25f, vm.Call("half", 2.5f));
        Assert.Equal("hi bob", vm.Call("greet", "bob"));
        Assert.Equal(true, vm.Call("isBig", 1000L));
        Assert.Equal(false, vm.Call("isBig", 1));
        Assert.Null(vm.Call("nothing"));
        Assert.Equal(0, vm.StackTop);
    }

    [Fact]
    public void IntegersAreSixtyFourBitAndDoNotWrapAtThirtyTwo()
    {
        using var vm = new SqVm();
        vm.Run("t.nut", "function big() { return 2147483647 + 1 }");
        Assert.Equal(2147483648L, vm.Call("big"));
    }

    [Fact]
    public void StringsAreByteStringsAndRoundTripArbitraryBytes()
    {
        using var vm = new SqVm();
        var latin = new string(new[] { 'a', (char)0xC6, (char)0xD0, (char)0xFF, 'z' });
        vm.Run("t.nut", "function id(s) { return s }");
        Assert.Equal(latin, vm.Call("id", latin));
    }

    [Fact]
    public void CompileErrorsAreReportedWithPosition()
    {
        using var vm = new SqVm();
        Assert.False(vm.TryCompile("bad.nut", Src("function f( { return 1"), out string error));
        Assert.Contains("bad.nut", error);
        Assert.Contains("IDENTIFIER", error);
        Assert.True(vm.TryCompile("good.nut", Src("function f(a) { return a }"), out error));
        Assert.Null(error);
        Assert.Equal(0, vm.StackTop);
    }

    [Fact]
    public void RuntimeErrorsAreReportedAndTheVmStaysUsable()
    {
        using var vm = new SqVm();
        vm.Run("t.nut", "function boom(x) { return x.missing() }\nfunction ok() { return 7 }");
        Assert.False(vm.TryCall("boom", out _, out string error, 1));
        Assert.Contains("missing", error);
        Assert.Contains("boom", error);
        Assert.Equal(7L, vm.Call("ok"));
        Assert.False(vm.TryCall("notDefined", out _, out error));
        Assert.Contains("notDefined", error);
        Assert.Equal(0, vm.StackTop);
    }

    [Fact]
    public void PrintOutputIsDeliveredThroughTheLogEvent()
    {
        using var vm = new SqVm();
        var sb = new StringBuilder();
        vm.Log += (level, text) => { if (level == 0) sb.Append(text); };
        vm.Run("t.nut", "print(\"hello \" + 42 + \"\\n\")");
        Assert.Equal("hello 42\n", sb.ToString());
    }

    [Fact]
    public void GlobalNativeFunctionsReceiveTypedArgumentsAndReturnValues()
    {
        using var vm = new SqVm();
        vm.RegisterGlobal("nativeMix", c =>
        {
            Assert.Equal(3, c.ArgCount);
            long i = c.Int(0);
            float f = c.Float(1);
            string s = c.String(2);
            return c.Return(s + ":" + (i * f));
        });
        vm.RegisterGlobal("nativeNothing", c => c.ReturnNothing());
        vm.Run("t.nut", "function run() { return nativeMix(4, 0.5, \"x\") }\nfunction none() { return nativeNothing() }");
        Assert.Equal("x:2", vm.Call("run"));
        Assert.Null(vm.Call("none"));
        Assert.Equal(0, vm.StackTop);
    }

    [Fact]
    public void NativeArgumentErrorsAndExceptionsBecomeScriptErrors()
    {
        using var vm = new SqVm();
        vm.RegisterGlobal("needInt", c => c.Return(c.Int(0)));
        vm.RegisterGlobal("throws", c => throw new InvalidOperationException("from host"));
        vm.Run("t.nut", "function a() { return needInt(\"nope\") }\nfunction b() { return throws() }\nfunction c() { return needInt(3) }");
        Assert.False(vm.TryCall("a", out _, out string error));
        Assert.Contains("整数", error);
        Assert.False(vm.TryCall("b", out _, out error));
        Assert.Contains("from host", error);
        Assert.Equal(3L, vm.Call("c"));
    }

    [Fact]
    public void StaticTablesExposeNativeMethodsAndConstants()
    {
        using var vm = new SqVm();
        var counter = 0L;
        vm.DefineTable("Registry", true)
            .Method("push", c => { counter += c.Int(0); return c.ReturnNothing(); })
            .Method("total", c => c.Return(counter))
            .Constant("LIMIT", 99);
        vm.Run("t.nut", "Registry.push(5); Registry.push(7);\nfunction total() { return Registry.total() + Registry.LIMIT }");
        Assert.Equal(12L, counter);
        Assert.Equal(111L, vm.Call("total"));
    }

    [Fact]
    public void NativeObjectsKeepIdentityAndMethodsSeeTheirHandle()
    {
        using var vm = new SqVm();
        var proto = vm.DefineTable("Obj", false)
            .Method("id", c => c.Return(c.This().Id))
            .Method("other", c => c.Return(new SqHandle(c.This().Id + 1)))
            .Method("same", c => c.Return(c.Handle(0).Id == c.This().Id));
        vm.NewObject(proto, new SqHandle(10));
        vm.NewObject(proto, new SqHandle(11));
        vm.Run("t.nut", @"
            function ident(o) { return o.id() }
            function next(o) { return o.other() }
            function same(a, b) { return a == b }
            function sameHandle(a, b) { return a.same(b) }
        ");
        Assert.Equal(10L, vm.Call("ident", new SqHandle(10)));
        var n = vm.Call("next", new SqHandle(10));
        Assert.Equal(new SqHandle(11), n);
        // 同一句柄总是同一张表,所以脚本里 == 成立;不同句柄不相等
        Assert.Equal(true, vm.Call("same", new SqHandle(10), new SqHandle(10)));
        Assert.Equal(false, vm.Call("same", new SqHandle(10), new SqHandle(11)));
        Assert.Equal(true, vm.Call("sameHandle", new SqHandle(10), new SqHandle(10)));
        Assert.Equal(false, vm.Call("sameHandle", new SqHandle(10), new SqHandle(11)));
        // 注销之后,宿主再传这个句柄进去,脚本拿到的是 null,调用它的方法就是脚本错误
        vm.ReleaseObject(new SqHandle(11));
        Assert.False(vm.TryCall("next", out _, out _, new SqHandle(11)));
    }

    [Fact]
    public void DerivedPrototypesInheritBaseMethods()
    {
        using var vm = new SqVm();
        var baseProto = vm.DefineTable("Base", false).Method("kind", c => c.Return("base"));
        var derived = vm.DefineTable("Derived", false, baseProto).Method("extra", c => c.Return("extra"));
        vm.NewObject(derived, new SqHandle(1));
        vm.Run("t.nut", "function f(o) { return o.kind() + \"/\" + o.extra() }");
        Assert.Equal("base/extra", vm.Call("f", new SqHandle(1)));
    }

    [Fact]
    public void MissingGlobalHandlerSuppliesStubsAndCanReportNotFound()
    {
        using var vm = new SqVm();
        var seen = new List<string>();
        vm.SetMissingGlobalHandler(name =>
        {
            if (!name.StartsWith("sq_", StringComparison.Ordinal))
            {
                return null;
            }
            seen.Add(name);
            return c => c.Return(c.ArgCount);
        });
        vm.Run("t.nut", @"
            function a() { return sq_Unknown(1, 2, 3) }
            function again() { return sq_Unknown() }
            function b() { return plainMissing }
        ");
        Assert.Equal(3L, vm.Call("a"));
        Assert.Equal(0L, vm.Call("again"));
        Assert.Equal(new[] { "sq_Unknown" }, seen);                  // 第二次直接命中缓存
        Assert.False(vm.TryCall("b", out _, out string error));
        Assert.Contains("plainMissing", error);
    }

    [Fact]
    public void MissingMemberHandlerSuppliesStubsOnObjects()
    {
        using var vm = new SqVm();
        var proto = vm.DefineTable("Obj", false).Method("known", c => c.Return(1));
        var seen = new List<string>();
        vm.SetMissingMemberHandler(proto, name =>
        {
            seen.Add(name);
            return c => c.Return(name.Length);
        });
        vm.NewObject(proto, new SqHandle(5));
        vm.Run("t.nut", "function f(o) { return o.known() + o.stubby() }");
        Assert.Equal(7L, vm.Call("f", new SqHandle(5)));
        Assert.Equal(new[] { "stubby" }, seen);
    }

    [Fact]
    public void NativeFunctionsCanCallBackIntoScripts()
    {
        using var vm = new SqVm();
        vm.RegisterGlobal("hostTwice", c =>
        {
            object r = c.Vm.Call("scriptDouble", c.Int(0));
            return c.Return(((long)r) * 2);
        });
        vm.Run("t.nut", "function scriptDouble(x) { return x * 2 }\nfunction go() { return hostTwice(5) }");
        Assert.Equal(20L, vm.Call("go"));
        Assert.Equal(0, vm.StackTop);
    }

    [Fact]
    public void StandardMathAndStringLibrariesAreAvailable()
    {
        using var vm = new SqVm();
        vm.Run("t.nut", "function m() { return abs(-3) }\nfunction f() { return format(\"%d-%s\", 5, \"a\") }");
        Assert.Equal(3L, vm.Call("m"));
        Assert.Equal("5-a", vm.Call("f"));
    }

    [Fact]
    public void ManyVirtualMachinesCanCoexistAndDisposeTwice()
    {
        var a = new SqVm();
        var b = new SqVm();
        a.RegisterGlobal("who", c => c.Return("a"));
        b.RegisterGlobal("who", c => c.Return("b"));
        a.Run("t", "function f() { return who() }");
        b.Run("t", "function f() { return who() }");
        Assert.Equal("a", a.Call("f"));
        Assert.Equal("b", b.Call("f"));
        a.Dispose();
        a.Dispose();
        Assert.Equal("b", b.Call("f"));
        b.Dispose();
        Assert.Throws<ObjectDisposedException>(() => a.Call("f"));
    }
}
