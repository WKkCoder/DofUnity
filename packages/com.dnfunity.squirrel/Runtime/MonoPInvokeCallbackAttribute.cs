using System;

namespace Dnf.Squirrel
{
    /// <summary>
    /// IL2CPP(iOS 等 AOT 平台)要求被原生代码回调的静态方法带这个属性,并且只能是静态方法。
    /// 名字与 Unity 内置的同名,IL2CPP 按名字识别;在普通 .NET 里它什么也不做。
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    internal sealed class MonoPInvokeCallbackAttribute : Attribute
    {
        public MonoPInvokeCallbackAttribute(Type delegateType)
        {
        }
    }
}
