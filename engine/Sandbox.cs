using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text;
using System.Threading.Tasks;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;
using TypeAttributes = dnlib.DotNet.TypeAttributes;
using MethodAttributes = dnlib.DotNet.MethodAttributes;
using MethodImplAttributes = dnlib.DotNet.MethodImplAttributes;

namespace Peel;

/// <summary>
/// Örnek kodun çalıştırılması. Kod hiçbir zaman olduğu gibi çalışmaz: önce bir "çalıştırma kopyası" üretilir
/// (Watch listesindeki API çağrıları Peel.Fx'e yönlendiren taslaklarla değiştirilir, anti-debug çağrıları kesilir,
/// istenirse çözücü yöntemlere trambolin eklenir), sonra toplanabilir bir AssemblyLoadContext'e yüklenir.
/// Bunun dışındaki yalıtım katmanları: .NET WebAssembly çalışma zamanı (gerçek dosya sistemi ve P/Invoke yok),
/// tarayıcıda Web Worker + CSP + JS kilidi, Node'da izin modeliyle başlatılan alt süreç ve bekçi (zaman aşımı).
/// </summary>
public static class Sandbox
{
    public const string StubType = "PeelSandboxStubs";
    public const string TrampType = "PeelTramp";

    public static ModuleDefMD Load(byte[] data)
    {
        var asmResolver = new NullResolver();
        var ctx = new ModuleContext(asmResolver, new Resolver(asmResolver));
        return ModuleDefMD.Load(data, new ModuleCreationOptions(ctx) { TryToLoadPdbFromDisk = false });
    }

    /// <summary>Modülü baytlara yazar. preserve: belirteçler (token) korunur, böylece yoklama sonuçları geçişler arasında karşılaştırılabilir.</summary>
    public static byte[] Write(ModuleDef m, bool preserve)
    {
        using var ms = new MemoryStream();
        if (m is ModuleDefMD md && !md.IsILOnly)
        {
            var o = new NativeModuleWriterOptions(md, optimizeImageSize: false);
            if (preserve) o.MetadataOptions.Flags |= MetadataFlags.PreserveAll;
            o.MetadataOptions.Flags |= MetadataFlags.KeepOldMaxStack;
            md.NativeWrite(ms, o);
        }
        else
        {
            var o = new ModuleWriterOptions(m);
            if (preserve) o.MetadataOptions.Flags |= MetadataFlags.PreserveAll;
            o.MetadataOptions.Flags |= MetadataFlags.KeepOldMaxStack;
            m.Write(ms, o);
        }
        return ms.ToArray();
    }

    public sealed class Copy
    {
        public byte[] Bytes = Array.Empty<byte>();
        public int Redirected;
        public List<string> Neutralized = new();
        /// <summary>trambolin adı → (özgün yöntem anahtarı, trambolini barındıran türün belirteci ya da null = PeelTramp)</summary>
        public Dictionary<string, (string Key, int? Home)> Tramps = new();

        public System.Reflection.MethodInfo? Find(Assembly asm, string name)
        {
            const BindingFlags all = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            var (_, home) = Tramps[name];
            var type = home is int tok ? asm.ManifestModule.ResolveType(tok) : asm.GetType(TrampType);
            return type?.GetMethod(name, all);
        }
    }

