using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Peel;

/// <summary>Sandbox'ta yan etki kaydı. Çalıştırma kopyasında ağ, dosya, süreç, kayıt defteri, derleme yükleme gibi
/// çağrılar bu sınıfa yönlendirilir (Sandbox.Stubs): hiçbiri gerçekten yapılmaz, yalnızca kaydedilir.
/// Assembly.Load(byte[]) ile yüklenmek istenen derlemeler "katman" olarak saklanır.</summary>
public static class Fx
{
    public const int MaxCalls = 400;
    public const int MaxLayers = 8;
    public static readonly List<(string Api, string[] Args)> Calls = new();
    public static readonly List<byte[]> Layers = new();

    /// <summary>Örneğin yüklendiği toplanabilir bağlam: bellekten yüklenen katmanlar da (yönlendirilmiş kopya olarak) buraya yüklenir.</summary>
    public static System.Runtime.Loader.AssemblyLoadContext? Alc;
    static int depth;
    public static readonly List<System.Reflection.Assembly> LoadedLayers = new();

    public static void Reset()
    {
        Calls.Clear();
        Layers.Clear();
        LoadedLayers.Clear();
    }

    /// <summary>Çalıştırma kopyasındaki taslak metotların ortak giriş noktası. Dönüş: başvuru türü dönen API'ler için sahte değer
    /// (çoğunlukla null); değer türleri taslakta default(T) ile döner.</summary>
    public static object? Record(string api, string ret, object?[] args)
    {
        // kaynak akışı: yan etki değil; mono'nun ResourceResolve yolu (ConfuserEx resources) motoru çökerttiği için
        // ad önce derlemenin kendisinde, sonra bellekten yüklenen katmanlarda aranır
        if (api == "System.Reflection.Assembly::GetManifestResourceStream" && args.Length >= 2 && args[0] is System.Reflection.Assembly self)
        {
            string? name = args.Length == 3 && args[1] is Type t ? t.Namespace + "." + args[2] : args[1] as string;
            if (name == null) return null;
            // başka derlemeye bağlı (AssemblyLinked) kaynak mono'yu çökertir: yalnızca gömülü kaynaklar okunur
            foreach (var a in new[] { self }.Concat(LoadedLayers))
                if (a.GetManifestResourceNames().Contains(name) && a.GetManifestResourceInfo(name) is { } info
                    && (info.ResourceLocation & System.Reflection.ResourceLocation.Embedded) != 0 && info.ReferencedAssembly == null)
                    return a.GetManifestResourceStream(name);
            return null;
        }
        if (Calls.Count < MaxCalls)
        {
            var list = new string[args.Length];
            for (int i = 0; i < args.Length; i++) list[i] = Describe(args[i]);
            Calls.Add((api, list));
        }
        if (api.Contains("::Load", StringComparison.Ordinal) || api.EndsWith("::ExecuteAssembly", StringComparison.Ordinal))
        {
            byte[]? first = null;
            foreach (var a in args)
                if (a is byte[] b && b.Length > 0)
                {
                    first ??= b;
                    if (Layers.Count < MaxLayers) Layers.Add((byte[])b.Clone());
                }
            // iç katman gerçek hâliyle değil, kendi çalıştırma kopyasıyla (yan etkileri yönlendirilmiş) yüklenir
            if (first != null && api.EndsWith("::Load", StringComparison.Ordinal) && Alc != null && depth < 3)
            {
                depth++;
                try
                {
                    var copy = Sandbox.BuildCopy(first);
                    var loaded = Alc.LoadFromStream(new System.IO.MemoryStream(copy.Bytes));
                    LoadedLayers.Add(loaded);
                    return loaded;
                }
                catch (Exception e) when (e is not SandboxExitException)
                {
                    throw new BadImageFormatException("sandbox: katman yüklenemedi");
                }
                finally { depth--; }
            }
        }
        switch (api)
        {
            case "System.Environment::Exit":
            case "System.Environment::FailFast":
            case "System.Windows.Forms.Application::Exit":
            case "System.Diagnostics.Process::Kill":
                throw new SandboxExitException(api);
            case "System.Environment::get_MachineName":
                return "PEEL-SANDBOX";
            case "System.Environment::get_UserName":
                return "peel";
            case "System.Environment::get_UserDomainName":
                return "PEEL";
            case "System.Environment::GetFolderPath":
                return @"C:\Users\peel\" + (args.Length > 0 ? Describe(args[0]) : "folder");
            case "System.Environment::GetEnvironmentVariable":
                return null;
        }
        // kod ilerleyebilsin diye zararsız sahte değerler (dönüş türüne göre)
        return ret switch
        {
            "System.String" => "",
            "System.Byte[]" => Array.Empty<byte>(),
            "System.String[]" => Array.Empty<string>(),
            "System.IO.Stream" or "System.IO.FileStream" => new System.IO.MemoryStream(),
            "System.IO.StreamReader" => new System.IO.StreamReader(new System.IO.MemoryStream()),
            "System.IO.StreamWriter" => new System.IO.StreamWriter(new System.IO.MemoryStream()),
            "System.IO.TextReader" => new System.IO.StringReader(""),
            "System.IO.TextWriter" => System.IO.TextWriter.Null,
            "System.Object" => new object(),
            _ => null,
        };
    }

    /// <summary>Yönlendirilen kurucu (newobj). Yan etkili kurucular (dosya açan, bağlanan) sahte nesne döndürür; diğerleri
    /// için gerçek nesne oluşturulmaya çalışılır (çalışma zamanında yoksa ya da tarayıcıda desteklenmiyorsa boş nesne).</summary>
    public static object New(string api, string type, bool effectful, object?[] args)
    {
        Record(api, "", args);
        if (type == "System.IO.FileStream") return new System.IO.MemoryStream();
        if (!effectful)
        {
            try
            {
                var t = Type.GetType(type) ?? AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(type)).FirstOrDefault(x => x != null);
                if (t != null) return Activator.CreateInstance(t, args)!;
            }
            catch { }
        }
        return new Dummy();
    }

    /// <summary>Oluşturulamayan türlerin yerine geçen nesne (using/Dispose çağrıları çalışsın).</summary>
    public sealed class Dummy : IDisposable
    {
        public void Dispose() { }
    }

    public static string Describe(object? o)
    {
        switch (o)
        {
            case null: return "null";
            case string s: return s.Length > 300 ? s[..300] + "…" : s;
            case byte[] b: return $"byte[{b.Length}]";
            case char c: return c.ToString();
            case bool v: return v ? "true" : "false";
            case IFormattable f when o.GetType().IsPrimitive || o is Enum: return f.ToString(null, CultureInfo.InvariantCulture);
            case string[] arr:
                {
                    var sb = new StringBuilder("[");
                    for (int i = 0; i < arr.Length && i < 8; i++) sb.Append(i > 0 ? ", " : "").Append(Describe(arr[i]));
                    return sb.Append(']').ToString();
                }
            default:
                // nesnelerin ToString'i örnek kodunu çalıştırabilir: yalnızca tür adı
                return "<" + (o.GetType().FullName ?? "?") + ">";
        }
    }
}

/// <summary>Environment.Exit / FailFast sandbox'ta süreci bitirmez: bu istisna ile çalıştırma sonlanır.</summary>
public sealed class SandboxExitException : Exception
{
    public SandboxExitException(string api) : base("sandbox: " + api) { Api = api; }
    public string Api { get; }
}
