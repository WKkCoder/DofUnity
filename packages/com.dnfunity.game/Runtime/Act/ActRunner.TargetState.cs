namespace Dnf.Game.Act
{
    /// <summary>[IS TARGET STATE]/[IS NOT TARGET STATE](M72,E-172):条件块 0x010d91bd、参数 0x107b2e0、Character +0x36b0 分支 0x10d92c7。</summary>
    public sealed partial class ActRunner
    {
        /// <summary>对齐客户端 0x107b2e0:读参数向量第 index 项;越界返回 -1(空向量时 index 0 即 -1,E-172)。</summary>
        private static int ReadActIntParam(ActRecord rec, int index)
        {
            if (rec.Ints == null || index < 0 || index >= rec.Ints.Length)
            {
                return -1;
            }
            return rec.Ints[index];
        }

        /// <summary>getState(+0xb8) 与 0x107b2e0(0) 比较得 BL;param=-1 时 cast 目标 @019d9acc 查 +0x36b0(非 -1 无条件保留)。</summary>
        private bool KeepTargetState(ActRecord rec, IActEntity e)
        {
            if (!_owner.ActTargetStateOwnerEligible || !e.ActTargetStateSubjectEligible)
            {
                return false;
            }
            int param = ReadActIntParam(rec, 0);
            bool match = e.ActAiState == param;
            if (rec.Kind == ActRecordKind.IsNotTargetState)
            {
                match = !match;
            }
            if (param != -1)
            {
                return match;
            }
            if (e.Kind == ActEntityKind.Character && e.ActTargetStateFocus != -1)
            {
                return true;
            }
            return match;
        }
    }
}
