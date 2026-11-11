using System.Collections.Generic;

// 服务端对应物:CUser::compound_item 0x0867218c 的返回码(0x11 配方或扣料出错、0x16 技能/会员/学会条件不满足、0x17 产物里有多件装备、
// 0x15 材料不够、4 背包放不下、100 同类放不下的另一种说法);这里换成按原因命名的结果,数值不照搬。
namespace Dnf.Game
{
    /// <summary>一次制作的结果(<see cref="Ok"/> 以外都是"什么也没动")。</summary>
    public enum CraftResult
    {
        Ok = 0,

        /// <summary>配方为空或数据残缺(服务端 _GetRecipeItem 取不到配方物品,返回 0x11)。</summary>
        UnknownRecipe,

        /// <summary>数量不在 1 到 65535 之间(服务端数量是 16 位无符号数),或乘上数量后溢出。</summary>
        InvalidCount,

        /// <summary>按配方物品制作时,背包里的配方物品不够(每次制作消耗一件)。</summary>
        NoRecipeItem,

        /// <summary>按已学配方制作时,配方簿里没有这个配方。</summary>
        NotLearned,

        /// <summary>闸门不放行(技能要求、会员专属、副职业条件等;服务端 _CheckCompoundSkill 0x08670f1e,返回 0x16)。</summary>
        NotAllowed,

        /// <summary>配方没有产物,或产物(含失败产物)的物品编号查不到(服务端 _IsCompoundResult2MoreEquip 0x086720d0 把查不到的产物算作不合格)。</summary>
        NoProduct,

        /// <summary>产物里有装备、乘上数量后多于一件(服务端 0x17)。</summary>
        TooManyEquipment,

        /// <summary>材料里有时装:时装合成在服务端是另一条路径(process_recipe_avatar_items 0x0868c4f4),这一线不做。</summary>
        AvatarInput,

        /// <summary>材料(含金币)不够(服务端 CheckNeedItemList 0x08507acc,返回 0x15)。</summary>
        MissingMaterials,

        /// <summary>背包放不下产物(服务端在背包副本上先试扣料再试放产物,放不下返回 4)。</summary>
        BagFull,
    }

    /// <summary>用配方物品学会配方的结果。</summary>
    public enum LearnResult
    {
        Ok = 0,
        UnknownRecipe,
        AlreadyLearned,
        NoRecipeItem,
        NotAllowed,
    }

    /// <summary>一次制作的报告。<see cref="Result"/> 不是 <see cref="CraftResult.Ok"/> 时其余字段都是空的。</summary>
    public sealed class CraftReport
    {
        internal CraftReport(CraftResult result)
        {
            Result = result;
            Consumed = new RecipeItem[0];
            Granted = new RecipeItem[0];
            Roll = -1;
        }

        internal CraftReport(
            CraftResult result, int count, bool success, int roll, long goldSpent, int recipeItemsConsumed,
            IReadOnlyList<RecipeItem> consumed, IReadOnlyList<RecipeItem> granted)
        {
            Result = result;
            Count = count;
            Success = success;
            Roll = roll;
            GoldSpent = goldSpent;
            RecipeItemsConsumed = recipeItemsConsumed;
            Consumed = consumed;
            Granted = granted;
        }

        public CraftResult Result { get; }

        public bool Ok => Result == CraftResult.Ok;

        /// <summary>制作了几份(请求的数量)。</summary>
        public int Count { get; }

        /// <summary>成功(拿到产物表);失败(拿到失败产物表,可能为空)。没有成功率的配方必成。只在 <see cref="Ok"/> 时有意义。</summary>
        public bool Success { get; }

        /// <summary>掷出的数(0 到 99);没有掷骰是 −1。服务端一次制作只掷一次,不是每份各掷一次。</summary>
        public int Roll { get; }

        /// <summary>扣掉的金币。</summary>
        public long GoldSpent { get; }

        /// <summary>按配方物品制作时消耗的配方物品件数(按已学配方制作为 0)。</summary>
        public int RecipeItemsConsumed { get; }

        /// <summary>扣掉的材料(不含金币),同一个编号合并成一项。</summary>
        public IReadOnlyList<RecipeItem> Consumed { get; }

        /// <summary>放进背包的物品,同一个编号合并成一项。</summary>
        public IReadOnlyList<RecipeItem> Granted { get; }
    }

    /// <summary>预览里的一行材料:要几件、背包里有几件(金币行的 <see cref="Code"/> 是 0)。</summary>
    public readonly struct MaterialLine
    {
        public MaterialLine(int code, long need, long have)
        {
            Code = code;
            Need = need;
            Have = have;
        }

        public int Code { get; }

        public long Need { get; }

        public long Have { get; }

        public bool IsGold => Code == RecipeItem.GoldCode;

        public bool Enough => Have >= Need;
    }

    /// <summary>制作前的预览(不动背包):这一次制作会得到什么结果、各行材料够不够、按材料算最多能做几份。</summary>
    public sealed class CraftPreview
    {
        internal CraftPreview(CraftResult result, IReadOnlyList<MaterialLine> lines, IReadOnlyList<RecipeItem> outputs, int maxByMaterials)
        {
            Result = result;
            Lines = lines;
            Outputs = outputs;
            MaxByMaterials = maxByMaterials;
        }

        /// <summary>如果现在就做,会得到的结果(<see cref="CraftResult.Ok"/> 表示能做;成功率另算,预览不掷骰)。</summary>
        public CraftResult Result { get; }

        public bool CanCraft => Result == CraftResult.Ok;

        /// <summary>材料行(已乘上数量;金币行在前,其余按配方次序、同编号合并)。</summary>
        public IReadOnlyList<MaterialLine> Lines { get; }

        /// <summary>成功时的产物(已乘上数量,同编号合并)。</summary>
        public IReadOnlyList<RecipeItem> Outputs { get; }

        /// <summary>只按材料(与按配方物品制作时的配方物品件数)算,最多能做几份(上限 65535);一份也做不了是 0。</summary>
        public int MaxByMaterials { get; }
    }
}
