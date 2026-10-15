using System;
using System.Collections.Generic;

namespace Dnf.Game
{
    /// <summary>
    /// 依附体:挂在一个对象(Parent)上的附加逻辑,来源是另一个对象(Source,通常是施法者)。
    /// 逻辑由一份 ap_*.nut 脚本提供:脚本里的全局函数 sq_AddFunctionName(appendage) 在创建时被调用,
    /// 用 appendage.sq_AddFunctionName(事件名, 函数名) 登记回调(onStart、proc、onEnd、isEnd、prepareDraw、onSetHp 等)。
    /// </summary>
    public sealed class Appendage : NativeObject
    {
        public override string TypeName => "Appendage";

        /// <summary>被依附的对象。</summary>
        public GameObject Parent { get; internal set; }

        /// <summary>发起依附的对象。</summary>
        public GameObject Source { get; internal set; }

        /// <summary>脚本路径(规范化,相对 sqr/ 之前的 PVF 路径,例如 sqr/character/atmage/icecrash/ap_icecrash.nut)。</summary>
        public string ScriptPath { get; internal set; }

        public int SkillIndex { get; internal set; }

        public bool Valid { get; internal set; } = true;

        /// <summary>事件名 -> 脚本里的函数名。</summary>
        public Dictionary<string, string> Callbacks { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

        private VarTables _varTables;
        private GameTimer _timer;

        /// <summary>依附体上按名字区分的变量表:appendage.getVar("名字");不带名字的是单独的一张(嵌在依附体里,即 sq_var),不是名字叫 "default" 的那张(E-048)。</summary>
        public VarStore GetVar(string name)
        {
            return (_varTables ?? (_varTables = new VarTables(World, false))).Get(name);
        }

        /// <summary>依附体自己的那张不带名字的变量表(appendage.getVar() 与 appendage.sq_var)。</summary>
        public VarStore Vars => GetVar(null);

        /// <summary>依附体自己的计时器(appendage.getTimer())。</summary>
        public GameTimer Timer => _timer ?? (_timer = new GameTimer(World));

        public bool Started { get; internal set; }

        /// <summary>创建后经过的毫秒数。</summary>
        public int AgeMs { get; internal set; }

        /// <summary>剩余有效时间(毫秒);小于 0 表示没有时限(sq_SetValidTime 设置)。</summary>
        public int ValidTimeMs { get; internal set; } = -1;

        /// <summary>依附体名下按名字挂的属性改变(appendage.sq_AddChangeStatus / sq_getChangeStatus)。</summary>
        public System.Collections.Generic.Dictionary<string, ChangeStatus> NamedChangeStatuses { get; } =
            new System.Collections.Generic.Dictionary<string, ChangeStatus>(System.StringComparer.Ordinal);

        /// <summary>依附体名下按名字挂的"光环"(appendage.sq_AddAuraMaster / sq_getAuraMaster),这里只有占位对象。</summary>
        public System.Collections.Generic.Dictionary<string, ServiceObject> Auras { get; } =
            new System.Collections.Generic.Dictionary<string, ServiceObject>(System.StringComparer.Ordinal);

        /// <summary>依附体名下按名字挂的"最大生命提升"(appendage.sq_AddHpMaxUp / sq_getHpMaxUp),这里只有占位对象,没有真的改最大生命。</summary>
        public System.Collections.Generic.Dictionary<string, ServiceObject> HpMaxUps { get; } =
            new System.Collections.Generic.Dictionary<string, ServiceObject>(System.StringComparer.Ordinal);
    }
}
