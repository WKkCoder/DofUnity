using System;
using Dnf.Game;
using Dnf.Render;
using Dnf.Script;

namespace Dnf.Play
{
    public sealed partial class HeroProgress
    {
        private string _heroName = CharacterRoster.LocalName;

        /// <summary>主角头顶名牌展示的名称;默认 <see cref="CharacterRoster.LocalName"/>,联网进城时写入选中角色名。</summary>
        public string HeroName
        {
            get => _heroName;
            set => _heroName = string.IsNullOrEmpty(value) ? CharacterRoster.LocalName : value;
        }

        /// <summary>是否因为服务端职业非鬼剑士而显式退回了鬼剑士外观与技能。</summary>
        public bool IsJobFallback { get; private set; }

        /// <summary>
        /// 按联网选中角色在内存中建立主角进度(绝不读写离线 <see cref="HeroSave"/>):
        /// 仅鬼剑士(<see cref="EquipRules.SwordmanJob"/>)套用真实一转与觉醒成长增量;其他职业显式退回鬼剑士基础数据并标记 <see cref="IsJobFallback"/>。
        /// </summary>
        public static HeroProgress CreateOnline(GameData data, MobRewardTable rewards, OnlineHeroProfile profile)
        {
            if (data == null)
            {
                throw new ArgumentNullException(nameof(data));
            }
            if (profile == null)
            {
                throw new ArgumentNullException(nameof(profile));
            }
            bool fallback = profile.Job != EquipRules.SwordmanJob;
            CharacterDef chr = data.GetCharacter(EquipRules.SwordmanJob);
            if (chr == null)
            {
                return null;
            }
            int growtype = fallback ? DefaultGrowtype : Math.Max(0, profile.GrowType);
            int awakening = fallback ? 0 : Math.Max(0, Math.Min(2, profile.Awakening));
            ExpTable table = data.ExpTable;
            var progress = new HeroProgress(
                table,
                data.SpTable,
                data.GetGrowth(chr),
                rewards ?? MobRewardTable.Placeholder(table),
                data.ExpRates,
                profile.Level < 1 ? 1 : profile.Level,
                exp: -1,
                growtype: growtype,
                awakening: awakening);
            progress.HeroName = profile.Name;
            progress.Job = chr.Job;
            progress.IsJobFallback = fallback;
            progress.AttachSkills(SkillCatalog.Load(data, chr.Job), chr.Tags.Lines, grantTotalPoints: true);
            return progress;
        }

        /// <summary>
        /// 宿主入口(公开签名不引用 <c>Dnf.Game</c>,保证无传递引用的客户端程序集在 Unity 下不产生 CS0012):
        /// 按联网角色档案从数据资源建立不落盘的在线 <see cref="HeroProgress"/>。
        /// </summary>
        public static HeroProgress CreateForOnlineHero(DnfAssets assets, OnlineHeroProfile profile)
        {
            if (assets == null)
            {
                throw new ArgumentNullException(nameof(assets));
            }
            if (profile == null)
            {
                throw new ArgumentNullException(nameof(profile));
            }
            var data = new GameData(new PvfScriptSource(assets.Pvf));
            return CreateOnline(data, MobRewards.Load(assets, data.ExpTable), profile);
        }
    }
}
