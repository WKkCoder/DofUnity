using System.Collections.Generic;

namespace Dnf.Game
{
    /// <summary>一条属性改变:类型(脚本里的 CHANGE_STATUS_TYPE_*)、是否按比例、数值。数值的单位与对战斗的影响尚未实现,只按脚本给的值保存。</summary>
    public readonly struct ChangeStatusParameter
    {
        public ChangeStatusParameter(int type, bool isRate, float value)
        {
            Type = type;
            IsRate = isRate;
            Value = value;
        }

        public int Type { get; }
        public bool IsRate { get; }
        public float Value { get; }
    }

    /// <summary>
    /// "属性改变"依附体(buff/debuff):一组属性改变加一个有效时间。脚本用 sq_CreateChangeStatus 创建,
    /// 再用 CNSquirrelAppendage.sq_Append(依附体, 被依附者, 来源) 挂到对象上;或用 appendage.sq_AddChangeStatus(名字, ……) 挂在另一个依附体名下。
    /// 这里只记录参数并按有效时间到期,不改变对象的战斗属性(属性系统还没有)。
    /// </summary>
    public sealed class ChangeStatus : NativeObject
    {
        public override string TypeName => "ChangeStatus";

        public string Name { get; internal set; }

        /// <summary>有效时间(毫秒);小于等于 0 表示不会自己到期(脚本里写 0 的是被动加成)。</summary>
        public int ValidTimeMs { get; internal set; }

        public int ElapsedMs { get; internal set; }

        public int SkillIndex { get; internal set; } = -1;

        public int CauseKind { get; internal set; }

        /// <summary>挂接时给的依附体编号(APID_*,取值见 <c>Dnf.Script.EngineConstants</c>,证据 E-045);没给为 0。sq_RemoveChangeStatus(对象, 编号) 按它移除。</summary>
        public int AppendageId { get; internal set; }

        public GameObject Parent { get; internal set; }

        public GameObject Source { get; internal set; }

        public bool Valid { get; internal set; } = true;

        public List<ChangeStatusParameter> Parameters { get; } = new List<ChangeStatusParameter>();
    }

    /// <summary>
    /// 脚本能拿到、但这里只实现一小部分的"服务"对象:对象管理器、舞台、主控件、鼠标。
    /// 它们在原版里是界面与地图系统的一部分;这里只提供脚本在没有界面时需要的最小答案。
    /// </summary>
    public sealed class ServiceObject : NativeObject
    {
        public ServiceObject(string kind)
        {
            Kind = kind;
        }

        public string Kind { get; }

        public override string TypeName => Kind;
    }
}
