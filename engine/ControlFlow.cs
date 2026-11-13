using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace Peel;

/// <summary>
/// Akış düzleştirme çözücüsü (ConfuserEx "ctrl flow", switch kipi). Kalıp:
///   H: ldc.i4 K ; xor ; dup ; stloc S ; ldc.i4 N ; rem.un ; switch (T0..Tn)
/// Her blok bir sonraki durumu yığına koyup H'ye dallanır; durum ya sabittir ya da S'den sabitlerle hesaplanır
/// ("ldloc S; ldc A; mul; ldc B; xor"). Çözüm yol duyarlı bir benzetimdir: yöntem girişinden başlayıp S'nin somut
/// değeriyle ilerlenir; yalnızca durum hesaplayan "saf kuyruk" ya da H'ye giren her kenar için varılacak gerçek blok
/// hesaplanır ve kenar doğrudan o bloğa bağlanır (gerekirse yığındaki durum değerleri pop edilir). Aynı kenar iki farklı
/// hedefe gidiyorsa ya da bir değer bilinmiyorsa yöntem olduğu gibi bırakılır. Sonra ulaşılamayan dağıtıcı silinir.
/// </summary>
static class ControlFlowPass
{
    sealed class Dispatcher
    {
        public required int Head { get; set; } // ldc K
        public required int K;
        public required Local S;
        public required uint N;
        public required Instruction[] Targets;
        public required Instruction Default;
    }

    static Dispatcher? FindDispatcher(IList<Instruction> ins, IList<Local> locals, int from = 0)
    {
        for (int h = from; h + 6 < ins.Count; h++)
        {
            if (!ins[h].IsLdcI4() || ins[h + 1].OpCode.Code != Code.Xor || ins[h + 2].OpCode.Code != Code.Dup) continue;
            if (!ins[h + 3].IsStloc() || !ins[h + 4].IsLdcI4() || ins[h + 5].OpCode.Code != Code.Rem_Un || ins[h + 6].OpCode.Code != Code.Switch) continue;
            if (ins[h + 3].GetLocal(locals) is not { } s || h + 7 >= ins.Count) continue;
            int n = ins[h + 4].GetLdcI4Value();
            if (n <= 0) continue;
            return new Dispatcher
            {
                Head = h, K = ins[h].GetLdcI4Value(), S = s, N = (uint)n,
                Targets = (Instruction[])ins[h + 6].Operand, Default = ins[h + 7],
            };
        }
        return null;
    }

    public static bool LooksFlattened(MethodDef m)
    {
        try { return m.HasBody && FindDispatcher(m.Body.Instructions, m.Body.Variables) != null; }
        catch { return false; }
    }

    public static bool Run(PassContext ctx)
    {
        int methods = 0, edges = 0, failed = 0;
        foreach (var t in ctx.Module.GetTypes())
            foreach (var m in t.Methods)
            {
                if (!m.HasBody || !LooksFlattened(m)) continue;
                Il.Prepare(m);
                int done = 0;
                bool ok = true;
                // bir yöntemde birden çok dağıtıcı olabilir (her istisna kapsamı ayrı düzleştirilir)
                int from = 0;
                for (int round = 0; round < 16; round++)
                {
                    var d = FindDispatcher(m.Body.Instructions, m.Body.Variables, from);
                    if (d == null) break;
                    var r = Solve(m, d);
                    if (r < 0)
                    {
                        ok = false;
                        ctx.Log.Add($"akış çözülemedi: {Names.Show(m.Name)} ({why})");
                        from = d.Head + 1;
                        continue;
                    }
                    done += r;
                    Cleanup(m);
                    from = 0;
                }
                Il.Finish(m);
                if (done > 0) { methods++; edges += done; }
                if (!ok) failed++;
            }
        ctx.Bump("flattenedMethods", methods);
        ctx.Bump("flowEdges", edges);
        if (failed > 0)
        {
            ctx.Bump("flowUnsolved", failed);
            ctx.Log.Add($"{failed} yöntemde akış çözülemedi (desteklenmeyen kip ya da bilinmeyen durum değeri), olduğu gibi bırakıldı");
        }
        if (methods > 0) ctx.Log.Add($"{methods} yöntemde akış düzleştirme çözüldü ({edges} kenar)");
        return methods > 0;
    }

