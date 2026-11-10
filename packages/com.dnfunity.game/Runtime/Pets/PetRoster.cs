using System;
using System.Collections.Generic;

// 服务端:宠物是独立于普通装备的物品空间(背包物品的类别字节 5 = 宠物,6 = 神器;SwapCreatureItem 0x083399fa 按它分派),
// 宠物实例各自带着等级、经验、饱食;同一时刻只有一只装备在宠物格上(CCreatureMgr::Equip 0x083392f6)。
namespace Dnf.Game
{
    /// <summary>玩家名下的全部宠物实例(不含"装备到哪"——那是 <see cref="PetEquipment"/> 的事)。实例编号 <see cref="PetInstance.Uid"/> 在名册里不重复。</summary>
    public sealed class PetRoster
    {
        private readonly List<PetInstance> _pets = new List<PetInstance>();

        /// <summary>全部宠物(按加入的先后)。</summary>
        public IReadOnlyList<PetInstance> Pets => _pets;

        public int Count => _pets.Count;

        /// <summary>按实例编号找宠物;没有返回 null。</summary>
        public PetInstance Find(int uid)
        {
            foreach (PetInstance pet in _pets)
            {
                if (pet.Uid == uid)
                {
                    return pet;
                }
            }
            return null;
        }

        /// <summary>加入一只宠物;为空或实例编号已有时返回 false、什么都不动。</summary>
        public bool Add(PetInstance pet)
        {
            if (pet == null || Find(pet.Uid) != null)
            {
                return false;
            }
            _pets.Add(pet);
            return true;
        }

        /// <summary>移除一只宠物;没有返回 false。(装备着的宠物要先卸下,见 <see cref="PetEquipActions.UnequipPet"/>。)</summary>
        public bool Remove(int uid)
        {
            for (int i = 0; i < _pets.Count; i++)
            {
                if (_pets[i].Uid == uid)
                {
                    _pets.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        public void Clear()
        {
            _pets.Clear();
        }

        /// <summary>
        /// 下一个没被占用的实例编号:现有最大编号加一(至少 1);最大编号已经是 <see cref="int.MaxValue"/> 时从 1 起找第一个空号。
        /// 真找不到(要名册里有 21 亿只,实际不会发生)返回 0;0 不是合法的实例编号(存档里实例号从 1 起),调用方按失败处理。
        /// </summary>
        public int NextUid()
        {
            int max = 0;
            foreach (PetInstance pet in _pets)
            {
                max = Math.Max(max, pet.Uid);
            }
            if (max < int.MaxValue)
            {
                return max + 1;
            }
            for (int uid = 1; uid < int.MaxValue; uid++)
            {
                if (Find(uid) == null)
                {
                    return uid;
                }
            }
            return 0;
        }
    }
}
