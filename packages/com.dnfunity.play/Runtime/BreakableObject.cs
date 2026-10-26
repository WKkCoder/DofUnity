using System;
using System.Collections.Generic;
using Dnf.Data;
using Dnf.Game;
using Dnf.Render;

// 证据:E-126(哪些物件能被打、受击框、打几下、打够后的反应;槽位含义是推断,依据与不确定处写在 BreakableData 与账本里)。
namespace Dnf.Play
{
    /// <summary>一次命中对物件造成的结果。</summary>
    public enum BreakableOutcome
    {
        /// <summary>没打够:播受击动画(有的话)。</summary>
        Reacted = 0,

        /// <summary>这一下打够了、物件消失(木桶、箱子、罐子)。</summary>
        Destroyed = 1,

        /// <summary>这一下打够了、物件留在原地只触发它挂的动作(树、石柱这类动作物件);之后再打只做受击反应。</summary>
        Triggered = 2,
    }

    /// <summary>
    /// 房间里一个能被打的物件的战斗状态(证据 E-126)。能被打 = 摆在可破坏物件目录里(<see cref="PassiveObjectDef.Breakable"/>)、
    /// 并且基础动画的某一帧带受击框(类型 14);受击框取"第一个带受击框的帧"的那一组(物件站着不动,不随动画换框,是简化)。
    /// 命中判定与怪物相同:主角的攻击框与受击框相交(同一击对同一个物件只算一次)。
    /// 每一击算一"下"(不看伤害数值:服务端对物件掉落的资料里没有物件生命值,打几下取自 <see cref="BreakableData.HitsNeeded"/>,是推断)。
    /// </summary>
    public sealed class BreakableObject
    {
        private readonly List<Aabb> _hurtBoxes;

        public RoomObject Object { get; }

        public BreakableData Data { get; }

        /// <summary>已经被打中的次数。</summary>
        public int HitsTaken { get; private set; }

        /// <summary>已经打够过(动作已经触发)。</summary>
        public bool Triggered { get; private set; }

        public bool Destroyed => Object.Destroyed;

        /// <summary>受击框(世界坐标;物件不动,算一次就够)。</summary>
        public IReadOnlyList<Aabb> HurtBoxes => _hurtBoxes;

        private BreakableObject(RoomObject obj, List<Aabb> boxes)
        {
            Object = obj;
            Data = obj.Def.Breakable;
            _hurtBoxes = boxes;
        }

        /// <summary>这个物件能被打就返回它的战斗状态;不是可破坏物件、或基础动画里没有受击框返回 null。</summary>
        public static BreakableObject TryCreate(RoomObject obj)
        {
            if (obj == null || obj.Def.Breakable == null || obj.Ani == null)
            {
                return null;
            }
            var boxes = new List<Aabb>();
            foreach (AniFrame frame in obj.Ani.Frames)
            {
                foreach (AniBox box in frame.Boxes)
                {
                    if (box.Type == AniBoxType.Damage)
                    {
                        boxes.Add(Combat.ToWorld(box, obj.X, obj.Y, obj.Z, Direction.Right));
                    }
                }
                if (boxes.Count > 0)
                {
                    break;
                }
            }
            return boxes.Count == 0 ? null : new BreakableObject(obj, boxes);
        }

        /// <summary>
        /// 吃一击:计数加一;没打够就换播受击动画(第 n 下用 [etc motion] 的第 n 项,项数不够用最后一项,没有就不播);
        /// 打够的那一下,物件要消失(<see cref="BreakableData.DestroyedWhenDone"/>)就消失,否则触发一次、再换播受击动画。
        /// </summary>
        public BreakableOutcome Hit(DnfAssets assets)
        {
            HitsTaken++;
            if (HitsTaken < Data.HitsNeeded)
            {
                PlayReaction(assets);
                return BreakableOutcome.Reacted;
            }
            if (Triggered)
            {
                PlayReaction(assets);
                return BreakableOutcome.Reacted;
            }
            Triggered = true;
            if (Data.DestroyedWhenDone)
            {
                Object.Destroy();
                return BreakableOutcome.Destroyed;
            }
            PlayReaction(assets);
            return BreakableOutcome.Triggered;
        }

        private void PlayReaction(DnfAssets assets)
        {
            int count = Data.ReactionMotions.Count;
            if (count == 0)
            {
                return;
            }
            Object.PlayReaction(assets, Math.Min(HitsTaken, count) - 1);
        }
    }

    /// <summary>
    /// 一座副本里物件掉物品要用的东西(证据 E-126):<c>.dgn</c> 的掉落项与通用池、副本的标准等级与难度、物件掉落表与物品池(<see cref="MonsterDrops"/> 里)。
    /// 创建一次、整座副本的各个房间共用。
    /// </summary>
    public sealed class ObjectDropSetup
    {
        public ObjectDropSetup(DungeonObjectItems items, int standardLevel, int difficulty, MonsterDrops drops)
        {
            Items = items ?? DungeonObjectItems.Empty;
            StandardLevel = standardLevel;
            Difficulty = difficulty;
            Drops = drops;
        }

        public DungeonObjectItems Items { get; }

        /// <summary>副本的标准等级(<c>[basis level]</c>):掉落项没写等级时用它。</summary>
        public int StandardLevel { get; }

        public int Difficulty { get; }

        /// <summary>物件掉落表、物品池、按编号取物品;为 null 时只剩没有表也能出的东西(什么都不出)。</summary>
        public MonsterDrops Drops { get; }

        /// <summary>掷一个 <c>[item]</c> 动作(见 <see cref="ObjectItemRoll.Roll"/>)。</summary>
        public List<ItemInfo> Roll(SpecialAction action, IDropRandom random)
        {
            return ObjectItemRoll.Roll(Items, action.P1, action.P2, action.P3, StandardLevel, Difficulty, Drops?.ObjectTable, Drops?.ObjectPool,
                Drops == null ? (Func<int, ItemInfo>)null : Drops.ItemById, random);
        }
    }
}