    static bool IsPure(Instruction i, Local s) =>
        i.IsLdcI4() || i.OpCode.Code is Code.Dup or Code.Pop or Code.Nop or Code.Xor or Code.Add or Code.Sub or Code.Mul
            or Code.Shl or Code.Shr or Code.Shr_Un or Code.And or Code.Or or Code.Not or Code.Neg or Code.Br
        || (i.IsLdloc() && i.Operand == s);

    /// <summary>j'den başlayan, yalnızca saf talimatlarla H'ye varan zincir mi?</summary>
    static bool PureTail(IList<Instruction> ins, Dictionary<Instruction, int> index, int j, Dispatcher d)
    {
        for (int steps = 0; steps < 48 && j < ins.Count; steps++)
        {
            if (j == d.Head) return true;
            var i = ins[j];
            if (!IsPure(i, d.S)) return false;
            j = i.OpCode.Code == Code.Br ? index[(Instruction)i.Operand] : j + 1;
        }
        return false;
    }

    /// <summary>Saf zinciri somut S ve yığınla benzetir; H'den sonra gerçek hedefe kadar (saf hedefler üzerinden) sürer.</summary>
    static (int Target, int S)? Resolve(IList<Instruction> ins, Dictionary<Instruction, int> index, int j, int? s, List<int?> stack, Dispatcher d)
    {
        var st = new List<int?>(stack);
        for (int hops = 0; hops < 256; hops++)
        {
            for (int steps = 0; j != d.Head; steps++)
            {
                if (steps > 64 || j >= ins.Count) return null;
                var i = ins[j];
                if (!IsPure(i, d.S)) return null;
                if (i.OpCode.Code == Code.Br) { j = index[(Instruction)i.Operand]; continue; }
                if (!Step(i, st, s, d.S)) return null;
                j++;
            }
            if (st.Count != 1 || st[0] is not int v) return null;
            int ns = v ^ d.K;
            uint idx = (uint)ns % d.N;
            var target = idx < d.Targets.Length ? d.Targets[idx] : d.Default;
            int tj = index[target];
            if (tj == d.Head || !PureTail(ins, index, tj, d)) return (tj, ns);
            j = tj;
            s = ns;
            st.Clear();
        }
        return null;
    }

    /// <summary>Yığın etkisi (yalnızca saf talimatlar somut hesaplanır).</summary>
    static bool Step(Instruction i, List<int?> st, int? s, Local sLocal)
    {
        unchecked
        {
            if (i.IsLdcI4()) { st.Add(i.GetLdcI4Value()); return true; }
            switch (i.OpCode.Code)
            {
                case Code.Nop: return true;
                case Code.Dup: if (st.Count < 1) return false; st.Add(st[^1]); return true;
                case Code.Pop: if (st.Count < 1) return false; st.RemoveAt(st.Count - 1); return true;
                case Code.Not: case Code.Neg:
                    if (st.Count < 1) return false;
                    st[^1] = st[^1] is int a ? (i.OpCode.Code == Code.Not ? ~a : -a) : null;
                    return true;
                case Code.Xor: case Code.Add: case Code.Sub: case Code.Mul: case Code.And: case Code.Or:
                case Code.Shl: case Code.Shr: case Code.Shr_Un:
                    {
                        if (st.Count < 2) return false;
                        int? x = st[^2], y = st[^1];
                        st.RemoveRange(st.Count - 2, 2);
                        st.Add(x is int p && y is int q ? i.OpCode.Code switch
                        {
                            Code.Xor => p ^ q, Code.Add => p + q, Code.Sub => p - q, Code.Mul => p * q, Code.And => p & q,
                            Code.Or => p | q, Code.Shl => p << (q & 31), Code.Shr => p >> (q & 31), _ => (int)((uint)p >> (q & 31)),
                        } : null);
                        return true;
                    }
            }
            if (i.IsLdloc() && i.Operand == sLocal) { st.Add(s); return true; }
            return false;
        }
    }

    readonly record struct Edge(Instruction From, int Slot); // Slot: -1 düşme, -2 dal işleneni, >=0 switch dizini

    /// <summary>Çözülen kenar sayısı; çözülemezse -1 (yöntem değiştirilmez).</summary>
    static string? why;

