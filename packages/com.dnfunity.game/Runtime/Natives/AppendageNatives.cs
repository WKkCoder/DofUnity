using System;
using Dnf.Data;
using Dnf.Squirrel;

namespace Dnf.Game
{
    /// <summary>依附体(Appendage)相关的原生接口:静态表 CNSquirrelAppendage 与依附体对象的成员。</summary>
    public sealed partial class GameWorld
    {
        /// <summary>在静态原生表上登记一个接口:脚本里写 表名.接口(参数……),参数从 0 起,没有"对象"。</summary>
        internal void DefineStatic(SqNativeTable table, string name, NativeStatus status, string note, NativeBody body)
        {
            string key = table.Name + "." + name;
            var info = new NativeInfo { Name = key, Status = status, Forms = NativeForm.Member, Note = note };
            _natives[key] = info;
            table.Method(name, call =>
            {
                info.Calls++;
                Note(key);
                OnNativeCall?.Invoke(key, null);
                return body(new NativeCall(this, call, 0, null));
            });
        }

        /// <summary>脚本里的静态表 CNSquirrelAppendage(依附体的创建、查询与挂接)。</summary>
        private SqNativeTable AppendageTable { get; set; }

        private Appendage FindAppendage(GameObject parent, string scriptPath)
        {
            string normalized = PvfPath.Normalize("sqr/" + scriptPath);
            foreach (Appendage a in parent.Appendages)
            {
                if (a.Valid && string.Equals(a.ScriptPath, normalized, StringComparison.Ordinal))
                {
                    return a;
                }
            }
            return null;
        }

        private void RegisterAppendageNatives()
        {
            const NativeStatus I = NativeStatus.Inferred;

            SqNativeTable t = AppendageTable = Vm.DefineTable("CNSquirrelAppendage", true);
            DefineStatic(t, "sq_AppendAppendage", I,
                "给对象挂依附体 (被依附者, 来源, 技能, 标志, 脚本路径, 标志):已有同路径依附体时返回已有的;返回依附体,失败为 null。两个布尔参数含义未知,忽略", c =>
                {
                    GameObject parent = c.Arg<GameObject>(0);
                    GameObject source = c.Arg<GameObject>(1);
                    if (parent == null || !parent.Valid)
                    {
                        return c.ReturnNull();
                    }
                    string path = c.Str(4);
                    Appendage existing = FindAppendage(parent, path);
                    if (existing != null)
                    {
                        return c.Return(existing);
                    }
                    return c.Return(CreateAppendage(parent, source, c.Int32(2), path));
                });
            DefineStatic(t, "sq_RemoveAppendage", I, "移除对象身上某个路径的依附体 (对象, 脚本路径)", c =>
            {
                GameObject parent = c.Arg<GameObject>(0);
                if (parent != null)
                {
                    Appendage a = FindAppendage(parent, c.Str(1));
                    if (a != null)
                    {
                        EndAppendage(a);
                    }
                }
                return c.ReturnNothing();
            });
            DefineStatic(t, "sq_IsAppendAppendage", I, "对象身上是否有某个路径的依附体 (对象, 脚本路径)", c =>
            {
                GameObject parent = c.Arg<GameObject>(0);
                return c.Return(parent != null && FindAppendage(parent, c.Str(1)) != null);
            });
            DefineStatic(t, "sq_GetAppendage", I, "取对象身上某个路径的依附体 (对象, 脚本路径),没有为 null", c =>
            {
                GameObject parent = c.Arg<GameObject>(0);
                return c.Return(parent == null ? null : FindAppendage(parent, c.Str(1)));
            });

            Define("sq_AddFunctionName", NativeStatus.Verified,
                "依附体:登记一个事件的回调函数名 (事件名, 函数名)。每个事件名只登记一次:已经登记过的再登记会被忽略,先登记的有效"
                + "(客户端 0x009d9e30:先在 this+0x1DC 的名字映射里查找,找不到才插入并赋值,找到了什么也不做;名字的比较区分大小写,E-048)", NativeForm.Member, c =>
            {
                Appendage a = c.SelfAs<Appendage>();
                string eventName = c.Str(0);
                if (!a.Callbacks.ContainsKey(eventName))
                {
                    a.Callbacks.Add(eventName, c.Str(1));
                }
                return c.ReturnNothing();
            });
            Define("sq_SetValidTime", I, "依附体:设置有效时间(毫秒),到时自动结束", NativeForm.Both, c =>
            {
                c.SelfAs<Appendage>().ValidTimeMs = c.Int32(0);
                return c.ReturnNothing();
            });
        }
    }
}
