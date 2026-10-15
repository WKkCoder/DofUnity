using System.Text;
using Dnf.Data;

namespace Dnf.Game.Tests;

/// <summary>手工拼标记流 + 字符串池,得到一个读取游标(合成数据,不含原版内容)。</summary>
internal sealed class TokenStreamBuilder
{
    private readonly List<string> _pool = new();
    private readonly List<ScriptToken> _tokens = new();

    private int Intern(string s)
    {
        int i = _pool.IndexOf(s);
        if (i < 0)
        {
            _pool.Add(s);
            i = _pool.Count - 1;
        }
        return i;
    }

    private TokenStreamBuilder Add(TokenType type, int value)
    {
        _tokens.Add(new ScriptToken((byte)type, value));
        return this;
    }

    public TokenStreamBuilder Tag(string name) => Add(TokenType.Tag, Intern(name));

    public TokenStreamBuilder Int(int value) => Add(TokenType.Int, value);

    public TokenStreamBuilder Ints(params int[] values)
    {
        foreach (int v in values)
        {
            Int(v);
        }
        return this;
    }

    public TokenStreamBuilder Float(float value) => Add(TokenType.Float, BitConverter.SingleToInt32Bits(value));

    public TokenStreamBuilder Floats(params float[] values)
    {
        foreach (float v in values)
        {
            Float(v);
        }
        return this;
    }

    public TokenStreamBuilder Str(string text) => Add(TokenType.String, Intern(text));

    public ScriptCursor Cursor()
    {
        var s = new MemoryStream();
        void U32(uint v) => s.Write(BitConverter.GetBytes(v));
        U32((uint)_pool.Count);
        uint offset = (uint)((_pool.Count + 1) * 4);
        foreach (string item in _pool)
        {
            U32(offset);
            offset += (uint)item.Length;
        }
        U32(offset);
        foreach (string item in _pool)
        {
            s.Write(Encoding.Latin1.GetBytes(item));
        }
        return new ScriptCursor(_tokens.ToArray(), new PvfStringTable(s.ToArray()));
    }
}
