using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

namespace Peel;

public sealed class PassContext
{
    public required ModuleDefMD Module;
    public required byte[] Input;
    public required JsonElement Options;
    public readonly List<string> Log = new();
    public readonly Dictionary<string, int> Stats = new();
    /// <summary>Geçişin bilerek kaldırdığı (gizleyiciye ait) yan etkiler: davranış karşılaştırmasında sayılmaz.</summary>
    public readonly List<string> IgnoreFx = new();
    public void Bump(string key, int n = 1) { if (n != 0) Stats[key] = Stats.GetValueOrDefault(key) + n; }
    public bool Flag(string name) => Options.ValueKind == JsonValueKind.Object && Options.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}

/// <summary>Geçiş dağıtıcısı. Her geçiş modülü yerinde değiştirir; değiştiyse modül belirteçler korunarak yazılır,
/// yeniden okunur ve tüm yöntem gövdeleri ayrıştırılır (yapısal doğrulama). Davranış doğrulaması ve geri alma
/// JavaScript hattındadır (src/core/pipeline.js): her geçişten sonra Runner görüntüsü temelle karşılaştırılır.</summary>
public static class Passes
{
    public static readonly string[] Order = { "antidebug", "resources", "controlflow", "proxy", "strings", "fold", "rename", "cleanup" };

    public sealed class Result
    {
        public byte[]? Bytes;
        public string Json = "";
    }

    public static Result Run(byte[] input, string id, JsonElement options)
    {
        var mod = Sandbox.Load(input);
        var ctx = new PassContext { Module = mod, Input = input, Options = options };
        bool changed = id switch
        {
            "antidebug" => AntiDebugPass.Run(ctx),
            "resources" => ResourcesPass.Run(ctx),
            "proxy" => ProxyPass.Run(ctx),
            "strings" => StringsPass.Run(ctx),
            "controlflow" => ControlFlowPass.Run(ctx),
            "fold" => FoldPass.Run(ctx),
            "rename" => RenamePass.Run(ctx),
            "cleanup" => CleanupPass.Run(ctx),
            _ => throw new ArgumentException("bilinmeyen geçiş: " + id),
        };
        byte[]? bytes = null;
        if (changed)
        {
            try { bytes = Sandbox.Write(mod, preserve: true); }
            catch (Exception e)
            {
                ctx.Log.Add("belirteçler korunamadı: " + e.Message);
                bytes = Sandbox.Write(mod, preserve: false);
            }
            Structural(bytes);
        }
        var json = Json.Of(w =>
        {
            w.WriteStartObject();
            w.WriteBoolean("ok", true);
            w.WriteBoolean("changed", changed);
            w.WriteStartObject("stats");
            foreach (var (k, v) in ctx.Stats) w.WriteNumber(k, v);
            w.WriteEndObject();
            w.Strings("log", ctx.Log.Select(JsonExt.Safe));
            w.Strings("ignoreFx", ctx.IgnoreFx);
            w.WriteEndObject();
        });
        return new Result { Bytes = bytes, Json = json };
    }

    /// <summary>Yazılan modül yeniden okunur; her gövde ayrıştırılır ve yığın derinliği hesaplanır. Hata geçişi düşürür.</summary>
    public static void Structural(byte[] bytes)
    {
        var m = Sandbox.Load(bytes);
        foreach (var t in m.GetTypes())
            foreach (var md in t.Methods)
            {
                if (!md.HasBody) continue;
                var body = md.Body;
                if (body.Instructions.Count > 0 && !MaxStackCalculator.GetMaxStack(body.Instructions, body.ExceptionHandlers, out _))
                    throw new InvalidOperationException($"yığın dengesiz: {Names.Show(t.Name)}::{Names.Show(md.Name)}");
            }
    }

    // ---- ortak çözümlemeler (Inspector da kullanır) ----

    public static readonly System.Text.RegularExpressions.Regex GuidName = new(@"^(<PrivateImplementationDetails>)?\{?[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}?$");

    public static bool IsRuntimeType(TypeDef t) => t.IsGlobalModuleType || Names.IsObfuscated(t.Name) || GuidName.IsMatch(t.Name);

