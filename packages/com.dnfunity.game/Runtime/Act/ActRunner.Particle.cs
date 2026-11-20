namespace Dnf.Game.Act
{
    /// <summary>运行器对世界的请求：粒子发射（r19-particle）。</summary>
    public partial interface IActHost
    {
        /// <summary>
        /// [PARTICLE]（行为码 0xaa，执行块 0x010c5f91）：在拥有者位置加未镜像偏移处触发一次粒子发射。
        /// 粒子的运动与区域 X 偏移按 <see cref="ActParticleRequest.FacingLeft"/> 水平镜像；默认什么都不做。
        /// </summary>
        void SpawnParticle(ActParticleRequest request)
        {
        }
    }

    public sealed partial class ActRunner
    {
        /// <summary>通过 [PARTICLE] 行为向宿主发出的粒子请求总数（测试与审计用）。</summary>
        public int ParticlesRequested { get; private set; }

        /// <summary>
        /// [PARTICLE]（0x010c5f91）：始终作用于动作拥有者（不看目标集合），三轴偏移 (dx, dy, dz) 原样加到拥有者坐标上（不镜像），
        /// 发射朝向取拥有者的 <see cref="IActEntity.FacingLeft"/>（0x00e57241）。
        /// </summary>
        private void ExecuteParticleBehavior(ActBehavior b)
        {
            if (string.IsNullOrEmpty(b.ParticlePath))
            {
                return;
            }
            var request = new ActParticleRequest(
                _owner,
                b.ParticlePath,
                _program.Path,
                b.ParticleOffsetX,
                b.ParticleOffsetY,
                b.ParticleOffsetZ,
                _owner.X + b.ParticleOffsetX,
                _owner.Y + b.ParticleOffsetY,
                _owner.Z + b.ParticleOffsetZ,
                _owner.FacingLeft);
            ParticlesRequested++;
            _host.SpawnParticle(request);
        }
    }
}
