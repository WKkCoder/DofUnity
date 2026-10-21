// 证据:脚本拿到的动画对象及其接口的核对状态见 docs/native-audit.tsv(E-046、E-048);最小世界的整条技能流程见 E-037。
namespace Dnf.Game
{
    /// <summary>
    /// 脚本能拿到的动画对象:obj.getCurrentAnimation() 取到的是对象自己的播放器(<see cref="Animator"/>)的视图,
    /// sq_CreateAnimation / sq_CreateCNRDAnimation 创建的是独立的播放器。方法见 Natives/AnimationNatives.cs。
    /// </summary>
    public sealed class AnimationObject : NativeObject
    {
        public AnimationObject(Animator animator, bool sharedWithObject)
        {
            Animator = animator;
            SharedWithObject = sharedWithObject;
        }

        public override string TypeName => "Animation";

        public Animator Animator { get; }

        /// <summary>是否是某个游戏对象自己的当前动画(随对象一起推进);独立创建的动画只在脚本调用 sq_AnimationProc 时推进。</summary>
        public bool SharedWithObject { get; }

        /// <summary>脚本设置的"强制结束"标志(setEnd);只记录。</summary>
        public bool ForcedEnd { get; set; }
    }

    /// <summary>
    /// sq_GetSkill(obj, 技能) 取到的技能运行时对象:封装等级、冷却、封印标志等。冷却与耗蓝没有实现,查询类方法返回中性值。
    /// </summary>
    public sealed class SkillObject : NativeObject
    {
        public SkillObject(GameObject owner, int skillId)
        {
            Owner = owner;
            SkillId = skillId;
        }

        public override string TypeName => "Skill";

        public GameObject Owner { get; }

        public int SkillId { get; }

        /// <summary>主动功能是否被封印(setSealActiveFunction);只记录。</summary>
        public bool SealActiveFunction { get; set; }
    }
}