    public static IEnumerable<string> RefNames(MethodDef m)
    {
        if (!m.HasBody) yield break;
        foreach (var i in m.Body.Instructions)
            if (i.Operand is IMemberRef r && r is IMethod or IField) yield return Watch.TypeName(r.DeclaringType) + "::" + r.Name;
            else if (i.Operand is ITypeDefOrRef tr) yield return Watch.TypeName(tr);
    }
}

// ---------------------------------------------------------------------------------------------------------------

static class AntiDebugPass
{
    public static bool Run(PassContext ctx)
    {
        var done = Guards.Neutralize(ctx.Module, keepTamper: true);
        foreach (var d in done) ctx.Log.Add("kesildi: " + Names.Show(d));
        ctx.Bump("guards", done.Count);
        return done.Count > 0;
    }
}

/// <summary>Şifreli kaynaklar (ConfuserEx resources): modül başlatıcısı gizli bir derlemeyi Assembly.Load ile yükler ve
/// ResourceResolve olayına bağlar. Sandbox'ta başlatıcı çalıştırılır, yüklenmek istenen derleme yakalanır, içindeki
/// kaynaklar ana modüle geri eklenir ve başlatıcı çağrısı kesilir.</summary>
static class ResourcesPass
{
    public static bool Run(PassContext ctx)
    {
        var mod = ctx.Module;
        var cctor = mod.GlobalType?.FindStaticConstructor();
        if (cctor?.HasBody != true) return false;
        var inits = new List<Instruction>();
        foreach (var i in cctor.Body.Instructions)
        {
            if (i.OpCode.Code != Code.Call || Il.ResolveDef(i.Operand as IMethod) is not { } t || t.Module != mod) continue;
            var refs = Passes.RefNames(t).ToList();
            if (refs.Any(r => r.EndsWith("::add_ResourceResolve", StringComparison.Ordinal) || r.EndsWith("::add_AssemblyResolve", StringComparison.Ordinal))
                && refs.Any(r => r == "System.Reflection.Assembly::Load"))
                inits.Add(i);
        }
        if (inits.Count == 0) return false;
        Fx.Reset();
        var copy = Sandbox.BuildCopy(ctx.Input);
        using (var l = Sandbox.LoadCopy(copy.Bytes, runModuleInit: true))
            if (l.InitError != null) ctx.Log.Add("başlatıcı: " + l.InitError);
        int added = 0;
        foreach (var layer in Fx.Layers)
        {
            ModuleDefMD inner;
            try { inner = Sandbox.Load(layer); }
            catch { continue; }
            foreach (var r in inner.Resources.OfType<EmbeddedResource>())
            {
                // ConfuserEx 2: ana modülde aynı adla gizli derlemeye bağlı (AssemblyLinked) kaynak kalır; gömülüyle değiştirilir
                var existing = mod.Resources.FirstOrDefault(x => x.Name == r.Name);
                if (existing is EmbeddedResource) continue;
                if (existing != null) mod.Resources.Remove(existing);
                mod.Resources.Add(new EmbeddedResource(r.Name, r.CreateReader().ToArray(), r.Attributes));
                ctx.Log.Add("kaynak geri kazanıldı: " + r.Name);
                added++;
            }
        }
        if (added == 0) return false;
        foreach (var i in inits) { i.OpCode = OpCodes.Nop; i.Operand = null; }
        ctx.Bump("resources", added);
        // gizli kaynak derlemesinin yüklenmesi artık olmaz: bu yan etki ve katman karşılaştırmadan düşer
        ctx.IgnoreFx.Add("System.Reflection.Assembly::Load");
        return true;
    }
}

/// <summary>Vekil çağrılar (ConfuserEx ref proxy "mild"): gövdesi yalnızca argümanları aktarıp tek bir çağrı yapan
/// yöntemler. Çağrı yerleri doğrudan hedefe bağlanır. Temsilci (delegate) tabanlı "strong" kip yalnızca raporlanır.</summary>
static class ProxyPass
{
    public static Dictionary<MethodDef, (OpCode Op, IMethod Target)> Find(ModuleDef mod)
    {
        var map = new Dictionary<MethodDef, (OpCode, IMethod)>();
        foreach (var t in mod.GetTypes())
            foreach (var m in t.Methods)
            {
                if (!m.IsStatic || !m.HasBody || m.Body.HasExceptionHandlers) continue;
                if (!(t.IsGlobalModuleType || Names.IsObfuscated(m.Name))) continue;
                var ins = m.Body.Instructions.Where(i => i.OpCode.Code != Code.Nop).ToList();
                int n = m.Parameters.Count;
                if (ins.Count != n + 2 || ins[^1].OpCode.Code != Code.Ret) continue;
                bool args = true;
                for (int k = 0; k < n; k++) if (ins[k].GetParameterIndex() != k || !ins[k].IsLdarg()) { args = false; break; }
                if (!args) continue;
                var call = ins[n];
                if (call.OpCode.Code is not (Code.Call or Code.Callvirt or Code.Newobj) || call.Operand is not IMethod target) continue;
                if (target is MethodDef td && td == m) continue;
                var sig = target.MethodSig;
                if (sig == null) continue;
                int expected = sig.Params.Count + (sig.HasThis && call.OpCode.Code != Code.Newobj ? 1 : 0);
                if (expected != n) continue;
                map[m] = (call.OpCode, target);
            }
        return map;
    }

