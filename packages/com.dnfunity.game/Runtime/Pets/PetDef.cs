using System;

// 服务端静态读码得到的宠物脚本字段(装载:importCreatureScript 0x08a35e47;缺省值:CreatureScript::clear 0x0833dfac;
// 拷到运行期脚本:CCreatureScript::Set 0x0833c8e8)。这里只有自己命名的字段,不含原版数据。
namespace Dnf.Game
{
    /// <summary>宠物脚本 <c>[artifact slot]</c> 一节允许的神器颜色(服务端认 red / blue / green 三个字符串)。</summary>
    [Flags]
    public enum PetArtifactColors
    {
        None = 0,
        Red = 1,
        Blue = 2,
        Green = 4,
    }

    /// <summary>宠物种类(服务端实例上的"种类标志")。</summary>
    public enum PetKind
    {
        /// <summary>普通型:经验来自事件,等级查全局经验表,有饱食、会饿死。</summary>
        Normal = 0,

        /// <summary>选择成长型:等级由喂的物品个数决定,饱食恒满、不会饿死(这一型的物品喂养与进化选择这一版没有实现)。</summary>
        Grow = 1,
    }

    /// <summary>
    /// 脚本里 <c>[selective grow creature]</c> … <c>[/selective grow creature]</c> 一节(只在这一节之内才认的子标签)。
    /// 缺省值是服务端构造时的值:标志 0、选择等级与四个 A / B 项是 −1、每级物品数 1。
    /// </summary>
    public sealed class PetGrowInfo
    {
        /// <summary>"没写"的值。</summary>
        public const int Unset = -1;

        /// <summary><c>[using exp item]</c> 不为 0(用物品喂养)。</summary>
        public bool UsesExpItem { get; set; }

        /// <summary><c>[grow level]</c>:选择进化方向的等级。</summary>
        public int SelectLevel { get; set; } = Unset;

        /// <summary><c>[grow Atype creature id]</c>。</summary>
        public int ATypeCreatureId { get; set; } = Unset;

        /// <summary><c>[grow Btype creature id]</c>。</summary>
        public int BTypeCreatureId { get; set; } = Unset;

        /// <summary><c>[grow Atype item index]</c>。</summary>
        public int ATypeItemIndex { get; set; } = Unset;

        /// <summary><c>[grow Btype item index]</c>。</summary>
        public int BTypeItemIndex { get; set; } = Unset;

        /// <summary><c>[item count for levelup]</c>;0 时服务端换算等级按 <see cref="PetRules.DefaultGrowItemsPerLevel"/>。</summary>
        public int ItemsPerLevel { get; set; } = 1;

        /// <summary>整节复制(提交到脚本时用:服务端在结束标签处才把局部对象整体拷过去)。</summary>
        public PetGrowInfo Copy()
        {
            return new PetGrowInfo
            {
                UsesExpItem = UsesExpItem,
                SelectLevel = SelectLevel,
                ATypeCreatureId = ATypeCreatureId,
                BTypeCreatureId = BTypeCreatureId,
                ATypeItemIndex = ATypeItemIndex,
                BTypeItemIndex = BTypeItemIndex,
                ItemsPerLevel = ItemsPerLevel,
            };
        }
    }

    /// <summary>
    /// 一只宠物的定义(宠物脚本里与成长、喂养、神器格有关的字段;技能、动作、攻击信息等战斗部分不在这里)。
    /// 缺省值与服务端装载前的 <c>clear()</c> 一致:各等级项 1,神器颜色全无,进化三项 0。
    /// </summary>
    public sealed class PetDef
    {
        public PetDef(int id, string path)
        {
            Id = id;
            Path = path ?? string.Empty;
        }

        /// <summary>宠物编号(<c>creature.lst</c> 里的键;物品的 <c>[creature species]</c> 与进化目标都指向它)。</summary>
        public int Id { get; }

        /// <summary>脚本路径(已按列表位置解析成完整路径)。</summary>
        public string Path { get; }

        /// <summary><c>[name]</c>(按 Latin1 逐字节还原的原始文本,显示用)。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary><c>[start level]</c>。服务端装载后不拷进运行期脚本,新建宠物的等级也不取它(固定 1);保留字段只为对拍。</summary>
        public int StartLevel { get; set; } = 1;

        /// <summary><c>[permission level]</c>。服务端不拷进运行期脚本,用途未知。</summary>
        public int PermissionLevel { get; set; } = 1;

        /// <summary><c>[max level]</c>:等级上限(服务端运行期唯一用到的等级项)。</summary>
        public int MaxLevel { get; set; } = 1;

        /// <summary><c>[parent max level]</c>。服务端不拷进运行期脚本,用途未知。</summary>
        public int ParentMaxLevel { get; set; } = 1;

        /// <summary><c>[artifact slot]</c> 里写了哪几种颜色。</summary>
        public PetArtifactColors ArtifactColors { get; set; }

        /// <summary><c>[evolution quest]</c>(0 表示没有)。进化行为这一版没有实现。</summary>
        public int EvolutionQuest { get; set; }

        /// <summary><c>[evolution creature id]</c>(0 表示没有)。</summary>
        public int EvolutionCreatureId { get; set; }

        /// <summary><c>[evolution level]</c>(0 表示没有)。</summary>
        public int EvolutionLevel { get; set; }

        /// <summary>选择成长型的一节;没写时是全缺省值。</summary>
        public PetGrowInfo Grow { get; set; } = new PetGrowInfo();

        /// <summary>
        /// 种类:服务端的 <c>IsItemGrowCreature</c>——A、B 两个物品索引都是 −1 时不是成长型,否则看 <c>[using exp item]</c> 的标志。
        /// </summary>
        public PetKind Kind
        {
            get
            {
                bool neither = Grow.ATypeItemIndex == PetGrowInfo.Unset && Grow.BTypeItemIndex == PetGrowInfo.Unset;
                return !neither && Grow.UsesExpItem ? PetKind.Grow : PetKind.Normal;
            }
        }

        /// <summary>
        /// 这个装备部位号的神器格对这只宠物开不开放(部位 23 红、24 蓝、25 绿,对应 <see cref="EquipmentParts.ArtifactRed"/> 等);
        /// 其它部位号一律不开放。
        /// </summary>
        public bool AllowsArtifactPart(int part)
        {
            switch (part)
            {
                case EquipmentParts.ArtifactRed:
                    return (ArtifactColors & PetArtifactColors.Red) != 0;
                case EquipmentParts.ArtifactBlue:
                    return (ArtifactColors & PetArtifactColors.Blue) != 0;
                case EquipmentParts.ArtifactGreen:
                    return (ArtifactColors & PetArtifactColors.Green) != 0;
                default:
                    return false;
            }
        }
    }
}
