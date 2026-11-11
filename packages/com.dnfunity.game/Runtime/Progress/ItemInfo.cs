using System;
using System.Collections.Generic;
using Dnf.Data;

// 证据:E-117(服务端 ImportEquipmentScript 0x08991aeb 读 [equipment type] 时用 GetEquipmentType 0x08990f1f 查 characterEquipmentTypeTags 数组 @0x093f4fc0;
// ImportStackableScript 0x089aeec8 里 [stackable type] 的"字符串 → 编号"链;CItem 的取值函数 get_grade 0x08110c54、get_rarity 0x080f12d6、
// get_gen_rate 0x0832e02a、isSpecialMonsterDropItem 0x08539e00)。
namespace Dnf.Game
{
    /// <summary>物品的两大类:装备(.equ)与可叠加物品(.stk)。</summary>
    public enum ItemKind
    {
        Equipment = 0,
        Stackable = 1,
    }

    /// <summary>
    /// 装备类型(<c>[equipment type]</c> 后面的字符串)与服务端编号的对应:服务端把它存成 <c>CEquipItem</c> 偏移 0x234 的整数,
    /// <c>GetEquipmentType</c> 就是在 26 个标签的数组里找下标(找不到返回 26)。编号的次序就是数组的次序,证据 E-117。
    /// 由这个编号派生的三个判断也来自服务端:<c>IsAvatarItem</c>(0x08514d46)是 0 到 9、<c>IsEnchantalbe</c>(0x08514db0)是 10 到 21、
    /// <c>IsCreatureItem</c>(0x08514d72)是 22 到 25。
    /// </summary>
    public static class EquipmentParts
    {
        public const int HatAvatar = 0;
        public const int AuroraAvatar = 9;
        public const int Weapon = 10;
        public const int TitleName = 11;
        public const int Coat = 12;
        public const int Shoulder = 13;
        public const int Pants = 14;
        public const int Shoes = 15;
        public const int Waist = 16;
        public const int Amulet = 17;
        public const int Wrist = 18;
        public const int Ring = 19;
        public const int Support = 20;
        public const int MagicStone = 21;
        public const int Creature = 22;
        public const int ArtifactRed = 23;
        public const int ArtifactBlue = 24;
        public const int ArtifactGreen = 25;

        /// <summary>标签不在表里时的编号(服务端返回数组长度)。</summary>
        public const int Unknown = 26;

        private static readonly string[] Tags =
        {
            "[hat avatar]", "[hair avatar]", "[face avatar]", "[coat avatar]", "[pants avatar]", "[shoes avatar]", "[breast avatar]",
            "[waist avatar]", "[skin avatar]", "[aurora avatar]", "[weapon]", "[title name]", "[coat]", "[shoulder]", "[pants]",
            "[shoes]", "[waist]", "[amulet]", "[wrist]", "[ring]", "[support]", "[magic stone]", "[creature]", "[artifact red]",
            "[artifact blue]", "[artifact green]",
        };

        /// <summary>标签对应的编号(不区分大小写);不认识返回 <see cref="Unknown"/>。</summary>
        public static int FromTag(string tag)
        {
            if (tag != null)
            {
                for (int i = 0; i < Tags.Length; i++)
                {
                    if (string.Equals(Tags[i], tag, StringComparison.OrdinalIgnoreCase))
                    {
                        return i;
                    }
                }
            }
            return Unknown;
        }

        /// <summary>编号对应的标签;越界返回 null。</summary>
        public static string TagOf(int part)
        {
            return part >= 0 && part < Tags.Length ? Tags[part] : null;
        }

        public static bool IsAvatar(int part) => part >= 0 && part <= AuroraAvatar;

        public static bool IsEnchantable(int part) => part >= Weapon && part <= MagicStone;

        /// <summary>宠物与三种宠物装备(掉落里的第四类物品)。</summary>
        public static bool IsCreature(int part) => part >= Creature && part <= ArtifactGreen;
    }