    /// <summary>Çalıştırma kopyası. tramps: çağrılacak çözücüler (anahtar → IMethod; MethodSpec genel örnekleme taşır).</summary>
    public static Copy BuildCopy(byte[] input, Func<ModuleDefMD, IEnumerable<(string Key, IMethod Method)>>? tramps = null, bool neutralize = true)
    {
        var mod = Load(input);
        var copy = new Copy();
        var corlib = mod.CorLibTypes;
        var engineRef = new AssemblyRefUser("PeelNet.Engine", new Version(0, 0, 0, 0));
        var fxType = new TypeRefUser(mod, "Peel", "Fx", engineRef);
        var record = new MemberRefUser(mod, "Record",
            MethodSig.CreateStatic(corlib.Object, corlib.String, corlib.String, new SZArraySig(corlib.Object)), fxType);
        var create = new MemberRefUser(mod, "New",
            MethodSig.CreateStatic(corlib.Object, corlib.String, corlib.String, corlib.Boolean, new SZArraySig(corlib.Object)), fxType);

        var tramp = tramps?.Invoke(mod).ToList();
        var holder = new TypeDefUser("", StubType, corlib.Object.TypeDefOrRef)
        {
            Attributes = TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class,
        };
        mod.Types.Add(holder);
        var stubs = new Dictionary<string, MethodDef?>();

        if (neutralize) copy.Neutralized.AddRange(Guards.Neutralize(mod, keepTamper: true));

        foreach (var type in mod.GetTypes().ToList())
        {
            if (type == holder) continue;
            foreach (var m in type.Methods)
            {
                if (!m.HasBody) continue;
                var body = m.Body;
                // yönlendirilen türlerin yerel değişkenleri object olur (tarayıcı çalışma zamanında olmayan türler yüklenmesin)
                foreach (var loc in body.Variables)
                    if (loc.Type is ClassSig cs && Watch.IsWatchedType(Watch.TypeName(cs.TypeDefOrRef))) loc.Type = corlib.Object;
                var ins = body.Instructions;
                for (int k = 0; k < ins.Count; k++)
                {
                    var target = Il.CallTarget(ins[k]);
                    if (target == null) continue;
                    if (k > 0 && ins[k - 1].OpCode.Code is Code.Constrained or Code.Tailcall) continue;
                    var api = Watch.Api(target);
                    if (api == null) continue;
                    bool isNew = ins[k].OpCode.Code == Code.Newobj;
                    var key = (isNew ? "new " : "") + target.FullName;
                    if (!stubs.TryGetValue(key, out var stub))
                    {
                        stub = MakeStub(mod, target, isNew, api, isNew ? create : record, $"s{stubs.Count}");
                        stubs[key] = stub;
                        if (stub != null) holder.Methods.Add(stub);
                    }
                    if (stub == null) continue;
                    ins[k].OpCode = OpCodes.Call;
                    ins[k].Operand = stub;
                    copy.Redirected++;
                }
            }
        }

        if (tramp is { Count: > 0 })
        {
            var tt = new TypeDefUser("", TrampType, corlib.Object.TypeDefOrRef)
            {
                Attributes = TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.Class,
            };
            mod.Types.Add(tt);
            int n = 0;
            foreach (var (key, target) in tramp)
            {
                var t = MakeTramp(mod, target, $"PeelTramp_{n++}");
                if (t == null) continue;
                // özel (private/protected) hedefe yalnızca kendi türünden erişilebilir
                var decl = Il.ResolveDef(target)?.DeclaringType;
                var home = decl != null && !decl.IsGlobalModuleType && !decl.HasGenericParameters && Il.ResolveDef(target)!.Access is MethodAttributes.Private or MethodAttributes.Family or MethodAttributes.FamANDAssem ? decl : tt;
                home.Methods.Add(t);
                copy.Tramps[t.Name] = (key, home == tt ? null : home.MDToken.ToInt32());
            }
        }
        // belirteçler korunur: yoklama sonuçları özgün ve temizlenmiş modül arasında belirteçle eşleşir
        try { copy.Bytes = Write(mod, preserve: true); }
        catch { copy.Bytes = Write(mod, preserve: false); }
        return copy;
    }

    static bool HasGenericVar(TypeSig? t)
    {
        for (; t != null; t = t.Next)
        {
            if (t.ElementType is ElementType.Var or ElementType.MVar) return true;
            if (t is GenericInstSig g && g.GenericArguments.Any(HasGenericVar)) return true;
        }
        return false;
    }

    static bool IsValue(TypeSig t) => t.IsValueType && t.ElementType != ElementType.ByRef;