    static int Fail(string reason) { why = reason; return -1; }

    static int Solve(MethodDef m, Dispatcher d)
    {
        var body = m.Body;
        var ins = body.Instructions;
        var index = new Dictionary<Instruction, int>();
        for (int k = 0; k < ins.Count; k++) index[ins[k]] = k;
        // yöntem doğrudan durum zinciriyle başlıyorsa yeniden bağlanacak bir kenar olsun diye başa nop eklenir
        if (PureTail(ins, index, 0, d))
        {
            ins.Insert(0, Instruction.Create(OpCodes.Nop));
            d.Head++;
            index.Clear();
            for (int k = 0; k < ins.Count; k++) index[ins[k]] = k;
        }
        bool hasRet = m.HasReturnType;
        var rewrites = new Dictionary<Edge, (int Target, int Depth)>();
        var seen = new HashSet<string>();
        var work = new Stack<(int Pc, int? S, List<int?> Stack)>();
        work.Push((0, body.InitLocals ? 0 : null, new List<int?>()));
        foreach (var eh in body.ExceptionHandlers)
        {
            if (eh.HandlerStart != null) work.Push((index[eh.HandlerStart], null, eh.IsCatch || eh.IsFilter ? new List<int?> { null } : new List<int?>()));
            if (eh.FilterStart != null) work.Push((index[eh.FilterStart], null, new List<int?> { null }));
        }
        int budget = 200_000;
        var others = new HashSet<int>();
        for (var o = FindDispatcher(ins, body.Variables); o != null; o = FindDispatcher(ins, body.Variables, o.Head + 1))
            if (o.Head != d.Head) others.Add(o.Head);

        bool Transfer(Instruction from, int slot, int to, int? s, List<int?> st)
        {
            if (to == d.Head || PureTail(ins, index, to, d))
            {
                var r = Resolve(ins, index, to, s, st, d);
                if (r == null) return false;
                var e = new Edge(from, slot);
                var val = (r.Value.Target, st.Count);
                if (rewrites.TryGetValue(e, out var prev) && prev != val) return false;
                rewrites[e] = val;
                work.Push((r.Value.Target, r.Value.S, new List<int?>()));
                return true;
            }
            work.Push((to, s, st));
            return true;
        }

        while (work.Count > 0)
        {
            if (--budget < 0) return Fail("bütçe");
            var (pc, s, st) = work.Pop();
            if (pc >= ins.Count) return Fail("yöntem sonu");
            if (pc == d.Head) return Fail("kenarsız varış"); // H'ye kenarsız varış (çözülemez)
            var key = pc + "|" + s + "|" + string.Join(",", st);
            if (!seen.Add(key)) continue;
            var i = ins[pc];
            // başka bir dağıtıcının başı (ayrı istisna kapsamı): o bölge ayrı turda çözülür, buradan izlenmez
            if (others.Contains(pc)) continue;
            var next = new List<int?>(st);
            // aynı durum yerelini kullanan başka bir dağıtıcı (ayrı istisna kapsamı): S somut olarak izlenir
            if (i.IsStloc() && i.Operand == d.S)
            {
                if (next.Count < 1) return Fail("yığın @" + pc);
                s = next[^1];
                next.RemoveAt(next.Count - 1);
                if (!Transfer(i, -1, pc + 1, s, next)) return Fail("kenar @" + pc);
                continue;
            }
            if (!Step(i, next, s, d.S))
            {
                if (i.IsLdloc() && i.GetLocal(m.Body.Variables) == d.S) return Fail("S okuması @" + pc); // S saf olmayan bağlamda okunuyor
                i.CalculateStackUsage(hasRet, out int pushes, out int pops);
                if (pops == -1) next.Clear();
                else
                {
                    if (next.Count < pops) return Fail("yığın @" + pc);
                    next.RemoveRange(next.Count - pops, pops);
                }
                for (int p = 0; p < pushes; p++) next.Add(null);
            }
            switch (i.OpCode.FlowControl)
            {
                case FlowControl.Return:
                case FlowControl.Throw:
                    continue;
                case FlowControl.Branch:
                    if (i.OpCode.Code is Code.Leave or Code.Leave_S) next.Clear();
                    if (!Transfer(i, -2, index[(Instruction)i.Operand], s, next)) return Fail("kenar @" + pc);
                    continue;
                case FlowControl.Cond_Branch:
                    if (i.Operand is Instruction[] arr)
                    {
                        for (int a = 0; a < arr.Length; a++)
                            if (!Transfer(i, a, index[arr[a]], s, next)) return Fail("kenar @" + pc);
                    }
                    else if (!Transfer(i, -2, index[(Instruction)i.Operand], s, next)) return Fail("kenar @" + pc);
                    if (!Transfer(i, -1, pc + 1, s, next)) return Fail("kenar @" + pc);
                    continue;
                default:
                    if (i.OpCode.Code == Code.Endfinally) continue;
                    if (!Transfer(i, -1, pc + 1, s, next)) return Fail("kenar @" + pc);
                    continue;
            }
        }
        if (rewrites.Count == 0) return Fail("kenar yok");

        // kenarları yeniden bağla: yığında kalan durum değerleri için pop'lu saplama yöntem sonuna eklenir
        var targets = ins.ToArray();
        var stubs = new Dictionary<(int, int), Instruction>();
        Instruction Dest(int target, int depth)
        {
            if (depth == 0) return targets[target];
            if (stubs.TryGetValue((target, depth), out var st)) return st;
            var first = Instruction.Create(OpCodes.Pop);
            ins.Add(first);
            for (int p = 1; p < depth; p++) ins.Add(Instruction.Create(OpCodes.Pop));
            ins.Add(Instruction.Create(OpCodes.Br, targets[target]));
            return stubs[(target, depth)] = first;
        }
        foreach (var (e, (target, depth)) in rewrites)
        {
            var dest = Dest(target, depth);
            if (e.Slot == -2) e.From.Operand = dest;
            else if (e.Slot >= 0)
            {
                var arr = (Instruction[])((Instruction[])e.From.Operand).Clone();
                arr[e.Slot] = dest;
                e.From.Operand = arr;
            }
            else ins.Insert(ins.IndexOf(e.From) + 1, Instruction.Create(OpCodes.Br, dest));
        }
        return rewrites.Count;
    }

