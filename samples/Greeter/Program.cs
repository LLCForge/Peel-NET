using System;
using System.Text;

namespace Greeter
{
    internal static class Program
    {
        private static int Score(string name)
        {
            int total = 0;
            foreach (char c in name) total = (total * 31 + c) % 1000;
            return total;
        }

        private static string Banner(int width)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < width; i++) sb.Append(i % 2 == 0 ? '=' : '-');
            return sb.ToString();
        }

        private static void Main(string[] args)
        {
            string name = args.Length > 0 ? args[0] : "Peel";
            Console.WriteLine(Banner(12));
            Console.WriteLine("Hello, " + name + "!");
            Console.WriteLine("Score: " + Score(name));
            Console.WriteLine("Update channel: https://updates.example.com/greeter/stable");
            int level = Score(name) > 500 ? 3 : 1;
            switch (level)
            {
                case 1: Console.WriteLine("Level one"); break;
                case 3: Console.WriteLine("Level three"); break;
                default: Console.WriteLine("Unknown"); break;
            }
            Console.WriteLine(Banner(12));
        }
    }
}
