using System;

[assembly: SmartAssembly.Attributes.PoweredBy("Peel-NET detection fixture")]
[assembly: BabelObfuscator]

// Yalnızca tespit sınaması: ticari korumaların bıraktığı işaret özniteliklerinin ADLARI (boş sınıflar). Bu derleme
// hiçbir şekilde korunmuş değildir; Peel bunları tanımalı, "çözülemez (yalnızca rapor)" olarak işaretlemeli ve
// içeriğine dokunmadan bırakmalıdır.
namespace SmartAssembly.Attributes
{
    [AttributeUsage(AttributeTargets.Assembly)]
    public sealed class PoweredByAttribute : Attribute
    {
        public PoweredByAttribute(string s) { }
    }
}

[AttributeUsage(AttributeTargets.Assembly)]
public sealed class BabelObfuscatorAttribute : Attribute
{
}

namespace Markers
{
    public static class Hello
    {
        public static string Greet(string name) => "Hello, " + name;
    }
}
