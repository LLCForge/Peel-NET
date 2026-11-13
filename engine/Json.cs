using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Peel;

/// <summary>Yansımasız JSON yazımı (budanmış WebAssembly derlemesinde güvenli).</summary>
public sealed class Json : IDisposable
{
    readonly MemoryStream ms = new();
    public readonly Utf8JsonWriter W;

    public Json() { W = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }); }

    public static string Of(Action<Utf8JsonWriter> write)
    {
        using var j = new Json();
        write(j.W);
        return j.ToString();
    }

    public override string ToString()
    {
        W.Flush();
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public void Dispose() => W.Dispose();
}

public static class JsonExt
{
    public static void Strings(this Utf8JsonWriter w, string name, IEnumerable<string> items)
    {
        w.WriteStartArray(name);
        foreach (var s in items) w.WriteStringValue(s);
        w.WriteEndArray();
    }

    /// <summary>Geçersiz UTF-16 (eşsiz vekil) içeren dizgeler JSON'a yazılamaz: \uXXXX biçimine çevrilir.</summary>
    public static string Safe(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (!char.IsSurrogate(s[i])) continue;
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { i++; continue; }
            var sb = new StringBuilder(s.Length + 8);
            for (int k = 0; k < s.Length; k++)
            {
                char c = s[k];
                if (char.IsHighSurrogate(c) && k + 1 < s.Length && char.IsLowSurrogate(s[k + 1])) { sb.Append(c).Append(s[++k]); }
                else if (char.IsSurrogate(c)) sb.Append($"\\u{(int)c:X4}");
                else sb.Append(c);
            }
            return sb.ToString();
        }
        return s;
    }
}
