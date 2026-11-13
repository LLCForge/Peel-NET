using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace Peel;

/// <summary>Modül başlatıcısındaki (&lt;Module&gt;..cctor) koruma çağrıları: anti-debug, anti-dump, anti-tamper.
/// Sınıflandırma çağrılan yöntemin (bir düzey iç çağrılarıyla) başvurduğu API'lere göredir.</summary>
public static class Guards
{
    static readonly string[] DebugApis =
    {
        "System.Diagnostics.Debugger::get_IsAttached", "System.Diagnostics.Debugger::IsLogging",
        "System.Environment::FailFast", "!IsDebuggerPresent", "!CheckRemoteDebuggerPresent",
        "!NtQueryInformationProcess", "!OutputDebugString", "!ZwQueryInformationProcess", "!CloseHandle",
        "COR_ENABLE_PROFILING", "COR_PROFILER",
    };

    static readonly string[] TamperApis = { "System.Runtime.InteropServices.Marshal::GetHINSTANCE", "!VirtualProtect" };
    static readonly string[] DumpApis = { "!VirtualProtect", "System.Runtime.InteropServices.Marshal::GetHINSTANCE" };

    public enum Kind { None, AntiDebug, AntiTamper, AntiDump }

    static IEnumerable<string> Refs(MethodDef m, int depth = 1)
    {
        if (!m.HasBody) yield break;
        foreach (var i in m.Body.Instructions)
        {
            if (i.Operand is string s) yield return s;
            if (i.Operand is not IMethod t) continue;
            if (t is MethodDef d && d.ImplMap != null) yield return "!" + d.ImplMap.Name;
            else yield return Watch.TypeName(t.DeclaringType) + "::" + t.Name;
            if (depth > 0 && Il.ResolveDef(t) is { } inner && inner.Module == m.Module && inner != m)
                foreach (var r in Refs(inner, depth - 1)) yield return r;
        }
    }

    public static Kind Classify(MethodDef m)
    {
        var refs = Refs(m).ToList();
        bool Has(string api) => refs.Any(r => api.StartsWith('!') ? r.EndsWith(api, StringComparison.Ordinal) : r.Contains(api, StringComparison.Ordinal));
        bool tamper = Has("System.Runtime.InteropServices.Marshal::GetHINSTANCE") && Has("!VirtualProtect");
        // anti-tamper yöntem gövdelerini çözer; anti-dump yalnızca PE başlıklarını siler (gövde çözmez: döngü sabit sayıda)
        if (tamper)
        {
            bool decrypts = m.Body.Instructions.Count(i => i.OpCode.Code == Code.Xor) >= 2;
            return decrypts ? Kind.AntiTamper : Kind.AntiDump;
        }
        if (DebugApis.Any(Has)) return Kind.AntiDebug;
        return Kind.None;
    }

    /// <summary>Başlatıcıdaki koruma çağrılarının listesi: (talimat, hedef, tür).</summary>
    public static List<(Instruction Ins, MethodDef Target, Kind Kind)> Find(ModuleDef mod)
    {
        var list = new List<(Instruction, MethodDef, Kind)>();
        var cctor = mod.GlobalType?.FindStaticConstructor();
        if (cctor?.HasBody != true) return list;
        foreach (var i in cctor.Body.Instructions)
        {
            if (i.OpCode.Code != Code.Call || Il.ResolveDef(i.Operand as IMethod) is not { } t || t.Module != mod) continue;
            if (t.MethodSig?.Params.Count != 0 || t.MethodSig.RetType.ElementType != ElementType.Void) continue;
            var k = Classify(t);
            if (k != Kind.None) list.Add((i, t, k));
        }
        return list;
    }

    /// <summary>Anti-debug/anti-dump çağrılarını nop yapar. Anti-tamper çağrısı (gövdeleri çözen) korunur.</summary>
    public static List<string> Neutralize(ModuleDef mod, bool keepTamper)
    {
        var done = new List<string>();
        foreach (var (ins, t, k) in Find(mod))
        {
            if (k == Kind.AntiTamper && keepTamper) continue;
            ins.OpCode = OpCodes.Nop;
            ins.Operand = null;
            done.Add($"{k}: {t.Name}");
        }
        return done;
    }
}
