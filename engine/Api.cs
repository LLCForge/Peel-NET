using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;

namespace Peel;

/// <summary>JavaScript'e açılan yüzey (Web Worker ya da Node alt süreci çağırır). Çağrılar durumsuzdur: her çağrı
/// modül baytlarını alır; bekçi motoru öldürüp yeniden başlatsa da hat kaldığı yerden devam eder.
/// Büyük ikili çıktılar (temizlenmiş modül, yakalanan katmanlar) Take* ile ayrıca alınır.</summary>
public static partial class Api
{
    static byte[] output = Array.Empty<byte>();
    static List<byte[]> layers = new();

    public static void Main() { }

    [JSExport]
    public static string Version() => "0.1.0";

    /// <summary>Statik inceleme: metadata, tespitler, dizgeler, başvurulan API'ler, kaynaklar.</summary>
    [JSExport]
    public static string Inspect(byte[] data) => Guard(() => Inspector.Run(data));

    /// <summary>Davranış anlık görüntüsü (Runner). Yakalanan katmanlar TakeLayer ile alınır.</summary>
    [JSExport]
    public static string Snapshot(byte[] data, bool main, bool probes) => Guard(() =>
    {
        var json = Runner.Snapshot(data, main, probes);
        layers = Fx.Layers.ToList();
        return json;
    });

    [JSExport]
    public static byte[] TakeLayer(int index) => index >= 0 && index < layers.Count ? layers[index] : Array.Empty<byte>();

    /// <summary>Tek geçiş. Değiştiyse yeni baytlar TakeOutput ile alınır.</summary>
    [JSExport]
    public static string Pass(byte[] data, string id, string options) => Guard(() =>
    {
        var opts = JsonDocument.Parse(string.IsNullOrEmpty(options) ? "{}" : options).RootElement;
        var r = Passes.Run(data, id, opts);
        output = r.Bytes ?? Array.Empty<byte>();
        return r.Json;
    });

    [JSExport]
    public static byte[] TakeOutput()
    {
        var o = output;
        output = Array.Empty<byte>();
        return o;
    }

    /// <summary>mode: "cs" | "il"</summary>
    [JSExport]
    public static string Decompile(byte[] data, string mode, string name) => Guard(() =>
    {
        var text = mode == "il" ? Peel.Decompile.IL(data, name) : Peel.Decompile.CSharp(data, name);
        return Json.Of(w => { w.WriteStartObject(); w.WriteBoolean("ok", true); w.WriteString("text", JsonExt.Safe(text)); w.WriteEndObject(); });
    });

    static string Guard(Func<string> f)
    {
        try { return f(); }
        catch (Exception e)
        {
            var u = Sandbox.Unwrap(e);
            return Json.Of(w =>
            {
                w.WriteStartObject();
                w.WriteBoolean("ok", false);
                w.WriteString("error", JsonExt.Safe(u.GetType().Name + ": " + u.Message));
                w.WriteString("stack", JsonExt.Safe(u.StackTrace ?? ""));
                w.WriteEndObject();
            });
        }
    }
}

