using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.PE;

namespace Peel;

/// <summary>Statik inceleme ve tespit. Ticari korumalar (VM tabanlı ve yerel sarmalayıcılar) yalnızca tanınır:
/// decodable=false olanlar çözülmeye çalışılmaz.</summary>
public static class Inspector
{
    sealed class Det
    {
        public required string Id, Name, Kind;
        public double Confidence;
        public string? Version;
        public bool Decodable = true;
        public List<string> Evidence = new();
    }

    // (kimlik, ad, tür, çözülebilir, işaretler: tür/öznitelik adı parçaları)
    static readonly (string Id, string Name, string Kind, bool Decodable, string[] Types)[] Markers =
    {
        ("dotfuscator", "Dotfuscator", "obfuscator", true, new[] { "DotfuscatorAttribute" }),
        ("smartassembly", "SmartAssembly", "protector", false, new[] { "SmartAssembly.Attributes.PoweredByAttribute", "SmartAssembly.", "{z}" }),
        ("babel", "Babel Obfuscator", "protector", false, new[] { "BabelObfuscatorAttribute", "BabelAttribute" }),
        ("agile", "Agile.NET (CliSecure)", "protector", false, new[] { "<AgileDotNetRT>", "AgileDotNetRT", "CliSecureRT", "SecureTeam.Attributes" }),
        ("armdot", "ArmDot", "protector", false, new[] { "ArmDot.Client", "ArmDot." }),
        ("vmprotect", "VMProtect", "protector", false, new[] { "VMProtect.", "VMProtectSDK" }),
        ("koivm", "KoiVM", "protector", false, new[] { "KoiVM.", "#Koi" }),
        ("cryptoobfuscator", "Crypto Obfuscator", "protector", false, new[] { "CryptoObfuscator.", "ProtectedWithCryptoObfuscatorAttribute" }),
        ("dnguard", "DNGuard HVM", "protector", false, new[] { "ZYXDNGuarder", "HVMRuntm" }),
        ("goliath", "Goliath.NET", "obfuscator", true, new[] { "ObfuscatedByGoliath" }),
        ("yano", "Yano", "obfuscator", true, new[] { "YanoAttribute" }),
        ("spices", "Spices.Net", "obfuscator", true, new[] { "NineRays.Obfuscator" }),
        ("xenocode", "Xenocode", "obfuscator", true, new[] { "Xenocode.Client.Attributes" }),
        ("maxtocode", "MaxtoCode", "protector", false, new[] { "InfaceMaxtoCode", "MaxtoCode" }),
        ("costura", "Costura.Fody (gömülü bağımlılıklar)", "packer", true, new[] { "Costura.AssemblyLoader" }),
    };

