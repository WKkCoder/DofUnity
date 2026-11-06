using System;
using System.Collections.Generic;

// 客户端状态 0x0d 一族(基类阶段机 0x00863369、逐帧 0x0086916b、换阶段 0x00687480)的技能放法;和 E-135 的施法蓄力配方一样,表里只有读码得到的引擎常量(下标、列号、对象编号),不含 PVF 里的数据值。
namespace Dnf.Play
{
    /// <summary>
    /// 投掷类技能的放法(客户端状态 0x0d 一族):状态分两个阶段——阶段 0 一进来就在主角位置放出若干个老式被动对象(可选地同时开霸体),
    /// 过了阶段 0 的时长换阶段 1(霸体关掉),过了阶段 1 的时长回站立。主角身体不打人,伤害全在放出的对象上。
    /// 下标都是状态码里写死的常量,不是数据值:阶段 0 与阶段 1 的时长(毫秒)取 [static data] 的哪一项、霸体开关取哪一项(非 0 开)、
    /// 两段姿势动画取角色脚本 <c>[throw motion N-1]</c>(阶段 0)与 <c>[throw motion N-2]</c>(阶段 1)的哪个 N(<see cref="PoseSelector"/>)、放哪几个对象。
    /// 对象规格复用 <see cref="SkillObjectSpec"/>:在阶段 <see cref="SkillObjectSpec.Stage"/> 刚进来那一刻放(多数技能是 0,少数在阶段 1 的进入块里才创建,0x00cdf4dc、0x00cbbb07),帧号必须是 0,不能带"等特效"或"落地放";
    /// 伤害:客户端放每个对象时,创建函数的"伤害百分比"参数与参数缓冲的首个数都取自 [level info] 的同一列(同一个取列函数 0x00913d70,再乘同一个被动倍率),
    /// 所以对象规格里百分比列与固定值列必须是同一列(<see cref="SkillObjectSpec.DamageRateColumn"/> == <see cref="SkillObjectSpec.DamageFixedColumn"/>)。
    /// 对象拿这两个数怎么算伤害没读到:按 E-135 的老式对象惯例,百分比参数当倍率、缓冲首个数当固定值(推断)。
    /// </summary>
    public sealed class ThrowRecipe
    {
        public ThrowRecipe(int skillId, int chargeStaticIndex, int shootStaticIndex, int armorStaticIndex, int poseSelector, IEnumerable<SkillObjectSpec> objects)
        {
            if (skillId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(skillId), "技能号必须是正数");
            }
            if (chargeStaticIndex < 0 || shootStaticIndex < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(chargeStaticIndex), "两个阶段的时长都要指定 [static data] 的下标");
            }
            if (poseSelector < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(poseSelector), "姿势选择号从 1 数");
            }
            var list = new List<SkillObjectSpec>();
            foreach (SkillObjectSpec o in objects ?? throw new ArgumentNullException(nameof(objects)))
            {
                if (o == null || o.ObjectId <= 0 || o.Stage < 0 || o.Stage > 1 || o.Frame != 0 || o.OnLanding || o.AfterEffect >= 0 || o.DamageRateColumn < 0 || o.DamageFixedColumn != o.DamageRateColumn)
                {
                    throw new ArgumentException("投掷类的对象在阶段 0 或 1 进来时放(段号 0~1、帧号为 0、不等特效、不落地放),百分比与固定值取同一列", nameof(objects));
                }
                list.Add(o);
            }
            if (list.Count == 0)
            {
                throw new ArgumentException("投掷类技能至少放一个对象", nameof(objects));
            }
            SkillId = skillId;
            ChargeStaticIndex = chargeStaticIndex;
            ShootStaticIndex = shootStaticIndex;
            ArmorStaticIndex = armorStaticIndex;
            PoseSelector = poseSelector;
            Objects = list.AsReadOnly();
        }

        public int SkillId { get; }

        /// <summary>阶段 0 的时长(毫秒)在 [static data] 里的下标。</summary>
        public int ChargeStaticIndex { get; }

        /// <summary>阶段 1 的时长(毫秒)在 [static data] 里的下标。</summary>
        public int ShootStaticIndex { get; }

        /// <summary>霸体开关在 [static data] 里的下标(值非 0 就在阶段 0 开霸体);负数表示这个技能没有霸体。</summary>
        public int ArmorStaticIndex { get; }

        /// <summary>姿势动画的选择号 N:阶段 0 用 <see cref="ChargePoseTag"/>、阶段 1 用 <see cref="ShootPoseTag"/>。</summary>
        public int PoseSelector { get; }

        /// <summary>阶段 0 的姿势动画在角色脚本里的标签。</summary>
        public string ChargePoseTag => "[throw motion " + PoseSelector + "-1]";

        /// <summary>阶段 1 的姿势动画在角色脚本里的标签。</summary>
        public string ShootPoseTag => "[throw motion " + PoseSelector + "-2]";

        /// <summary>按创建顺序在对应阶段进来时放的对象;<see cref="SkillObjectSpec.OffsetY"/> 是纵深偏移,前方偏移与离地偏移默认 0(个别对象在数据里另有离地,见默认表注释)。</summary>
        public IReadOnlyList<SkillObjectSpec> Objects { get; }
    }

    public sealed partial class SkillRecipes
    {
        private readonly Dictionary<int, ThrowRecipe> _throws = new Dictionary<int, ThrowRecipe>();

        /// <summary>同 <see cref="SkillRecipes(IEnumerable{SkillRecipe}, IEnumerable{BuffRecipe}, IEnumerable{GuardRecipe}, IEnumerable{BuffRecipe})"/>,另带投掷类技能(状态 0x0d 一族)。</summary>
        public SkillRecipes(IEnumerable<SkillRecipe> recipes, IEnumerable<BuffRecipe> buffs, IEnumerable<GuardRecipe> guards, IEnumerable<BuffRecipe> classBuffs, IEnumerable<ThrowRecipe> throws)
            : this(recipes, buffs, guards, classBuffs)
        {
            if (throws != null)
            {
                foreach (ThrowRecipe t in throws)
                {
                    _throws[t.SkillId] = t;
                }
            }
        }

        /// <summary>这个技能的投掷类放法;不是投掷类技能返回 null。</summary>
        public ThrowRecipe FindThrow(int skillId)
        {
            return _throws.TryGetValue(skillId, out ThrowRecipe t) ? t : null;
        }

        /// <summary>全部投掷类技能。</summary>
        public IEnumerable<ThrowRecipe> AllThrows => _throws.Values;

        /// <summary>投掷类技能的个数。</summary>
        public int ThrowCount => _throws.Count;
    }
}
