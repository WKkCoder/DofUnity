using System;
using System.Collections.Generic;
using System.Globalization;
using Dnf.Data;

namespace Dnf.Render
{
    /// <summary>
    /// 一个换装图层:层名(例如 coat_a、katanac)、叠放顺序键、它的 IMG 路径。
    /// 顺序键越小越先画(在后面),越大越后画(在前面);键来自装备脚本的 [layer variation](证据 E-065)。
    /// </summary>
    public readonly struct AvatarLayer
    {
        public string Name { get; }
        public int Key { get; }

        /// <summary>IMG 路径,小写,形如 "sprite/character/swordman/equipment/avatar/coat/sm_coat0000a.img"。</summary>
        public string ImagePath { get; }

        public AvatarLayer(string name, int key, string imagePath)
        {
            Name = name;
            Key = key;
            ImagePath = imagePath;
        }
    }

    /// <summary>
    /// 某个职业的换装资源布局,由角色脚本(.chr)的 [body image path] 推出:
    /// 皮肤是 ".../equipment/avatar/skin/sm_body%04d.img",其余部件在 ".../equipment/avatar/&lt;部件&gt;/",武器在 ".../equipment/weapon/&lt;种类&gt;/"。
    /// 文件名前缀("sm_")取自皮肤文件名里 "body" 之前的部分。
    /// </summary>
    public sealed class AvatarClass
    {
        private readonly DnfAssets _assets;
        private Dictionary<string, string> _weaponFiles;

        /// <summary>装备脚本里 [animation job] 的取值,例如 "[swordman]"。</summary>
        public string JobTag { get; }

        /// <summary>换装根目录(小写,不带结尾的斜杠),例如 "sprite/character/swordman/equipment/avatar"。</summary>
        public string AvatarRoot { get; }

        /// <summary>武器根目录,例如 "sprite/character/swordman/equipment/weapon"。</summary>
        public string WeaponRoot { get; }

        /// <summary>部件文件名前缀,例如 "sm_"。</summary>
        public string FilePrefix { get; }

        /// <summary>皮肤 IMG 路径模板,带一个 %04d。</summary>
        public string BodyImagePattern { get; }

        public AvatarClass(DnfAssets assets, string jobTag, string avatarRoot, string filePrefix)
        {
            _assets = assets ?? throw new ArgumentNullException(nameof(assets));
            JobTag = jobTag ?? throw new ArgumentNullException(nameof(jobTag));
            AvatarRoot = (avatarRoot ?? throw new ArgumentNullException(nameof(avatarRoot))).TrimEnd('/').ToLowerInvariant();
            FilePrefix = (filePrefix ?? string.Empty).ToLowerInvariant();
            int cut = AvatarRoot.LastIndexOf('/');
            WeaponRoot = (cut >= 0 ? AvatarRoot.Substring(0, cut) : AvatarRoot) + "/weapon";
            BodyImagePattern = AvatarRoot + "/skin/" + FilePrefix + "body%04d.img";
        }

        /// <summary>
        /// 从角色脚本读布局。<paramref name="chrPath"/> 例如 "character/swordman/swordman.chr",
        /// 取它的 [body image path](形如 "Character/swordman/Equipment/Avatar/skin/sm_body%04d.img")。
        /// </summary>
        public static AvatarClass FromChr(DnfAssets assets, string chrPath, string jobTag)
        {
            ScriptLine[] doc = assets.ReadScript(chrPath);
            if (doc == null)
            {
                throw new DnfFormatException("PVF 里没有角色脚本: " + chrPath);
            }
            string body = null;
            foreach (ScriptLine line in doc)
            {
                if (line.TagIs("[body image path]"))
                {
                    body = line.FirstString();
                    break;
                }
            }
            if (string.IsNullOrEmpty(body))
            {
                throw new DnfFormatException("角色脚本没有 [body image path]: " + chrPath);
            }
            string path = "sprite/" + body.Replace('\\', '/').ToLowerInvariant();
            int slash = path.LastIndexOf('/');
            int skinDir = slash > 0 ? path.LastIndexOf('/', slash - 1) : -1;
            int bodyAt = path.IndexOf("body", slash + 1, StringComparison.Ordinal);
            if (skinDir < 0 || bodyAt < 0)
            {
                throw new DnfFormatException("无法从 [body image path] 推出换装目录: " + body);
            }
            return new AvatarClass(assets, jobTag, path.Substring(0, skinDir), path.Substring(slash + 1, bodyAt - slash - 1));
        }