    static MethodDef? MakeStub(ModuleDef mod, IMethod target, bool isNew, string api, IMethod record, string name)
    {
        var sig = target.MethodSig;
        if (sig == null || target is MethodSpec || sig.Generic || sig.Params.Any(HasGenericVar) || HasGenericVar(sig.RetType)) return null;
        var corlib = mod.CorLibTypes;
        var ps = new List<TypeSig>();
        if (sig.HasThis && !isNew) ps.Add(corlib.Object);
        foreach (var p in sig.Params) ps.Add(p.ElementType == ElementType.ByRef || p.IsPointer ? p : IsValue(p) ? p : corlib.Object);
        var retVoid = sig.RetType.ElementType == ElementType.Void;
        TypeSig ret = isNew ? corlib.Object : retVoid ? corlib.Void : IsValue(sig.RetType) ? sig.RetType : corlib.Object;

        var m = new MethodDefUser(name, MethodSig.CreateStatic(ret, ps.ToArray()),
            MethodImplAttributes.IL | MethodImplAttributes.Managed | MethodImplAttributes.NoInlining,
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig);
        var body = new CilBody { InitLocals = true };
        m.Body = body;
        var I = body.Instructions;
        I.Add(Instruction.Create(OpCodes.Ldstr, api));
        if (!isNew) I.Add(Instruction.Create(OpCodes.Ldstr, sig.RetType.FullName));
        else
        {
            I.Add(Instruction.Create(OpCodes.Ldstr, Watch.TypeName(target.DeclaringType)));
            I.Add(Instruction.Create(Watch.EffectfulCtor(target) ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0));
        }
        I.Add(Instruction.Create(OpCodes.Ldc_I4, ps.Count));
        I.Add(Instruction.Create(OpCodes.Newarr, corlib.Object.TypeDefOrRef));
        for (int i = 0; i < ps.Count; i++)
        {
            I.Add(Instruction.Create(OpCodes.Dup));
            I.Add(Instruction.Create(OpCodes.Ldc_I4, i));
            if (ps[i].ElementType == ElementType.ByRef || ps[i].IsPointer) I.Add(Instruction.Create(OpCodes.Ldnull));
            else
            {
                I.Add(Instruction.Create(OpCodes.Ldarg, m.Parameters[i]));
                if (IsValue(ps[i])) I.Add(Instruction.Create(OpCodes.Box, ps[i].ToTypeDefOrRef()));
            }
            I.Add(Instruction.Create(OpCodes.Stelem_Ref));
        }
        I.Add(Instruction.Create(OpCodes.Call, record));
        if (ret.ElementType == ElementType.Void)
        {
            I.Add(Instruction.Create(OpCodes.Pop));
        }
        else if (IsValue(ret))
        {
            I.Add(Instruction.Create(OpCodes.Pop));
            var tmp = new Local(ret);
            body.Variables.Add(tmp);
            I.Add(Instruction.Create(OpCodes.Ldloca, tmp));
            I.Add(Instruction.Create(OpCodes.Initobj, ret.ToTypeDefOrRef()));
            I.Add(Instruction.Create(OpCodes.Ldloc, tmp));
        }
        I.Add(Instruction.Create(OpCodes.Ret));
        return m;
    }

    /// <summary>Çözücüyü aynı derlemenin içinden çağıran trambolin (Assembly.GetCallingAssembly denetimleri geçer).
    /// Dönüş object (değer türleri kutulanır).</summary>
    static MethodDef? MakeTramp(ModuleDef mod, IMethod target, string name)
    {
        var sig = target.MethodSig;
        if (sig == null || sig.HasThis || sig.Params.Any(HasGenericVar)) return null;
        var ret = sig.RetType;
        if (target is MethodSpec spec && ret is GenericMVar mv && spec.GenericInstMethodSig is { } gi && mv.Number < gi.GenericArguments.Count)
            ret = gi.GenericArguments[(int)mv.Number];
        else if (HasGenericVar(ret)) return null;
        if (ret.ElementType == ElementType.Void) return null;
        var corlib = mod.CorLibTypes;
        var m = new MethodDefUser(name, MethodSig.CreateStatic(corlib.Object, sig.Params.ToArray()),
            MethodImplAttributes.IL | MethodImplAttributes.Managed | MethodImplAttributes.NoInlining,
            MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig);
        var body = new CilBody();
        m.Body = body;
        for (int i = 0; i < sig.Params.Count; i++) body.Instructions.Add(Instruction.Create(OpCodes.Ldarg, m.Parameters[i]));
        body.Instructions.Add(Instruction.Create(OpCodes.Call, target));
        if (ret.IsValueType) body.Instructions.Add(Instruction.Create(OpCodes.Box, ret.ToTypeDefOrRef()));
        body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        return m;
    }

    // ---- çalıştırma ----

    public sealed class Loaded : IDisposable
    {
        public AssemblyLoadContext Alc = null!;
        public Assembly Asm = null!;
        public string? InitError;
        public void Dispose()
        {
            try { Alc.Unload(); } catch { }
        }
    }

