namespace Dnf.Game.Act
{
    /// <summary>
    /// r19-particle：[PARTICLE]（行为码 0xaa，加载块 0x013419f8）编译分支。
    /// 仅作加法扩展，不改动既有怪物/动作标签。
    /// </summary>
    internal static partial class ActCompiler
    {
        /// <summary>
        /// [PARTICLE] "路径" [dx dy dz]：先读 1 个非空字符串（.ptl 相对路径），再读 0 到 3 个整数字面偏移（缺省补 0；全库 1,493 处为 "s i i i"、2 处为 "s"）。
        /// 缺路径、空路径、多余整数或尾随字符串一律记为 <see cref="ActBehaviorKind.Unsupported"/>，不静默猜。
        /// </summary>
        private static ActBehavior ParseParticleBehavior(ActCursor cur, string tag)
        {
            string path = cur.TakeString();
            if (string.IsNullOrEmpty(path))
            {
                return new ActBehavior { Kind = ActBehaviorKind.Unsupported, Tag = tag };
            }
            int[] offsets = cur.Ints();
            if (offsets.Length > 3 || cur.AtString)
            {
                return new ActBehavior { Kind = ActBehaviorKind.Unsupported, Tag = tag };
            }
            return new ActBehavior
            {
                Kind = ActBehaviorKind.Particle,
                Tag = tag,
                ParticlePath = path,
                ParticleOffsetX = offsets.Length > 0 ? offsets[0] : 0,
                ParticleOffsetY = offsets.Length > 1 ? offsets[1] : 0,
                ParticleOffsetZ = offsets.Length > 2 ? offsets[2] : 0,
            };
        }
    }
}
