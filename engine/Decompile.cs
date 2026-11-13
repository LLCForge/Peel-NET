using System;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Disassembler;
using ICSharpCode.Decompiler.Metadata;

namespace Peel;

/// <summary>ICSharpCode.Decompiler (ILSpy motoru, MIT) ile C# ya da IL çıktısı. Başvurulan derlemeler (mscorlib, System ...)
/// tarayıcıda dosya olarak yoktur: çalışan .NET çalışma zamanının kendi derlemelerinin metadata'sı verilir (yönlendirme
/// derlemeleri dahil); bulunamayanlar için ayrıştırıcı adları metadata'dan yazar.</summary>
public static class Decompile
{
    sealed class RuntimeResolver : IAssemblyResolver
    {
        static readonly System.Collections.Generic.Dictionary<string, MetadataFile?> Cache = new(StringComparer.OrdinalIgnoreCase);

        public unsafe MetadataFile? Resolve(IAssemblyReference reference)
        {
            if (Cache.TryGetValue(reference.Name, out var f)) return f;
            f = null;
            try
            {
                var asm = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyName(new System.Reflection.AssemblyName(reference.Name));
                if (asm.TryGetRawMetadata(out byte* blob, out int length))
                {
                    var image = System.Collections.Immutable.ImmutableArray.Create(new ReadOnlySpan<byte>(blob, length).ToArray());
                    f = new MetadataFile(MetadataFile.MetadataFileKind.Metadata, reference.Name + ".dll", MetadataReaderProvider.FromMetadataImage(image));
                }
            }
            catch { }
            return Cache[reference.Name] = f;
        }

        public MetadataFile? ResolveModule(MetadataFile mainModule, string moduleName) => null;
        public Task<MetadataFile?> ResolveAsync(IAssemblyReference reference) => Task.FromResult(Resolve(reference));
        public Task<MetadataFile?> ResolveModuleAsync(MetadataFile mainModule, string moduleName) => Task.FromResult<MetadataFile?>(null);
    }

    public static string CSharp(byte[] bytes, string name)
    {
        var file = new PEFile(name, new MemoryStream(bytes), PEStreamOptions.PrefetchEntireImage);
        var settings = new DecompilerSettings(LanguageVersion.Latest)
        {
            ThrowOnAssemblyResolveErrors = false,
            LoadInMemory = true,
            ShowXmlDocumentation = false,
            UseSdkStyleProjectFormat = false,
            RemoveDeadCode = true,
            RemoveDeadStores = false,
        };
        var dec = new CSharpDecompiler(file, new RuntimeResolver(), settings);
        // önce kullanıcı türleri, en sonda <Module> (gizleyicilerin çalışma zamanı çoğunlukla oradadır)
        var md = file.Metadata;
        var types = md.TypeDefinitions.Where(h => md.GetTypeDefinition(h).GetDeclaringType().IsNil).ToList();
        var global = types.Where(h => md.GetString(md.GetTypeDefinition(h).Name) == "<Module>").ToList();
        var ordered = types.Except(global).Concat(global).ToList();
        try
        {
            var head = dec.DecompileModuleAndAssemblyAttributesToString();
            return head + Environment.NewLine + dec.DecompileTypesAsString(ordered);
        }
        catch
        {
            return dec.DecompileWholeModuleAsString();
        }
    }

    public static string IL(byte[] bytes, string name)
    {
        var file = new PEFile(name, new MemoryStream(bytes), PEStreamOptions.PrefetchEntireImage);
        var output = new PlainTextOutput();
        var dis = new ReflectionDisassembler(output, CancellationToken.None) { DetectControlStructure = true, ShowSequencePoints = false };
        dis.WriteModuleContents(file);
        return output.ToString();
    }
}