    /// <summary>
    /// 可叠加物品的种类(<c>[stackable type]</c> 后面的字符串)与服务端编号的对应(<c>CStackableItem</c> 偏移 0x2b0,<c>GetItemType</c> 0x08514a84 返回它)。
    /// 编号表是从 <c>ImportStackableScript</c> 里"字符串 → 赋值"那 40 条分支读出来的(证据 E-117;其中"随机可升级"与"多重可升级"都是 26)。
    /// </summary>
    public static class StackableTypes
    {
        public const int Waste = 0;
        public const int Material = 1;
        public const int Recipe = 3;
        public const int Throw = 4;
        public const int Quest = 8;
        public const int Etc = 11;

        /// <summary>标签不在表里时返回它。</summary>
        public const int Unknown = -1;

        private static readonly Dictionary<string, int> ByTag = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["[waste]"] = 0, ["[material]"] = 1, ["[material expert job]"] = 2, ["[recipe]"] = 3, ["[throw]"] = 4, ["[set]"] = 5,
            ["[legacy]"] = 6, ["[stackable legacy]"] = 7, ["[quest]"] = 8, ["[creature]"] = 9, ["[feed]"] = 10, ["[creature expitem]"] = 35,
            ["[etc]"] = 11, ["[only effect]"] = 12, ["[upgradable legacy]"] = 13, ["[cera package]"] = 14, ["[usable cera package]"] = 38,
            ["[cera booster]"] = 15, ["[booster]"] = 16, ["[booster random]"] = 17, ["[unlimited waste]"] = 18,
            ["[multi upgradable legacy]"] = 26, ["[multi upgradable legacy bonus cera]"] = 27, ["[quest receive]"] = 19,
            ["[avatar emblem]"] = 20, ["[unlimited etc]"] = 21, ["[disguise]"] = 22, ["[expert town potion]"] = 24,
            ["[global effect]"] = 23, ["[contract]"] = 25, ["[enchant waste]"] = 28, ["[random upgradable legacy]"] = 26,
            ["[upgrade limit cube]"] = 29, ["[tarot card lottery]"] = 30, ["[tarot card]"] = 31, ["[town and dungeon]"] = 32,
            ["[unlimited town and dungeon]"] = 33, ["[booster selection]"] = 34, ["[dye]"] = 36, ["[teleport potion]"] = 37,
        };

