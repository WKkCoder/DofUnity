using System;
using System.Collections.Generic;

// 服务端静态读码:配方数据的结构是 RecipeInfo(挂在 CStackableItem 偏移 0x174),由 createRecipeInfo 0x089ba83c 按 [int data] 一串整数建出
// (输入表、产物表、技能要求表、尾部标志),再由 RecipeInfo::additionalInfo 0x08513d44 补上 [success rate]、[broadcast type]、[maintain ability]、
// [fail output];[bead item] 另存一张表,制作时接在输入表后面(CUser::compound_item 0x0867218c)。本文件只放数据形状,读法见 RecipeScript。
namespace Dnf.Game
{
    /// <summary>配方里的一项:物品编号与数量。编号 0 是金币(服务端把金币当作"物品编号 0"放在同一张表里,CInventory::CheckNeedItemList 0x08507acc)。</summary>
    public readonly struct RecipeItem : IEquatable<RecipeItem>
    {
        /// <summary>金币的"物品编号"。</summary>
        public const int GoldCode = 0;

        /// <summary>对战点数的"物品编号"(服务端 CheckNeedItemList 的第二个特例;全库没有配方用到,这里不支持)。</summary>
        public const int PvpPointCode = 2;

        public RecipeItem(int code, int count)
        {
            Code = code;
            Count = count;
        }

        public int Code { get; }

        public int Count { get; }

        public bool IsGold => Code == GoldCode;

        public bool Equals(RecipeItem other)
        {
            return Code == other.Code && Count == other.Count;
        }

        public override bool Equals(object obj)
        {
            return obj is RecipeItem other && Equals(other);
        }

        public override int GetHashCode()
        {
            return unchecked((Code * 397) ^ Count);
        }

        public override string ToString()
        {
            return Code + "x" + Count;
        }
    }

    /// <summary>配方的技能要求项:技能编号与最低等级(服务端 SkillSlot::check_skill_list 的一项)。</summary>
    public readonly struct RecipeSkill : IEquatable<RecipeSkill>
    {
        public RecipeSkill(int skill, int level)
        {
            Skill = skill;
            Level = level;
        }

        public int Skill { get; }

        public int Level { get; }

        public bool Equals(RecipeSkill other)
        {
            return Skill == other.Skill && Level == other.Level;
        }

        public override bool Equals(object obj)
        {
            return obj is RecipeSkill other && Equals(other);
        }

        public override int GetHashCode()
        {
            return unchecked((Skill * 397) ^ Level);
        }
    }

    /// <summary>配方的种类:由 <c>[int data]</c> 末尾那个可有可无的标志整数决定(1 是会员专属、2 是副职业配方,别的值或没有都是普通配方)。</summary>
    public enum RecipeKind
    {
        Normal = 0,
        Premium = 1,
        ExpertJob = 2,
    }

    /// <summary>
    /// 制作类别(<see cref="ItemMakingTypes"/>):<c>[minimum itemmaking level]</c> 后面的字符串换成的编号,
    /// 服务端 ImportStackableScript 0x089aeec8 里的 11 项映射,存在配方物品脚本偏移 0x444。
    /// </summary>
    public static class ItemMakingTypes
    {
        /// <summary>没写 <c>[minimum itemmaking level]</c> 时的值。</summary>
        public const int None = -1;

        private static readonly string[] Names =
        {
            "weapon", "cloth", "leather", "larmor", "harmor", "plate", "amulet", "wrist", "ring", "support", "magic stone",
        };

        /// <summary>类别个数。</summary>
        public static int Count => Names.Length;

        /// <summary>类别名对应的编号(区分大小写,与服务端的映射一致);不认识返回 <see cref="None"/>。</summary>
        public static int FromName(string name)
        {
            for (int i = 0; i < Names.Length; i++)
            {
                if (string.Equals(Names[i], name, StringComparison.Ordinal))
                {
                    return i;
                }
            }
            return None;
        }

        /// <summary>编号对应的类别名;不在范围内返回 null。</summary>
        public static string NameOf(int type)
        {
            return (uint)type < (uint)Names.Length ? Names[type] : null;
        }
    }

    /// <summary>
    /// 一个配方的定义(只读)。<see cref="Code"/> 是配方物品(<c>[stackable type] [recipe]</c> 的可叠加物品)的编号。
    /// 输入 <see cref="Inputs"/>、产物 <see cref="Outputs"/>、技能要求 <see cref="Requirements"/> 来自 <c>[int data]</c>;
    /// <see cref="ExtraInputs"/> 来自 <c>[bead item]</c>,制作时接在输入后面(<see cref="AllInputs"/>);
    /// <see cref="SuccessRate"/> 为 0 表示不掷骰、必成,否则是 1 到 100 的百分数;<see cref="FailOutputs"/> 是失败时代替产物的那张表。
    /// </summary>
    public sealed class RecipeDef
    {
        private static readonly RecipeItem[] NoItems = new RecipeItem[0];
        private static readonly RecipeSkill[] NoSkills = new RecipeSkill[0];
        private static readonly string[] NoStrings = new string[0];

        private readonly RecipeItem[] _inputs;
        private readonly RecipeItem[] _outputs;
        private readonly RecipeItem[] _extra;
        private readonly RecipeItem[] _fail;
        private readonly RecipeSkill[] _requirements;
        private readonly string[] _categories;
        private readonly RecipeItem[] _all;

