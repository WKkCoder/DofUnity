using System.Text.Json;

namespace Dnf.Cli.Verification;

/// <summary>一项验证的结果:通过与否、度量值、失败说明。只记录数字与哈希,不含任何原版内容。</summary>
internal sealed class VerificationResult
{
    private const int MaxFailureLines = 40;

    public VerificationResult(string name)
    {
        Name = name;
    }

    public string Name { get; }
    public double Seconds { get; set; }
    public SortedDictionary<string, object?> Metrics { get; } = new(StringComparer.Ordinal);
    public List<string> Failures { get; } = new();
    public long SuppressedFailures { get; private set; }

    public bool Passed => Failures.Count == 0 && SuppressedFailures == 0;

    public void Fail(string message)
    {
        if (Failures.Count < MaxFailureLines)
        {
            Failures.Add(message);
        }
        else
        {
            SuppressedFailures++;
        }
    }

    public void Expect(bool condition, string message)
    {
        if (!condition)
        {
            Fail(message);
        }
    }

    public void ExpectEqual<T>(string what, T expected, T actual) where T : IEquatable<T>
    {
        if (!expected.Equals(actual))
        {
            Fail(what + ":期望 " + expected + ",实际 " + actual);
        }
    }

    public void WriteJson(Utf8JsonWriter w)
    {
        w.WriteStartObject();
        w.WriteString("name", Name);
        w.WriteBoolean("passed", Passed);
        w.WriteNumber("seconds", Math.Round(Seconds, 2));
        w.WritePropertyName("metrics");
        w.WriteStartObject();
        foreach (KeyValuePair<string, object?> kv in Metrics)
        {
            w.WritePropertyName(kv.Key);
            WriteValue(w, kv.Value);
        }
        w.WriteEndObject();
        w.WritePropertyName("failures");
        w.WriteStartArray();
        foreach (string f in Failures)
        {
            w.WriteStringValue(f);
        }
        w.WriteEndArray();
        w.WriteNumber("suppressed_failures", SuppressedFailures);
        w.WriteEndObject();
    }

    private static void WriteValue(Utf8JsonWriter w, object? v)
    {
        switch (v)
        {
            case null:
                w.WriteNullValue();
                break;
            case string s:
                w.WriteStringValue(s);
                break;
            case bool b:
                w.WriteBooleanValue(b);
                break;
            case int i:
                w.WriteNumberValue(i);
                break;
            case long l:
                w.WriteNumberValue(l);
                break;
            case double d:
                w.WriteNumberValue(d);
                break;
            case IDictionary<string, long> dict:
                w.WriteStartObject();
                foreach (KeyValuePair<string, long> kv in dict)
                {
                    w.WriteNumber(kv.Key, kv.Value);
                }
                w.WriteEndObject();
                break;
            default:
                w.WriteStringValue(v.ToString());
                break;
        }
    }
}

internal static class JsonHelpers
{
    public static long Int64(this JsonElement e) => e.GetInt64();

    public static JsonDocument Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new UserError("缺少 Python 参考输出: " + path + "。请先运行对应的 tools/py 脚本(见 docs/roadmap.md)。");
        }
        using FileStream fs = File.OpenRead(path);
        return JsonDocument.Parse(fs);
    }
}