        /// <summary>皮肤变体的 IMG 路径。</summary>
        public string BodyImage(int variation)
        {
            return BodyImagePattern.Replace("%04d", variation.ToString("D4", CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// 武器 IMG 的完整路径:武器分种类放在不同目录里,而装备脚本的目录名与图片目录名并不总是一致
        /// (例如重剑脚本在 hsword/,图片在 lswd/),所以按文件名在武器根目录下查找。找不到返回 null。
        /// </summary>
        internal string ResolveWeaponImage(string fileName)
        {
            if (_weaponFiles == null)
            {
                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (string name in _assets.Images.NamesWithPrefix(WeaponRoot + "/"))
                {
                    string lower = name.ToLowerInvariant();
                    map[lower.Substring(lower.LastIndexOf('/') + 1)] = lower;
                }
                _weaponFiles = map;
            }
            return _weaponFiles.TryGetValue(fileName.ToLowerInvariant(), out string full) ? full : null;
        }
    }

    /// <summary>
    /// 一件换装或武器装备(.equ):读它在本职业 [animation job] 块里的 [variation] 与 [layer variation]。
    /// 图片编号规则(对全库 swordman 的 6,843 件装备脚本核对过,证据 E-065):
    ///   换装部件(层名形如 coat_a)的 IMG = sm_&lt;部件&gt;&lt;A×100+B,四位&gt;&lt;字母&gt;.img,A、B 是 [variation] 的两个数;
    ///   武器(层名形如 katanac)的 IMG = &lt;种类&gt;&lt;B,四位&gt;&lt;字母&gt;.img,只看第二个数。
    /// </summary>
    public sealed class AvatarItem
    {
        public string EquPath { get; }

        /// <summary>[equipment type] 的第一个字符串,例如 "[coat avatar]"、"[skin avatar]"、"[weapon]"。</summary>
        public string EquipmentType { get; }

        public bool HasVariation { get; }
        public int VariationA { get; }
        public int VariationB { get; }
        public IReadOnlyList<AvatarLayer> Layers { get; }

        /// <summary>
        /// [sub type]:武器装备的种类下标(没写是 0),用来在角色 .chr 的 [weapon hit info] 里找这件武器的命中默认值(证据 E-077)。
        /// 这个标签写在各职业的 [animation job] 块之后。
        /// </summary>
        public int SubType { get; }

        public bool IsSkin => string.Equals(EquipmentType, "[skin avatar]", StringComparison.OrdinalIgnoreCase);

        /// <summary>皮肤装备的 IMG 编号(A×100+B)。</summary>
        public int AvatarNumber => VariationA * 100 + VariationB;

        private AvatarItem(string equPath, string equipmentType, bool hasVariation, int a, int b, IReadOnlyList<AvatarLayer> layers, int subType)
        {
            EquPath = equPath;
            EquipmentType = equipmentType;
            HasVariation = hasVariation;
            VariationA = a;
            VariationB = b;
            Layers = layers;
            SubType = subType;
        }

        /// <summary>读一件装备;脚本不存在时返回 null。该职业没有 [animation job] 块时没有图层(皮肤以外的装备据此穿不上)。</summary>
        public static AvatarItem Read(DnfAssets assets, AvatarClass cls, string equPath)
        {
            ScriptLine[] doc = assets.ReadScript(equPath);
            if (doc == null)
            {
                return null;
            }
            string equipmentType = null;
            bool inJob = false;
            bool jobDone = false;
            bool hasVariation = false;
            bool hasSubType = false;
            int subType = 0;
            int a = 0, b = 0;
            var raw = new List<KeyValuePair<int, string>>();
            foreach (ScriptLine line in doc)
            {
                if (line.TagIs("[equipment type]"))
                {
                    if (equipmentType == null)
                    {
                        equipmentType = line.FirstString();
                    }
                    continue;
                }
                if (line.TagIs("[sub type]"))
                {
                    List<int> sub = line.Ints();
                    if (!hasSubType && sub.Count > 0)
                    {
                        subType = sub[0];
                        hasSubType = true;
                    }
                    continue;
                }
                if (line.TagIs("[animation job]"))
                {
                    if (inJob)
                    {
                        // 本职业的块到此为止;后面的职业块都不读(但还要读到文件尾,[sub type] 在最后面)
                        jobDone = true;
                        inJob = false;
                        continue;
                    }
                    inJob = !jobDone && string.Equals(line.FirstString(), cls.JobTag, StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inJob)
                {
                    continue;
                }
                if (line.TagIs("[variation]"))
                {
                    List<int> ints = line.Ints();
                    if (ints.Count >= 2)
                    {
                        a = ints[0];
                        b = ints[1];
                        hasVariation = true;
                    }
                }
                else if (line.TagIs("[layer variation]"))
                {
                    ScriptValue[] v = line.Values;
                    for (int i = 0; i + 1 < v.Length; i++)
                    {
                        if (v[i].IsNumber && v[i + 1].Kind == ScriptValueKind.String && !string.IsNullOrEmpty(v[i + 1].Text))
                        {
                            raw.Add(new KeyValuePair<int, string>(v[i].Int, v[i + 1].Text));
                            i++;
                        }
                    }
                }
            }
            var layers = new List<AvatarLayer>(raw.Count);
            if (hasVariation)
            {
                foreach (KeyValuePair<int, string> r in raw)
                {
                    layers.Add(new AvatarLayer(r.Value, r.Key, ImagePathFor(cls, r.Value, a, b)));
                }
            }
            return new AvatarItem(equPath, equipmentType, hasVariation, a, b, layers, subType);
        }

        private static string ImagePathFor(AvatarClass cls, string layerName, int a, int b)
        {
            string lower = layerName.ToLowerInvariant();
            int underscore = lower.IndexOf('_');
            if (underscore > 0)
            {
                string part = lower.Substring(0, underscore);
                string suffix = lower.Substring(underscore + 1);
                return cls.AvatarRoot + "/" + part + "/" + cls.FilePrefix + part
                    + (a * 100 + b).ToString("D4", CultureInfo.InvariantCulture) + suffix + ".img";
            }
            // 武器:层名 = 种类 + 字母(+ 可选的一位数字),例如 katanac、beamswdb1
            int cut = lower.Length - 1;
            if (cut > 0 && char.IsDigit(lower[cut]))
            {
                cut--;
            }
            if (cut <= 0)
            {
                return null;
            }
            string file = lower.Substring(0, cut) + b.ToString("D4", CultureInfo.InvariantCulture) + lower.Substring(cut) + ".img";
            return cls.ResolveWeaponImage(file);
        }
    }

    /// <summary>
    /// 一套穿戴:皮肤加若干件装备,所有图层按顺序键从小到大排好(同键时先放进来的先画)。
    /// 皮肤没有顺序键,用 <see cref="SkinKey"/>(我们自己的推断值):S03 截图上的像素确定它必须在武器后层之前、鞋与裤子的"前层"之前;
    /// 它与脖子后层的先后没有样本,只是按"后层应当在身体后面"假定的。键值本身运行时从各装备脚本读,不抄进仓库。
    /// </summary>
    public sealed class AvatarOutfit
    {
        public const int SkinKey = 650;

        public AvatarClass Class { get; }
        public IReadOnlyList<AvatarLayer> Layers { get; }

        /// <summary>穿上的装备(不含皮肤),按放入顺序。</summary>
        public IReadOnlyList<AvatarItem> Items { get; }

        public AvatarOutfit(AvatarClass cls, int skinNumber, IEnumerable<AvatarItem> items)
        {
            Class = cls ?? throw new ArgumentNullException(nameof(cls));
            var list = new List<AvatarLayer> { new AvatarLayer("skin", SkinKey, cls.BodyImage(skinNumber)) };
            var kept = new List<AvatarItem>();
            if (items != null)
            {
                foreach (AvatarItem item in items)
                {
                    if (item == null)
                    {
                        continue;
                    }
                    kept.Add(item);
                    foreach (AvatarLayer l in item.Layers)
                    {
                        if (l.ImagePath != null)
                        {
                            list.Add(l);
                        }
                    }
                }
            }
            // 稳定排序:按键升序,同键保持放入顺序
            var indexed = new List<KeyValuePair<int, AvatarLayer>>(list.Count);
            for (int i = 0; i < list.Count; i++)
            {
                indexed.Add(new KeyValuePair<int, AvatarLayer>(i, list[i]));
            }
            indexed.Sort((x, y) =>
            {
                int c = x.Value.Key.CompareTo(y.Value.Key);
                return c != 0 ? c : x.Key.CompareTo(y.Key);
            });
            var sorted = new List<AvatarLayer>(indexed.Count);
            foreach (KeyValuePair<int, AvatarLayer> kv in indexed)
            {
                sorted.Add(kv.Value);
            }
            Layers = sorted;
            Items = kept;
        }

        /// <summary>
        /// 武器各图层在某个动作上的动画路径:每个图层有自己的 .ani(与基础动作帧数、延时、帧偏移相同),放在装备脚本所在目录下的 &lt;层名&gt;/ 里,
        /// 例如 equipment/character/swordman/weapon/katana/katanac/attack1.ani(模板来自 equipment/character/&lt;职业&gt;.lay 的 "%s/Attack1.ani",证据 E-069)。
        /// 攻击判定框(类型 15)写在这些武器图层的动画里,基础动作的 .ani 只有受击框。<paramref name="motionFile"/> 形如 "attack1.ani"。
        /// </summary>
        public List<string> WeaponAnimationPaths(string motionFile)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(motionFile))
            {
                return result;
            }
            string file = motionFile.ToLowerInvariant();
            foreach (AvatarItem item in Items)
            {
                if (!string.Equals(item.EquipmentType, "[weapon]", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                string equ = item.EquPath.Replace('\\', '/').ToLowerInvariant();
                int cut = equ.LastIndexOf('/');
                string dir = cut >= 0 ? equ.Substring(0, cut) : string.Empty;
                foreach (AvatarLayer layer in item.Layers)
                {
                    result.Add(dir + "/" + layer.Name.ToLowerInvariant() + "/" + file);
                }
            }
            return result;
        }

        /// <summary>
        /// 用游戏数据自带的"默认外观"装备穿一套:皮肤 + 发型、上衣、裤子、鞋、帽子的 *_default,加一件武器。
        /// 路径都相对 <paramref name="equipRoot"/>(例如 "equipment/character/swordman");不存在的件被跳过。
        /// 默认外观与原版截图在像素上一致(证据 E-065);武器是演示用的选择,不是数据规定的"默认"。
        /// </summary>
        public static AvatarOutfit CreateDefault(DnfAssets assets, AvatarClass cls, string equipRoot, string weaponEquPath)
        {
            string root = equipRoot.TrimEnd('/') + "/";
            int skin = 0;
            AvatarItem skinItem = AvatarItem.Read(assets, cls, root + "avatar/skin/askin_default.equ");
            if (skinItem != null && skinItem.HasVariation)
            {
                skin = skinItem.AvatarNumber;
            }
            var items = new List<AvatarItem>();
            foreach (string rel in new[]
            {
                "avatar/hair/ahair_default.equ", "avatar/coat/acoat_default.equ", "avatar/pants/apants_default.equ",
                "avatar/shoes/ashoes_default.equ", "avatar/cap/ahat_default.equ",
            })
            {
                AvatarItem item = AvatarItem.Read(assets, cls, root + rel);
                if (item != null)
                {
                    items.Add(item);
                }
            }
            if (!string.IsNullOrEmpty(weaponEquPath))
            {
                AvatarItem weapon = AvatarItem.Read(assets, cls, weaponEquPath.StartsWith("equipment/", StringComparison.OrdinalIgnoreCase) ? weaponEquPath : root + weaponEquPath);
                if (weapon != null)
                {
                    items.Add(weapon);
                }
            }
            return new AvatarOutfit(cls, skin, items);
        }

        /// <summary>
        /// 这套穿戴在动画 <paramref name="ani"/> 的第 <paramref name="aniFrame"/> 帧的精灵:每个图层取同一个 IMG 帧号(.ani 帧的 [image] 帧号),
        /// 位置模型与 NPC 相同(<see cref="TownProjection"/>),镜像以世界 x 为轴。
        /// 某层在该帧没有像素(缺 IMG、帧号越界、帧是空的或全透明的占位帧)就不出精灵。返回的精灵按图层顺序排好,可以直接并进场景列表。
        /// </summary>
        public List<SceneSprite> SpritesAt(DnfAssets assets, AniFile ani, string animationPath, int aniFrame, TownProjection projection,
            int worldX, int worldY, int worldZ, bool facingMirror, int sortKey, int objectId)
        {
            var result = new List<SceneSprite>(Layers.Count);
            if (ani == null || (uint)aniFrame >= (uint)ani.Frames.Length)
            {
                return result;
            }
            AniFrame f = ani.Frames[aniFrame];
            bool mirrored = facingMirror ^ (f.TryGetScale(out float scaleX, out _) && scaleX < 0f);
            foreach (AvatarLayer layer in Layers)
            {
                if (!assets.TryGetFrame(layer.ImagePath, f.FrameIndex, out SpriteFrame sprite) || sprite.Width == 0 || sprite.Height == 0 || sprite.IsBlank)
                {
                    continue;
                }
                projection.TopLeft(worldX, worldY, worldZ, f.X, f.Y, sprite.OffsetX, sprite.OffsetY, sprite.Width, mirrored, out int sx, out int sy);
                result.Add(new SceneSprite(sprite, sx, sy, sortKey, objectId, animationPath, aniFrame, mirrored));
            }
            return result;
        }
    }
}
