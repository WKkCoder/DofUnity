// 证据:最小世界对外报告的表现层事件(声音、粒子等)与脚本接口的对应见 docs/native-audit.tsv(E-046、E-048)。
namespace Dnf.Game
{
    /// <summary>
    /// 世界对外报告的"表现层事件"(声音、粒子、屏幕震动……)。逻辑核心只产生事件,不播放;渲染层(Unity)实现它。
    /// 方法都有空实现可用,见 <see cref="NullWorldEvents"/>。
    /// </summary>
    public interface IWorldEvents
    {
        void PlaySound(GameObject source, string soundKey);
        void CreateParticle(GameObject source, string path, int x, int y, int z);
        void RemoveParticle(GameObject source, string path);
        void Shake(GameObject source, int strength, int durationMs);
    }

    public sealed class NullWorldEvents : IWorldEvents
    {
        public static readonly NullWorldEvents Instance = new NullWorldEvents();

        public void PlaySound(GameObject source, string soundKey)
        {
        }

        public void CreateParticle(GameObject source, string path, int x, int y, int z)
        {
        }

        public void RemoveParticle(GameObject source, string path)
        {
        }

        public void Shake(GameObject source, int strength, int durationMs)
        {
        }
    }
}
