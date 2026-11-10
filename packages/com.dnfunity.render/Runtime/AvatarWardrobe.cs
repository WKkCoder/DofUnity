using System;
using System.Collections.Generic;

namespace Dnf.Render
{
    /// <summary>
    /// 主角的装扮外观(r14-avatar):在一套"基础外观"(皮肤加默认发型、上衣、裤子、鞋、帽子,再加武器,见 <see cref="AvatarOutfit.CreateDefault"/>)
    /// 之上穿装扮,穿上的装扮按部位替换基础外观里对应部位的图层,脱下就恢复,武器与其余部位不受影响。
    /// 部位号 0 到 9 与服务端时装部位一一对应(帽子、头发、脸、上衣、裤子、鞋、胸饰、腰饰、皮肤、灵气);
    /// 与 Dnf.Game 的 <c>AvatarSlots</c>、<c>EquipmentParts</c> 是两张各自独立的表,靠 Dnf.Play.Tests 的跨表测试保持一致(本程序集不依赖 Dnf.Game)。
    ///
    /// 事实(观察):
    ///   ① 每个部位的装备脚本只往自己的图层目录里放图(帽子 cap、头发 hair、脸 face、上衣 coat、裤子 pants、鞋 shoes、胸饰 neck、腰饰 belt),
    ///      皮肤件不带图层、只给出皮肤图片的编号,灵气件在剑魂职业下没有 [animation job] 块(全库统计,本机核对);
    ///   ② 各部位的顺序键([layer variation] 的整数)互相交错(例如头发有压在皮肤后面的层,也有压在帽子前面的层),
    ///      所以不能"一个部位一整段"地叠,必须像 <see cref="AvatarOutfit"/> 那样把全部图层按键整体排序(证据 E-065);
    ///   ③ 全库里有图层的装扮件,图片不齐全的只有个位数(其中有整件缺的,也有只缺一两层的),皮肤件也只缺一件;图片要到全部图包里找,不止职业自己的那几个包。
    /// 约定(没有原版可对照,只是让结果确定):同一个顺序键的图层,先画基础外观里原有的部位(原位替换),再画基础外观里没有的部位(按部位号从小到大),
    /// 武器最后——所以外观只取决于"穿了哪些",与穿的先后无关。
    /// 没做:装扮脚本里的 [hide equipment]、[hide layer] 一类的隐藏规则(语义没核对)、灵气的显示、染色、克隆时装。
    /// </summary>
    public sealed class AvatarWardrobe
    {
        /// <summary>装扮部位数(部位号 0 到 9)。</summary>
        public const int PartCount = 10;

        /// <summary>皮肤的部位号:皮肤件不带图层,换的是 <see cref="AvatarOutfit.SkinNumber"/>。</summary>
        public const int SkinPart = 8;

        // 与服务端标签表(E-117)的前十项同序:下标就是部位号
        private static readonly string[] Tags =
        {
            "[hat avatar]", "[hair avatar]", "[face avatar]", "[coat avatar]", "[pants avatar]",
            "[shoes avatar]", "[breast avatar]", "[waist avatar]", "[skin avatar]", "[aurora avatar]",
        };

        private readonly DnfAssets _assets;
        private readonly AvatarItem[] _worn = new AvatarItem[PartCount];
        private AvatarOutfit _base;
        private AvatarOutfit _current;

        public AvatarWardrobe(DnfAssets assets, AvatarOutfit baseOutfit)
        {
            _assets = assets ?? throw new ArgumentNullException(nameof(assets));
            _base = baseOutfit ?? throw new ArgumentNullException(nameof(baseOutfit));
            _current = baseOutfit;
        }

        /// <summary>不穿任何装扮时的外观(默认外观加武器)。</summary>
        public AvatarOutfit Base => _base;

        /// <summary>现在的外观:没穿任何装扮时就是 <see cref="Base"/> 本身(同一个对象),否则是合成出来的新外观。</summary>
        public AvatarOutfit Current => _current;

        /// <summary>外观的变化次数(穿上、换穿、脱下、换底座各算一次;什么也没变不算);宿主据此重赋 <c>TownHero.Outfit</c>。</summary>
        public int Version { get; private set; }

        /// <summary>穿着的装扮件数。</summary>
        public int WornCount
        {
            get
            {
                int count = 0;
                foreach (AvatarItem item in _worn)
                {
                    if (item != null)
                    {
                        count++;
                    }
                }
                return count;
            }
        }

        /// <summary>穿着的装扮,按部位号从小到大。</summary>
        public IEnumerable<AvatarItem> WornItems
        {
            get
            {
                foreach (AvatarItem item in _worn)
                {
                    if (item != null)
                    {
                        yield return item;
                    }
                }
            }
        }

        /// <summary>部位号对应的 [equipment type] 标签(例如 3 是 "[coat avatar]");部位号不在 0 到 9 返回 null。</summary>
        public static string TagOf(int part)
        {
            return (uint)part < (uint)PartCount ? Tags[part] : null;
        }

        /// <summary>[equipment type] 标签对应的装扮部位号(不区分大小写);不是装扮标签(例如武器)返回 -1。</summary>
        public static int PartOf(string equipmentType)
        {
            if (equipmentType == null)
            {
                return -1;
            }
            for (int part = 0; part < PartCount; part++)
            {
                if (string.Equals(Tags[part], equipmentType, StringComparison.OrdinalIgnoreCase))
                {
                    return part;
                }
            }
            return -1;
        }

