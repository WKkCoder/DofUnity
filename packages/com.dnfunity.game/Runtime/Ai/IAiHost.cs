// 证据:函数条件的参数顺序与区域判断的几何见 E-052、E-128(静态读码);"冷却中为真"的方向见 E-053。
namespace Dnf.Game.Ai
{
    /// <summary>
    /// .ai 求值器(<see cref="AiEvaluator"/>)向宿主(一只怪物所在的房间)要的东西:随机数、自己与目标的位置、
    /// 各个"问一句"的函数条件。求值器本身不碰任何游戏对象,换一个宿主就能在合成数据上测。
    /// </summary>
    public interface IAiHost
    {
        /// <summary>客户端的 rand(0):31 位非负整数(<c>Dnf.Render.ClientRandom</c> 的序列)。</summary>
        int Rand();

        /// <summary>自己的位置(世界像素)。</summary>
        float SelfX { get; }

        float SelfY { get; }

        /// <summary>自己朝左(原版朝向 1);朝右是朝向 0。</summary>
        bool SelfFacingLeft { get; }

        /// <summary>现在有没有目标(怪物的目标是主角)。没有目标时区域判断一律为假。</summary>
        bool HasTarget { get; }

        float TargetX { get; }

        float TargetY { get; }

        /// <summary>
        /// 第 <paramref name="index"/> 招(攻击动作列表的下标)现在能不能出:<b>真 = 还在冷却、或者根本没有这一招</b>
        /// (脚本里 [true] 分支通常是"出不了,返回 -1",证据 E-053)。
        /// </summary>
        bool IsAttackCooling(int index);

        /// <summary>第 <paramref name="action"/> 个攻击动作的第 <paramref name="trigger"/> 个触发器现在是不是开着(.act 的 [ENABLE] 初值与 [TRIGGER] 行为的开关)。</summary>
        bool IsAttackEnabled(int action, int trigger);

        /// <summary>同上,但是"自定义动作"一侧(.mob 的 [etc action])。</summary>
        bool IsCustomEnabled(int action, int trigger);

        /// <summary>自己是不是处在"自由状态"(站着或走着,没有出招、挨打、倒地)。</summary>
        bool IsFreeState();

        /// <summary>受击检查(事件脚本里问"刚才那一下是不是某种命中");<paramref name="mode"/> 是脚本给的参数。</summary>
        bool DamageCheck(int mode);

        /// <summary>
        /// 数一数闭区间 [<paramref name="xMin"/>, <paramref name="xMax"/>] × [<paramref name="yMin"/>, <paramref name="yMax"/>] 里的活对象个数。
        /// <paramref name="teamMode"/>:-1 只数敌对、1 只数友方、0 中立、100 不限;<paramref name="typeFilter"/>:1 到 5 与 100 见 <see cref="AiObjectKind"/>;
        /// <paramref name="idFilter"/>:对象编号,-1 不限。自己不算。
        /// </summary>
        int CountInArea(float xMin, float xMax, float yMin, float yMax, int teamMode, int typeFilter, int idFilter);

        /// <summary>
        /// 求值器自己不会的条件(设置类关键字 [set target monster]、[set bound] 等,以及没做的函数):宿主按 <paramref name="node"/> 的内部码处理,
        /// 做成了就返回 true/false 作为条件值,不支持返回 false。
        /// </summary>
        bool Other(AiNode node, AiScratch scratch);
    }

    /// <summary>"对象种类"过滤用的编号(OperatingValue.lst 里 apc、monster、passive object、character、summon、all 的值)。</summary>
    public static class AiObjectKind
    {
        public const int Apc = 1;
        public const int Monster = 2;
        public const int PassiveObject = 3;
        public const int Character = 4;
        public const int Summon = 5;
        public const int All = 100;
    }
}
