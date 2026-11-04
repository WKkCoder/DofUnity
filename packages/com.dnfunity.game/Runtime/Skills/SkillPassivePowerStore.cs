using System.Collections.Generic;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Dnf.Game.Tests")]

namespace Dnf.Game
{
    /// <summary>运行期 +0x5938 表(E-231);按对象 Id 存,避免改 World 层类型。</summary>
    internal static class SkillPassivePowerStore
    {
        private static readonly Dictionary<long, List<SkillPassivePowerRate.Link>> ByObject = new Dictionary<long, List<SkillPassivePowerRate.Link>>();
        private static int _changeEffectSkill = -1;

        internal static int ChangeEffectSkillContext => _changeEffectSkill;

        internal static void BeginChangeEffect(int passiveSkillId) => _changeEffectSkill = passiveSkillId;

        internal static void EndChangeEffect() => _changeEffectSkill = -1;

        internal static List<SkillPassivePowerRate.Link> Links(GameObject o)
        {
            if (o == null)
            {
                return null;
            }
            if (!ByObject.TryGetValue(o.Id, out List<SkillPassivePowerRate.Link> list))
            {
                list = new List<SkillPassivePowerRate.Link>();
                ByObject[o.Id] = list;
            }
            return list;
        }

        internal static void Clear(GameObject o)
        {
            if (o != null)
            {
                ByObject.Remove(o.Id);
            }
        }

        internal static void Add(GameObject o, int passiveSkillId, int activeSkillId, int staticIndex, bool terminatesChain)
        {
            Links(o)?.Add(new SkillPassivePowerRate.Link(passiveSkillId, activeSkillId, staticIndex, terminatesChain));
        }

        internal static void RemoveForActive(GameObject o, int activeSkillId)
        {
            if (o != null && ByObject.TryGetValue(o.Id, out List<SkillPassivePowerRate.Link> list))
            {
                list.RemoveAll(link => link.ActiveSkillId == activeSkillId);
            }
        }

        internal static IReadOnlyList<SkillPassivePowerRate.Link> Snapshot(GameObject o)
        {
            return o != null && ByObject.TryGetValue(o.Id, out List<SkillPassivePowerRate.Link> list)
                ? list.ToArray()
                : System.Array.Empty<SkillPassivePowerRate.Link>();
        }
    }
}