    public static bool Run(PassContext ctx)
    {
        var proxies = Find(ctx.Module);
        if (proxies.Count == 0) return false;
        int fixedSites = 0;
        foreach (var t in ctx.Module.GetTypes())
            foreach (var m in t.Methods)
            {
                if (!m.HasBody || proxies.ContainsKey(m)) continue;
                foreach (var i in m.Body.Instructions)
                {
                    if (i.OpCode.Code != Code.Call || i.Operand is not MethodDef d || !proxies.TryGetValue(d, out var p)) continue;
                    // zincirleme vekiller: hedef de vekilse sona kadar izlenir
                    var (op, target) = p;
                    for (int hop = 0; hop < 8 && target is MethodDef td && proxies.TryGetValue(td, out var next); hop++) (op, target) = next;
                    i.OpCode = op;
                    i.Operand = target;
                    fixedSites++;
                }
            }
        ctx.Bump("proxies", proxies.Count);
        ctx.Bump("proxyCalls", fixedSites);
        if (fixedSites > 0) ctx.Log.Add($"{fixedSites} vekil çağrı doğrudan hedefe bağlandı ({proxies.Count} vekil)");
        return fixedSites > 0;
    }
}

/// <summary>Dizge ve sabit çözücüleri: çözücü yöntem sandbox'ta (trambolin üzerinden, aynı derlemenin içinden) gerçekten
/// çağrılır, sonuç çağrı yerine sabit olarak yazılır. Argümanlar yalnızca yan etkisiz sabit ifadelerden hesaplanır.</summary>
static class StringsPass
{
    static readonly string[] Decryptorish =
    {
        "System.Text.Encoding::GetString", "System.Text.Encoding::get_UTF8", "System.Text.Encoding::get_Unicode",
        "System.Text.Encoding::get_ASCII", "System.Text.Encoding::get_Default", "System.String::Intern",
        "System.Buffer::BlockCopy", "System.BitConverter::ToInt32", "System.Runtime.CompilerServices.RuntimeHelpers::InitializeArray",
        "System.Convert::FromBase64String", "System.Diagnostics.StackTrace", "System.Diagnostics.StackFrame",
        "System.Reflection.Assembly::GetManifestResourceStream", "System.Array::Copy", "System.String::.ctor",
        "System.Reflection.Assembly::GetCallingAssembly", "System.Reflection.Assembly::GetExecutingAssembly",
    };

    static readonly string[] Nondeterministic =
    {
        "System.Random", "System.DateTime::get_Now", "System.DateTime::get_UtcNow", "System.Guid::NewGuid",
        "System.Environment::get_TickCount", "System.Diagnostics.Stopwatch",
    };

    static bool PrimitiveOrString(TypeSig t) => t.ElementType is ElementType.I1 or ElementType.U1 or ElementType.I2 or ElementType.U2
        or ElementType.I4 or ElementType.U4 or ElementType.I8 or ElementType.U8 or ElementType.R4 or ElementType.R8
        or ElementType.Char or ElementType.Boolean or ElementType.String;

