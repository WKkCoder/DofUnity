using System;
using System.Collections.Generic;

namespace Dnf.Net.Session
{
    /// <summary>
    /// 宠物属性表里的一只宠物(NOTI/105 的一条)。字段名按读序与服务端取值函数名给出(<c>GetStomach</c>、<c>GetExp</c>、<c>GetLevel</c>、<c>GetName</c>、<c>IsNoCharge</c>),
    /// 语义按函数名推断、未对照取值(笔记 §2.5)。
    /// </summary>
    public sealed class CreatureAttributeEntry
    {
        private readonly byte[] _name;

        internal CreatureAttributeEntry(
            uint uid,
            byte stomach,
            byte growthFlag,
            uint exp,
            byte growthByte1,
            byte growthByte2,
            byte level,
            byte[] name,
            byte noCharge)
        {
            Uid = uid;
            Stomach = stomach;
            GrowthFlag = growthFlag;
            Exp = exp;
            GrowthByte1 = growthByte1;
            GrowthByte2 = growthByte2;
            Level = level;
            _name = name ?? new byte[0];
            NoCharge = noCharge;
        }

        /// <summary>宠物 uid(u32)。[推断] 宠物类物品条目的 Data 位就是它(笔记 §2.5,无非空样本)。</summary>
        public uint Uid { get; }

        /// <summary>饱腹度(u8,服务端 <c>GetStomach</c>)。</summary>
        public byte Stomach { get; }

        /// <summary>
        /// 成长标志(u8 原值)。服务端 <c>IsGrowCreature()</c> 为真时这一字节写 0,客户端读到 0 才继续读后面两个 u8;
        /// 非成长分支写非 0 值,后面没有这两个 u8(两侧条件互相一致,笔记 §2.5)。
        /// </summary>
        public byte GrowthFlag { get; }

        /// <summary>是否带那两个额外字节(成长标志为 0)。</summary>
        public bool HasGrowthBytes => GrowthFlag == 0;

        /// <summary>经验(u32,服务端 <c>GetExp</c>)。</summary>
        public uint Exp { get; }

        /// <summary>成长分支的第一个额外字节(没有时为 0);含义未知(服务端写入点 0x08340b3a 没有取值函数名可参考)。</summary>
        public byte GrowthByte1 { get; }

        /// <summary>成长分支的第二个额外字节(没有时为 0);含义未知(服务端写入点 0x08340b50)。</summary>
        public byte GrowthByte2 { get; }

        /// <summary>等级(u8,服务端 <c>GetLevel</c>)。</summary>
        public byte Level { get; }

        /// <summary>名字的原始字节(u32 长度 + 字节,不转字符串)。</summary>
        public IReadOnlyList<byte> Name => Array.AsReadOnly(_name);

        /// <summary>"免费/免充值"标志原值(u8,服务端 <c>IsNoCharge</c>)。</summary>
        public byte NoCharge { get; }

        /// <summary><see cref="NoCharge"/> 非 0。</summary>
        public bool IsNoCharge => NoCharge != 0;
    }

    /// <summary>
    /// 宠物属性表包(类 0、包号 105,服务端 <c>MakeNotipacketCreatureItemList</c> 0x083409e0)的解析结果:宠物<b>花名册</b>(属性),
    /// 不是物品格子——宠物栏的格子在物品空间包的空间 7。样本是空表(条数 0)。不可变;构造入口只给 <see cref="CreatureAttributeParser"/>。
    /// </summary>
    public sealed class CreatureAttributeNotice
    {
        private readonly IReadOnlyList<CreatureAttributeEntry> _creatures;

        internal CreatureAttributeNotice(CreatureAttributeEntry[] creatures, int consumedByteCount, int trailingByteCount)
        {
            _creatures = Array.AsReadOnly(creatures ?? new CreatureAttributeEntry[0]);
            ConsumedByteCount = consumedByteCount;
            TrailingByteCount = trailingByteCount;
        }

        /// <summary>各只宠物,按线上顺序。</summary>
        public IReadOnlyList<CreatureAttributeEntry> Creatures => _creatures;

        /// <summary>解析消耗的字节数。</summary>
        public int ConsumedByteCount { get; }

        /// <summary>最后一只之后剩余的字节数(包尾校验与补零,只计数)。</summary>
        public int TrailingByteCount { get; }
    }
}
