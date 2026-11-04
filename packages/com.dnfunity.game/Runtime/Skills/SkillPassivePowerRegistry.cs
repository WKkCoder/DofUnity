using System;
using System.Collections.Generic;
using System.Linq;
using Dnf.Script;

namespace Dnf.Game
{
    /// <summary>
    /// 建槽前对已学被动跑 onChangeSkillEffect,收集 sq_AddPassiveSkillAttackBonusRate 登记的链接(E-231)。
    /// </summary>
    public static class SkillPassivePowerRegistry
    {
        private static readonly object Gate = new object();
        private static GameWorld _sharedWorld;
        private static IScriptSource _sharedSource;

        public static IReadOnlyList<SkillPassivePowerRate.Link> CollectForBook(GameData data, int job, SkillBook book)
        {
            if (data == null || book == null)
            {
                return Array.Empty<SkillPassivePowerRate.Link>();
            }
            var passives = new List<int>();
            foreach (KeyValuePair<int, int> learned in book.Learned)
            {
                if (learned.Value < 1)
                {
                    continue;
                }
                SkillDef def = book.Catalog.Find(learned.Key) ?? data.GetSkill(job, learned.Key);
                if (def != null && SkillPassivePowerRate.IsPassiveType(def))
                {
                    passives.Add(learned.Key);
                }
            }
            if (passives.Count == 0)
            {
                return Array.Empty<SkillPassivePowerRate.Link>();
            }
            IScriptSource source = data.Source;
            if (source == null)
            {
                return Array.Empty<SkillPassivePowerRate.Link>();
            }
            lock (Gate)
            {
                GameWorld world = EnsureWorld(data, source);
                if (world == null)
                {
                    return Array.Empty<SkillPassivePowerRate.Link>();
                }
                GameObject hero = world.SpawnCharacter(job, "passive_reg", 0, 0, 0, Direction.Right);
                SkillPassivePowerStore.Clear(hero);
                foreach (KeyValuePair<int, int> learned in book.Learned)
                {
                    if (learned.Value > 0)
                    {
                        hero.SkillLevels[learned.Key] = learned.Value;
                    }
                }
                foreach (int passiveId in passives)
                {
                    SkillPassivePowerStore.BeginChangeEffect(passiveId);
                    try
                    {
                        world.ChangeSkillEffect(hero, passiveId, Array.Empty<byte>());
                    }
                    finally
                    {
                        SkillPassivePowerStore.EndChangeEffect();
                    }
                }
                return SkillPassivePowerStore.Snapshot(hero).ToArray();
            }
        }

        private static GameWorld EnsureWorld(GameData data, IScriptSource source)
        {
            if (_sharedWorld != null && ReferenceEquals(_sharedSource, source))
            {
                return _sharedWorld;
            }
            _sharedWorld?.Dispose();
            _sharedWorld = null;
            _sharedSource = source;
            try
            {
                var world = new GameWorld(data, new ScriptHost(source));
                ScriptLoadSummary load = world.LoadScripts();
                if (!load.EntryOk)
                {
                    world.Dispose();
                    return null;
                }
                _sharedWorld = world;
                return world;
            }
            catch
            {
                return null;
            }
        }
    }
}