    public static HashSet<MethodDef> Candidates(ModuleDef mod)
    {
        var set = new HashSet<MethodDef>();
        var pending = new HashSet<MethodDef>();
        var entry = mod.EntryPoint;
        foreach (var t in mod.GetTypes())
            foreach (var m in t.Methods)
            {
                if (!m.IsStatic || !m.HasBody || m.IsStaticConstructor || m == entry || m.ImplMap != null) continue;
                var sig = m.MethodSig;
                if (sig.Params.Count > 4 || !sig.Params.All(PrimitiveOrString)) continue;
                bool generic = m.HasGenericParameters && sig.RetType is GenericMVar;
                if (!generic && !PrimitiveOrString(sig.RetType)) continue;
                var refs = Passes.RefNames(m).ToList();
                if (refs.Any(r => Nondeterministic.Any(n => r.StartsWith(n, StringComparison.Ordinal)))) continue;
                // yan etkili API'ye dokunan yöntem çözücü sayılmaz (dosya, ağ, süreç ...)
                if (m.Body.Instructions.Any(i => Il.CallTarget(i) is { } c && Watch.Api(c) != null)) continue;
                // kullanıcı türlerindeki yöntemler (ad gizlenmiş olsa da) yalnızca tamsayı anahtarla çağrılıyorsa çözücü sayılır:
                // parametresiz ya da dizge parametreli kullanıcı yöntemleri (kaynak okuma, base64 çözme ...) koda ait davranıştır
                bool runtimeType = t.IsGlobalModuleType || Passes.GuidName.IsMatch(t.Name) || (Names.IsObfuscated(t.Name) && t.Methods.All(x => !x.HasBody || x.IsStatic) && t.Fields.All(f => f.IsStatic) && t != mod.EntryPoint?.DeclaringType);
                if (!runtimeType && (sig.Params.Count == 0 || sig.Params.Any(p => p.ElementType == ElementType.String))) continue;
                bool runtime = runtimeType || Names.IsObfuscated(m.Name);
                bool decryptorish = refs.Any(r => Decryptorish.Any(d => r.StartsWith(d, StringComparison.Ordinal)));
                bool returnsString = sig.RetType.ElementType == ElementType.String || generic;
                if (decryptorish && (runtime || returnsString)) set.Add(m);
                else if (runtime && returnsString) pending.Add(m);
            }
        // çözücüyü çağıran erişimciler (Obfuscar: "string A() => _s[0] ?? Çöz(0, 12, 34)") de çözücüdür
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (var m in pending.ToList())
            {
                if (!m.Body.Instructions.Any(i => Il.ResolveDef(Il.CallTarget(i)) is { } d && set.Contains(d))) continue;
                set.Add(m);
                pending.Remove(m);
                grew = true;
            }
        }
        return set;
    }

    sealed class Site
    {
        public required MethodDef Method;
        public required int Start;
        public required int Call;
        public required IMethod Target;
        public required List<object?> Args;
        public required string Key;
        public required string TargetKey;
    }

    public static bool Run(PassContext ctx)
    {
        var mod = ctx.Module;
        var cands = Candidates(mod);
        if (cands.Count == 0) return false;
        var sites = new List<Site>();
        int unresolved = 0;
        foreach (var t in mod.GetTypes())
            foreach (var m in t.Methods)
            {
                if (!m.HasBody || cands.Contains(m)) continue;
                var ins = m.Body.Instructions;
                Il.RemoveTrivialBranches(m.Body);
                HashSet<Instruction>? targets = null;
                for (int k = 0; k < ins.Count; k++)
                {
                    if (ins[k].OpCode.Code != Code.Call || ins[k].Operand is not IMethod target) continue;
                    if (Il.ResolveDef(target) is not { } def || !cands.Contains(def)) continue;
                    targets ??= Il.Targets(m.Body);
                    var a = Il.ArgsOf(ins, k, target.MethodSig.Params.Count, targets);
                    if (a == null) { unresolved++; continue; }
                    var tk = target.FullName;
                    sites.Add(new Site
                    {
                        Method = m, Start = a.Value.Start, Call = k, Target = target, Args = a.Value.Values, TargetKey = tk,
                        Key = tk + "|" + string.Join("|", a.Value.Values.Select(v => v == null ? "null" : v.GetType().Name + ":" + v)),
                    });
                    if (sites.Count >= 50000) break;
                }
            }
        if (sites.Count == 0)
        {
            if (unresolved > 0) ctx.Log.Add($"{unresolved} çözücü çağrısının argümanları sabit değil");
            return false;
        }

        // her hedef (genel örnekleme dahil) için bir trambolin
        var targetsByKey = sites.GroupBy(s => s.TargetKey).ToDictionary(g => g.Key, g => g.First().Target);
        Fx.Reset();
        var copy = Sandbox.BuildCopy(ctx.Input, cm => targetsByKey.Select(kv => (kv.Key, Translate(cm, kv.Value))).Where(x => x.Item2 != null)!);
        var values = new Dictionary<string, (bool Ok, object? Value)>();
        using (var l = Sandbox.LoadCopy(copy.Bytes, runModuleInit: true))
        {
            if (l.InitError != null) ctx.Log.Add("modül başlatıcısı: " + l.InitError);
            var byTarget = copy.Tramps.ToDictionary(kv => kv.Value.Key, kv => copy.Find(l.Asm, kv.Key));
            int failures = 0;
            string? firstError = null;
            foreach (var s in sites)
            {
                if (values.ContainsKey(s.Key)) continue;
                if (!byTarget.TryGetValue(s.TargetKey, out var mi) || mi == null) { values[s.Key] = (false, null); continue; }
                var ps = mi.GetParameters();
                var args = s.Args.Select((v, i) => Sandbox.Coerce(v, ps[i].ParameterType)).ToArray();
                object? value = null;
                var c = Sandbox.Capture(() => value = mi.Invoke(null, args));
                bool ok = c.Exception == null && !c.Exited && (value == null || Il.Const(value) != null);
                if (!ok) { failures++; firstError ??= c.Exception ?? "dönüş sabite çevrilemedi"; }
                values[s.Key] = (ok, value);
            }
            if (failures > 0) ctx.Log.Add($"{failures} çözücü çağrısı başarısız: {firstError}");
        }

        var touched = new HashSet<MethodDef>();
        int strings = 0, consts = 0;
        foreach (var g in sites.GroupBy(s => s.Method))
        {
            var m = g.Key;
            var ins = m.Body.Instructions;
            foreach (var s in g)
            {
                var (ok, value) = values.GetValueOrDefault(s.Key);
                if (!ok) continue;
                var c = Il.Const(value);
                if (c == null) continue;
                // kısa dallar taşmasın: gövde ilk değişiklikten önce açılır (talimat nesneleri ve sıraları korunur)
                if (!touched.Contains(m)) Il.Prepare(m);
                Il.Replace(ins, s.Start, s.Call, c);
                touched.Add(m);
                if (value is string) strings++; else consts++;
            }
        }
        foreach (var m in touched) Il.Finish(m);
        ctx.Bump("strings", strings);
        ctx.Bump("constants", consts);
        ctx.Bump("decryptors", cands.Count);
        if (unresolved > 0) ctx.Bump("unresolvedCalls", unresolved);
        if (strings + consts > 0) ctx.Log.Add($"{strings} dizge, {consts} sabit çözüldü ({cands.Count} çözücü)");
        return strings + consts > 0;
    }

    /// <summary>Özgün modüldeki hedefi çalıştırma kopyasındaki karşılığına çevirir (aynı baytlardan okunduğu için belirteçler aynı).</summary>
    static IMethod? Translate(ModuleDefMD cm, IMethod target)
    {
        var def = Il.ResolveDef(target);
        if (def == null || cm.ResolveToken(def.MDToken) is not MethodDef cdef) return null;
        if (target is MethodSpec spec && spec.GenericInstMethodSig is { } gi)
        {
            var imp = new Importer(cm, ImporterOptions.TryToUseDefs);
            var args = gi.GenericArguments.Select(a => imp.Import(a)).ToList();
            return new MethodSpecUser(cdef, new GenericInstMethodSig(args));
        }
        return cdef;
    }
}

