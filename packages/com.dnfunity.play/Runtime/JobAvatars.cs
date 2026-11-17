using System;
using System.Collections.Generic;
using Dnf.Data;
using Dnf.Game;
using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>
    /// 每个职业的基础形象(默认外观):皮肤加各部件的 <c>*_default.equ</c>,路径与读法与鬼剑士的 <see cref="AvatarOutfit.CreateDefault"/> 同一套,
    /// 只是职业目录与部件目录不再写死:
    /// <list type="bullet">
    /// <item>职业目录:<c>equipment/character/&lt;角色脚本所在目录名&gt;</c>(<see cref="DnfCharacterTitles.ChrPathOf"/> 的第二段,例如 <c>[at gunner]</c> → <c>gunner</c>)。</item>
    /// <item>部件子目录:<c>avatar</c> 与 <c>at_avatar</c> 各试一遍,取"本职业的 <c>[animation job]</c> 块真能读出图层"的那个
    /// (<c>[at gunner]</c>、<c>[at fighter]</c>、<c>[at mage]</c> 的默认外观在 <c>at_avatar</c> 里,别的职业在 <c>avatar</c> 里;一个职业两边都读不出图层就没有形象)。</item>
    /// <item>部件:头发、上衣、裤子、鞋、帽子(<c>ahat</c>)、头盔(<c>acap</c>)、胸饰(<c>aneck</c>)、腰饰(<c>abelt</c>)、脸(<c>aface</c>)的 <c>_default</c>,存在并有图层的才穿;
    /// 皮肤取 <c>askin_default</c> 的编号(与 <see cref="AvatarOutfit.CreateDefault"/> 一致)。</item>
    /// <item>鬼剑士(职业 0)直接用城镇里主角的那一套(<see cref="DemoHero.CreateOutfit"/>,带新手武器),与离线选角界面逐像素一致。</item>
    /// </list>
    /// 在用户自备数据上 11 个职业都读得出图层(本线实测);默认外观不随成长类型变(成长类型不同的是技能与称号,外观靠装备,见 <see cref="DnfJobAvatars"/>)。
    /// </summary>
    public static class JobOutfits
    {
        private static readonly string[] Sub = { "avatar", "at_avatar" };

        private static readonly string[] Parts =
        {
            "hair/ahair_default.equ", "coat/acoat_default.equ", "pants/apants_default.equ", "shoes/ashoes_default.equ",
            "cap/ahat_default.equ", "cap/acap_default.equ", "neck/aneck_default.equ", "belt/abelt_default.equ", "face/aface_default.equ",
        };

        /// <summary>职业的装备根目录(<c>equipment/character/&lt;目录&gt;</c>);职业号越界返回 null。</summary>
        public static string EquipRootOf(int job)
        {
            string chr = DnfCharacterTitles.ChrPathOf(job);
            if (chr == null)
            {
                return null;
            }
            string[] parts = chr.Split('/');
            return "equipment/character/" + parts[1];
        }

        /// <summary>职业的换装布局(从角色脚本推出);职业号越界或脚本读不出时抛 <see cref="DnfFormatException"/>。</summary>
        public static AvatarClass ClassOf(DnfAssets assets, int job)
        {
            string chr = DnfCharacterTitles.ChrPathOf(job);
            if (chr == null)
            {
                throw new DnfFormatException("没有这个职业号: " + job);
            }
            return AvatarClass.FromChr(assets, chr, EquipRules.JobTag(job));
        }

        /// <summary>
        /// 造这个职业的基础形象。读不出任何图层(两个子目录都没有本职业的默认外观)抛 <see cref="DnfFormatException"/>,
        /// 供 <see cref="DnfCharacterAvatars"/> 记成"没有形象"。
        /// </summary>
        public static AvatarOutfit CreateBase(DnfAssets assets, int job)
        {
            if (assets == null)
            {
                throw new ArgumentNullException(nameof(assets));
            }
            if (job == EquipRules.SwordmanJob)
            {
                return DemoHero.CreateOutfit(assets);
            }
            AvatarClass cls = ClassOf(assets, job);
            string root = EquipRootOf(job);
            AvatarOutfit best = null;
            int bestLayers = 0;
            foreach (string sub in Sub)
            {
                AvatarOutfit outfit = TryCreate(assets, cls, root + "/" + sub + "/");
                int layers = outfit == null ? 0 : outfit.Layers.Count;
                if (layers > bestLayers)
                {
                    best = outfit;
                    bestLayers = layers;
                }
            }
            if (best == null)
            {
                throw new DnfFormatException("职业 " + job + " 没有可用的默认外观: " + root);
            }
            return best;
        }

        // 一个子目录里的默认外观:至少有一个部件读出图层才算
        private static AvatarOutfit TryCreate(DnfAssets assets, AvatarClass cls, string dir)
        {
            int skin = 0;
            AvatarItem skinItem = AvatarItem.Read(assets, cls, dir + "skin/askin_default.equ");
            if (skinItem != null && skinItem.HasVariation)
            {
                skin = skinItem.AvatarNumber;
            }
            var items = new List<AvatarItem>();
            foreach (string part in Parts)
            {
                AvatarItem item = AvatarItem.Read(assets, cls, dir + part);
                if (item != null && item.Layers.Count > 0)
                {
                    items.Add(item);
                }
            }
            return items.Count == 0 ? null : new AvatarOutfit(cls, skin, items);
        }
    }

    /// <summary>
    /// 联网选角界面的头像来源:按角色的职业号(<see cref="CharacterSlotOnline.Job"/>)给出该职业的基础形象(<see cref="JobOutfits"/>)加该职业角色脚本的站立动作,
    /// 每个职业一个 <see cref="DnfCharacterAvatars"/>、第一次要时才读。没有联网信息的角色(离线名册)按鬼剑士,与 <see cref="DnfCharacterAvatars"/> 一致;
    /// 职业号越界或这个职业读不出形象时不画(只有底座与文字,宁可空着也不拿别的职业的形象冒充),原因在 <see cref="Failure"/>。
    /// </summary>
    public sealed class DnfJobAvatars : ICharacterAvatars
    {
        private readonly Func<int, string, DnfCharacterAvatars> _factory;
        private readonly Dictionary<int, DnfCharacterAvatars> _byJob = new Dictionary<int, DnfCharacterAvatars>();
        private readonly HashSet<int> _reported = new HashSet<int>();

        /// <param name="factory">
        /// 给一个职业(职业号与它的角色脚本路径)造 <see cref="DnfCharacterAvatars"/> 的函数;为 null 时用 <see cref="JobOutfits.CreateBase"/> 造基础形象
        /// (合成数据的测试换成自己的)。每个职业只调用一次。
        /// </param>
        public DnfJobAvatars(DnfAssets assets, Func<int, string, DnfCharacterAvatars> factory = null)
        {
            if (assets == null)
            {
                throw new ArgumentNullException(nameof(assets));
            }
            _factory = factory ?? ((job, chr) => new DnfCharacterAvatars(assets, DnfCharacterAvatars.DefaultPoseTag, chr, a => JobOutfits.CreateBase(a, job)));
        }

        /// <summary>最近一次发现读不出形象的原因(职业号与原因,不含任何角色信息;同一个职业只报一次,不会每帧重新拼);没有失败是 null。</summary>
        public string Failure { get; private set; }

        public IReadOnlyList<SceneSprite> Pose(CharacterSlot slot, long ageMs, int footX, int footY)
        {
            int job = slot != null && slot.Online != null ? slot.Online.Job : EquipRules.SwordmanJob;
            DnfCharacterAvatars avatars = ForJob(job);
            if (avatars == null)
            {
                return Array.Empty<SceneSprite>();
            }
            IReadOnlyList<SceneSprite> sprites = avatars.Pose(slot, ageMs, footX, footY);
            if (sprites.Count == 0 && avatars.Failure != null && _reported.Add(job))
            {
                Failure = "job " + job + ": " + avatars.Failure;
            }
            return sprites;
        }

        private DnfCharacterAvatars ForJob(int job)
        {
            if (!_byJob.TryGetValue(job, out DnfCharacterAvatars avatars))
            {
                string chr = DnfCharacterTitles.ChrPathOf(job);
                if (chr == null)
                {
                    Failure = "job " + job + ": 没有这个职业号";
                }
                avatars = chr == null ? null : _factory(job, chr);
                _byJob[job] = avatars;
            }
            return avatars;
        }
    }
}
