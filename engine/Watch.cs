using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;

namespace Peel;

/// <summary>Sandbox'ta gerçek yerine kayda yönlendirilen API'ler. Tür listesindekilerin tüm üyeleri (kurucu dahil),
/// yöntem listesindekilerin yalnızca o yöntemleri yönlendirilir. P/Invoke (DllImport) yöntemleri her zaman yönlendirilir.</summary>
public static class Watch
{
    static readonly string[] NamespacePrefixes =
    {
        "System.Net.Sockets", "System.Net.Http", "System.Net.Mail", "System.Net.WebSockets", "System.Net.NetworkInformation",
        "Microsoft.Win32", "System.Management", "System.ServiceProcess", "System.DirectoryServices",
    };

    static readonly HashSet<string> Types = new(StringComparer.Ordinal)
    {
        "System.Net.WebClient", "System.Net.WebRequest", "System.Net.HttpWebRequest", "System.Net.FtpWebRequest",
        "System.Net.WebResponse", "System.Net.HttpWebResponse", "System.Net.Dns", "System.Net.HttpListener",
        "System.Net.ServicePointManager", "System.Net.WebProxy", "System.Net.CredentialCache",
        "System.Diagnostics.Process", "System.Diagnostics.ProcessStartInfo", "System.Diagnostics.Debugger",
        "System.Diagnostics.EventLog",
        "System.IO.File", "System.IO.Directory", "System.IO.FileInfo", "System.IO.DirectoryInfo", "System.IO.DriveInfo",
        "System.IO.FileStream", "System.IO.FileSystemWatcher", "System.IO.Pipes.NamedPipeClientStream",
        "System.IO.Pipes.NamedPipeServerStream",
        "System.Security.Cryptography.ProtectedData",
        "System.Windows.Forms.MessageBox", "System.Windows.Forms.Application", "System.Windows.Forms.Clipboard",
        "System.Windows.Forms.SendKeys", "System.Windows.Forms.Screen", "System.Windows.Clipboard", "System.Windows.MessageBox",
        "System.Drawing.Graphics",
    };

    static readonly HashSet<string> Methods = new(StringComparer.Ordinal)
    {
        "System.Environment::Exit", "System.Environment::FailFast", "System.Environment::GetEnvironmentVariable",
        "System.Environment::GetEnvironmentVariables", "System.Environment::SetEnvironmentVariable",
        "System.Environment::get_MachineName", "System.Environment::get_UserName", "System.Environment::get_UserDomainName",
        "System.Environment::GetFolderPath", "System.Environment::GetLogicalDrives", "System.Environment::get_CommandLine",
        "System.Reflection.Assembly::LoadFile", "System.Reflection.Assembly::LoadFrom", "System.Reflection.Assembly::UnsafeLoadFrom",
        "System.AppDomain::ExecuteAssembly",
        "System.Threading.Thread::Sleep", "System.Threading.Thread::Start",
        "System.Console::ReadLine", "System.Console::ReadKey", "System.Console::Read",
        "System.Runtime.InteropServices.Marshal::GetDelegateForFunctionPointer",
        "System.Reflection.Assembly::GetManifestResourceStream",
    };

    /// <summary>Yalnızca ilk parametresi byte[] olan aşırı yüklemeler (bellekten derleme yükleme: katman yakalama).</summary>
    static readonly HashSet<string> ByteLoads = new(StringComparer.Ordinal) { "System.Reflection.Assembly::Load", "System.AppDomain::Load" };

    public static string TypeName(ITypeDefOrRef? t)
    {
        if (t == null) return "";
        if (t is TypeSpec ts && ts.TypeSig is GenericInstSig g) t = g.GenericType.TypeDefOrRef;
        var full = t.FullName;
        int lt = full.IndexOf('`');
        return lt > 0 ? full[..lt] : full;
    }

    public static bool IsWatchedType(string full)
    {
        if (Types.Contains(full)) return true;
        foreach (var p in NamespacePrefixes)
            if (full.StartsWith(p, StringComparison.Ordinal) && (full.Length == p.Length || full[p.Length] == '.')) return true;
        return false;
    }

    /// <summary>Tarayıcı çalışma zamanında bulunmayan türler: kurucuları da yönlendirilir (gerçek nesne oluşturulamaz).</summary>
    static readonly string[] MissingPrefixes = { "Microsoft.Win32", "System.Management", "System.Windows", "System.DirectoryServices", "System.ServiceProcess", "System.Drawing" };

    /// <summary>Kurucusu yan etkili olan türler (dosya açar, bağlanır). Diğer izlenen türlerin kurucusu gerçek çalışır:
    /// nesne gerçek olur, yalnızca etkili üyeleri yönlendirilir (sahte nesne üzerinde arayüz çağrısı motoru çökertir).</summary>
    static readonly HashSet<string> EffectfulCtors = new(StringComparer.Ordinal)
    {
        "System.IO.FileStream", "System.IO.FileSystemWatcher", "System.Net.Sockets.TcpClient", "System.Net.Sockets.UdpClient",
        "System.Net.HttpListener", "System.IO.Pipes.NamedPipeClientStream", "System.IO.Pipes.NamedPipeServerStream",
    };

    public static bool EffectfulCtor(IMethod ctor)
    {
        var type = TypeName(ctor.DeclaringType);
        return MissingPrefixes.Any(p => type.StartsWith(p, StringComparison.Ordinal)) || (EffectfulCtors.Contains(type) && ctor.MethodSig.Params.Count > 0);
    }

    /// <summary>Yönlendirilecekse API kimliği ("Ad.Alanı.Tür::Yöntem" ya da "dll!İşlev"), değilse null.</summary>
    public static string? Api(IMethod m)
    {
        if (m is MethodDef def)
        {
            if (def.ImplMap != null) return $"{def.ImplMap.Module?.Name ?? "?"}!{def.ImplMap.Name}";
            return null; // modülün kendi kodu
        }
        if (m is not MemberRef mr || !mr.IsMethodRef) return null;
        var type = TypeName(mr.DeclaringType);
        var id = type + "::" + mr.Name;
        if (IsWatchedType(type) || Methods.Contains(id)) return id;
        if (ByteLoads.Contains(id) && mr.MethodSig.Params.Count >= 1 && mr.MethodSig.Params[0] is SZArraySig a && a.Next.ElementType == ElementType.U1) return id;
        return null;
    }

    /// <summary>Raporda davranış sınıfı.</summary>
    public static string Category(string api)
    {
        if (api.Contains('!')) return "native";
        if (api.StartsWith("System.Net", StringComparison.Ordinal)) return "network";
        if (api.StartsWith("System.Diagnostics.Process", StringComparison.Ordinal)) return "process";
        if (api.StartsWith("System.IO.", StringComparison.Ordinal)) return "file";
        if (api.StartsWith("Microsoft.Win32", StringComparison.Ordinal)) return "registry";
        if (api.Contains("::Load", StringComparison.Ordinal) || api.Contains("ExecuteAssembly", StringComparison.Ordinal)) return "load";
        if (api.StartsWith("System.Diagnostics.Debugger", StringComparison.Ordinal)) return "antidebug";
        return "other";
    }
}
