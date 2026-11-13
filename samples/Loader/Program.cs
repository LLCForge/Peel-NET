using System;
using System.IO;
using System.Reflection;

namespace Loader
{
    // Katman örneği: gömülü kaynakta tek baytlık XOR ile saklanmış zararsız bir derleme (Greeter) vardır. Program onu
    // çözüp bellekten yükler ve giriş noktasını çağırır. Peel sandbox'ı Assembly.Load(byte[]) çağrısını yakalar ve iç
    // derlemeyi ayrı bir katman olarak raporlar (gerçekte yüklenmez).
    internal static class Program
    {
        private static byte[] Payload()
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Loader.payload.bin"))
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                byte[] data = ms.ToArray();
                for (int i = 0; i < data.Length; i++) data[i] ^= 0x5A;
                return data;
            }
        }

        private static void Main(string[] args)
        {
            Console.WriteLine("Loader: unpacking stage 2");
            byte[] stage = Payload();
            Console.WriteLine("Stage size: " + stage.Length);
            Assembly asm = Assembly.Load(stage);
            if (asm == null)
            {
                Console.WriteLine("Stage 2 not loaded");
                return;
            }
            asm.EntryPoint.Invoke(null, new object[] { new string[0] });
        }
    }
}
