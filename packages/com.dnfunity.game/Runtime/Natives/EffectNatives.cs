using Dnf.Squirrel;

namespace Dnf.Game
{
    /// <summary>表现层与世界查询类的原生接口:声音、粒子、震屏、判断对象能否被抓取、强制发送命中等。</summary>
    public sealed partial class GameWorld
    {
        private void RegisterEffectNatives()
        {
            const NativeForm B = NativeForm.Both;
            const NativeStatus I = NativeStatus.Inferred;
            const NativeStatus T = NativeStatus.Trace;

            Define("sq_PlaySound", I, "播放声音 (声音键[, 其他]);只发出事件,不播放", B, c =>
            {
                Events.PlaySound(c.Self as GameObject, c.Str(0));
                return c.ReturnNothing();
            });
            Define("stopSound", I, "停止声音:占位(事件里没有停止)", B, c => c.ReturnNothing());
            DefineFree("sq_CreateParticle", I, "创建粒子 (粒子文件, 对象, x, y, z, ……):只发出事件", c =>
            {
                Events.CreateParticle(c.Arg<GameObject>(1), c.Str(0), c.Int32(2), c.Int32(3), c.Int32(4));
                return c.ReturnNothing();
            });
            DefineFree("sq_RemoveParticle", I, "移除粒子 (粒子文件, 对象):只发出事件", c =>
            {
                Events.RemoveParticle(c.Arg<GameObject>(1), c.Str(0));
                return c.ReturnNothing();
            });
            foreach (string n in new[] { "sq_SetMyShake", "sq_setShake" })
            {
                Define(n, I, "震屏 (强度, 持续毫秒):只发出事件", B, c =>
                {
                    Events.Shake(c.Self as GameObject, c.Int32(0), c.Int32(1));
                    return c.ReturnNothing();
                });
            }
            Define("sq_flashScreen", T, "闪屏:占位", B, c => c.ReturnNothing());
            Define("sq_SetBodyEffect", T, "身体特效:占位", B, c => c.ReturnNothing());

            // ---- 世界查询(没有地图:所有位置都可走)
            Define("isMovablePos", I, "位置 (x, y) 是否可走:没有地图,总是真", NativeForm.Member, c => c.Return(true));
            Define("sq_findNearLinearMovableXPos", I, "沿直线找最近的可走 x:没有地图,返回目标 x (x0, y0, x1, y1, 步长)", NativeForm.Member, c => c.Return(c.Int32(2)));
            Define("sq_SimpleMoveToNearMovablePos", T, "把对象移到附近可走的位置:没有地图,不动", B, c => c.ReturnNothing());
            Define("sq_IsHoldable", I, "目标能否被抓住 (自己, 目标):这里只有怪物能", B, c =>
            {
                return c.Return(c.Arg<GameObject>(0) is GameObject t && t.Valid && t.Kind == ObjectKind.Monster);
            });
            Define("sq_IsGrabable", I, "目标能否被抓取 (自己, 目标):这里只有怪物能", B, c =>
            {
                return c.Return(c.Arg<GameObject>(0) is GameObject t && t.Valid && t.Kind == ObjectKind.Monster);
            });
            DefineFree("sq_IsFixture", I, "目标是不是固定物 (目标):这里总是否", c => c.Return(false));
            DefineFree("sq_HoldAndDelayDie", T,
                "让目标被抓住并延迟死亡 (目标, 抓取者, ……):占位,没有改变目标的状态", c => c.ReturnNothing());
            Define("sq_SendHitObjectPacket", I,
                "强制让攻击者对目标造成一次命中 (攻击者, 目标, x, y, z):记录命中并调用 onAttack(单机里没有网络延迟)", B, c =>
                {
                    GameObject attacker = c.SelfAs<GameObject>();
                    GameObject target = c.Arg<GameObject>(0);
                    if (target != null && target.Valid)
                    {
                        RecordHit(attacker, target, true);
                        InvokeCallback("onAttack", attacker, attacker, target, null, false);
                        DrainPackets(attacker);
                    }
                    return c.ReturnNothing();
                });
        }
    }
}