/// <summary>Sabit katlama: yan etkisiz sabit ifadeler (ldc + aritmetik) tek sabite indirgenir; sabit koşullu dallar
/// (opak yüklemler) koşulsuz dala ya da nop'a çevrilir.</summary>
static class FoldPass
{
    public static bool Run(PassContext ctx)
    {
        int folded = 0, branches = 0;
        foreach (var t in ctx.Module.GetTypes())
            foreach (var m in t.Methods)
            {
                if (!m.HasBody) continue;
                var ins = m.Body.Instructions;
                if (!ins.Any(i => i.IsLdcI4() || i.OpCode.Code is Code.Ldc_I8 or Code.Ldc_R8)) continue;
                int trivial = Il.RemoveTrivialBranches(m.Body);
                bool any = trivial > 0;
                if (any) Il.Prepare(m);
                ctx.Bump("trivialBranches", trivial);
                var targets = Il.Targets(m.Body);
                for (int k = 1; k < ins.Count; k++)
                {
                    var code = ins[k].OpCode.Code;
                    // ldc ; brtrue/brfalse
                    if (code is Code.Brtrue or Code.Brtrue_S or Code.Brfalse or Code.Brfalse_S && ins[k - 1].IsLdcI4() && !targets.Contains(ins[k]))
                    {
                        if (!any) { Il.Prepare(m); any = true; }
                        bool taken = (ins[k - 1].GetLdcI4Value() != 0) == (ins[k].OpCode.Code is Code.Brtrue or Code.Brtrue_S);
                        ins[k - 1].OpCode = OpCodes.Nop;
                        ins[k - 1].Operand = null;
                        if (taken) ins[k].OpCode = OpCodes.Br;
                        else { ins[k].OpCode = OpCodes.Nop; ins[k].Operand = null; }
                        branches++;
                        continue;
                    }
                    if (ins[k].OpCode.FlowControl != FlowControl.Next || ins[k].IsLdcI4() || code is Code.Nop or Code.Ldc_I8 or Code.Ldc_R8 or Code.Ldc_R4 or Code.Ldstr or Code.Ldnull) continue;
                    // ins[k] bir işlem: tek değer üreten en kısa sabit pencere aranır
                    for (int s = k - 1; s >= 0 && k - s <= 12; s--)
                    {
                        if (targets.Contains(ins[s + 1])) break;
                        var v = Il.Simulate(ins, s, k + 1);
                        if (v == null) continue;
                        if (v.Count == 1 && Il.Const(v[0]) is { } c && v[0] is not string && v[0] != null)
                        {
                            if (!any) { Il.Prepare(m); any = true; }
                            Il.Replace(ins, s, k, c);
                            folded++;
                        }
                        break;
                    }
                }
                if (any) Il.Finish(m);
            }
        ctx.Bump("folded", folded);
        ctx.Bump("opaqueBranches", branches);
        return folded + branches > 0;
    }
}

