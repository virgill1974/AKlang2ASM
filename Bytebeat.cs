using System;

namespace Aklang2Asm
{
    // Bytebeat formulas: unsigned 16 bit integer expressions over the sample number t, C operators
    // and precedence. Overflow wraps, x/0 = x%0 = 0, shift counts are taken & 63 and clear the
    // value from 16 on, comparisons are unsigned (0/1) - exactly what the 68000 code of aklang2asm
    // computes with .w instructions (mulu.w, divu.w, lsl.w/lsr.w Dn).
    // The same code is in aklang2asm and in the AmigaKlang editor (Bytebeat.cs): keep both copies
    // identical apart from the namespace, C# 7.3 compatible.
    public static class Bytebeat
    {
        public const int MaxLength = 120;

        public enum Op { Const, T, Neg, Not, LogNot, Mul, Div, Mod, Add, Sub, Shl, Shr, Lt, Le, Gt, Ge, Eq, Ne, And, Xor, Or, LogAnd, LogOr, Cond }

        public sealed class Node
        {
            public Op Op;
            public ushort Value;        // Const
            public Node A, B, C;        // operands, C = else branch of ?:
        }

        // classic formulas for the examples list of the editor
        public static readonly string[] Examples =
        {
            "t*(t>>5|t>>8)",
            "t*(42&t>>10)",
            "t*((t>>12|t>>8)&63&t>>4)",
            "(t*5&t>>7)|(t*3&t>>10)",
            "t*(t>>11&t>>8&123&t>>3)",
            "(t>>7|t|t>>6)*10+4*(t&t>>13|t>>6)",
        };

        // ------------------------------------------------------------------ semantics
        public static ushort Unary(Op op, ushort a)
        {
            switch (op)
            {
                case Op.Neg: return (ushort)-a;
                case Op.Not: return (ushort)~a;
                default: return (ushort)(a == 0 ? 1 : 0);    // LogNot
            }
        }

        public static ushort Binary(Op op, ushort a, ushort b)
        {
            switch (op)
            {
                case Op.Mul: return (ushort)(a * b);
                case Op.Div: return b == 0 ? (ushort)0 : (ushort)(a / b);
                case Op.Mod: return b == 0 ? (ushort)0 : (ushort)(a % b);
                case Op.Add: return (ushort)(a + b);
                case Op.Sub: return (ushort)(a - b);
                case Op.Shl: return (b & 63) >= 16 ? (ushort)0 : (ushort)(a << (b & 63));
                case Op.Shr: return (b & 63) >= 16 ? (ushort)0 : (ushort)(a >> (b & 63));
                case Op.Lt: return (ushort)(a < b ? 1 : 0);
                case Op.Le: return (ushort)(a <= b ? 1 : 0);
                case Op.Gt: return (ushort)(a > b ? 1 : 0);
                case Op.Ge: return (ushort)(a >= b ? 1 : 0);
                case Op.Eq: return (ushort)(a == b ? 1 : 0);
                case Op.Ne: return (ushort)(a != b ? 1 : 0);
                case Op.And: return (ushort)(a & b);
                case Op.Xor: return (ushort)(a ^ b);
                case Op.Or: return (ushort)(a | b);
                case Op.LogAnd: return (ushort)(a != 0 && b != 0 ? 1 : 0);
                default: return (ushort)(a != 0 || b != 0 ? 1 : 0);    // LogOr
            }
        }

        public static ushort Eval(Node n, ushort t)
        {
            switch (n.Op)
            {
                case Op.Const: return n.Value;
                case Op.T: return t;
                case Op.Neg:
                case Op.Not:
                case Op.LogNot: return Unary(n.Op, Eval(n.A, t));
                case Op.Cond: return Eval(n.A, t) != 0 ? Eval(n.B, t) : Eval(n.C, t);
                default: return Binary(n.Op, Eval(n.A, t), Eval(n.B, t));
            }
        }

        // the sample value of a slot: low byte of the result, centered, then the gain of vol()
        public static short Sample(Node n, ushort t, byte gain)
        {
            short x = (short)(((Eval(n, t) & 255) - 128) << 8);
            return (short)(x * gain >> 7);
        }