    public static Loaded LoadCopy(byte[] bytes, bool runModuleInit)
    {
        var alc = new AssemblyLoadContext("peel-sample", isCollectible: true);
        Fx.Alc = alc;
        var l = new Loaded { Alc = alc, Asm = alc.LoadFromStream(new MemoryStream(bytes)) };
        if (runModuleInit)
        {
            try { RuntimeHelpers.RunModuleConstructor(l.Asm.ManifestModule.ModuleHandle); }
            catch (Exception e) { l.InitError = Describe(e); }
        }
        return l;
    }

    public static Exception Unwrap(Exception e)
    {
        while (e is TargetInvocationException or TypeInitializationException or AggregateException && e.InnerException != null) e = e.InnerException!;
        return e;
    }

    public static string Describe(Exception e)
    {
        e = Unwrap(e);
        var msg = e.Message;
        if (msg.Length > 200) msg = msg[..200];
        return e.GetType().FullName + ": " + msg;
    }

    sealed class Bounded : StringWriter
    {
        const int Max = 64 * 1024;
        public override void Write(char value) { if (GetStringBuilder().Length < Max) base.Write(value); }
        public override void Write(string? value)
        {
            if (value == null) return;
            var sb = GetStringBuilder();
            if (sb.Length >= Max) return;
            base.Write(value.Length + sb.Length > Max ? value[..(Max - sb.Length)] : value);
        }
        public override void Write(char[] buffer, int index, int count) => Write(new string(buffer, index, count));
    }

    public sealed class Captured
    {
        public string Out = "";
        public string? Exception;
        public bool Exited;
    }

    /// <summary>Konsol çıktısı yakalanarak çalıştırır. Girdi boştur.</summary>
    public static Captured Capture(Action run)
    {
        var o = Console.Out;
        var e = Console.Error;
        var w = new Bounded();
        var r = new Captured();
        Console.SetOut(w);
        Console.SetError(w);
        Console.SetIn(new StringReader(""));
        try { run(); }
        catch (Exception ex)
        {
            var u = Unwrap(ex);
            if (u is SandboxExitException se) r.Exited = true;
            else r.Exception = Describe(u);
        }
        finally
        {
            Console.Out.Flush();
            Console.SetOut(o);
            Console.SetError(e);
        }
        r.Out = w.ToString();
        return r;
    }

    public static void InvokeEntry(Assembly asm)
    {
        var ep = asm.EntryPoint ?? throw new InvalidOperationException("giriş noktası yok");
        var args = ep.GetParameters().Length == 0 ? null : new object[] { Array.Empty<string>() };
        var result = ep.Invoke(null, args);
        if (result is Task t)
        {
            if (!t.IsCompleted) throw new InvalidOperationException("async Main tek iş parçacıklı sandbox'ta tamamlanamadı");
            t.GetAwaiter().GetResult();
        }
    }

    /// <summary>Çağrı argümanını hedef parametre türüne denetimsiz (IL anlamında) çevirir.</summary>
    public static object? Coerce(object? v, Type t)
    {
        if (v == null) return null;
        if (t.IsInstanceOfType(v)) return v;
        unchecked
        {
            long l = v switch { int i => i, long x => x, _ => 0 };
            if (v is double d)
            {
                if (t == typeof(float)) return (float)d;
                return Convert.ChangeType(d, t);
            }
            if (t == typeof(int)) return (int)l;
            if (t == typeof(uint)) return (uint)l;
            if (t == typeof(long)) return l;
            if (t == typeof(ulong)) return (ulong)l;
            if (t == typeof(short)) return (short)l;
            if (t == typeof(ushort)) return (ushort)l;
            if (t == typeof(sbyte)) return (sbyte)l;
            if (t == typeof(byte)) return (byte)l;
            if (t == typeof(char)) return (char)l;
            if (t == typeof(bool)) return l != 0;
            if (t == typeof(double)) return (double)l;
            if (t == typeof(float)) return (float)l;
            if (t.IsEnum) return Enum.ToObject(t, l);
        }
        return v;
    }
}

/// <summary>Tarayıcıda dosya sistemi yok: derleme çözümü her zaman başarısız olur (dnlib bunu tolere eder).</summary>
sealed class NullResolver : IAssemblyResolver
{
    public AssemblyDef? Resolve(IAssembly assembly, ModuleDef sourceModule) => null;
}
