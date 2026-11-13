using System;
using System.Collections.Generic;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace Peel;

/// <summary>IL yardımcıları: sabit ifadeleri değerlendirme (yalnızca yan etkisiz yığın işlemleri), dal hedefleri ve
/// talimat dizisini yerinde değiştirme. Değiştirilecek gövdeler önce SimplifyMacros/SimplifyBranches ile açılır,
/// sonra Optimize* ile toparlanır (Prepare/Finish).</summary>
public static class Il
{
    public static void Prepare(MethodDef m)
    {
        m.Body.SimplifyMacros(m.Parameters);
        m.Body.SimplifyBranches();
    }

    public static void Finish(MethodDef m)
    {
        m.Body.OptimizeMacros();
        m.Body.OptimizeBranches();
    }

    /// <summary>Bir sonraki talimata giden koşulsuz dallar (ConfuserEx sabit kodlamasının "ldc; br; call" kalıbı) nop olur.</summary>
    public static int RemoveTrivialBranches(CilBody body)
    {
        var ins = body.Instructions;
        int n = 0;
        for (int k = 0; k + 1 < ins.Count; k++)
        {
            if (ins[k].OpCode.Code is Code.Br or Code.Br_S && ins[k].Operand == ins[k + 1])
            {
                ins[k].OpCode = OpCodes.Nop;
                ins[k].Operand = null;
                n++;
            }
        }
        return n;
    }

    /// <summary>Dal hedefleri ve istisna bloğu sınırları: bu talimatlar başka bir talimatla birleştirilemez.</summary>
    public static HashSet<Instruction> Targets(CilBody body)
    {
        var set = new HashSet<Instruction>();
        foreach (var i in body.Instructions)
        {
            if (i.Operand is Instruction t) set.Add(t);
            else if (i.Operand is Instruction[] arr) foreach (var a in arr) set.Add(a);
        }
        foreach (var eh in body.ExceptionHandlers)
        {
            if (eh.TryStart != null) set.Add(eh.TryStart);
            if (eh.TryEnd != null) set.Add(eh.TryEnd);
            if (eh.HandlerStart != null) set.Add(eh.HandlerStart);
            if (eh.HandlerEnd != null) set.Add(eh.HandlerEnd);
            if (eh.FilterStart != null) set.Add(eh.FilterStart);
        }
        return set;
    }

    /// <summary>ins[s..e) aralığını boş yığından başlayarak değerlendirir. Desteklenmeyen talimatta null.</summary>
    public static List<object?>? Simulate(IList<Instruction> ins, int s, int e)
    {
        var st = new List<object?>();
        for (int k = s; k < e; k++)
        {
            var i = ins[k];
            var code = i.OpCode.Code;
            if (i.IsLdcI4()) { st.Add(i.GetLdcI4Value()); continue; }
            switch (code)
            {
                case Code.Nop: continue;
                case Code.Ldc_I8: st.Add((long)i.Operand); continue;
                case Code.Ldc_R4: st.Add((float)i.Operand); continue;
                case Code.Ldc_R8: st.Add((double)i.Operand); continue;
                case Code.Ldstr: st.Add((string)i.Operand); continue;
                case Code.Ldnull: st.Add(null); continue;
                case Code.Dup:
                    if (st.Count < 1) return null;
                    st.Add(st[^1]);
                    continue;
                case Code.Pop:
                    if (st.Count < 1) return null;
                    st.RemoveAt(st.Count - 1);
                    continue;
            }
            if (IsBinary(code))
            {
                if (st.Count < 2) return null;
                var r = Binary(code, st[^2], st[^1]);
                if (r == null) return null;
                st.RemoveRange(st.Count - 2, 2);
                st.Add(r);
                continue;
            }
            if (IsUnary(code))
            {
                if (st.Count < 1) return null;
                var r = Unary(code, st[^1]);
                if (r == null) return null;
                st[^1] = r;
                continue;
            }
            return null;
        }
        return st;
    }