        // ------------------------------------------------------------------ parser
        // formula without blanks, as written into script.txt
        public static string Compact(string text)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in text ?? "") if (!char.IsWhiteSpace(c)) sb.Append(c);
            return sb.ToString();
        }

        public static bool TryParse(string text, out Node node, out string error)
        {
            node = null;
            error = null;
            if (string.IsNullOrWhiteSpace(text)) { error = "empty formula"; return false; }
            if (text.Length > MaxLength) { error = "longer than " + MaxLength + " characters"; return false; }
            var p = new Parser(text);
            try
            {
                node = p.Conditional();
                if (p.Peek() != "") throw p.Fail("unexpected '" + p.Peek() + "'");
                return true;
            }
            catch (FormatException e)
            {
                node = null;
                error = e.Message;
                return false;
            }
        }

        public static Node Parse(string text)
        {
            Node node;
            string error;
            if (!TryParse(text, out node, out error)) throw new FormatException("Bytebeat formula: " + error);
            return node;
        }

        static Node Make(Op op, Node a, Node b = null, Node c = null)
        {
            var n = new Node { Op = op, A = a, B = b, C = c };
            // constant parts are folded with the same semantics (the 68000 code uses immediates)
            if (a.Op == Op.Const && (b == null || b.Op == Op.Const) && (c == null || c.Op == Op.Const))
                return new Node { Op = Op.Const, Value = Eval(n, 0) };
            return n;
        }

        sealed class Parser
        {
            readonly string s;
            int pos;

            public Parser(string text) { s = text; }

            public FormatException Fail(string message) => new FormatException(message + " at " + (Start() + 1));

            int Start()
            {
                int i = pos;
                while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
                return i;
            }

            // next token without consuming it ("" = end)
            public string Peek()
            {
                int i = Start();
                if (i >= s.Length) return "";
                char c = s[i];
                if (char.IsLetterOrDigit(c))
                {
                    int j = i;
                    while (j < s.Length && char.IsLetterOrDigit(s[j])) j++;
                    return s.Substring(i, j - i);
                }
                if (i + 1 < s.Length)
                {
                    string two = s.Substring(i, 2);
                    if (two == "<<" || two == ">>" || two == "<=" || two == ">=" || two == "==" || two == "!=" || two == "&&" || two == "||")
                        return two;
                }
                return c.ToString();
            }

            bool Accept(string token)
            {
                if (Peek() != token) return false;
                pos = Start() + token.Length;
                return true;
            }

            void Expect(string token)
            {
                if (!Accept(token)) throw Fail(Peek() == "" ? "'" + token + "' missing" : "'" + token + "' expected");
            }

            public Node Conditional()
            {
                var cond = Binary(0);
                if (!Accept("?")) return cond;
                var then = Conditional();
                Expect(":");
                return Make(Op.Cond, cond, then, Conditional());
            }

            // binary operators by C precedence, lowest first
            static readonly string[][] Levels =
            {
                new[] { "||" }, new[] { "&&" }, new[] { "|" }, new[] { "^" }, new[] { "&" }, new[] { "==", "!=" },
                new[] { "<", "<=", ">", ">=" }, new[] { "<<", ">>" }, new[] { "+", "-" }, new[] { "*", "/", "%" },
            };

            static Op BinaryOp(string token)
            {
                switch (token)
                {
                    case "||": return Op.LogOr;
                    case "&&": return Op.LogAnd;
                    case "|": return Op.Or;
                    case "^": return Op.Xor;
                    case "&": return Op.And;
                    case "==": return Op.Eq;
                    case "!=": return Op.Ne;
                    case "<": return Op.Lt;
                    case "<=": return Op.Le;
                    case ">": return Op.Gt;
                    case ">=": return Op.Ge;
                    case "<<": return Op.Shl;
                    case ">>": return Op.Shr;
                    case "+": return Op.Add;
                    case "-": return Op.Sub;
                    case "*": return Op.Mul;
                    case "/": return Op.Div;
                    default: return Op.Mod;
                }
            }

            Node Binary(int level)
            {
                if (level == Levels.Length) return Unary();
                var left = Binary(level + 1);
                while (true)
                {
                    string token = Peek();
                    if (Array.IndexOf(Levels[level], token) < 0) return left;
                    Accept(token);
                    left = Make(BinaryOp(token), left, Binary(level + 1));
                }
            }

            Node Unary()
            {
                if (Accept("-")) return Make(Op.Neg, Unary());
                if (Accept("~")) return Make(Op.Not, Unary());
                if (Accept("!")) return Make(Op.LogNot, Unary());
                if (Accept("+")) return Unary();
                return Primary();
            }

            Node Primary()
            {
                string token = Peek();
                if (token == "") throw Fail("formula incomplete");
                if (Accept("("))
                {
                    var n = Conditional();
                    Expect(")");
                    return n;
                }
                if (token == "t")
                {
                    Accept(token);
                    return new Node { Op = Op.T };
                }
                if (char.IsDigit(token[0]))
                {
                    int value;
                    bool ok = token.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                        ? int.TryParse(token.Substring(2), System.Globalization.NumberStyles.AllowHexSpecifier, null, out value) && token.Length > 2
                        : int.TryParse(token, System.Globalization.NumberStyles.None, null, out value);
                    if (!ok) throw Fail("bad number '" + token + "'");
                    if (value > 65535) throw Fail("number '" + token + "' too big (max. 65535)");
                    Accept(token);
                    return new Node { Op = Op.Const, Value = (ushort)value };
                }
                throw Fail(char.IsLetter(token[0]) ? "unknown name '" + token + "' (only t)" : "unexpected '" + token + "'");
            }
        }
    }
}
