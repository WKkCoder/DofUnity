using System;
using Dnf.Game.Act;
using Dnf.Render;

// SUB ANI 线:.act 的 [MOTION] 里叠加层与声音的事件流,在房间会话这一侧的落点。
namespace Dnf.Play
{
    /// <summary>
    /// 房间会话接 .act 事件流(<see cref="IActHost.MotionEvent"/>):
    /// 声音事件进已有的声音出口(<see cref="SoundCueQueue"/>,播放的一方每帧 <see cref="DrainSounds"/> 取走,副本会话再原样转发);
    /// 叠加层事件这一版没有画面出口,只登记个数(<see cref="ActLayerStarts"/>、<see cref="ActLayerEnds"/>),并把每条事件交给旁路(<see cref="ActMotionObserver"/>),
    /// 画面一侧以后从那里挂接;事件本身的挂接说明见 <see cref="IActHost.MotionEvent"/>。
    /// <para>
    /// 观察(汇总数,本机脚本扫描,非 REA):[SOUND] 里的字符串是 audio.xml 的 ID,不是文件路径(没有一个带路径分隔符或扩展名,绝大多数命中 audio.xml 的效果类条目),
    /// 所以直接当声音标签进队列,放声音的一方照旧按 audio.xml 查;不在这里解析路径。
    /// 推断:[SOUND PROBABILITY] 的第二个整数是百分数(取值全在 0 到 100);没有对拍的数据。
    /// 未知:客户端对声音的"主人"怎么算(语音互斥用)没有读;这里按怪物的序号加 1(被动对象取它最早的那只怪物,都没有取 0),同主人的普通音效不互斥。
    /// </para>
    /// <para>
    /// 概率声音的随机数是这一段自己的一条线(<see cref="_actSoundSeed"/>),不碰脚本随机数、出招语音与物理等任何一条:
    /// 加这一段之前的房间推演(怪物行为、伤害、清图)因此逐位不变。
    /// </para>
    /// </summary>
    public sealed partial class RoomCombatSession
    {
        private uint _actSoundSeed = 2166136261u;

        /// <summary>收到的叠加层"开始"事件个数。</summary>
        public int ActLayerStarts { get; private set; }

        /// <summary>收到的叠加层"结束"事件个数(出招阶段离开、换招、自定义动作切走时由运行器收口)。</summary>
        public int ActLayerEnds { get; private set; }

        /// <summary>收到的声音事件个数(含概率没掷中的)。</summary>
        public int ActSoundEvents { get; private set; }

        /// <summary>真正进了声音队列的个数。</summary>
        public int ActSoundsQueued { get; private set; }

        /// <summary>概率没掷中、没进队列的个数。</summary>
        public int ActSoundsSkipped { get; private set; }

        /// <summary>
        /// 叠加层与声音事件的旁路(画面一侧挂接、测试观察用):每条事件在计数与声音出口处理之前原样交一份(概率声音不论掷没掷中都会交,掷骰的结果不在事件里);
        /// 不挂什么都不发生,挂了也不影响声音出口、计数与房间推演。<c>owner</c> 是脚本里的拥有者:被动对象就是 <see cref="PassiveObject"/> 本身,
        /// 怪物用 <see cref="MonsterActor.From"/> 认。回调里不要再推动这个会话。
        /// </summary>
        public Action<IActEntity, ActMotionEvent> ActMotionObserver { get; set; }

        private void ReceiveActMotionEvent(IActEntity owner, ActMotionEvent e)
        {
            ActMotionObserver?.Invoke(owner, e);
            switch (e.Code)
            {
                case ActMotionEventCode.LayerStart:
                    ActLayerStarts++;
                    break;
                case ActMotionEventCode.LayerEnd:
                    ActLayerEnds++;
                    break;
                case ActMotionEventCode.Sound:
                {
                    ActSoundEvents++;
                    ActSoundEvent sound = e.Sound;
                    if (sound == null)
                    {
                        break;
                    }
                    if (sound.Percent < 100 && !RollActSound(sound.Percent))
                    {
                        ActSoundsSkipped++;
                        break;
                    }
                    MonsterActor monster = MonsterActor.From(owner);
                    _sounds.Add(sound.Key, SoundCueKind.Effect, monster == null ? 0 : monster.Index + 1);
                    ActSoundsQueued++;
                    break;
                }
            }
        }

        /// <summary>百分数掷一次:0 以下永不响;不小于 100 的调用方不会走到这里。</summary>
        private bool RollActSound(int percent)
        {
            return percent > 0 && ClientRandom.Next(ref _actSoundSeed) % 100 < percent;
        }

        private sealed partial class ActWorld
        {
            public void MotionEvent(IActEntity owner, ActMotionEvent e)
            {
                _session.ReceiveActMotionEvent(owner, e);
            }
        }
    }
}