        public RecipeDef(
            int code,
            IEnumerable<RecipeItem> inputs,
            IEnumerable<RecipeItem> outputs,
            RecipeKind kind = RecipeKind.Normal,
            IEnumerable<RecipeSkill> requirements = null,
            IEnumerable<RecipeItem> extraInputs = null,
            int successRate = 0,
            IEnumerable<RecipeItem> failOutputs = null,
            int broadcastType = 0,
            int maintainAbility = 0,
            int makingType = ItemMakingTypes.None,
            int makingLevel = 0,
            IEnumerable<string> categories = null,
            int typeIndex = 0,
            string path = null,
            bool malformed = false)
        {
            Code = code;
            Path = path;
            Kind = kind;
            SuccessRate = successRate;
            BroadcastType = broadcastType;
            MaintainAbility = maintainAbility;
            MakingType = makingType;
            MakingLevel = makingLevel;
            TypeIndex = typeIndex;
            Malformed = malformed;
            _inputs = inputs == null ? NoItems : new List<RecipeItem>(inputs).ToArray();
            _outputs = outputs == null ? NoItems : new List<RecipeItem>(outputs).ToArray();
            _extra = extraInputs == null ? NoItems : new List<RecipeItem>(extraInputs).ToArray();
            _fail = failOutputs == null ? NoItems : new List<RecipeItem>(failOutputs).ToArray();
            _requirements = requirements == null ? NoSkills : new List<RecipeSkill>(requirements).ToArray();
            _categories = categories == null ? NoStrings : new List<string>(categories).ToArray();
            _all = new RecipeItem[_inputs.Length + _extra.Length];
            Array.Copy(_inputs, 0, _all, 0, _inputs.Length);
            Array.Copy(_extra, 0, _all, _inputs.Length, _extra.Length);
        }

        /// <summary>配方物品的编号。</summary>
        public int Code { get; }

        /// <summary>配方物品脚本的规范路径(合成数据可以是 null)。</summary>
        public string Path { get; }

        public RecipeKind Kind { get; }

        /// <summary><c>[int data]</c> 的第一张表:制作要消耗的物品(含金币)。</summary>
        public IReadOnlyList<RecipeItem> Inputs => _inputs;

        /// <summary><c>[int data]</c> 的第二张表:制作成功得到的物品。</summary>
        public IReadOnlyList<RecipeItem> Outputs => _outputs;

        /// <summary><c>[int data]</c> 的第三张表:制作者要会的技能。</summary>
        public IReadOnlyList<RecipeSkill> Requirements => _requirements;

        /// <summary><c>[bead item]</c>:额外要消耗的物品,制作时接在 <see cref="Inputs"/> 后面。</summary>
        public IReadOnlyList<RecipeItem> ExtraInputs => _extra;

        /// <summary>制作时真正要消耗的全部物品:<see cref="Inputs"/> 在前、<see cref="ExtraInputs"/> 在后(服务端 compound_item 先拷输入表、再把额外表接上去)。</summary>
        public IReadOnlyList<RecipeItem> AllInputs => _all;

        /// <summary><c>[success rate]</c>:0 表示不掷骰(必成),否则成功的百分数。</summary>
        public int SuccessRate { get; }

        /// <summary><c>[fail output]</c>:失败时代替 <see cref="Outputs"/> 的物品表(可以为空,为空就什么也得不到)。</summary>
        public IReadOnlyList<RecipeItem> FailOutputs => _fail;

        /// <summary><c>[broadcast type]</c>:不为 0 时成功后向全服与公会播报(这里只存值,播报是别的模块的事)。</summary>
        public int BroadcastType { get; }

        /// <summary><c>[maintain ability]</c>:为 1 时产物沿用材料装备的词条(装备实例的状态,背包里没有这一层,这里只存值)。</summary>
        public int MaintainAbility { get; }

        /// <summary><see cref="ItemMakingTypes"/> 的编号;没写 <c>[minimum itemmaking level]</c> 是 <see cref="ItemMakingTypes.None"/>。</summary>
        public int MakingType { get; }

        /// <summary><c>[minimum itemmaking level]</c> 的等级(没写为 0);不为 0 时服务端制作成功后给制作熟练度加点。</summary>
        public int MakingLevel { get; }

        /// <summary><c>[string data]</c> 里的全部字符串(制作类别名,如手工、织造;全库每个配方恰好一个)。</summary>
        public IReadOnlyList<string> Categories => _categories;

        /// <summary>第一个类别名;没有为 null。</summary>
        public string Category => _categories.Length > 0 ? _categories[0] : null;

        /// <summary><c>[stackable type]</c> 后面那个整数(含义没有查明:服务端读进脚本但不拷给物品)。</summary>
        public int TypeIndex { get; }

        /// <summary>数据残缺:<c>[int data]</c> 的各张表声明的项数超过了实际的整数个数,或 <c>[minimum itemmaking level]</c> 读不全。残缺的配方不能制作。</summary>
        public bool Malformed { get; }

        /// <summary>有成功率(要掷骰)。</summary>
        public bool HasSuccessRoll => SuccessRate != 0;
    }
}
