namespace Dnf.Script
{
    public enum RegistrationKind
    {
        /// <summary>pushScriptFiles:公共脚本(头文件里的常量、职业公共函数、被动技能)。</summary>
        ScriptFile,

        /// <summary>pushState:一个状态(技能、普攻、跳跃……)的脚本;回调函数名是 &lt;回调&gt;_&lt;后缀&gt;。</summary>
        State,

        /// <summary>pushPassiveObj:被动对象(技能产生的飞行物、特效体)的脚本,按编号登记。</summary>
        PassiveObject,
    }

    /// <summary>脚本在加载时向引擎登记的一项内容(证据 E-035)。</summary>
    public sealed class ScriptRegistration
    {
        public ScriptRegistration(int order, RegistrationKind kind, string table, string path, string resolvedPath, string suffix, long job, long id, long skill)
        {
            Order = order;
            Kind = kind;
            Table = table;
            Path = path;
            ResolvedPath = resolvedPath;
            Suffix = suffix;
            Job = job;
            Id = id;
            Skill = skill;
        }

        /// <summary>登记的先后顺序(从 0 起)。</summary>
        public int Order { get; }

        public RegistrationKind Kind { get; }

        /// <summary>调用的静态表:IRDSQRCharacter(通用,带职业编号)或 CNAvenger(复仇者专用,职业固定为牧师)。</summary>
        public string Table { get; }

        /// <summary>脚本里写的路径(相对 sqr/,大小写与分隔符照原样)。</summary>
        public string Path { get; }

        /// <summary>规范化后的 PVF 路径(小写、'/' 分隔,含 "sqr/" 前缀)。</summary>
        public string ResolvedPath { get; }

        /// <summary>状态的回调函数名后缀(只有 State 有)。</summary>
        public string Suffix { get; }

        /// <summary>职业编号(ENUM_CHARACTERJOB_*;ScriptFile 在 IRDSQRCharacter 下没有职业,记 -1)。</summary>
        public long Job { get; }

        /// <summary>状态编号(State,STATE_*)或被动对象编号(PassiveObject);ScriptFile 记 0。</summary>
        public long Id { get; }

        /// <summary>技能编号(State,SKILL_*,-1 表示不是技能);其他记 0。</summary>
        public long Skill { get; }
    }
}
