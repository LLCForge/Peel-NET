using System;
using System.Text;

namespace MathKit
{
    // Kütüphane (DLL) örneği: giriş noktası yoktur. Davranış doğrulaması statik yöntemlerin sabit argümanlarla
    // yoklanmasıyla yapılır (Runner.Probes).
    public static class Kit
    {
        private static readonly string Alphabet = "abcdefghijklmnopqrstuvwxyz";

        public static long Fibonacci(int n)
        {
            if (n < 0) throw new ArgumentOutOfRangeException(nameof(n), "negative input");
            long a = 0, b = 1;
            for (int i = 0; i < n; i++)
            {
                long t = a + b;
                a = b;
                b = t;
            }
            return a;
        }

        public static bool IsPrime(int n)
        {
            if (n < 2) return false;
            for (int d = 2; (long)d * d <= n; d++)
                if (n % d == 0) return false;
            return true;
        }

        public static string Caesar(string text, int shift)
        {
            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                int idx = Alphabet.IndexOf(char.ToLowerInvariant(c));
                if (idx < 0) { sb.Append(c); continue; }
                char shifted = Alphabet[((idx + shift) % 26 + 26) % 26];
                sb.Append(char.IsUpper(c) ? char.ToUpperInvariant(shifted) : shifted);
            }
            return sb.ToString();
        }

        public static string Describe(int value)
        {
            if (value == 0) return "zero";
            return (value > 0 ? "positive " : "negative ") + (IsPrime(Math.Abs(value)) ? "prime" : "number") + " (" + Fibonacci(Math.Abs(value) % 20) + ")";
        }

        public static int Checksum(string text)
        {
            unchecked
            {
                uint h = 2166136261;
                foreach (char c in text) { h ^= c; h *= 16777619; }
                return (int)(h & 0x7fffffff);
            }
        }
    }
}
