namespace Dnf.Game
{
    /// <summary>
    /// 鬼剑士(.chr 里 job 0)的语音与固定音效的声音标签(证据 E-089)。这些标签写死在客户端里,不在任何脚本中:
    /// <list type="bullet">
    /// <item>受击与死亡:客户端有一张"职业 → (受击, 死亡)"的全局字符串表,鬼剑士那一对是 <see cref="Hurt"/> 与 <see cref="Die"/>。</item>
    /// <item>其余的语音:暗殿骑士的职业初始化函数里按固定顺序建了一张 12 项的语音表,一个标志位决定用暗殿骑士自己的一套,还是鬼剑士的一套。
    ///   顺序依次是 空中攻击、攻击、受击、死亡、跳跃、(空)、后跳、突刺、没魔法、冷却中、指挥宠物、投掷。
    ///   标志为"鬼剑士"时的 12 个标签就是下面这些;每个标签的名字与 audio.xml 里的条目一一对得上(<c>SM_BACKSTEP</c> 对应 sm_back.ogg 等)。
    ///   把这一套当成鬼剑士自己的语音表是推断(鬼剑士自己的初始化函数没有单独读)。</item>
    /// <item>上挑:客户端的鬼剑士一族每帧处理函数在上挑状态里,标志为"鬼剑士"时放 <see cref="UpperSlash"/>(暗殿骑士放 R_UPPER)。</item>
    /// <item>跳跃:跳跃状态起跳那一刻放 <see cref="JumpStart"/>,落地那一刻放 <see cref="JumpEnd"/>。</item>
    /// </list>
    /// 每个声音具体在哪一帧、概率多大(同一个语音不一定每次都放)没有读,这里一律触发一次。
    /// </summary>
    public static class SwordmanVoice
    {
        public const string JumpAttack = "R_SM_JUMP_ATK";
        public const string Attack = "R_SM_ATK";
        public const string Hurt = "R_SM_DMG";
        public const string Die = "SM_DIE";
        public const string Jump = "SM_JUMP";
        public const string Backstep = "SM_BACKSTEP";
        public const string Thrust = "SM_THRUST";
        public const string NoMana = "SM_NOMANA";
        public const string CoolTime = "SM_COOLTIME";
        public const string CommandPet = "SM_COMMAND_PET";
        public const string Throw = "SM_THROW";

        /// <summary>上挑状态开始时放的音效(鬼剑士一套)。</summary>
        public const string UpperSlash = "UPPER_SLASH_02";

        /// <summary>跳跃状态:起跳与落地的音效。</summary>
        public const string JumpStart = "JUMP_START";

        public const string JumpEnd = "JUMP_END";

        /// <summary>空中挥剑的音效(客户端里有几处引用,触发条件没读到,暂不用)。</summary>
        public const string AirSword = "AIR_SWORD";
    }
}