/// <summary>Gizleyici işaret öznitelikleri (ConfusedByAttribute, SuppressIldasm ...) kaldırılır.</summary>
static class CleanupPass
{
    public static readonly string[] Markers =
    {
        "ConfusedByAttribute", "SuppressIldasmAttribute", "DotfuscatorAttribute", "PoweredByAttribute",
        "BabelObfuscatorAttribute", "ObfuscatedByGoliath", "YanoAttribute", "NineRays.Obfuscator.Evaluation",
        "CryptoObfuscator.ProtectedWithCryptoObfuscatorAttribute", "Xenocode.Client.Attributes.AssemblyAttributes.ProcessedByXenocode",
    };

    public static bool IsMarker(string fullName) => Markers.Any(m => fullName == m || fullName.EndsWith("." + m, StringComparison.Ordinal));

    public static bool Run(PassContext ctx)
    {
        int removed = 0;
        foreach (var owner in new IHasCustomAttribute?[] { ctx.Module, ctx.Module.Assembly })
        {
            if (owner == null) continue;
            foreach (var ca in owner.CustomAttributes.ToList())
            {
                if (!IsMarker(ca.TypeFullName)) continue;
                owner.CustomAttributes.Remove(ca);
                ctx.Log.Add("işaret özniteliği kaldırıldı: " + ca.TypeFullName);
                removed++;
            }
        }
        // artık kullanılmayan işaret türleri (ConfusedByAttribute sınıfı) de silinir
        foreach (var t in ctx.Module.Types.ToList())
        {
            if (!IsMarker(t.FullName) || ctx.Module.GetTypes().Any(x => x != t && x.CustomAttributes.Any(c => c.AttributeType?.FullName == t.FullName))) continue;
            bool used = ctx.Module.GetTypes().SelectMany(x => x.Methods).Any(m => m.CustomAttributes.Any(c => c.AttributeType?.FullName == t.FullName)
                || (m.HasBody && m.Body.Instructions.Any(i => (i.Operand as IMemberRef)?.DeclaringType?.FullName == t.FullName)));
            if (used) continue;
            ctx.Module.Types.Remove(t);
            ctx.Log.Add("işaret türü silindi: " + t.FullName);
            removed++;
        }
        ctx.Bump("markers", removed);
        return removed > 0;
    }
}