    /// <summary>Ulaşılamayan talimatlar silinir (dal hedefi ya da istisna sınırı olmayanlar); kalanlar nop olur.</summary>
    static void Cleanup(MethodDef m)
    {
        var body = m.Body;
        var ins = body.Instructions;
        var index = new Dictionary<Instruction, int>();
        for (int k = 0; k < ins.Count; k++) index[ins[k]] = k;
        var live = new bool[ins.Count];
        var work = new Stack<int>();
        work.Push(0);
        foreach (var eh in body.ExceptionHandlers)
        {
            if (eh.HandlerStart != null) work.Push(index[eh.HandlerStart]);
            if (eh.FilterStart != null) work.Push(index[eh.FilterStart]);
        }
        while (work.Count > 0)
        {
            int pc = work.Pop();
            if (pc >= ins.Count || live[pc]) continue;
            live[pc] = true;
            var i = ins[pc];
            if (i.Operand is Instruction t) work.Push(index[t]);
            if (i.Operand is Instruction[] arr) foreach (var a in arr) work.Push(index[a]);
            if (i.OpCode.FlowControl is not (FlowControl.Branch or FlowControl.Return or FlowControl.Throw) && i.OpCode.Code != Code.Endfinally) work.Push(pc + 1);
        }
        var referenced = new HashSet<Instruction>();
        for (int k = 0; k < ins.Count; k++)
        {
            if (!live[k]) continue;
            if (ins[k].Operand is Instruction t) referenced.Add(t);
            if (ins[k].Operand is Instruction[] arr) foreach (var a in arr) referenced.Add(a);
        }
        foreach (var eh in body.ExceptionHandlers)
            foreach (var x in new[] { eh.TryStart, eh.TryEnd, eh.HandlerStart, eh.HandlerEnd, eh.FilterStart })
                if (x != null) referenced.Add(x);
        for (int k = ins.Count - 1; k >= 0; k--)
        {
            if (live[k]) continue;
            if (referenced.Contains(ins[k])) { ins[k].OpCode = OpCodes.Nop; ins[k].Operand = null; }
            else ins.RemoveAt(k);
        }
        // kullanılmayan durum yereli kalabilir; ILSpy göstermez
    }
}