    public static string Run(byte[] data)
    {
        if (data.Length < 64 || data[0] != 'M' || data[1] != 'Z') return Fail("not-pe", "PE dosyası değil (MZ imzası yok)");
        ModuleDefMD? mod = null;
        string? loadError = null;
        try { mod = Sandbox.Load(data); }
        catch (Exception e) { loadError = e.Message; }
        var sections = new List<string>();
        try
        {
            using var pe = new PEImage(data);
            sections.AddRange(pe.ImageSectionHeaders.Select(s => s.DisplayName));
        }
        catch { }
        var dets = new List<Det>();
        NativeWrappers(sections, dets, mod == null);
        if (mod == null)
            return Json.Of(w =>
            {
                w.WriteStartObject();
                w.WriteBoolean("ok", true);
                w.WriteBoolean("net", false);
                w.WriteString("error", "Bir .NET derlemesi değil (CLR başlığı yok)" + (loadError != null ? ": " + JsonExt.Safe(loadError) : ""));
                w.Strings("sections", sections);
                WriteDets(w, dets);
                w.WriteEndObject();
            });

        var allTypes = mod.GetTypes().ToList();
        var typeNames = allTypes.Select(t => t.FullName).ToList();
        var attrs = (mod.Assembly?.CustomAttributes ?? new CustomAttributeCollection()).Concat(mod.CustomAttributes).ToList();
        var methods = allTypes.SelectMany(t => t.Methods).ToList();
        var strings = new List<string>();
        var stringSet = new HashSet<string>();
        var memberRefs = new Dictionary<string, int>();
        var userRefs = new Dictionary<string, int>();
        int badBodies = 0, ilSize = 0;
        foreach (var m in methods)
        {
            CilBody? body;
            try { body = m.Body; }
            catch { badBodies++; continue; }
            if (body == null) continue;
            ilSize += body.Instructions.Count;
            foreach (var i in body.Instructions)
            {
                if (i.Operand is string s && s.Length > 0 && stringSet.Add(s) && strings.Count < 4000) strings.Add(s);
                if (i.Operand is IMethod im && im is MemberRef or MethodSpec)
                {
                    var key = Watch.TypeName(im.DeclaringType) + "::" + im.Name;
                    memberRefs[key] = memberRefs.GetValueOrDefault(key) + 1;
                    // gizleyicinin çalışma zamanı (<Module> ve iç türleri) kullanıcı kodu sayılmaz: tehdit kanıtına katılmaz
                    if (!IsRuntimeOwner(m.DeclaringType)) userRefs[key] = userRefs.GetValueOrDefault(key) + 1;
                }
            }
        }
        var pinvoke = methods.Where(m => m.ImplMap != null).Select(m => $"{m.ImplMap.Module?.Name ?? "?"}!{m.ImplMap.Name}").Distinct().ToList();
        var streams = mod.Metadata.AllStreams.Select(s => s.Name).ToList();

        // ---- işaretli ürünler ----
        var haystack = typeNames.Concat(attrs.Select(a => a.TypeFullName)).Concat(mod.GetAssemblyRefs().Select(a => a.Name.String)).Concat(streams).ToList();
        foreach (var mk in Markers)
        {
            var hits = mk.Types.SelectMany(p => haystack.Where(h => h.Contains(p, StringComparison.Ordinal)).Take(2).Select(h => $"işaret: {Names.Show(h)}")).Distinct().ToList();
            if (hits.Count == 0) continue;
            dets.Add(new Det { Id = mk.Id, Name = mk.Name, Kind = mk.Kind, Decodable = mk.Decodable, Confidence = Math.Min(0.95, 0.7 + 0.1 * hits.Count), Evidence = hits });
        }

        // ConfuserEx (ve çatalları): ConfusedByAttribute metni
        var confused = attrs.FirstOrDefault(a => a.TypeFullName == "ConfusedByAttribute" || a.TypeFullName.EndsWith(".ConfusedByAttribute"));
        var proxies = ProxyPass.Find(mod);
        var decryptors = StringsPass.Candidates(mod);
        var guards = Guards.Find(mod);
        if (confused != null)
        {
            var text = confused.ConstructorArguments.Count > 0 ? confused.ConstructorArguments[0].Value?.ToString() ?? "" : "";
            var d = new Det { Id = "confuserex", Name = "ConfuserEx", Kind = "obfuscator", Confidence = 0.99, Evidence = { $"ConfusedByAttribute(\"{JsonExt.Safe(text)}\")" } };
            var ver = System.Text.RegularExpressions.Regex.Match(text, @"v?(\d+\.\d+(\.\d+)?)");
            if (ver.Success) d.Version = ver.Groups[1].Value;
            if (text.Length > 0 && !text.StartsWith("ConfuserEx", StringComparison.Ordinal) && !text.StartsWith("Confuser.Core", StringComparison.Ordinal)) d.Name = "ConfuserEx (çatal: " + text.Split(' ')[0] + ")";
            dets.Add(d);
        }
        else if (mod.GlobalType is { } gt && (gt.Methods.Count(m => Names.IsObfuscated(m.Name)) >= 3 || guards.Count > 0) && (decryptors.Any(m => m.DeclaringType.IsGlobalModuleType && m.HasGenericParameters) || guards.Count > 0))
        {
            dets.Add(new Det
            {
                Id = "confuserex", Name = "ConfuserEx (işaretsiz)", Kind = "obfuscator", Confidence = 0.6,
                Evidence = { $"<Module> içinde {gt.Methods.Count} yöntem, genel çözücü/koruma başlatıcısı" },
            });
        }

        // Eazfuscator.NET: "#=q" önekli adlar
        int eaz = allTypes.Count(t => t.Name.StartsWith("#=")) + methods.Count(m => m.Name.StartsWith("#="));
        if (eaz > 0) dets.Add(new Det { Id = "eazfuscator", Name = "Eazfuscator.NET", Kind = "obfuscator", Confidence = Math.Min(0.95, 0.6 + eaz / 50.0), Evidence = { $"{eaz} ad '#=' önekli" } });

        // .NET Reactor: kayıtsız sürüm filigranı ve tipik çalışma zamanı izleri
        var reactorWatermark = strings.FirstOrDefault(s => s.Contains(".NET Reactor", StringComparison.Ordinal));
        if (reactorWatermark != null || typeNames.Any(n => n.Contains("<PrivateImplementationDetails>{") && n.Contains("}.")) && pinvoke.Any(p => p.EndsWith("!VirtualProtect")))
        {
            var d = new Det { Id = "reactor", Name = ".NET Reactor", Kind = "protector", Decodable = false, Confidence = reactorWatermark != null ? 0.9 : 0.55 };
            if (reactorWatermark != null) d.Evidence.Add("filigran: " + JsonExt.Safe(reactorWatermark.Length > 120 ? reactorWatermark[..120] : reactorWatermark));
            else d.Evidence.Add("GUID'li <PrivateImplementationDetails> + VirtualProtect");
            dets.Add(d);
        }

        // Obfuscar: GUID'li <PrivateImplementationDetails>{...} dizge sınıfı
        var obfuscarType = allTypes.FirstOrDefault(t => Passes.GuidName.IsMatch(t.Name)
            && t.Methods.Count(m => m.IsStatic && m.MethodSig.Params.Count == 0 && m.MethodSig.RetType.ElementType == ElementType.String) >= 1);
        if (obfuscarType != null && dets.All(d => d.Id != "reactor"))
            dets.Add(new Det { Id = "obfuscar", Name = "Obfuscar", Kind = "obfuscator", Confidence = 0.8, Evidence = { $"dizge sınıfı: {obfuscarType.Name}" } });

        // ---- teknikler ----
        int obfNames = allTypes.Count(t => !t.IsGlobalModuleType && Names.IsObfuscated(t.Name)) + methods.Count(m => !m.IsRuntimeSpecialName && Names.IsObfuscated(m.Name));
        int totalNames = allTypes.Count + methods.Count;
        if (obfNames > 0)
            dets.Add(new Det { Id = "renaming", Name = "Ad gizleme", Kind = "technique", Confidence = Math.Min(0.95, 0.4 + (double)obfNames / Math.Max(1, totalNames)), Evidence = { $"{obfNames}/{totalNames} tür/yöntem adı okunamaz" } });
        int decryptCalls = methods.Where(m => m.HasBody).Sum(m => m.Body.Instructions.Count(i => i.OpCode.Code == Code.Call && Il.ResolveDef(i.Operand as IMethod) is { } d && decryptors.Contains(d)));
        if (decryptCalls > 0)
            dets.Add(new Det { Id = "string-encryption", Name = "Dizge/sabit şifreleme", Kind = "technique", Confidence = 0.85, Evidence = new[] { $"{decryptors.Count} çözücü, {decryptCalls} çağrı yeri" }.Concat(decryptors.Take(3).Select(d => "çözücü: " + Names.Show(d.DeclaringType.Name) + "::" + Names.Show(d.Name))).ToList() });
        if (proxies.Count > 0)
            dets.Add(new Det { Id = "proxy-calls", Name = "Vekil çağrılar", Kind = "technique", Confidence = 0.8, Evidence = { $"{proxies.Count} vekil yöntem" } });
        int delegateProxies = mod.GlobalType?.NestedTypes.Count(t => t.IsDelegate) ?? 0;
        if (delegateProxies + allTypes.Count(t => t.IsDelegate && Names.IsObfuscated(t.Name) && t.Fields.Count(f => f.IsStatic) > 0) >= 3)
            dets.Add(new Det { Id = "proxy-delegates", Name = "Temsilci tabanlı vekiller (strong)", Kind = "technique", Decodable = false, Confidence = 0.6, Evidence = { "statik temsilci alanlarıyla dolaylı çağrılar (çözülmez, raporlanır)" } });
        int flow = methods.Count(m => m.HasBody && ControlFlowPass.LooksFlattened(m));
        if (flow > 0)
            dets.Add(new Det { Id = "control-flow", Name = "Akış düzleştirme", Kind = "technique", Confidence = 0.75, Evidence = { $"{flow} yöntemde durum değişkenli switch dağıtıcı" } });
        foreach (var g in guards.GroupBy(g => g.Kind))
        {
            var (id, name) = g.Key switch
            {
                Guards.Kind.AntiTamper => ("anti-tamper", "Anti-tamper (şifreli yöntem gövdeleri)"),
                Guards.Kind.AntiDump => ("anti-dump", "Anti-dump"),
                _ => ("anti-debug", "Anti-debug"),
            };
            dets.Add(new Det { Id = id, Name = name, Kind = "technique", Decodable = g.Key != Guards.Kind.AntiTamper, Confidence = 0.85, Evidence = g.Select(x => "başlatıcı: " + Names.Show(x.Target.Name)).Take(3).ToList() });
        }
        if (badBodies > 0)
            dets.Add(new Det { Id = "invalid-bodies", Name = "Ayrıştırılamayan yöntem gövdeleri", Kind = "technique", Decodable = false, Confidence = 0.7, Evidence = { $"{badBodies} gövde okunamadı (şifreli ya da bozuk)" } });
        if (attrs.Any(a => a.TypeFullName == "System.Runtime.CompilerServices.SuppressIldasmAttribute"))
            dets.Add(new Det { Id = "suppress-ildasm", Name = "SuppressIldasm", Kind = "technique", Confidence = 0.99, Evidence = { "SuppressIldasmAttribute" } });
        var linked = mod.Resources.OfType<AssemblyLinkedResource>().Select(r => r.Name.String).ToList();
        if (memberRefs.ContainsKey("System.AppDomain::add_ResourceResolve") || (linked.Count > 0 && memberRefs.ContainsKey("System.AppDomain::add_AssemblyResolve")))
            dets.Add(new Det { Id = "resource-encryption", Name = "Şifreli kaynaklar", Kind = "technique", Confidence = 0.75,
                Evidence = new[] { "AppDomain çözümleme işleyicisi + Assembly.Load(byte[])" }.Concat(linked.Take(3).Select(n => "gizli derlemeye bağlı kaynak: " + n)).ToList() });
        var peResources = mod.Resources.OfType<EmbeddedResource>().Where(r => IsPe(r)).Select(r => r.Name.String).ToList();
        if (peResources.Count > 0)
            dets.Add(new Det { Id = "embedded-assemblies", Name = "Gömülü PE/derlemeler", Kind = "technique", Confidence = 0.9, Evidence = peResources.Take(5).Select(n => "kaynak: " + n).ToList() });
        // gizleyicinin çalışma zamanındaki yükleme (ConfuserEx resources) sayılmaz: yalnızca kullanıcı kodu
        if (userRefs.Keys.Any(k => k is "System.Reflection.Assembly::Load" or "System.AppDomain::Load") && (userRefs.ContainsKey("System.Reflection.MethodBase::Invoke") || userRefs.ContainsKey("System.Reflection.Assembly::get_EntryPoint")))
            dets.Add(new Det { Id = "reflective-load", Name = "Bellekten derleme yükleme", Kind = "technique", Confidence = 0.8, Evidence = { "Assembly.Load + EntryPoint/Invoke" } });

        return Json.Of(w =>
        {
            w.WriteStartObject();
            w.WriteBoolean("ok", true);
            w.WriteBoolean("net", true);
            w.WriteStartObject("info");
            w.WriteString("name", JsonExt.Safe(mod.Name));
            w.WriteString("assembly", JsonExt.Safe(mod.Assembly?.FullName ?? ""));
            w.WriteString("kind", mod.Kind switch { ModuleKind.Dll => "dll", ModuleKind.Windows => "winexe", ModuleKind.Console => "exe", _ => "netmodule" });
            w.WriteString("runtime", mod.RuntimeVersion);
            w.WriteString("framework", TargetFramework(mod) ?? "");
            w.WriteString("machine", mod.Machine.ToString());
            w.WriteBoolean("ilOnly", mod.IsILOnly);
            w.WriteBoolean("hasEntry", mod.EntryPoint != null);
            w.WriteNumber("types", allTypes.Count);
            w.WriteNumber("methods", methods.Count);
            w.WriteNumber("instructions", ilSize);
            w.WriteNumber("strings", strings.Count);
            w.WriteNumber("obfuscatedNames", obfNames);
            w.WriteEndObject();
            w.Strings("sections", sections);
            w.Strings("streams", streams);
            WriteDets(w, dets);
            w.Strings("strings", strings.Select(JsonExt.Safe));
            w.Strings("pinvoke", pinvoke.Select(JsonExt.Safe));
            w.WriteStartArray("refs");
            foreach (var (k, v) in userRefs.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                w.WriteStartArray();
                w.WriteStringValue(JsonExt.Safe(k));
                w.WriteNumberValue(v);
                w.WriteEndArray();
            }
            w.WriteEndArray();
            w.Strings("assemblyRefs", mod.GetAssemblyRefs().Select(a => JsonExt.Safe(a.FullName)));
            w.WriteStartArray("resources");
            foreach (var r in mod.Resources)
            {
                w.WriteStartObject();
                w.WriteString("name", JsonExt.Safe(r.Name));
                w.WriteNumber("size", r is EmbeddedResource er ? (long)er.Length : 0);
                w.WriteBoolean("pe", r is EmbeddedResource e2 && IsPe(e2));
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    static bool IsRuntimeOwner(TypeDef t)
    {
        while (t.DeclaringType != null) t = t.DeclaringType;
        return t.IsGlobalModuleType || Passes.GuidName.IsMatch(t.Name);
    }

    static bool IsPe(EmbeddedResource r)
    {
        try
        {
            var reader = r.CreateReader();
            return reader.Length > 0x80 && reader.ReadByte() == 'M' && reader.ReadByte() == 'Z';
        }
        catch { return false; }
    }

    static string? TargetFramework(ModuleDef mod)
    {
        var a = mod.Assembly?.CustomAttributes.FirstOrDefault(c => c.TypeFullName == "System.Runtime.Versioning.TargetFrameworkAttribute");
        return a?.ConstructorArguments.Count > 0 ? a.ConstructorArguments[0].Value?.ToString() : null;
    }

    /// <summary>Yerel (native) sarmalayıcılar: PE bölüm adlarından. Bunlar .NET düzeyinde çözülemez.</summary>
    static void NativeWrappers(List<string> sections, List<Det> dets, bool notNet)
    {
        void Add(string id, string name, string ev) => dets.Add(new Det { Id = id, Name = name, Kind = "protector", Decodable = false, Confidence = 0.85, Evidence = { ev } });
        if (sections.Any(s => s.StartsWith(".themida") || s.StartsWith(".winlice"))) Add("themida", "Themida / WinLicense", "PE bölümü: " + string.Join(", ", sections.Where(s => s.StartsWith(".themida") || s.StartsWith(".winlice"))));
        if (sections.Any(s => s.StartsWith(".vmp"))) Add("vmprotect-native", "VMProtect (yerel)", "PE bölümü: " + string.Join(", ", sections.Where(s => s.StartsWith(".vmp"))));
        if (sections.Any(s => s.StartsWith(".enigma"))) Add("enigma", "Enigma Protector", "PE bölümü: .enigma*");
        if (sections.Any(s => s.StartsWith("UPX"))) dets.Add(new Det { Id = "upx", Name = "UPX", Kind = "packer", Decodable = false, Confidence = 0.9, Evidence = { "PE bölümü: UPX*" } });
        if (notNet && sections.Any(s => s == ".managed" || s == "hydrated")) dets.Add(new Det { Id = "nativeaot", Name = "NativeAOT (yerel derlenmiş .NET)", Kind = "technique", Decodable = false, Confidence = 0.8, Evidence = { "PE bölümü: .managed/hydrated" } });
    }

    static void WriteDets(Utf8JsonWriter w, List<Det> dets)
    {
        w.WriteStartArray("detections");
        foreach (var d in dets)
        {
            w.WriteStartObject();
            w.WriteString("id", d.Id);
            w.WriteString("name", d.Name);
            w.WriteString("kind", d.Kind);
            w.WriteNumber("confidence", Math.Round(d.Confidence, 2));
            if (d.Version != null) w.WriteString("version", d.Version);
            w.WriteBoolean("decodable", d.Decodable);
            w.Strings("evidence", d.Evidence.Select(JsonExt.Safe));
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    static string Fail(string code, string message) => Json.Of(w =>
    {
        w.WriteStartObject();
        w.WriteBoolean("ok", true);
        w.WriteBoolean("net", false);
        w.WriteString("code", code);
        w.WriteString("error", message);
        w.WriteStartArray("detections");
        w.WriteEndArray();
        w.WriteEndObject();
    });
}