        /// <summary>标签对应的编号(不区分大小写);不认识返回 <see cref="Unknown"/>。</summary>
        public static int FromTag(string tag)
        {
            return tag != null && ByTag.TryGetValue(tag, out int value) ? value : Unknown;
        }
    }

    /// <summary>
    /// 一件物品的头部信息(.equ / .stk 里掉落、拾取、摆放要用的那些标签):编号与路径来自 <c>equipment.lst</c> / <c>stackable.lst</c>。
    /// 只留这一层要用的字段;装备的基础属性读进 <see cref="Stats"/>(攻击、防御、生命魔法、速度、元素、抗性等,深度 0 的数值标签),
    /// 武器的外观图层读进 <see cref="Look"/>。强化券、附魔卡的属性块、套装定义与强化等级区间读进 <see cref="Enhance"/>(<see cref="EnhanceData"/>);强化表、增幅、品级不在物品脚本里。数值是原版数据,不入库。
    /// </summary>
    public sealed class ItemInfo
    {
        public ItemInfo(int code, string path, ItemKind kind)
        {
            Code = code;
            Path = path;
            Kind = kind;
            Part = -1;
            StackableType = StackableTypes.Unknown;
            UsableJobs = new List<string>();
            TextKind = -1;
        }

        private EquipStats _stats = EquipStats.Empty;

        /// <summary>
        /// 装备的基础属性(<see cref="EquipStats"/>:.equ 里深度 0 的数值标签,块里的同名标签不算);可叠加物品,
        /// 以及没写任何属性项的装备,是 <see cref="EquipStats.Empty"/>(不为 null,给 null 也当作没有)。穿上后经 HeroProgress 汇总进主角属性。
        /// </summary>
        public EquipStats Stats
        {
            get => _stats;
            set => _stats = value ?? EquipStats.Empty;
        }

        /// <summary>武器的外观图层数据(<see cref="EquipLook"/>,只给数据,渲染层自己决定怎么用);不是武器(含可叠加物品)为 null。</summary>
        public EquipLook Look { get; set; }

        /// <summary>物品编号(.lst 里的编号;装备与可叠加物品的编号不重叠)。</summary>
        public int Code { get; }

        /// <summary>规范化后的脚本路径。</summary>
        public string Path { get; }

        public ItemKind Kind { get; }

        /// <summary><c>[name]</c> 的字符串键(如 name_12345);原文在 equipment.kor.str / stackable.kor.str 里,用 <see cref="TextKind"/> 与键查(见 StrLibrary)。</summary>
        public string NameKey { get; set; }

        /// <summary><c>[explain]</c> 的字符串键;没写为 null。</summary>
        public string ExplainKey { get; set; }

        /// <summary><c>[basic explain]</c> 的字符串键(装备的基础说明);没写为 null。</summary>
        public string BasicExplainKey { get; set; }

        /// <summary><c>[flavor text]</c> 的字符串键(背景故事一类的文字);没写为 null。</summary>
        public string FlavorKey { get; set; }

        /// <summary>
        /// 文字引用的种类编号(<c>[name]</c> 等标签的值是"种类编号 + 键"一对,这里是前一半,决定去哪份 *.kor.str 里查,见 StrLibrary.PathOfKind);
        /// 没读到任何文字引用时为 −1。
        /// </summary>
        public int TextKind { get; set; }

        /// <summary><c>[grade]</c>:物品等级,掉落池按它和怪物等级的差选(见 <see cref="ItemPool"/>)。</summary>
        public int Grade { get; set; }

        /// <summary><c>[rarity]</c>:稀有度 0 到 5。</summary>
        public int Rarity { get; set; }

        /// <summary><c>[creation rate]</c>:进掉落池的权重;0 或没写就不进。</summary>
        public int CreationRate { get; set; }

        /// <summary><c>[special monster drop]</c>:特殊怪物才掉的物品,不进普通池(<c>isSpecialMonsterDropItem</c>)。</summary>
        public bool SpecialMonsterDrop { get; set; }

        /// <summary>装备类型编号(见 <see cref="EquipmentParts"/>);可叠加物品是 −1。</summary>
        public int Part { get; set; }

        /// <summary>可叠加物品的种类编号(见 <see cref="StackableTypes"/>);装备是 −1。</summary>
        public int StackableType { get; set; }

        /// <summary><c>[equipment type]</c> / <c>[stackable type]</c> 后面跟的那个整数(含义没有查明,原样留着)。</summary>
        public int TypeIndex { get; set; }

        /// <summary><c>[field image]</c>:地上的图包(原样,如带大小写的路径)与帧号;没写为 null 与 −1。</summary>
        public string FieldImage { get; set; }

        public int FieldFrame { get; set; } = -1;

        /// <summary><c>[icon]</c>:图标图包与帧号。</summary>
        public string Icon { get; set; }

        public int IconFrame { get; set; } = -1;

        /// <summary><c>[minimum level]</c>:穿戴或使用的最低等级;没写为 0。</summary>
        public int MinimumLevel { get; set; }

        /// <summary><c>[attach type]</c>(如 [free]、[trade])。</summary>
        public string AttachType { get; set; }

        /// <summary><c>[usable job]</c> 里的职业标签(如 [all]);没写时为空表。</summary>
        public List<string> UsableJobs { get; }

        /// <summary><c>[price]</c>(没写为 0)。商店买入的单价。</summary>
        public int Price { get; set; }

        /// <summary><c>[value]</c>(没写为 0):商店收购时的基准价,收购价 = 它乘上价格倍率表的一档再除以 1000(见 <see cref="ItemPricing"/>,证据 E-119)。</summary>
        public int Value { get; set; }

        /// <summary><c>[finish point price]</c>(没写为 0):不为 0 的物品商店不收(服务端 <c>user_sell_item</c> 的一道检查,证据 E-119)。</summary>
        public int FinishPointPrice { get; set; }

        /// <summary><c>[possible kiri protect]</c>(标签本身就是开关,后面不带值):收购价用价格倍率表的另一档(服务端 <c>getItemSellPrice</c> 的第四个参数,证据 E-119)。</summary>
        public bool KiriProtectable { get; set; }

        /// <summary><c>[attach type]</c> 是 <c>[trade delete]</c>:商店不收(证据 E-119)。</summary>
        public bool IsTradeDelete => string.Equals(AttachType, "[trade delete]", StringComparison.OrdinalIgnoreCase);

        /// <summary><c>[stack limit]</c>(没写为 0,按 <see cref="Inventory.DefaultStackLimit"/> 处理)。</summary>
        public int StackLimit { get; set; }

        /// <summary><c>[move wav]</c>:物品"被拿起、放下"时的声音标签(audio.xml 的 ID,如某某_TOUCH);拾取地上物品时放它是推断。没写为 null。</summary>
        public string MoveSound { get; set; }

        /// <summary><c>[hp recovery]</c>:使用后回复生命(<see cref="RecoveryEffect"/>:定额或百分比、总量、发放的毫秒数);没写为 null(证据 E-120)。</summary>
        public RecoveryEffect HpRecovery { get; set; }

        /// <summary><c>[mp recovery]</c>:使用后回复魔法值,形状同 <see cref="HpRecovery"/>;没写为 null。</summary>
        public RecoveryEffect MpRecovery { get; set; }

        /// <summary><c>[cool time]</c>:使用后的冷却毫秒数(服务端按物品编号登记);没写为 0(没有冷却)。</summary>
        public int CoolTimeMs { get; set; }

        /// <summary><c>[cooltime group]</c>:冷却的归类号,同组共用冷却(推断);没写为 −1(按物品编号各自冷却)。</summary>
        public int CoolGroup { get; set; } = -1;

        public bool IsEquipment => Kind == ItemKind.Equipment;

        public bool IsStackable => Kind == ItemKind.Stackable;

        private ItemInfo _definition;

        /// <summary>
        /// 强化、套装、附魔卡、强化券这些"周边"标签读出来的数据(见 <see cref="EnhanceData"/>);没写任何这类标签的物品为 null。
        /// 属于定义(同编号共享),个体拷贝继承它。
        /// </summary>
        public EnhanceData Enhance { get; set; }

        /// <summary>
        /// 这件装备身上"会变"的状态(强化等级、增幅、附魔、品级、耐久,见 <see cref="EquipInstance"/>);共享的定义为 null。
        /// 状态跟着个体拷贝走,换状态要换一份新拷贝(<see cref="WithInstance"/>),同一份拷贝里的状态不要原地改。
        /// </summary>
        public EquipInstance Instance { get; private set; }

        /// <summary>按物品编号共享的定义:个体拷贝指回它的来源,定义自己就是定义。</summary>
        public ItemInfo Definition => _definition ?? this;

        /// <summary>这一份是带个体状态的拷贝。</summary>
        public bool IsInstance => _definition != null;

        /// <summary>这件装备当前的个体状态;没有个体状态时是初始值(一份新对象,改它不影响任何东西)。</summary>
        public EquipInstance StateOrDefault => Instance != null ? Instance.Clone() : new EquipInstance();

        /// <summary>
        /// 同一件装备换一份个体状态:返回新的拷贝(其余字段与定义相同,<see cref="Stats"/> 还是定义的基础属性,
        /// 强化、附魔、套装的加成由 <see cref="EquipmentSlots.Overlay"/> 叠在汇总上);<paramref name="state"/> 为 null
        /// 或是初始值时回到共享的定义(没有个体状态的装备就是定义本身,和以前完全一样)。只对装备有意义,可叠加物品原样返回。
        /// </summary>
        public ItemInfo WithInstance(EquipInstance state)
        {
            ItemInfo definition = Definition;
            if (!IsEquipment)
            {
                return this;
            }
            if (state == null || state.IsDefault)
            {
                return definition;
            }
            var copy = (ItemInfo)definition.MemberwiseClone();
            copy._definition = definition;
            copy.Instance = state.Clone();
            return copy;
        }

        /// <summary>宠物与宠物装备(服务端 <c>IsCreatureItem</c>);只对装备有意义。</summary>
        public bool IsCreature => IsEquipment && EquipmentParts.IsCreature(Part);

        /// <summary>时装(服务端 <c>IsAvatarItem</c>);只对装备有意义。</summary>
        public bool IsAvatar => IsEquipment && EquipmentParts.IsAvatar(Part);

        /// <summary>地上图包在素材库里的名字(客户端 <c>Item/xxx.img</c> → <c>sprite/item/xxx.img</c>,小写);没写 <c>[field image]</c> 为 null。</summary>
        public string FieldImageKey => FieldImage == null ? null : ImageKey(FieldImage);

        /// <summary>图标图包在素材库里的名字;没写为 null。</summary>
        public string IconKey => Icon == null ? null : ImageKey(Icon);

        /// <summary>脚本里的图包路径(<c>Item/FieldImage.img</c>)换成素材库的键(<c>sprite/item/fieldimage.img</c>)。</summary>
        public static string ImageKey(string scriptPath)
        {
            return "sprite/" + scriptPath.Replace('\\', '/').ToLowerInvariant();
        }
    }

    /// <summary>从物品脚本的标记流里读 <see cref="ItemInfo"/>。一遍扫描、只取用得到的标签(全库有 7 万多个 .equ,扫描要快)。</summary>
    public static class ItemScript
    {
        private enum Field
        {
            None,
            Name,
            Explain,
            BasicExplain,
            FlavorText,
            Grade,
            Rarity,
            CreationRate,
            SpecialMonsterDrop,
            EquipmentType,
            StackableType,
            FieldImage,
            Icon,
            MinimumLevel,
            AttachType,
            UsableJob,
            Price,
            Value,
            FinishPointPrice,
            KiriProtectable,
            StackLimit,
            MoveSound,
            HpRecovery,
            MpRecovery,
            CoolTime,
            CoolGroup,
        }

        private static readonly Dictionary<string, Field> Fields = new Dictionary<string, Field>(StringComparer.OrdinalIgnoreCase)
        {
            ["[name]"] = Field.Name,
            ["[explain]"] = Field.Explain,
            ["[basic explain]"] = Field.BasicExplain,
            ["[flavor text]"] = Field.FlavorText,
            ["[grade]"] = Field.Grade,
            ["[rarity]"] = Field.Rarity,
            ["[creation rate]"] = Field.CreationRate,
            ["[special monster drop]"] = Field.SpecialMonsterDrop,
            ["[equipment type]"] = Field.EquipmentType,
            ["[stackable type]"] = Field.StackableType,
            ["[field image]"] = Field.FieldImage,
            ["[icon]"] = Field.Icon,
            ["[minimum level]"] = Field.MinimumLevel,
            ["[attach type]"] = Field.AttachType,
            ["[usable job]"] = Field.UsableJob,
            ["[price]"] = Field.Price,
            ["[value]"] = Field.Value,
            ["[finish point price]"] = Field.FinishPointPrice,
            ["[possible kiri protect]"] = Field.KiriProtectable,
            ["[stack limit]"] = Field.StackLimit,
            ["[move wav]"] = Field.MoveSound,
            ["[hp recovery]"] = Field.HpRecovery,
            ["[mp recovery]"] = Field.MpRecovery,
            ["[cool time]"] = Field.CoolTime,
            ["[cooltime group]"] = Field.CoolGroup,
        };

        /// <summary>
        /// 读一件物品;不是标记流脚本时返回 null。服务端的读法是一个标签一块(同名标签后一次覆盖前一次),这里同样:
        /// 每个字段取该标签后面的第一个对应类型的值,重复出现时后者覆盖。装备没有 <c>[equipment type]</c>(或可叠加物品没有 <c>[stackable type]</c>)时
        /// 类型留空(<see cref="ItemInfo.Part"/> = <see cref="EquipmentParts.Unknown"/> 或 <see cref="ItemInfo.StackableType"/> = <see cref="StackableTypes.Unknown"/>)。
        /// </summary>
        public static ItemInfo Parse(ReadOnlySpan<byte> data, PvfStringTable strings, ItemKind kind, int code, string path)
        {
            if (strings == null)
            {
                throw new ArgumentNullException(nameof(strings));
            }
            if (!TokenScript.IsTokenScript(data))
            {
                return null;
            }
            ScriptToken[] tokens = TokenScript.Parse(data);
            var info = new ItemInfo(code, path, kind);
            if (kind == ItemKind.Equipment)
            {
                info.Part = EquipmentParts.Unknown;
            }
            Field field = Field.None;
            bool haveString = false;      // 当前标签下是否已经读到第一个字符串(路径、类型名)
            bool haveInt = false;         // 当前标签下是否已经读到第一个整数
            bool recoveryPercent = false; // [hp recovery]/[mp recovery] 下:符号字符串是不是 "%"
            int recoveryInts = 0;         // 同上:已经读到几个整数(第一个是数量,第二个是持续毫秒)
            int recoveryAmount = 0;
            for (int i = 0; i < tokens.Length; i++)
            {
                ScriptToken t = tokens[i];
                if (t.Type == (byte)TokenType.Tag)
                {
                    string tag = (uint)t.Value < (uint)strings.Count ? strings[t.Value] : null;
                    field = tag != null && Fields.TryGetValue(tag, out Field f) ? f : Field.None;
                    haveString = false;
                    haveInt = false;
                    if (field == Field.UsableJob)
                    {
                        info.UsableJobs.Clear();
                    }
                    else if (field == Field.SpecialMonsterDrop)
                    {
                        info.SpecialMonsterDrop = false;
                    }
                    else if (field == Field.KiriProtectable)
                    {
                        info.KiriProtectable = true;         // 标签本身就是开关
                    }
                    else if (field == Field.HpRecovery || field == Field.MpRecovery)
                    {
                        recoveryPercent = false;             // 同名标签后一次覆盖前一次
                        recoveryInts = 0;
                        recoveryAmount = 0;
                        if (field == Field.HpRecovery)
                        {
                            info.HpRecovery = null;
                        }
                        else
                        {
                            info.MpRecovery = null;
                        }
                    }
                    continue;
                }
                if (field == Field.None)
                {
                    continue;
                }
                bool isInt = t.Type == (byte)TokenType.Int || t.Type == (byte)TokenType.UInt;
                bool isString = t.Type == (byte)TokenType.String;
                switch (field)
                {
                    case Field.Name:
                    case Field.Explain:
                    case Field.BasicExplain:
                    case Field.FlavorText:
                        if (t.Type == (byte)TokenType.StringRefKind && i + 1 < tokens.Length && tokens[i + 1].Type == (byte)TokenType.StringRefKey && !haveString)
                        {
                            int key = tokens[i + 1].Value;
                            string text = (uint)key < (uint)strings.Count ? strings[key] : null;
                            switch (field)
                            {
                                case Field.Name:
                                    info.NameKey = text;
                                    info.TextKind = t.Value;
                                    break;
                                case Field.Explain:
                                    info.ExplainKey = text;
                                    break;
                                case Field.BasicExplain:
                                    info.BasicExplainKey = text;
                                    break;
                                default:
                                    info.FlavorKey = text;
                                    break;
                            }
                            if (info.TextKind < 0)
                            {
                                info.TextKind = t.Value;
                            }
                            haveString = true;
                        }
                        break;
                    case Field.Grade:
                        if (isInt && !haveInt)
                        {
                            info.Grade = t.Value;
                            haveInt = true;
                        }
                        break;
                    case Field.Rarity:
                        if (isInt && !haveInt)
                        {
                            info.Rarity = t.Value;
                            haveInt = true;
                        }
                        break;
                    case Field.CreationRate:
                        if (isInt && !haveInt)
                        {
                            info.CreationRate = t.Value;
                            haveInt = true;
                        }
                        break;
                    case Field.SpecialMonsterDrop:
                        if (isInt && !haveInt)
                        {
                            info.SpecialMonsterDrop = t.Value != 0;
                            haveInt = true;
                        }
                        break;
                    case Field.MinimumLevel:
                        if (isInt && !haveInt)
                        {
                            info.MinimumLevel = t.Value;
                            haveInt = true;
                        }
                        break;
                    case Field.Price:
                        if (isInt && !haveInt)
                        {
                            info.Price = t.Value;
                            haveInt = true;
                        }
                        break;
                    case Field.Value:
                        if (isInt && !haveInt)
                        {
                            info.Value = t.Value;
                            haveInt = true;
                        }
                        break;
                    case Field.FinishPointPrice:
                        if (isInt && !haveInt)
                        {
                            info.FinishPointPrice = t.Value;
                            haveInt = true;
                        }
                        break;
                    case Field.StackLimit:
                        if (isInt && !haveInt)
                        {
                            info.StackLimit = t.Value;
                            haveInt = true;
                        }
                        break;
                    case Field.AttachType:
                        if (isString && !haveString)
                        {
                            info.AttachType = StringAt(strings, t.Value);
                            haveString = true;
                        }
                        break;
                    case Field.UsableJob:
                        if (isString)
                        {
                            string job = StringAt(strings, t.Value);
                            if (job != null)
                            {
                                info.UsableJobs.Add(job);
                            }
                        }
                        break;
                    case Field.EquipmentType:
                        if (isString && !haveString)
                        {
                            info.Part = EquipmentParts.FromTag(StringAt(strings, t.Value));
                            haveString = true;
                        }
                        else if (isInt && !haveInt)
                        {
                            info.TypeIndex = t.Value;
                            haveInt = true;
                        }
                        break;
                    case Field.StackableType:
                        if (isString && !haveString)
                        {
                            info.StackableType = StackableTypes.FromTag(StringAt(strings, t.Value));
                            haveString = true;
                        }
                        else if (isInt && !haveInt)
                        {
                            info.TypeIndex = t.Value;
                            haveInt = true;
                        }
                        break;
                    case Field.FieldImage:
                        if (isString && !haveString)
                        {
                            info.FieldImage = StringAt(strings, t.Value);
                            haveString = true;
                        }
                        else if (isInt && !haveInt)
                        {
                            info.FieldFrame = t.Value;
                            haveInt = true;
                        }
                        break;
                    case Field.Icon:
                        if (isString && !haveString)
                        {
                            info.Icon = StringAt(strings, t.Value);
                            haveString = true;
                        }
                        else if (isInt && !haveInt)
                        {
                            info.IconFrame = t.Value;
                            haveInt = true;
                        }
                        break;
                    case Field.MoveSound:
                        if (isString && !haveString)
                        {
                            info.MoveSound = StringAt(strings, t.Value);
                            haveString = true;
                        }
                        break;
                    case Field.CoolTime:
                        if (isInt && !haveInt)
                        {
                            info.CoolTimeMs = t.Value;
                            haveInt = true;
                        }
                        break;
                    case Field.CoolGroup:
                        if (isInt && !haveInt)
                        {
                            info.CoolGroup = t.Value;
                            haveInt = true;
                        }
                        break;
                    case Field.HpRecovery:
                    case Field.MpRecovery:
                        // 形状:"+"|"%" 数量 持续毫秒 "myself";第一个字符串是符号(整数之前),第一个整数是数量,第二个是持续毫秒,其余忽略
                        if (isString && !haveString && recoveryInts == 0)
                        {
                            recoveryPercent = string.Equals(StringAt(strings, t.Value), "%", StringComparison.Ordinal);
                            haveString = true;
                        }
                        else if (isInt && recoveryInts < 2)
                        {
                            if (recoveryInts == 0)
                            {
                                recoveryAmount = t.Value;
                            }
                            var effect = new RecoveryEffect(recoveryPercent, recoveryAmount, recoveryInts == 0 ? 0 : t.Value);
                            recoveryInts++;
                            if (field == Field.HpRecovery)
                            {
                                info.HpRecovery = effect;
                            }
                            else
                            {
                                info.MpRecovery = effect;
                            }
                        }
                        break;
                }
            }
            if (kind == ItemKind.Equipment)
            {
                // 属性与外观各自按块规则再扫一遍标记流(上面那一遍不分深度,别的字段的读法不动)
                info.Stats = EquipStats.Read(tokens, strings);
                if (info.Part == EquipmentParts.Weapon)
                {
                    info.Look = EquipLook.Read(tokens, strings);
                }
            }
            info.Enhance = EnhanceData.Read(tokens, strings);
            return info;
        }

        private static string StringAt(PvfStringTable strings, int index)
        {
            return (uint)index < (uint)strings.Count ? strings[index] : null;
        }
    }
}
