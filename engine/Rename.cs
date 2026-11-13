using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;

namespace Peel;

/// <summary>Gizlenmiş adların okunur adlarla değiştirilmesi (de4dot tarzı: Class3, method_7, string_0 ...).
/// Sanal/soyut yöntemler, arayüz uygulamaları, P/Invoke ve özel adlar (.ctor, .cctor) korunur; bunların adı
/// dış türlerle bağlıdır. WinForms kaynakları (Tür.resources) tür adıyla birlikte yeniden adlandırılır.</summary>
static class RenamePass
{
    static string Hint(TypeSig? t)
    {
        if (t == null) return "obj";
        switch (t.ElementType)
        {
            case ElementType.String: return "string";
            case ElementType.Boolean: return "bool";
            case ElementType.I4: return "int";
            case ElementType.U4: return "uint";
            case ElementType.I8: return "long";
            case ElementType.U8: return "ulong";
            case ElementType.I2: return "short";
            case ElementType.U2: return "ushort";
            case ElementType.I1: return "sbyte";
            case ElementType.U1: return "byte";
            case ElementType.R4: return "float";
            case ElementType.R8: return "double";
            case ElementType.Char: return "char";
            case ElementType.Object: return "object";
            case ElementType.I: return "intptr";
            case ElementType.SZArray: return Hint(t.Next) + "Array";
        }
        var n = t.ToTypeDefOrRef()?.Name?.String;
        if (string.IsNullOrEmpty(n) || Names.IsObfuscated(n)) return "obj";
        int g = n.IndexOf('`');
        if (g > 0) n = n[..g];
        return char.ToLowerInvariant(n[0]) + n[1..];
    }

    static string TypeKind(TypeDef t)
    {
        if (t.IsInterface) return "IInterface";
        if (t.IsEnum) return "Enum";
        if (t.IsDelegate) return "Delegate";
        if (t.IsValueType) return "Struct";
        for (var b = t.BaseType; b != null;)
        {
            var name = b.FullName;
            if (name == "System.Exception") return "Exception";
            if (name == "System.Attribute") return "Attribute";
            if (name == "System.Windows.Forms.Form") return "Form";
            if (name == "System.Windows.Forms.UserControl") return "UserControl";
            if (name == "System.Object") break;
            var def = b.ResolveTypeDef();
            if (def == null) break;
            b = def.BaseType;
        }
        return "Class";
    }

    public static bool Run(PassContext ctx)
    {
        var mod = ctx.Module;
        bool shortToo = ctx.Flag("shortNames");
        bool Obf(string? n) => Names.IsObfuscated(n, shortToo);
        int types = 0, methods = 0, fields = 0, props = 0, events = 0, pars = 0, nss = 0;
        var nsMap = new Dictionary<string, string>();
        var kindCount = new Dictionary<string, int>();
        var resourceMoves = new List<(string From, TypeDef Type)>();

        foreach (var t in mod.GetTypes().ToList())
        {
            if (t.IsGlobalModuleType || t.IsRuntimeSpecialName) continue;
            var oldFull = t.FullName;
            if (!t.IsNested && !string.IsNullOrEmpty(t.Namespace) && t.Namespace.String.Split('.').Any(p => Obf(p)))
            {
                if (!nsMap.TryGetValue(t.Namespace, out var ns)) nsMap[t.Namespace] = ns = $"Namespace{nsMap.Count}";
                t.Namespace = ns;
                nss++;
            }
            if (Obf(t.Name))
            {
                var kind = TypeKind(t);
                int n = kindCount.GetValueOrDefault(kind);
                kindCount[kind] = n + 1;
                var gp = t.GenericParameters.Count;
                t.Name = kind + n + (gp > 0 ? "`" + gp : "");
                types++;
            }
            if (t.FullName != oldFull) resourceMoves.Add((oldFull, t));

            int mi = 0, fi = 0, pi = 0, ei = 0;
            foreach (var gp in t.GenericParameters) if (Obf(gp.Name)) gp.Name = "T" + gp.Number;
            foreach (var p in t.Properties)
            {
                if (!Obf(p.Name)) continue;
                var acc = p.GetMethods.Concat(p.SetMethods).ToList();
                if (acc.Any(a => a.IsVirtual)) continue;
                var name = "Prop" + pi++;
                p.Name = name;
                foreach (var g in p.GetMethods) g.Name = "get_" + name;
                foreach (var s in p.SetMethods) s.Name = "set_" + name;
                props++;
            }
            foreach (var e in t.Events)
            {
                if (!Obf(e.Name)) continue;
                var acc = new[] { e.AddMethod, e.RemoveMethod, e.InvokeMethod }.Where(a => a != null).ToList();
                if (acc.Any(a => a!.IsVirtual)) continue;
                var name = "Event" + ei++;
                e.Name = name;
                if (e.AddMethod != null) e.AddMethod.Name = "add_" + name;
                if (e.RemoveMethod != null) e.RemoveMethod.Name = "remove_" + name;
                events++;
            }
            foreach (var f in t.Fields)
            {
                if (!Obf(f.Name) || f.IsRuntimeSpecialName) continue;
                f.Name = t.IsEnum ? "Value" + fi++ : Hint(f.FieldType) + "_" + fi++;
                fields++;
            }
            foreach (var m in t.Methods)
            {
                foreach (var gp in m.GenericParameters) if (Obf(gp.Name)) gp.Name = "T" + gp.Number;
                foreach (var p in m.Parameters)
                {
                    if (p.IsHiddenThisParameter || (p.ParamDef != null && !Obf(p.Name))) continue;
                    // adı silinmiş parametreler (ParamDef yok) de adlandırılır; ILSpy aksi hâlde P_0 gösterir
                    if (p.ParamDef == null) p.CreateParamDef();
                    p.Name = Hint(p.Type) + "_" + p.MethodSigIndex;
                    pars++;
                }
                if (!Obf(m.Name) || m.IsRuntimeSpecialName || m.IsVirtual || m.IsAbstract || m.ImplMap != null || m.Overrides.Count > 0) continue;
                if (m.IsSpecialName && (m.Name.StartsWith("get_") || m.Name.StartsWith("set_") || m.Name.StartsWith("add_") || m.Name.StartsWith("remove_"))) continue;
                m.Name = m == mod.EntryPoint ? "Main" : "method_" + mi++;
                methods++;
            }
        }
        // WinForms/ResourceManager: "Ad.Alanı.Tür.resources" kaynakları türle birlikte taşınır
        foreach (var (from, t) in resourceMoves)
            foreach (var r in mod.Resources)
                if (r.Name == from + ".resources") { r.Name = t.FullName + ".resources"; ctx.Log.Add($"kaynak yeniden adlandırıldı: {r.Name}"); }

        ctx.Bump("types", types);
        ctx.Bump("methods", methods);
        ctx.Bump("fields", fields);
        ctx.Bump("properties", props);
        ctx.Bump("events", events);
        ctx.Bump("parameters", pars);
        ctx.Bump("namespaces", nss);
        int total = types + methods + fields + props + events + pars + nss;
        if (total > 0) ctx.Log.Add($"{total} ad yeniden adlandırıldı");
        return total > 0;
    }
}