        /// <summary>一件装备属于哪个装扮部位;不是装扮(或为 null)返回 -1。</summary>
        public static int PartOf(AvatarItem item)
        {
            return item == null ? -1 : PartOf(item.EquipmentType);
        }

        /// <summary>某部位现在穿着的装扮;没穿或部位号越界返回 null。</summary>
        public AvatarItem Worn(int part)
        {
            return (uint)part < (uint)PartCount ? _worn[part] : null;
        }

        /// <summary>
        /// 这件装扮在这个职业的画面上穿得出来吗:是装扮部位的装备、在本职业有 [variation];皮肤件要求它指向的皮肤图片能读出,
        /// 其余部位要求有图层且每一层的图片都能读出(全库只有个位数的件缺图,有的只缺一两层;"每一层都在"是为了不画出缺一块的外观而取的约定)。
        /// 装扮必须是用 <see cref="Base"/> 的职业(<see cref="AvatarOutfit.Class"/>)读出来的。
        /// </summary>
        public bool CanShow(AvatarItem item)
        {
            int part = PartOf(item);
            if (part < 0 || !item.HasVariation)
            {
                return false;
            }
            if (part == SkinPart)
            {
                return _assets.TryGetFrame(_base.Class.BodyImage(item.AvatarNumber), 0, out _);
            }
            if (item.Layers.Count == 0)
            {
                return false;
            }
            foreach (AvatarLayer layer in item.Layers)
            {
                if (layer.ImagePath == null || !_assets.TryGetFrame(layer.ImagePath, 0, out _))
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// 穿上一件装扮:替换同部位原来穿着的那件(没穿过就替换基础外观里的同部位)。穿不出来(见 <see cref="CanShow"/>)返回 false,什么也不变;
        /// 同一个对象已经穿着也返回 true、什么也不变。
        /// </summary>
        public bool Wear(AvatarItem item)
        {
            if (!CanShow(item))
            {
                return false;
            }
            int part = PartOf(item);
            if (ReferenceEquals(_worn[part], item))
            {
                return true;
            }
            _worn[part] = item;
            Rebuild();
            return true;
        }

        /// <summary>脱下某部位的装扮,基础外观里的那一部位原样回来。那个部位本来就没穿(或部位号越界)返回 false。</summary>
        public bool TakeOff(int part)
        {
            if ((uint)part >= (uint)PartCount || _worn[part] == null)
            {
                return false;
            }
            _worn[part] = null;
            Rebuild();
            return true;
        }

        /// <summary>脱下全部装扮。本来就没穿任何装扮返回 false。</summary>
        public bool Clear()
        {
            if (WornCount == 0)
            {
                return false;
            }
            Array.Clear(_worn, 0, _worn.Length);
            Rebuild();
            return true;
        }

        /// <summary>换基础外观(例如主角换了武器):穿着的装扮保留,按新底座重新合成。同一个对象什么也不变。</summary>
        public AvatarOutfit Rebase(AvatarOutfit baseOutfit)
        {
            if (baseOutfit == null)
            {
                throw new ArgumentNullException(nameof(baseOutfit));
            }
            if (!ReferenceEquals(_base, baseOutfit))
            {
                _base = baseOutfit;
                Rebuild();
            }
            return _current;
        }

        /// <summary>只换武器(<paramref name="weapon"/> 为 null 是脱下武器),装扮不动;等价于对 <see cref="Base"/> 做 <see cref="AvatarOutfit.WithWeapon"/> 再 <see cref="Rebase"/>。</summary>
        public AvatarOutfit SetWeapon(AvatarItem weapon)
        {
            return Rebase(_base.WithWeapon(weapon));
        }

        private void Rebuild()
        {
            _current = Compose();
            Version++;
        }

        private AvatarOutfit Compose()
        {
            if (WornCount == 0)
            {
                return _base;
            }
            var items = new List<AvatarItem>(_base.Items.Count + PartCount);
            var weapons = new List<AvatarItem>();
            var placed = new bool[PartCount];
            // 基础外观里原有的件:穿了同部位的装扮就在原位换成装扮(同部位的第二件基础件直接丢掉),没穿就保留;武器统一留到最后
            foreach (AvatarItem item in _base.Items)
            {
                int part = PartOf(item);
                if (part == SkinPart)
                {
                    continue;
                }
                if (part < 0)
                {
                    if (string.Equals(item.EquipmentType, "[weapon]", StringComparison.OrdinalIgnoreCase))
                    {
                        weapons.Add(item);
                    }
                    else
                    {
                        items.Add(item);
                    }
                    continue;
                }
                AvatarItem worn = _worn[part];
                if (worn == null)
                {
                    items.Add(item);
                }
                else if (!placed[part])
                {
                    items.Add(worn);
                    placed[part] = true;
                }
            }
            // 基础外观里没有的部位(脸、胸饰、腰饰一类):按部位号从小到大排在武器之前
            for (int part = 0; part < PartCount; part++)
            {
                if (part != SkinPart && _worn[part] != null && !placed[part])
                {
                    items.Add(_worn[part]);
                }
            }
            items.AddRange(weapons);
            int skin = _worn[SkinPart] != null ? _worn[SkinPart].AvatarNumber : _base.SkinNumber;
            return new AvatarOutfit(_base.Class, skin, items);
        }
    }
}
