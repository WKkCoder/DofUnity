namespace Dnf.Play
{
    /// <summary>
    /// 界面与场景用的声音标签(audio.xml 的 ID,证据 E-097)与场景标记。
    /// 标记(<see cref="CharacterSelectScene"/>)放在 <see cref="SoundCueKind.RoomEnter"/> 的标签里,调度器据此把上一个场景的环境声停掉、只留两边都有的;
    /// 它不是声音标签。
    /// </summary>
    public static class SceneMusic
    {
        /// <summary>
        /// 选角界面的背景音乐(audio.xml 里是 Music 条目,带 LOOP_DELAY)。观察:客户端选角界面的每帧刷新函数在第一次刷新时按这个标签放一次声音
        /// (用一个"已放过"的标志挡住重复);另一个"按场景种类选曲"的函数里选角那一种也是它(静态读码)。
        /// </summary>
        public const string CharacterSelect = "M_CHARACTER_SELECT";

        /// <summary>
        /// 结算界面出现时放的声音。观察:客户端里 8 处调用点按这个标签放声音(都是同一个"按标签放声音"的函数,放完紧跟着把一个界面窗口显示出来),
        /// 其中一处同时用到 RANK_UP 与 VICTORY_UP_LOOP,另一处用到 UI/Result 的素材与 result_admission%d.act,按名字是"结果"窗口(推断:结算界面)。
        /// 注意它<b>不是背景音乐</b>:audio.xml 里 M_RESULT 写了两次(一个 Effect、一个 Music),客户端装载时先出现的赢,所以放的是 Effect 那条(短音效);
        /// 因此这里发成界面音(<see cref="SoundCueKind.Ui"/>),不换背景音乐、也不停房间的环境声。结算时原版有没有换掉背景音乐没有录音可对照,不做。
        /// </summary>
        public const string ResultSound = "M_RESULT";

        /// <summary>选角界面的场景标记(<see cref="SoundCueKind.RoomEnter"/> 的标签)。</summary>
        public const string CharacterSelectScene = "scene:character-select";
    }
}
