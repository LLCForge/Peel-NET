using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using dnlib.DotNet;

namespace Peel;

/// <summary>Davranış anlık görüntüsü: giriş noktası çalıştırılır (konsol çıktısı, istisna, denenen yan etkiler) ve
/// yoklama yöntemleri (ilkel/dizge parametreli statik yöntemler) sabit argümanlarla çağrılır. Her geçişten sonra
/// aynı görüntü alınıp temelle karşılaştırılır; fark varsa geçiş geri alınır.</summary>
public static class Runner
{
    static readonly object?[][] ArgSets =
    {
        new object?[] { 0, 0L, false, "", 0.0, 'a', (byte)0 },
        new object?[] { 7, 7L, true, "peel", 1.5, 'Z', (byte)7 },
        new object?[] { -3, 123456789L, true, "Peel-NET 123", -2.25, '0', (byte)255 },
    };

    static bool Probeable(Type t) => t == typeof(int) || t == typeof(long) || t == typeof(bool) || t == typeof(string)
        || t == typeof(double) || t == typeof(char) || t == typeof(byte);

    static object? Arg(Type t, object?[] set) =>
        t == typeof(int) ? set[0] : t == typeof(long) ? set[1] : t == typeof(bool) ? set[2] : t == typeof(string) ? set[3]
        : t == typeof(double) ? set[4] : t == typeof(char) ? set[5] : set[6];

    /// <summary>Yoklama adayları belirteç (token) sırasıyla, en fazla 40.</summary>
    public static List<MethodBase> Probes(Assembly asm, int max = 40)
    {
        var list = new List<MethodBase>();
        Type[] types;
        try { types = asm.GetTypes(); }
        catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray()!; }
        foreach (var t in types.OrderBy(t => t.MetadataToken))
        {
            if (t.IsGenericTypeDefinition || t.Name is "<Module>" or Sandbox.StubType or Sandbox.TrampType) continue;
            MethodInfo[] ms;
            try { ms = t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly); }
            catch { continue; }
            foreach (var m in ms.OrderBy(m => m.MetadataToken))
            {
                try
                {
                    if (m.IsGenericMethodDefinition || m.ReturnType == typeof(void) || !Probeable(m.ReturnType) || m == asm.EntryPoint) continue;
                    var ps = m.GetParameters();
                    if (ps.Length > 4 || !ps.All(p => Probeable(p.ParameterType))) continue;
                    if (m.Name.StartsWith('<')) continue;
                    list.Add(m);
                }
                catch { }
                if (list.Count >= max) return list;
            }
        }
        return list;
    }

    public static string Snapshot(byte[] input, bool main, bool probes)
    {
        Fx.Reset();
        var copy = Sandbox.BuildCopy(input);
        using var loaded = Sandbox.LoadCopy(copy.Bytes, runModuleInit: false);
        var asm = loaded.Asm;
        Sandbox.Captured? mainRun = null;
        bool hasEntry = asm.EntryPoint != null;
        // modül başlatıcısı kendi başına da çalıştırılır: DLL'lerde davranışın bir kısmı buradadır
        var init = Sandbox.Capture(() => System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(asm.ManifestModule.ModuleHandle));
        if (main && hasEntry && init.Exception == null && !init.Exited) mainRun = Sandbox.Capture(() => Sandbox.InvokeEntry(asm));
        var probeResults = new List<(int Token, List<string> Results)>();
        if (probes && init.Exception == null && !init.Exited)
        {
            foreach (var m in Probes(asm))
            {
                var res = new List<string>();
                foreach (var set in ArgSets)
                {
                    object? value = null;
                    var c = Sandbox.Capture(() => value = m.Invoke(null, m.GetParameters().Select(p => Arg(p.ParameterType, set)).ToArray()));
                    res.Add(c.Exception != null ? "!" + c.Exception.Split(':')[0] : c.Exited ? "!exit" : Fx.Describe(value) + (c.Out.Length > 0 ? " | " + c.Out : ""));
                }
                probeResults.Add((m.MetadataToken, res));
            }
        }
        return Json.Of(w =>
        {
            w.WriteStartObject();
            w.WriteBoolean("hasEntry", hasEntry);
            w.WriteBoolean("ran", mainRun != null);
            w.WriteBoolean("mainOk", mainRun != null && mainRun.Exception == null);
            w.WriteString("out", JsonExt.Safe(init.Out + (mainRun?.Out ?? "")));
            if (init.Exception != null) w.WriteString("init", JsonExt.Safe(init.Exception));
            if (mainRun?.Exception != null) w.WriteString("exception", JsonExt.Safe(mainRun.Exception));
            w.WriteBoolean("exited", init.Exited || mainRun?.Exited == true);
            w.WriteNumber("redirected", copy.Redirected);
            w.Strings("neutralized", copy.Neutralized);
            w.WriteStartArray("fx");
            foreach (var (api, args) in Fx.Calls)
            {
                w.WriteStartObject();
                w.WriteString("api", api);
                w.WriteString("cat", Watch.Category(api));
                w.Strings("args", args.Select(JsonExt.Safe));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteNumber("layers", Fx.Layers.Count);
            w.WriteStartArray("probes");
            foreach (var (token, results) in probeResults)
            {
                w.WriteStartObject();
                w.WriteNumber("token", token);
                w.Strings("results", results.Select(JsonExt.Safe));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        });
    }
}