    static bool IsBinary(Code c) => c is Code.Add or Code.Sub or Code.Mul or Code.Div or Code.Div_Un or Code.Rem or Code.Rem_Un
        or Code.And or Code.Or or Code.Xor or Code.Shl or Code.Shr or Code.Shr_Un;

    static bool IsUnary(Code c) => c is Code.Neg or Code.Not or Code.Conv_I1 or Code.Conv_I2 or Code.Conv_I4 or Code.Conv_U1
        or Code.Conv_U2 or Code.Conv_U4 or Code.Conv_I8 or Code.Conv_U8 or Code.Conv_R4 or Code.Conv_R8;

    static object? Binary(Code c, object? a, object? b)
    {
        unchecked
        {
            if (a is int x && b is int y)
            {
                return c switch
                {
                    Code.Add => x + y,
                    Code.Sub => x - y,
                    Code.Mul => x * y,
                    Code.Div => y == 0 || (x == int.MinValue && y == -1) ? null : x / y,
                    Code.Div_Un => y == 0 ? null : (int)((uint)x / (uint)y),
                    Code.Rem => y == 0 || (x == int.MinValue && y == -1) ? null : x % y,
                    Code.Rem_Un => y == 0 ? null : (int)((uint)x % (uint)y),
                    Code.And => x & y,
                    Code.Or => x | y,
                    Code.Xor => x ^ y,
                    Code.Shl => x << (y & 31),
                    Code.Shr => x >> (y & 31),
                    Code.Shr_Un => (int)((uint)x >> (y & 31)),
                    _ => null,
                };
            }
            if (a is long p && (b is long || (b is int && c is Code.Shl or Code.Shr or Code.Shr_Un)))
            {
                long q = b is long lb ? lb : (int)b!;
                return c switch
                {
                    Code.Add => p + q,
                    Code.Sub => p - q,
                    Code.Mul => p * q,
                    Code.Div => q == 0 || (p == long.MinValue && q == -1) ? null : p / q,
                    Code.Div_Un => q == 0 ? null : (long)((ulong)p / (ulong)q),
                    Code.Rem => q == 0 || (p == long.MinValue && q == -1) ? null : p % q,
                    Code.Rem_Un => q == 0 ? null : (long)((ulong)p % (ulong)q),
                    Code.And => p & q,
                    Code.Or => p | q,
                    Code.Xor => p ^ q,
                    Code.Shl => p << (int)(q & 63),
                    Code.Shr => p >> (int)(q & 63),
                    Code.Shr_Un => (long)((ulong)p >> (int)(q & 63)),
                    _ => null,
                };
            }
            if (a is double d1 && b is double d2)
            {
                return c switch
                {
                    Code.Add => d1 + d2,
                    Code.Sub => d1 - d2,
                    Code.Mul => d1 * d2,
                    Code.Div => d1 / d2,
                    Code.Rem => d1 % d2,
                    _ => null,
                };
            }
            if (a is float f1 && b is float f2)
            {
                // IL değerlendirme yığınında float'lar F türüdür; tam çift hassasiyet farkını önlemek için katlanmaz
                return null;
            }
            return null;
        }
    }

    static object? Unary(Code c, object? a)
    {
        unchecked
        {
            switch (a)
            {
                case int x:
                    return c switch
                    {
                        Code.Neg => -x,
                        Code.Not => ~x,
                        Code.Conv_I1 => (int)(sbyte)x,
                        Code.Conv_I2 => (int)(short)x,
                        Code.Conv_I4 => x,
                        Code.Conv_U1 => (int)(byte)x,
                        Code.Conv_U2 => (int)(ushort)x,
                        Code.Conv_U4 => x,
                        Code.Conv_I8 => (long)x,
                        Code.Conv_U8 => (long)(uint)x,
                        Code.Conv_R8 => (double)x,
                        _ => null,
                    };
                case long l:
                    return c switch
                    {
                        Code.Neg => -l,
                        Code.Not => ~l,
                        Code.Conv_I1 => (int)(sbyte)l,
                        Code.Conv_I2 => (int)(short)l,
                        Code.Conv_I4 => (int)l,
                        Code.Conv_U1 => (int)(byte)l,
                        Code.Conv_U2 => (int)(ushort)l,
                        Code.Conv_U4 => (int)(uint)l,
                        Code.Conv_I8 => l,
                        Code.Conv_U8 => l,
                        Code.Conv_R8 => (double)l,
                        _ => null,
                    };
                case double d:
                    return c switch
                    {
                        Code.Neg => -d,
                        Code.Conv_R8 => d,
                        _ => null,
                    };
            }
            return null;
        }
    }

