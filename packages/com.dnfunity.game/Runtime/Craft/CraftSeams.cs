using System;

// 本项目自己的设计:制作规则对外的三道接缝——闸门(别的线的状态进不进得来)、钱包、掷骰。
// 制作线不读副职业的任何状态(任务单的隔离要求):副职业等级、技能要求、会员资格、制作熟练度这些服务端读码里确实参与制作判定的东西,
// 全部通过 ICraftGate 由宿主决定;默认的 OpenCraftGate 什么都放行、什么都不记。
namespace Dnf.Game
{
    /// <summary>
    /// 制作的闸门:规则本身不读别的系统的状态,需要别的系统点头或记账的地方都问它。
    /// 服务端读码里的对应物:<c>CUser::_CheckCompoundSkill</c> 0x08670f1e(会员专属配方要会员;副职业配方要技能要求达标、且已学会该配方)、
    /// <c>CExpertJob::IsSuccess</c>/<c>IncreaseExpertJobExp</c>(副职业的逐次成功判定与经验)、制作成功后的 <c>CItemMakingSkill::GainItemMakingSkillPoint</c>
    /// 0x085453e0(制作熟练度加点)。这些都是别的线的状态,这里只留接缝。
    /// </summary>
    public interface ICraftGate
    {
        /// <summary>能不能学会这个配方(配方簿里"已经会了"与"背包里没有配方物品"由规则自己判,不经过这里)。</summary>
        bool CanLearn(RecipeDef recipe);

        /// <summary>能不能制作这个配方(技能要求、会员专属、副职业配方等的额外条件;材料、配方物品、已学与否由规则自己判)。</summary>
        bool CanCraft(RecipeDef recipe);

        /// <summary>制作完成(成功或失败,材料都已扣掉)之后通知一次;熟练度、副职业经验、任务计数之类由宿主在这里记。</summary>
        void OnCrafted(RecipeDef recipe, CraftReport report);
    }

    /// <summary>默认闸门:什么都放行、什么都不记。可以继承后只改要管的那一项。</summary>
    public class OpenCraftGate : ICraftGate
    {
        public static readonly OpenCraftGate Instance = new OpenCraftGate();

        public virtual bool CanLearn(RecipeDef recipe)
        {
            return true;
        }

        public virtual bool CanCraft(RecipeDef recipe)
        {
            return true;
        }

        public virtual void OnCrafted(RecipeDef recipe, CraftReport report)
        {
        }
    }

    /// <summary>制作用的钱包(金币):配方的输入里编号 0 的项就是金币(服务端 CInventory::CheckNeedItemList 0x08507acc 把金币当作"物品编号 0")。</summary>
    public interface ICraftPurse
    {
        long Gold { get; }

        /// <summary>扣金币;不够(或数额不大于 0)返回 false、什么都不扣。</summary>
        bool TrySpend(long amount);
    }

    /// <summary>最简单的钱包(测试与命令行用)。</summary>
    public sealed class CraftPurse : ICraftPurse
    {
        public CraftPurse(long gold = 0)
        {
            Gold = gold < 0 ? 0 : gold;
        }

        public long Gold { get; private set; }

        public bool TrySpend(long amount)
        {
            if (amount <= 0 || amount > Gold)
            {
                return false;
            }
            Gold -= amount;
            return true;
        }
    }

    /// <summary>
    /// 制作成功率的掷骰:<see cref="Below"/> 给 [0, n) 里的整数。服务端是 <c>get_rand_int(100)</c> 0x086b1b87,
    /// 实际取值范围是 [0, 100)(只有内部数列恰好等于 100 时才会得到 100,概率可忽略,这里不复现);失败条件是"成功率不为 0 且成功率 ≤ 掷出的数"。
    /// </summary>
    public interface ICraftRoll
    {
        int Below(int n);
    }

    /// <summary>自带的有种子的掷骰(同一个种子在 Unity 与命令行里掷出同一串数)。</summary>
    public sealed class SeededCraftRoll : ICraftRoll
    {
        private readonly DropRandom _random;

        public SeededCraftRoll(int seed)
        {
            _random = new DropRandom(seed);
        }

        public int Below(int n)
        {
            if (n <= 1)
            {
                return 0;
            }
            return _random.NextInclusive(n - 1);
        }
    }
}
