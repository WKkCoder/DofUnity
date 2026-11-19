using System.Collections.Generic;

namespace Dnf.Game.Act
{
    internal static partial class ActCompiler
    {
        private static ActRecord ParseAxisIndex(ActCursor cur, string tag, List<string> unsupported)
        {
            if (cur.TryInt(out int axis))
            {
                return new ActRecord { Kind = ActRecordKind.AxisIndex, Tag = tag, Number = axis };
            }
            AddUnsupportedTag(unsupported, tag);
            cur.SkipToHead(IsTriggerHead);
            return new ActRecord { Kind = ActRecordKind.Unsupported, Tag = tag };
        }

        private static ActRecord ParseChargedTime(ActCursor cur, string tag, List<string> unsupported)
        {
            if (cur.TryCompare(out ActCompare op, out int ms))
            {
                return new ActRecord { Kind = ActRecordKind.ChargedTime, Tag = tag, Sign = op, Number = ms };
            }
            AddUnsupportedTag(unsupported, tag);
            cur.SkipToHead(IsTriggerHead);
            return new ActRecord { Kind = ActRecordKind.Unsupported, Tag = tag };
        }

        private static ActBehavior ParseCluster1Behavior(ActCursor cur, string tag)
        {
            switch (tag)
            {
                case "[SET VISIBLITY]":
                    return cur.TryInt(out int vis)
                        ? new ActBehavior { Kind = ActBehaviorKind.SetVisibility, Tag = tag, RestFlag = vis != 0 }
                        : new ActBehavior { Kind = ActBehaviorKind.Unsupported, Tag = tag };
                case "[SET GRABABLE]":
                    return cur.TryInt(out int grab)
                        ? new ActBehavior { Kind = ActBehaviorKind.SetGrabable, Tag = tag, RestFlag = grab == 1 }
                        : new ActBehavior { Kind = ActBehaviorKind.Unsupported, Tag = tag };
                case "[SET DAMAGE CHECK]":
                {
                    if (!cur.TryInt(out int mode) || !cur.TryInt(out int durationMs))
                    {
                        return new ActBehavior { Kind = ActBehaviorKind.Unsupported, Tag = tag };
                    }
                    return new ActBehavior { Kind = ActBehaviorKind.SetDamageCheck, Tag = tag, RestFlag = mode == 1, RestDurationMs = durationMs };
                }
                case "[SET UNBEATABLE]":
                    return ParseSetUnbeatable(cur, tag);
                case "[SET PASSTYPE]":
                    return ParseSetPassType(cur, tag);
                case "[SET WIDTH]":
                {
                    ActValue w0 = cur.Value();
                    ActValue w1 = w0 == null ? null : cur.Value();
                    return w0 == null || w1 == null
                        ? new ActBehavior { Kind = ActBehaviorKind.Unsupported, Tag = tag }
                        : new ActBehavior { Kind = ActBehaviorKind.SetWidth, Tag = tag, WidthX = w0, WidthY = w1 };
                }
                case "[CHANGE TEAM]":
                {
                    if (!cur.TryInt(out int teamId) || !cur.TryInt(out int durationMs) || !cur.TryInt(out int hpPct))
                    {
                        return new ActBehavior { Kind = ActBehaviorKind.Unsupported, Tag = tag };
                    }
                    return new ActBehavior
                    {
                        Kind = ActBehaviorKind.ChangeTeam,
                        Tag = tag,
                        RestTeam = teamId,
                        RestDurationMs = durationMs,
                        HpMaxPercent = hpPct,
                    };
                }
                case "[DEFAULT TEAM]":
                    return new ActBehavior { Kind = ActBehaviorKind.DefaultTeam, Tag = tag };
                default:
                    return new ActBehavior { Kind = ActBehaviorKind.Unsupported, Tag = tag };
            }
        }

        private static ActBehavior ParseSetUnbeatable(ActCursor cur, string tag)
        {
            string kw = null;
            if (cur.AtOpen("[ON]") || cur.AtOpen("[OFF]"))
            {
                kw = cur.TakeOpen();
            }
            else if (cur.AtString)
            {
                string s = cur.TakeString();
                if (s == "[ON]" || s == "[OFF]")
                {
                    kw = s;
                }
                else
                {
                    return new ActBehavior { Kind = ActBehaviorKind.Unsupported, Tag = tag };
                }
            }
            if (kw == "[ON]")
            {
                return cur.TryInt(out int durationMs)
                    ? new ActBehavior { Kind = ActBehaviorKind.SetUnbeatable, Tag = tag, RestFlag = true, RestDurationMs = durationMs }
                    : new ActBehavior { Kind = ActBehaviorKind.Unsupported, Tag = tag };
            }
            if (kw == "[OFF]")
            {
                return new ActBehavior { Kind = ActBehaviorKind.SetUnbeatable, Tag = tag, RestFlag = false, RestDurationMs = 0 };
            }
            return new ActBehavior { Kind = ActBehaviorKind.Unsupported, Tag = tag };
        }

        private static ActBehavior ParseSetPassType(ActCursor cur, string tag)
        {
            string kw = null;
            if (cur.AtOpen(out string openTag) && TryPassTypeCode(openTag, out _))
            {
                kw = cur.TakeOpen();
            }
            else if (cur.AtString)
            {
                kw = cur.TakeString();
            }
            return kw != null && TryPassTypeCode(kw, out int code)
                ? new ActBehavior { Kind = ActBehaviorKind.SetPassType, Tag = tag, PassType = code }
                : new ActBehavior { Kind = ActBehaviorKind.Unsupported, Tag = tag };
        }

        private static bool TryPassTypeCode(string keyword, out int code)
        {
            switch (keyword)
            {
                case "[pass all]":
                    code = 0;
                    return true;
                case "[pass only float type]":
                    code = 1;
                    return true;
                case "[do not pass]":
                    code = 2;
                    return true;
                case "[pass only red team]":
                    code = 3;
                    return true;
                case "[pass only blue team]":
                    code = 4;
                    return true;
                case "[pass only character]":
                    code = 5;
                    return true;
                default:
                    code = 0;
                    return false;
            }
        }
    }
}
