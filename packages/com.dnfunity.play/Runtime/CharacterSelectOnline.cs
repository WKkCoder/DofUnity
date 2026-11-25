using Dnf.Render;

namespace Dnf.Play
{
    /// <summary>
    /// 联网选角(真名册)的设置,挂在 <see cref="OnlineShellOptions.CharacterSelect"/> 上:有它就用真名册与联网模式的选角界面,没有就是外壳原来的占位名册。
    /// </summary>
    public sealed class OnlineCharacterSelectOptions
    {
        /// <summary>职业称号的来源(<see cref="DnfCharacterTitles"/> 读游戏数据);为 null 时职业称号都是英文占位、默认名是 <see cref="PlaceholderTitles.DefaultNameText"/>。</summary>
        public ICharacterTitles Titles { get; set; }
    }

    /// <summary>联网选角界面上正在显示的提示(<see cref="CharacterSelectScreen.Prompt"/>)。</summary>
    public enum CharacterSelectPrompt
    {
        None = 0,

        /// <summary>选中的角色不能进入(X1 为 1 或状态字节为 8,<see cref="CharacterBlock"/>):客户端只提示、不发选角命令。</summary>
        CannotEnter = 1,

        /// <summary>选中的角色正在删除中(<see cref="CharacterBlock.PendingDelete"/>)。</summary>
        PendingDelete = 2,

        /// <summary>点了灰着的"创立角色"或"删除角色":联网版没有这两个功能。</summary>
        NotAvailable = 3,
    }

    /// <summary>鼠标点在滚动条的哪一部分(<see cref="CharacterSelectArt.HitScroll"/>)。</summary>
    public enum CharacterSelectScrollHit
    {
        None = 0,

        /// <summary>上箭头:往上滚一行。</summary>
        Up = 1,

        /// <summary>下箭头:往下滚一行。</summary>
        Down = 2,

        /// <summary>滑块上方的轨道:往上翻一屏。</summary>
        PageUp = 3,

        /// <summary>滑块下方的轨道:往下翻一屏。</summary>
        PageDown = 4,

        /// <summary>滑块本身(没有拖动,点了没有反应)。</summary>
        Thumb = 5,
    }

    /// <summary>
    /// 联网选角界面上的文字。有字体时是中文,没有字体时是点阵字库画得出的英文占位(大写字母、数字与少数符号)。
    /// 文字是本项目自己写的(原版的字符串表没有读),只有四个按钮的名字与基线 S02 上看到的一致。
    /// </summary>
    public static class CharacterSelectText
    {
        /// <summary>角色名下面第一行字:<c>Lv3 名字</c>(英文占位是 <c>LV3 NAME</c>,与离线界面一致)。名字为空时只有等级。</summary>
        public static string Caption(CharacterSlot slot, bool cjk)
        {
            if (!cjk)
            {
                return slot.Caption.TrimEnd();
            }
            string level = "Lv" + slot.Level;
            return string.IsNullOrEmpty(slot.Name) ? level : level + " " + slot.Name;
        }

        /// <summary>按钮上的字。</summary>
        public static string Button(CharacterSelectButton button, bool cjk)
        {
            switch (button)
            {
                case CharacterSelectButton.Create: return cjk ? "创立角色" : "CREATE";
                case CharacterSelectButton.Delete: return cjk ? "删除角色" : "DELETE";
                case CharacterSelectButton.Channel: return cjk ? "选择频道" : "CHANNEL";
                case CharacterSelectButton.Quit: return cjk ? "结束游戏" : "EXIT";
                default: return string.Empty;
            }
        }

        /// <summary>不能进入的角色头像下面那一行标记;能进入返回空串。</summary>
        public static string Marker(CharacterBlock block, bool cjk)
        {
            switch (block)
            {
                case CharacterBlock.None: return string.Empty;
                case CharacterBlock.PendingDelete: return cjk ? "待删除" : "DELETING";
                default: return cjk ? "不可进入" : "LOCKED";
            }
        }

        /// <summary>提示条上的字;没有提示返回空串。</summary>
        public static string Prompt(CharacterSelectPrompt prompt, bool cjk)
        {
            switch (prompt)
            {
                case CharacterSelectPrompt.CannotEnter: return cjk ? "该角色暂时不能进入游戏" : "THIS CHARACTER CANNOT ENTER";
                case CharacterSelectPrompt.PendingDelete: return cjk ? "该角色正在删除中" : "THIS CHARACTER IS BEING DELETED";
                case CharacterSelectPrompt.NotAvailable: return cjk ? "暂未开放" : "NOT AVAILABLE YET";
                default: return string.Empty;
            }
        }

        /// <summary>点了"游戏开始"之后、等进城结果时提示条上的字。</summary>
        public static string Busy(bool cjk)
        {
            return cjk ? "正在进入…" : "ENTERING...";
        }

        /// <summary>选中的角色点"游戏开始"被拦住时该弹的提示;能进入返回 <see cref="CharacterSelectPrompt.None"/>。</summary>
        public static CharacterSelectPrompt PromptFor(CharacterBlock block)
        {
            switch (block)
            {
                case CharacterBlock.None: return CharacterSelectPrompt.None;
                case CharacterBlock.PendingDelete: return CharacterSelectPrompt.PendingDelete;
                default: return CharacterSelectPrompt.CannotEnter;
            }
        }
    }

    /// <summary>
    /// 联网选角界面画一帧需要的、离线界面没有的状态(交给 <see cref="CharacterSelectArt"/> 的带 view 的 <c>Build</c>):
    /// 文字画笔(决定中文还是英文占位)、窗口第一行、等待状态与提示。由 <see cref="CharacterSelectScreen"/> 维护。
    /// </summary>
    public sealed class CharacterSelectView
    {
        /// <param name="fonts">这张画面自己的字体集(测试用);为 null 时用全进程共用的。</param>
        public CharacterSelectView(IGlyphFonts fonts = null)
        {
            Paint = new ShellPaint(new OverlayArt { Localize = false, Fonts = fonts });
        }

        /// <summary>文字画笔(不翻译、不转大写;没有字体时才退回点阵)。</summary>
        public ShellPaint Paint { get; }

        /// <summary>有没有字体:有就写中文,没有写英文占位。</summary>
        public bool Cjk => Paint.HasFonts;

        /// <summary>窗口里第一行是名册的第几行(0 起;一屏显示 <see cref="CharacterRoster.Rows"/> 行)。</summary>
        public int FirstRow { get; set; }

        /// <summary>已经点了"游戏开始"、在等结果:开始按钮变灰,提示条写"正在进入…"。</summary>
        public bool Busy { get; set; }

        /// <summary>正在显示的提示;没有是 <see cref="CharacterSelectPrompt.None"/>。</summary>
        public CharacterSelectPrompt Prompt { get; set; }

        /// <summary>是否允许"创立角色"按钮(默认为 false,灰显且点击只提示暂未开放)。</summary>
        public bool CreateEnabled { get; set; }

        /// <summary>是否允许"删除角色"按钮(默认为 false,灰显且点击只提示暂未开放)。</summary>
        public bool DeleteEnabled { get; set; }
    }
}
