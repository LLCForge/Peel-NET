// ConfuserEx 2 çalıştırıcısı (fixture üretimi). Kullanım: CrRunner <proje.crproj>
using System;
using System.Xml;
using Confuser.Core;
using Confuser.Core.Project;

// kullanım: CrRunner <proje.crproj>
internal static class Program
{
    private sealed class Log : ILogger
    {
        public bool Failed;
        public void Debug(string msg) { }
        public void DebugFormat(string format, params object[] args) { }
        public void Info(string msg) => Console.WriteLine(msg);
        public void InfoFormat(string format, params object[] args) => Console.WriteLine(format, args);
        public void Warn(string msg) => Console.WriteLine("WARN " + msg);
        public void WarnFormat(string format, params object[] args) => Console.WriteLine("WARN " + format, args);
        public void WarnException(string msg, Exception ex) => Console.WriteLine("WARN " + msg + " " + ex);
        public void Error(string msg) { Failed = true; Console.WriteLine("ERROR " + msg); }
        public void ErrorFormat(string format, params object[] args) { Failed = true; Console.WriteLine("ERROR " + format, args); }
        public void ErrorException(string msg, Exception ex) { Failed = true; Console.WriteLine("ERROR " + msg + " " + ex); }
        public void Progress(int progress, int overall) { }
        public void EndProgress() { }
        public void Finish(bool successful) { if (!successful) Failed = true; }
        public void BeginProgress(string format, params object[] args) { }
    }

    private static int Main(string[] args)
    {
        var doc = new XmlDocument();
        doc.Load(args[0]);
        var proj = new ConfuserProject();
        proj.Load(doc);
        proj.BaseDirectory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(args[0]));
        var log = new Log();
        ConfuserEngine.Run(new ConfuserParameters { Project = proj, Logger = log }).Wait();
        return log.Failed ? 1 : 0;
    }
}
