using System;
using Dnf.Data;

namespace Dnf.Game.Act
{
    /// <summary>
    /// 动作脚本 [PARTICLE]（行为码 0xaa，加载 0x013419f8，执行 0x010c5f91）向宿主发出的单次粒子发射请求。
    /// <para>
    /// 读码定案（0x010c5f91 / 0x00e57241）：
    /// 1) 发射坐标 = 拥有者世界坐标 + 原始偏移 (OffsetX, OffsetY, OffsetZ)，偏移量不随拥有者朝向水平镜像；
    /// 2) 粒子自身运动位移与发射区域 X 偏移按拥有者朝向 (<see cref="FacingLeft"/>) 水平镜像；
    /// 3) 客户端在单次发射后立即析构发射器（0x010c6138），已发射粒子继续在世界列表里推进直至寿命或触地消失。
    /// </para>
    /// </summary>
    public sealed class ActParticleRequest
    {
        public ActParticleRequest(
            IActEntity owner,
            string path,
            string fromProgram,
            int offsetX,
            int offsetY,
            int offsetZ,
            float worldX,
            float worldY,
            float worldZ,
            bool facingLeft)
        {
            Owner = owner ?? throw new ArgumentNullException(nameof(owner));
            Path = path ?? throw new ArgumentNullException(nameof(path));
            FromProgram = fromProgram;
            OffsetX = offsetX;
            OffsetY = offsetY;
            OffsetZ = offsetZ;
            WorldX = worldX;
            WorldY = worldY;
            WorldZ = worldZ;
            FacingLeft = facingLeft;
        }

        public IActEntity Owner { get; }
        public string Path { get; }
        public string FromProgram { get; }
        public int OffsetX { get; }
        public int OffsetY { get; }
        public int OffsetZ { get; }
        public float WorldX { get; }
        public float WorldY { get; }
        public float WorldZ { get; }
        public bool FacingLeft { get; }

        /// <summary>将脚本中的相对 .ptl 路径按来源 .act 所在目录解析后的规范化 PVF 路径。</summary>
        public string ResolvedPath => PvfPath.ResolveRelativeTo(FromProgram, Path);
    }

    /// <summary>r19-particle：[PARTICLE] 行为的参数字段（加载规则见 ActCompiler.Particle.cs）。</summary>
    public sealed partial class ActBehavior
    {
        /// <summary>[PARTICLE] 的 .ptl 相对路径原文。</summary>
        public string ParticlePath { get; internal set; }

        /// <summary>[PARTICLE] 的三轴固定偏移（单位像素，不随朝向镜像）。</summary>
        public int ParticleOffsetX { get; internal set; }

        public int ParticleOffsetY { get; internal set; }

        public int ParticleOffsetZ { get; internal set; }
    }
}
