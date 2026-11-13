using System;
using System.IO;
using System.Net;
using System.Reflection;
using System.Text;

namespace Updater
{
    // Zararsız örnek: gerçek bir şey indirmez, kurmaz ya da değiştirmez. Adresler çözülemeyen ".invalid" alan adındadır.
    // Amacı raporun davranış (denenen ağ/dosya çağrıları), IOC ve kaynak bölümlerini sınamaktır.
    internal static class Program
    {
        private const string Channel = "https://updates.peel-demo.invalid/channel/stable.txt";

        private static string Decode(string b64)
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(b64));
        }

        private static string Motd()
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Updater.motd.txt"))
            {
                if (s == null) return "(motd missing)";
                using (var r = new StreamReader(s)) return r.ReadToEnd().Trim();
            }
        }

        private static string Fetch(string url)
        {
            try
            {
                using (var web = new WebClient())
                {
                    return web.DownloadString(url);
                }
            }
            catch (Exception e)
            {
                return "offline (" + e.GetType().Name + ")";
            }
        }

        private static int Checksum(string text)
        {
            unchecked
            {
                int h = 17;
                foreach (char c in text) h = h * 23 + c;
                return h & 0x7fffffff;
            }
        }

        private static void Main(string[] args)
        {
            Console.WriteLine(Motd());
            string config = Decode("Y29uZmlnOiBjaGFubmVsPXN0YWJsZTsgcmV0cmllcz0z");
            Console.WriteLine("Config -> " + config);
            string folder = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string marker = Path.Combine(folder, "PeelDemo", "installed.flag");
            Console.WriteLine(File.Exists(marker) ? "Already installed" : "Fresh install");
            string latest = Fetch(Channel);
            Console.WriteLine("Latest: " + (latest.Length == 0 ? "unknown" : latest));
            Console.WriteLine("Checksum: " + Checksum(config));
            for (int i = 0; i < 3; i++)
            {
                Console.WriteLine("Step " + (i + 1) + "/3 " + (i == 2 ? "done" : "..."));
            }
        }
    }
}