    /// <summary>ins[callIdx] çağrısının `count` argümanını üreten en kısa sabit talimat penceresi. Pencerenin ilk talimatı
    /// dışındakiler dal hedefi olmamalı (yerinde birleştirilecekler).</summary>
    public static (int Start, List<object?> Values)? ArgsOf(IList<Instruction> ins, int callIdx, int count, HashSet<Instruction> targets, int maxWindow = 24)
    {
        if (count == 0) return targets.Contains(ins[callIdx]) ? null : (callIdx, new List<object?>());
        for (int s = callIdx - 1; s >= 0 && callIdx - s <= maxWindow; s--)
        {
            // s'den sonraki talimatlar (çağrı dahil) dal hedefi olamaz
            if (targets.Contains(ins[s + 1])) return null;
            var v = Simulate(ins, s, callIdx);
            if (v == null) continue;
            if (v.Count == count) return (s, v);
            if (v.Count > count) return null;
        }
        return null;
    }

    /// <summary>ins[s..e] (dahil) yerine tek talimat: ins[s] nesnesi korunur (dallar ona işaret edebilir), kalanı nop olur.</summary>
    public static void Replace(IList<Instruction> ins, int s, int e, Instruction with)
    {
        ins[s].OpCode = with.OpCode;
        ins[s].Operand = with.Operand;
        for (int k = s + 1; k <= e; k++)
        {
            ins[k].OpCode = OpCodes.Nop;
            ins[k].Operand = null;
        }
    }

    public static Instruction? Const(object? v) => v switch
    {
        null => Instruction.Create(OpCodes.Ldnull),
        string s => Instruction.Create(OpCodes.Ldstr, s),
        bool b => Instruction.Create(OpCodes.Ldc_I4, b ? 1 : 0),
        int i => Instruction.Create(OpCodes.Ldc_I4, i),
        uint u => Instruction.Create(OpCodes.Ldc_I4, unchecked((int)u)),
        short s16 => Instruction.Create(OpCodes.Ldc_I4, (int)s16),
        ushort u16 => Instruction.Create(OpCodes.Ldc_I4, (int)u16),
        sbyte i8 => Instruction.Create(OpCodes.Ldc_I4, (int)i8),
        byte u8 => Instruction.Create(OpCodes.Ldc_I4, (int)u8),
        char ch => Instruction.Create(OpCodes.Ldc_I4, (int)ch),
        long l => Instruction.Create(OpCodes.Ldc_I8, l),
        ulong ul => Instruction.Create(OpCodes.Ldc_I8, unchecked((long)ul)),
        float f => Instruction.Create(OpCodes.Ldc_R4, f),
        double d => Instruction.Create(OpCodes.Ldc_R8, d),
        _ => null,
    };

    /// <summary>Yöntemin çağrı hedefi (call/callvirt/newobj) ya da null.</summary>
    public static IMethod? CallTarget(Instruction i) =>
        i.OpCode.Code is Code.Call or Code.Callvirt or Code.Newobj ? i.Operand as IMethod : null;

    public static MethodDef? ResolveDef(IMethod? m) => m switch
    {
        MethodDef d => d,
        MethodSpec s => s.Method as MethodDef,
        _ => null,
    };
}
