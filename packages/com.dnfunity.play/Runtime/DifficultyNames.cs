namespace Dnf.Play
{
    /// <summary>
    /// 副本难度(0 到 4)的占位名(M33,证据 E-125):英文大写,<see cref="UiText"/> 在宿主装了字体时换成中文。
    /// 名字是本项目自己写的界面用语,不是原版字符串表里的文字;难度的编号次序来自服务端的难度表([easy] [medium] [hard] [ultimate] [hero] 五节)。
    /// </summary>
    public static class DifficultyNames
    {
        private static readonly string[] Names = { "NORMAL", "EXPERT", "MASTER", "KING", "HERO" };

        /// <summary>难度 <paramref name="difficulty"/> 的名字;越界返回 "?"。</summary>
        public static string Of(int difficulty)
        {
            return (uint)difficulty < (uint)Names.Length ? Names[difficulty] : "?";
        }
    }
}
