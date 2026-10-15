using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Aklang2Asm
{
	// ARM output for the Acorn Archimedes (option -arm): AK_Generate in vasm standard syntax.
	//
	// A port of akp2arc.py ("Aklang2Acorn") v1.0, May 2024, by kieran/Bitshifters
	// (https://github.com/kieranhj/archieklang), with his permission. The register use, the
	// interface and the code of the nodes are his:
	//   r0-r3 = v1-v4, r4-r6/r12/r14 = temp, r7 = sample count, r8 = sample buffer,
	//   r9 = temporary work buffer, r10 = AK_Vars (external samples on entry), r11 = 32767.
	// Values are kept as 32 bit numbers with the 16 bit rules of the editor (sign extension and
	// clamping where the editor has them), the delay buffers hold 2048 longs each.
	//
	// Added to the port (the first commit of this file gives the output of akp2arc.py): the loop
	// generator of the editor (crossfade of V1.14), clone with transpose, sample mixer, bytebeat,
	// up to 12 external samples and 4 bit ADPCM.
	class ArmGenerator
	{
		static readonly int[] DecayTable = { 32767, 32767, 32767, 16384, 10922, 8192, 6553, 4681, 3640, 2978, 2520, 2048, 1724, 1489, 1310, 1129, 992, 885, 799, 712, 642, 585, 537, 489, 448, 414, 385, 356, 330, 309, 289, 270, 254, 239, 225, 212, 201, 190, 181, 171, 163, 155, 148, 141, 134, 129, 123, 118, 113, 108, 104, 100, 96, 93, 89, 86, 83, 80, 77, 75, 72, 70, 68, 65, 63, 61, 60, 58, 56, 54, 53, 51, 50, 49, 47, 46, 45, 44, 43, 41, 40, 39, 38, 38, 37, 36, 35, 34, 33, 33, 32, 31, 30, 30, 29, 29, 28, 27, 27, 26, 26, 25, 25, 24, 24, 23, 23, 22, 22, 22, 21, 21, 20, 20, 20, 19, 19, 19, 18, 18, 18, 17, 17, 17, 17, 16, 16, 16 };

		class Instrument
		{
			public string Name;
			public int Length, RepeatOffset, RepeatLength;
			public bool Loop;
			public List<string> Commands = new List<string>();
		}

		readonly StringBuilder o = new StringBuilder();
		readonly string source;
		readonly List<Instrument> instruments = new List<Instrument>();
		int[] extSampleLens;             // samples per slot
		bool[] extAdpcm;                 // 4 bit ADPCM: stored after the delta samples, half the bytes
		int extDeltaLen;                 // bytes of the delta coded samples
		int totalSmpLen, totalExtSmpLen;
		bool usedAdpcm, usedDivide;
		int labelNr;

		int instNr, instance, dvalue, buffers, maxInstances, maxDvalues, maxBuffers;
		readonly List<int[]> adsrValues = new List<int[]>();

		ArmGenerator(string source) { this.source = source; }

		public static string Generate(string source, string script)
		{
			var g = new ArmGenerator(source);
			g.ParseScript(script);
			g.WriteHeader();
			g.WriteInstruments();
			g.WriteVars();
			return g.o.ToString();
		}

		void W(string line) { o.Append(line).Append('\n'); }

		// ---- the script ("Dan table") ----------------------------------------------------------

		// like the parse() of the Python package "parse" for the formats akp2arc.py uses: the whole
		// text has to match, {} is the shortest text, {:d} a number, {:S} text without spaces
		static string[] Parse(string format, string text)
		{
			var re = new StringBuilder(@"\A");
			foreach (string part in Regex.Split(format, @"(\{[^}]*\})"))
			{
				if (part.Length == 0) continue;
				if (part[0] == '{')
				{
					string type = part.Contains(":") ? part.Substring(part.IndexOf(':') + 1).TrimEnd('}') : "";
					if (type == "d") re.Append(@"([-+ ]?\d+)");
					else if (type == "S") re.Append(@"(\S+)");
					else re.Append(@"(.+?)");
				}
				else re.Append(Regex.Escape(part));
			}
			re.Append(@"\z");
			var m = Regex.Match(text, re.ToString(), RegexOptions.IgnoreCase | RegexOptions.Singleline);
			if (!m.Success) return null;
			var result = new string[m.Groups.Count - 1];
			for (int i = 0; i < result.Length; i++) result[i] = m.Groups[i + 1].Value;
			return result;
		}

		static int Int(string s) => int.Parse(s.Trim().TrimStart('+'));

		// "v2" -> 2, else null
		static int? V(string s)
		{
			var p = Parse("v{:d}", s);
			return p == null ? (int?)null : Int(p[0]);
		}

		// "128" -> 128, else null
		static int? C(string s)
		{
			var p = Parse("{:d}", s);
			return p == null ? (int?)null : Int(p[0]);
		}

		static Exception Bad(string what, string text) => new FormatException("-arm: " + what + ": '" + text + "'");

		void ParseScript(string script)
		{
			var parts = script.Split('$').ToList();
			string header = parts[0].Replace("\r", "").Replace("\n", "");
			parts.RemoveAt(0);

			// external sample lengths: 8 (akp2arc.py) to 12, "<length>a" = 4 bit ADPCM
			string[] entries = header.Split(',').Select(x => x.Trim()).ToArray();
			if (entries.Length < 8 || entries.Length > 12) throw Bad("8 to 12 external sample lengths expected", header);
			extSampleLens = new int[entries.Length];
			extAdpcm = new bool[entries.Length];
			for (int i = 0; i < entries.Length; i++)
			{
				bool adpcm = entries[i].EndsWith("a");
				string number = adpcm ? entries[i].Substring(0, entries[i].Length - 1) : entries[i];
				if (C(number) == null) throw Bad("external sample length", entries[i]);
				extSampleLens[i] = Int(number);
				extAdpcm[i] = adpcm && extSampleLens[i] > 0;
				if (extAdpcm[i]) totalExtSmpLen += (extSampleLens[i] + 1) / 2;
				else { extDeltaLen += extSampleLens[i]; totalExtSmpLen += extSampleLens[i]; }
			}

			foreach (string part in parts)
			{
				if (instruments.Count >= 32) break;
				string text = part.Replace("\r", "").Replace("\n", "");
				int hash = text.IndexOf('#');
				string def = hash < 0 ? text : text.Substring(0, hash);
				int comma = def.IndexOf(',');
				if (comma < 0) break;
				// "$ name, length, repeat offset, repeat length, loop": the name is what follows "$ "
				var info = Parse("{:d}, {:d}, {:d}, {:S}", def.Substring(comma + 1).Trim());
				if (info == null) break;
				var inst = new Instrument
				{
					Name = def.Substring(Math.Min(1, comma), comma - Math.Min(1, comma)),
					Length = Int(info[0]), RepeatOffset = Int(info[1]), RepeatLength = Int(info[2]), Loop = info[3] == "Y",
				};
				if (hash >= 0)
					foreach (string command in text.Substring(hash + 1).Split(';'))
						if (command.Trim().Length > 0) inst.Commands.Add(command.Trim());
				instruments.Add(inst);
				totalSmpLen += inst.Length;
			}
		}

		// ---- helpers of the nodes ---------------------------------------------------------------

		void SignExtend(int reg)
		{
			W($"\tmov r{reg}, r{reg}, asl #16");
			W($"\tmov r{reg}, r{reg}, asr #16\t; Sign extend word to long.");
		}

		// var: 1-4 = v1-v4, above the register number + 1
		void Clamp(int var)
		{
			if (var > 4) W($"\t; r{var - 1} = clamp(r{var - 1})");
			else W($"\t; v{var} = clamp(v{var})");

			W($"\tcmp r{var - 1}, r11\t\t; #32767");
			W($"\tmovgt r{var - 1}, r11\t; #32767");
			W($"\tcmn r{var - 1}, r11\t\t; #-32768");
			W($"\tmvnlt r{var - 1}, r11\t; #-32768");
		}

		// fits the 8 bit rotated immediate of ARM
		static bool Rotated(uint v)
		{
			for (int r = 0; r < 32; r += 2)
				if (((v << r) | (v >> ((32 - r) & 31))) <= 255) return true;
			return false;
		}

		// a constant in a register: mov or mvn if it fits, else built from its bytes (vasm splits
		// an immediate into two parts at most)
		void Const(int reg, int value, string comment)
		{
			string tail = "\t; " + comment;
			uint v = (uint)value;
			if (Rotated(v)) { W($"\tmov r{reg}, #0x{v:x}{tail}"); return; }
			if (Rotated(~v)) { W($"\tmvn r{reg}, #0x{~v:x}{tail}"); return; }
			bool first = true;
			for (int shift = 0; shift < 32; shift += 8)
			{
				uint part = v & (0xffu << shift);
				if (part == 0) continue;
				W(first ? $"\tmov r{reg}, #0x{part:x}{tail}" : $"\torr r{reg}, r{reg}, #0x{part:x}");
				first = false;
			}
		}

		// log2 of a power of two (-1 for 0), like the loops of akp2arc.py
		static int ShiftOf(int value)
		{
			int shift = -1;
			while (value != 0) { value >>= 1; shift++; }
			return shift;
		}

		void VolVar(int dstVar, int srcVar, int? gainV, int? gainC)
		{
			if (gainV != null)
			{
				if (srcVar == gainV) Console.WriteLine("Warning: Volume gain var using src var!");

				if (dstVar > 4) W($"\t; r{dstVar - 1} = vol(r{srcVar - 1}, v{gainV})");
				else W($"\t; v{dstVar} = vol(v{srcVar}, v{gainV})");

				W($"\tand r14, r{gainV - 1}, #0xff");

				W($"\tmul r{dstVar - 1}, r14, r{srcVar - 1}");
				W($"\tmov r{dstVar - 1}, r{dstVar - 1}, asr #7");
				SignExtend(dstVar - 1);
			}
			else
			{
				if (dstVar > 4) W($"\t; r{dstVar - 1} = vol(r{srcVar - 1}, {gainC})");
				else W($"\t; v{dstVar} = vol(v{srcVar}, {gainC})");

				int shiftVal = gainC.Value;
				if ((shiftVal & (shiftVal - 1)) == 0)
				{
					// Shift for 2^N muls.
					int shiftLeft = ShiftOf(shiftVal) - 7;
					if (shiftLeft == 0)
					{
						if (srcVar == dstVar) W($"\t; NOOP -- val<<{shiftLeft + 7}>>7");
						else W($"\tmov r{dstVar - 1}, r{srcVar - 1}\t; NOOP -- val<<{shiftLeft + 7}>>7");
					}
					else if (shiftLeft < 0) W($"\tmov r{dstVar - 1}, r{srcVar - 1}, asr #{-shiftLeft}\t; val<<{shiftLeft + 7}>>7");
					else W($"\tmov r{dstVar - 1}, r{srcVar - 1}, asl #{shiftLeft}\t; val<<{shiftLeft + 7}>>7");
				}
				else
				{
					W($"\tmov r14, #{gainC}");
					W($"\tmul r{dstVar - 1}, r14, r{srcVar - 1}");
					W($"\tmov r{dstVar - 1}, r{dstVar - 1}, asr #7");
					SignExtend(dstVar - 1);
				}
			}
		}

		void Vol(int var, int? gainV, int? gainC) => VolVar(var, var, gainV, gainC);

		// ---- the nodes --------------------------------------------------------------------------

		void FuncAdd(int var, string args)
		{
			var pv = Parse("v{:d}, v{:d}", args);
			var pc = Parse("v{:d}, {:d}", args);

			if (pv != null)
			{
				SignExtend(Int(pv[0]) - 1);
				SignExtend(Int(pv[1]) - 1);
				W($"\tadd r{var - 1}, r{Int(pv[0]) - 1}, r{Int(pv[1]) - 1}");
			}
			else
			{
				if (pc == null) throw Bad("add", args);
				SignExtend(Int(pc[0]) - 1);
				if (Int(pc[1]) < 0) W($"\tmvn r14, #{-Int(pc[1]) - 1}");
				else W($"\tmov r14, #{Int(pc[1])}");
				W($"\tadd r{var - 1}, r{Int(pc[0]) - 1}, r14");
			}

			Clamp(var);
		}

		void FuncVol(int var, string args)
		{
			var p = Parse("v{:d}, {:S}", args);
			if (p == null) throw Bad("vol", args);
			VolVar(var, Int(p[0]), V(p[1]), C(p[1]));
		}

		void FuncMul(int var, string args)
		{
			var pv = Parse("v{:d}, v{:d}", args);
			var pc = Parse("v{:d}, {:d}", args);

			if (pv != null)
			{
				int a = Int(pv[0]), b = Int(pv[1]);
				if (a == b)
				{
					W($"\tmov r14, r{b - 1}");
					W($"\tmul r{var - 1}, r14, r{a - 1}");
				}
				else if (var == a) W($"\tmul r{var - 1}, r{b - 1}, r{a - 1}");
				else W($"\tmul r{var - 1}, r{a - 1}, r{b - 1}");

				W($"\tmov r{var - 1}, r{var - 1}, asr #15");
				SignExtend(var - 1);
			}
			else
			{
				if (pc == null) throw Bad("mul", args);
				int a = Int(pc[0]), value = Int(pc[1]);
				if ((value & (value - 1)) == 0)
				{
					// Shift for 2^N muls.
					int shiftLeft = ShiftOf(value) - 15;
					if (shiftLeft == 0) W($"\t; NOOP -- val<<{shiftLeft + 15}>>15");
					else if (shiftLeft < 0) W($"\tmov r{var - 1}, r{a - 1}, asr #{-shiftLeft}\t; val<<{shiftLeft + 15}>>15");
					else W($"\tmov r{var - 1}, r{a - 1}, asl #{shiftLeft}\t; val<<{shiftLeft + 15}>>15");
				}
				else
				{
					if (value < 0) W($"\tmvn r14, #{-value - 1}");
					else W($"\tmov r14, #{value}");

					W($"\tmul r{var - 1}, r14, r{a - 1}");
					W($"\tmov r{var - 1}, r{var - 1}, asr #15");
					SignExtend(var - 1);
				}
			}
		}

		void FuncOscSaw(int var, string args)
		{
			var p = Parse("{:d}, {}, {}", args);
			if (p == null) throw Bad("osc_saw", args);
			int? freqV = V(p[1]), freqC = C(p[1]), gainV = V(p[2]), gainC = C(p[2]);

			W($"\tldr r4, [r10, #AK_OPINSTANCE+4*{instance}]");

			if (freqV != null) W($"\tadd r4, r4, r{freqV - 1}");
			else W($"\tadd r4, r4, #{freqC}");

			SignExtend(4);
			W($"\tstr r4, [r10, #AK_OPINSTANCE+4*{instance}]\t");

			VolVar(var, 5, gainV, gainC);
			instance += 1;
		}

		void FuncSh(int var, string args)
		{
			var p = Parse("{:d}, {}, {}", args);
			if (p == null) throw Bad("sh", args);
			int? valueV = V(p[1]), valueC = C(p[1]), stepV = V(p[2]), stepC = C(p[2]);

			W($"\tldr r6, [r10, #AK_OPINSTANCE+4*{instance + 0}]");
			W($"\tsubs r6, r6, #1");

			// buffer=val
			if (valueV != null) W($"\tstrlt r{valueV - 1}, [r10, #AK_OPINSTANCE+4*{instance + 1}]");
			else
			{
				W($"\tmovlt r14, #{valueC}");
				W($"\tstrlt r14, [r10, #AK_OPINSTANCE+4*{instance + 1}]");
			}

			// counter=step2
			if (stepV != null)
			{
				W($"\tmovlt r14, r{stepV - 1}, asl #24");
				W($"\tmovlt r14, r14, asr #24");
				W($"\tmovlt r4, r14");
				W($"\tmullt r14, r4, r14");
				W($"\tmovlt r6, r14, asr #2");
			}
			else W($"\tmovlt r6, #{(stepC * stepC) >> 2}");

			W($"\tstr r6, [r10, #AK_OPINSTANCE+4*{instance + 0}]");
			W($"\tldr r{var - 1}, [r10, #AK_OPINSTANCE+4*{instance + 1}]");

			instance += 2;
		}

		void FuncOscSine(int var, string args)
		{
			var p = Parse("{:d}, {}, {}", args);
			if (p == null) throw Bad("osc_sine", args);
			int? freqV = V(p[1]), freqC = C(p[1]), gainV = V(p[2]), gainC = C(p[2]);

			W($"\tldr r6, [r10, #AK_OPINSTANCE+4*{instance}]");

			if (freqV != null) W($"\tadd r6, r6, r{freqV - 1}");
			else W($"\tadd r6, r6, #{freqC}");

			W($"\tstr r6, [r10, #AK_OPINSTANCE+4*{instance}]");

			W($"\tsub r6, r6, #16384");
			SignExtend(6);

			W($"\tmovs r4, r6");
			W($"\trsblt r4, r4, #0");

			W($"\tsub r4, r11, r4\t; #32767");
			W($"\tmul r4, r6, r4");
			W($"\tmov r4, r4, asr #16");
			W($"\tmov r4, r4, asl #3");

			VolVar(var, 5, gainV, gainC);
			instance += 1;
		}

		void FuncOscTri(int var, string args)
		{
			var p = Parse("{:d}, {}, {}", args);
			if (p == null) throw Bad("osc_tri", args);
			int? freqV = V(p[1]), freqC = C(p[1]), gainV = V(p[2]), gainC = C(p[2]);

			W($"\tldr r6, [r10, #AK_OPINSTANCE+4*{instance}]");

			if (freqV != null) W($"\tadd r6, r6, r{freqV - 1}");
			else W($"\tadd r6, r6, #{freqC}");

			SignExtend(6);
			W($"\tstr r6, [r10, #AK_OPINSTANCE+4*{instance}]");
			W($"\tcmp r6, #0");
			W($"\tmvnmi r6, r6");
			W($"\tsub r6, r6, #16384");
			W($"\tmov r{var - 1}, r6, asl #1");

			Vol(var, gainV, gainC);
			instance += 1;
		}

		void FuncOscPulse(int var, string args)
		{
			var p = Parse("{:d}, {}, {}, {}", args);
			if (p == null) throw Bad("osc_pulse", args);
			int? freqV = V(p[1]), freqC = C(p[1]), gainV = V(p[2]), gainC = C(p[2]), dutyV = V(p[3]), dutyC = C(p[3]);

			W($"\tldr r6, [r10, #AK_OPINSTANCE+4*{instance}]");

			if (freqV != null) W($"\tadds r6, r6, r{freqV - 1}");
			else W($"\tadds r6, r6, #{freqC}");

			SignExtend(6);
			W($"\tstr r6, [r10, #AK_OPINSTANCE+4*{instance}]");

			if (dutyV != null)
			{
				W($"\tsub r4, r{dutyV - 1}, #63");
				W($"\tmov r4, r4, asl #9");
			}
			else
			{
				int value = (dutyC.Value - 63) << 9;
				if (value < 0) W($"\tmvn r4, #{-value - 1}\t; ({dutyC}-63)<<9");
				else W($"\tmov r4, #{value}\t; ({dutyC}-63)<<9");
			}

			W($"\tcmp r6, r4");
			W($"\tmvnlt r{var - 1}, r11\t; #-32768");
			W($"\tmovge r{var - 1}, r11\t; #32767");

			Vol(var, gainV, gainC);
			instance += 1;
		}

		void FuncOscNoise(int var, string args)
		{
			int? gainV = V(args), gainC = C(args);

			W($"\tldr r4, [r10, #AK_NOISESEEDS+0]");
			W($"\tldr r6, [r10, #AK_NOISESEEDS+4]");
			W($"\teor r4, r4, r6");
			W($"\tstr r4, [r10, #AK_NOISESEEDS+0]");

			W($"\tldr r12, [r10, #AK_NOISESEEDS+8]");
			W($"\tadd r12, r12, r6");
			W($"\tstr r12, [r10, #AK_NOISESEEDS+8]");

			W($"\tadd r6, r6, r4");
			W($"\tstr r6, [r10, #AK_NOISESEEDS+4]");

			SignExtend(12);
			VolVar(var, 13, gainV, gainC);
		}

		void FuncEnva(int var, string args)
		{
			var p = Parse("{:d}, {:d}, {:d}, {}", args);
			if (p == null) throw Bad("enva", args);
			int? gainV = V(p[3]), gainC = C(p[3]);

			W($"\tldr r6, [r10, #AK_OPINSTANCE+4*{instance}]");
			W($"\tmov r{var - 1}, r6, asr #8");
			W($"\tmov r4, #{DecayTable[Int(p[1])]}");    // attack
			W($"\tadd r6, r6, r4");
			W($"\tcmp r6, r11, asl #8");
			W($"\tmovgt r6, r11, asl #8");
			W($"\tstr r6, [r10, #AK_OPINSTANCE+4*{instance}]");

			Vol(var, gainV, gainC);
			instance += 1;
		}

		void FuncEnvd(int var, string args)
		{
			var p = Parse("{:d}, {:d}, {:d}, {}", args);
			if (p == null) throw Bad("envd", args);
			int? gainV = V(p[3]), gainC = C(p[3]);
			int sustain = Int(p[2]);

			W($"\tmov r4, #{DecayTable[Int(p[1])]}");
			W($"\tmul r6, r7, r4");
			W($"\tsubs r6, r11, r6, asr #8");
			if (sustain != 0)
			{
				W($"\tcmp r6, #{sustain << 8}");
				W($"\tmovle r6, #{sustain << 8}");
			}
			else W($"\tmovle r6, #0");
			W($"\tmov r{var - 1}, r6");

			Vol(var, gainV, gainC);
		}

		void FuncClone(int var, string args)
		{
			var p = Parse("{},{:d}, {:d}, {}", args) ?? Parse("{},{:d}, {:d}", args);
			if (p == null) throw Bad("clone", args);
			if (p[0] != "smp") Console.WriteLine("WARNING: Expected \"smp\"?");
			int sample = Int(p[1]), offset = Int(p[2]);
			if (p.Length > 3 && p[3].Trim() != "0") { CloneTranspose(var, sample, offset, p[3].Trim(), false); return; }

			if (offset != 0) W($"\tadd r{var - 1}, r7, #{offset}");
			else W($"\tmov r{var - 1}, r7");

			W($"\tldr r6, [r10, #AK_SMPADDR+4*{sample}]");
			W($"\tldr r4, [r10, #AK_SMPLEN+4*{sample}]");
			W($"\tcmp r{var - 1}, r4");
			W($"\tmovge r{var - 1}, #0");
			W($"\tldrltb r{var - 1}, [r6, r{var - 1}]");
			W($"\tmov r{var - 1}, r{var - 1}, asl #24");
			W($"\tmov r{var - 1}, r{var - 1}, asr #16");
		}

		// clone with a transpose (constant or v1-v4): the read position is a 16.16 counter of the
		// instance that advances by 65536 + (t < 0 ? t : 2 * t) while it is inside the source
		// (-32768 = half speed, 0 = original, 32767 = double speed), like the editor and the 68000 code
		void CloneTranspose(int var, int sample, int offset, string transpose, bool reverse)
		{
			int? transV = V(transpose), transC = C(transpose);
			if (transV == null && transC == null) throw Bad("clone: transpose", transpose);
			int r = var - 1;

			if (transV != null)
			{
				// the step first: the result variable may be the transpose variable
				W($"\tmov r5, r{transV - 1}, asl #16");
				W("\tmovs r5, r5, asr #16");
				W("\taddpl r5, r5, r5\t; 2 * t for t >= 0");
				W("\tadd r5, r5, #65536\t; step");
			}
			else Const(5, 65536 + (transC.Value < 0 ? transC.Value : 2 * transC.Value), "step");

			W($"\tldr r6, [r10, #AK_OPINSTANCE+4*{instance}]\t; position 16.16");
			W($"\tmov r{r}, r6, lsr #16");
			if (offset != 0) W($"\tadd r{r}, r{r}, #{offset}");
			W($"\tldr r4, [r10, #AK_SMPLEN+4*{sample}]");
			W($"\tldr r12, [r10, #AK_SMPADDR+4*{sample}]");
			W($"\tcmp r{r}, r4");
			W($"\tmovge r{r}, #0");
			W("\taddlt r6, r6, r5");
			W($"\tstrlt r6, [r10, #AK_OPINSTANCE+4*{instance}]");
			if (reverse)
			{
				W($"\tsublt r{r}, r4, r{r}");
				W($"\tsublt r{r}, r{r}, #1");
			}
			W($"\tldrltb r{r}, [r12, r{r}]");
			W($"\tmov r{r}, r{r}, asl #24");
			W($"\tmov r{r}, r{r}, asr #16");
			instance += 1;
		}

		void FuncImportedSample(int var, string args)
		{
			var p = Parse("{},{:d}", args);
			if (p == null) throw Bad("imported_sample", args);
			if (p[0] != "smp") Console.WriteLine("WARNING: Expected \"smp\"?");
			int sample = Int(p[1]);
			if (sample < extAdpcm.Length && extAdpcm[sample]) { ImportedAdpcm(var, sample); return; }

			W($"\tmov r{var - 1}, r7");
			W($"\tldr r6, [r10, #AK_EXTSMPADDR+4*{sample}]");
			W($"\tldr r4, [r10, #AK_EXTSMPLEN+4*{sample}]");
			W($"\tcmp r{var - 1}, r4");
			W($"\tmovge r{var - 1}, #0");
			W($"\tldrltb r{var - 1}, [r6, r{var - 1}]");
			W($"\tmov r{var - 1}, r{var - 1}, asl #24");
			W($"\tmov r{var - 1}, r{var - 1}, asr #16");
		}

		// a 4 bit ADPCM sample is decoded while it is played: state (value, step - 127) per instance,
		// the sample is the high byte of the 16 bit value of the decoder
		void ImportedAdpcm(int var, int sample)
		{
			usedAdpcm = true;
			string end = $"AdpcmEnd_{instNr}_{labelNr++}";

			W($"\tmov r{var - 1}, #0");
			W($"\tldr r4, [r10, #AK_EXTSMPLEN+4*{sample}]");
			W("\tcmp r7, r4");
			W($"\tbge {end}");
			W($"\tldr r6, [r10, #AK_EXTSMPADDR+4*{sample}]");
			W($"\tadd r12, r10, #AK_OPINSTANCE+4*{instance}");
			W("\tbl AK_AdpcmNext");
			W($"\tbic r{var - 1}, r4, #0xff");
			W($"{end}:");
			instance += 2;
		}

		void FuncCloneReverse(int var, string args)
		{
			var p = Parse("{},{:d}, {:d}, {}", args) ?? Parse("{},{:d}, {:d}", args);
			if (p == null) throw Bad("clone_reverse", args);
			if (p[0] != "smp") Console.WriteLine("WARNING: Expected \"smp\"?");
			int sample = Int(p[1]), offset = Int(p[2]);
			if (p.Length > 3 && p[3].Trim() != "0") { CloneTranspose(var, sample, offset, p[3].Trim(), true); return; }

			if (offset != 0) W($"\tadds r{var - 1}, r7, #{offset}");
			else W($"\tmovs r{var - 1}, r7");

			W($"\tldr r6, [r10, #AK_SMPADDR+4*{sample}]");
			W($"\tldr r4, [r10, #AK_SMPLEN+4*{sample}]");
			W($"\tcmp r{var - 1}, r4");
			W($"\tmovge r{var - 1}, #0");
			W($"\tsublt r{var - 1}, r4, r{var - 1}");
			W($"\tsublt r{var - 1}, r{var - 1}, #1\t; BUG FIX: -1 from length to avoid reading beyond end of sample");
			W($"\tldrltb r{var - 1}, [r6, r{var - 1}]");
			W($"\tmov r{var - 1}, r{var - 1}, asl #24");
			W($"\tmov r{var - 1}, r{var - 1}, asr #16");
		}

		void FuncCtrl(int var, string args)
		{
			int? src = V(args);
			if (src == null) throw Bad("ctrl", args);
			W($"\tmov r{var - 1}, r{src - 1}, asr #9");
			SignExtend(var - 1);
			W($"\tadd r{var - 1}, r{var - 1}, #64");
		}

		void FuncDlyCyc(int var, string args)
		{
			var p = Parse("{:d}, {}, {}, {}", args);
			if (p == null) throw Bad("dly_cyc", args);
			int? valV = V(p[1]);
			if (valV == null) throw Bad("dly_cyc: a variable expected", args);
			int? delayV = V(p[2]), delayC = C(p[2]), gainV = V(p[3]), gainC = C(p[3]);

			W($"\tmov r4, r{valV - 1}");
			Vol(5, gainV, gainC);

			W($"\tldr r6, [r10, #AK_OPINSTANCE+4*{instance}]");

			W($"\tstr r4, [r9, r6, lsl #2]");
			W($"\tadd r6, r6, #1");

			if (delayV != null) W($"\tcmp r6, r{delayV - 1}");
			else
			{
				W($"\tmov r14, #{delayC}");
				W($"\tcmp r6, r14");
			}

			W($"\tmovge r6, #0");

			W($"\tstr r6, [r10, #AK_OPINSTANCE+4*{instance}]");
			instance += 1;

			W($"\tldr r{var - 1}, [r9, r6, lsl #2]");

			W($"\tadd r9, r9, #2048*4\t; next temp buffer.");
			buffers += 1;
		}

		void FuncReverb(int var, string args)
		{
			var p = Parse("{}, {}, {}", args);
			if (p == null) throw Bad("reverb", args);
			int[] delays = { 557, 593, 641, 677, 709, 743, 787, 809 };

			for (int i = 0; i < 8; i++)
			{
				FuncCmbFltN(13, $"{16 + i}, {p[0]}, {delays[i]}, {p[1]}, {p[2]}");
				if (i == 0) W($"\tmov r5, r12");
				else if (i < 7) W($"\tadd r5, r5, r12");
				else W($"\tadd r{var - 1}, r5, r12");
			}

			Clamp(var);
		}

		void FuncCmbFltN(int var, string args)
		{
			var p = Parse("{:d}, {}, {}, {}, {}", args);
			if (p == null) throw Bad("cmb_flt_n", args);
			int? valV = V(p[1]);
			if (valV == null) throw Bad("cmb_flt_n: a variable expected", args);
			int? delayV = V(p[2]), delayC = C(p[2]), feedbackV = V(p[3]), feedbackC = C(p[3]), gainV = V(p[4]), gainC = C(p[4]);

			W($"\tldr r6, [r10, #AK_OPINSTANCE+4*{instance}]");

			W($"\tldr r4, [r9, r6, lsl #2]");

			Vol(5, feedbackV, feedbackC);

			SignExtend(valV.Value - 1);
			W($"\tadd r{var - 1}, r{valV - 1}, r4");
			Clamp(var);
			W($"\tstr r{var - 1}, [r9, r6, lsl #2]");
			W($"\tadd r6, r6, #1");

			if (delayV != null) W($"\tcmp r6, r{delayV - 1}");
			else
			{
				W($"\tmov r14, #{delayC}");
				W($"\tcmp r6, r14");
			}

			W($"\tmovge r6, #0");

			W($"\tstr r6, [r10, #AK_OPINSTANCE+4*{instance}]");
			instance += 1;

			Vol(var, gainV, gainC);

			W($"\tadd r9, r9, #2048*4\t; next temp buffer.");
			buffers += 1;
		}

		void FuncSvFltN(int var, string args)
		{
			var p = Parse("{:d}, {}, {}, {}, {}", args);
			if (p == null) throw Bad("sv_flt_n", args);
			int? valV = V(p[1]);
			if (valV == null) throw Bad("sv_flt_n: a variable expected", args);
			int? cutoffV = V(p[2]), cutoffC = C(p[2]), resonanceV = V(p[3]), resonanceC = C(p[3]);
			int? mode = C(p[4]);
			if (mode == null) throw Bad("sv_flt_n: mode", args);

			W($"\tadd r14, r10, #AK_OPINSTANCE+4*({instance}+AK_LPF)");
			W("\tldmia r14, {r4-r6}");

			W($"\tmov r14, r6, asr #7");

			if (cutoffV != null) W($"\tmla r4, r{cutoffV - 1}, r14, r4");
			else
			{
				W($"\tmov r12, #{cutoffC}");
				W($"\tmla r4, r14, r12, r4");
			}

			Clamp(5); // r4
			W($"\tstr r4, [r10, #AK_OPINSTANCE+4*({instance}+AK_LPF)]");

			if (resonanceV != null) W($"\tmul r14, r{resonanceV - 1}, r14");
			else
			{
				W($"\tmov r12, #{resonanceC}");
				W($"\tmul r14, r12, r14");
			}

			W($"\tmov r12, r{valV - 1}");
			W($"\tsub r12, r12, r4");
			W($"\tsub r5, r12, r14");

			Clamp(6); // r5
			W($"\tstr r5, [r10, #AK_OPINSTANCE+4*({instance}+AK_HPF)]");

			W($"\tmov r14, r5, asr #7");

			if (cutoffV != null) W($"\tmla r6, r{cutoffV - 1}, r14, r6");
			else
			{
				W($"\tmov r12, #{cutoffC}");
				W($"\tmla r6, r12, r14, r6");
			}

			Clamp(7); // r6
			W($"\tstr r6, [r10, #AK_OPINSTANCE+4*({instance}+AK_BPF)]");

			if (mode == 0) W($"\tmov r{var - 1}, r4");
			else if (mode == 1) W($"\tmov r{var - 1}, r5");
			else if (mode == 2) W($"\tmov r{var - 1}, r6");
			else if (mode == 3)
			{
				W($"\tadd r{var - 1}, r5, r6");
				Clamp(var);
			}

			instance += 3;
		}

		void ChordNote(int note1, int note2, int note3, int noteNum, int multVal, int multShift)
		{
			string chordVar;
			if (note1 == noteNum) chordVar = "AK_CHORD1";
			else if (note2 == noteNum) chordVar = "AK_CHORD2";
			else if (note3 == noteNum) chordVar = "AK_CHORD3";
			else return;

			W($"\tldr r5, [r10, #AK_OPINSTANCE+4*({instance}+{chordVar})]");

			W($"\tcmp r12, r5, lsr #{8 + multShift}");
			W($"\tldrgtb r14, [r4, r5, lsr #{8 + multShift}]");
			W($"\tmovle r14, #0");

			W($"\tadd r5, r5, #{multVal << 8}");
			W($"\tstr r5, [r10, #AK_OPINSTANCE+4*({instance}+{chordVar})]");

			W($"\tmov r14, r14, asl #24");
			W($"\tadd r6, r6, r14, asr #{24 - 7}");
		}

		void FuncChordgen(int var, string args)
		{
			var p = Parse("{:d}, {:d}, {:d}, {:d}, {:d}, {}", args);
			if (p == null) throw Bad("chordgen", args);
			int sampleNr = Int(p[1]), note1 = Int(p[2]), note2 = Int(p[3]), note3 = Int(p[4]);
			int? shiftV = V(p[5]), shiftC = C(p[5]);

			W($"\tldr r4, [r10, #AK_SMPADDR+4*{sampleNr}]");
			W($"\tldr r12, [r10, #AK_SMPLEN+4*{sampleNr}]");
			W($"\tcmp r7, r12");
			W($"\tldrltb r6, [r4, r7]");
			W($"\tmovge r6, #0");
			W($"\tmov r6, r6, asl #24");
			W($"\tmov r6, r6, asr #{24 - 7}");

			if (shiftV != null)
			{
				// the shift is a byte in the editor (akp2arc.py took the whole variable)
				W($"\tand r14, r{shiftV - 1}, #0xff");
				W("\tadd r4, r4, r14");
				W("\tsub r12, r12, r14");
			}
			else if (shiftC != 0)
			{
				W($"\tadd r4, r4, #{shiftC}");
				W($"\tsub r12, r12, #{shiftC}");
			}

			int[] mult = { 271, 287, 304, 322, 342, 362, 383, 203, 215, 228, 483, 2 };
			int[] shift = { 8, 8, 8, 8, 8, 8, 8, 7, 7, 7, 8, 0 };
			for (int n = 1; n <= 12; n++) ChordNote(note1, note2, note3, n, mult[n - 1], shift[n - 1]);

			W($"\tmov r{var - 1}, r6");
			Clamp(var);
			instance += 3;
		}

		void FuncDistortion(int var, string args)
		{
			var p = Parse("{}, {}", args);
			if (p == null) throw Bad("distortion", args);
			int? valV = V(p[0]);
			if (valV == null) throw Bad("distortion: a variable expected", args);
			int? gainV = V(p[1]), gainC = C(p[1]);

			if (gainV != null) W($"\tmul r14, r{valV - 1}, r{gainV - 1}");
			else
			{
				W($"\tmov r14, #{gainC}");
				W($"\tmul r14, r{valV - 1}, r14");
			}

			W($"\tmov r14, r14, asr #5");
			Clamp(15);
			W($"\tmov r14, r14, asr #1");

			W($"\tmovs r4, r14");
			W($"\trsbmi r4, r4, #0\t; abs(val)");
			W($"\tsub r4, r11, r4");
			W($"\tmul r4, r14, r4");
			W($"\tmov r4, r4, asr #16");
			W($"\tmov r{var - 1}, r4, asl #3");
		}

		void FuncOnepoleFlt(int var, string args)
		{
			var p = Parse("{:d}, {}, {}, {}", args);
			if (p == null) throw Bad("onepole_flt", args);
			int? valV = V(p[1]);
			if (valV == null) throw Bad("onepole_flt: a variable expected", args);
			int? cutoffV = V(p[2]), cutoffC = C(p[2]);
			int? mode = C(p[3]);
			if (mode == null) throw Bad("onepole_flt: mode", args);

			W($"\tldr r4, [r10, #AK_OPINSTANCE+4*{instance}]\t; pole");
			W($"\tmov r6, r4, asr #7");
			W($"\tmov r14, r{valV - 1}, asr #7");

			if (cutoffV != null)
			{
				W($"\tmul r5, r{cutoffV - 1}, r14");
				W($"\tmul r6, r{cutoffV - 1}, r6");
			}
			else
			{
				W($"\tmov r12, #{cutoffC}");
				W($"\tmul r5, r12, r14");
				W($"\tmul r6, r12, r6");
			}

			W($"\tsub r4, r4, r6");
			W($"\tadd r4, r4, r5");
			Clamp(5);
			W($"\tstr r4, [r10, #AK_OPINSTANCE+4*{instance}]\t; pole");

			if (mode == 0) W($"\tmov r{var - 1}, r4");
			else if (mode == 1)
			{
				W($"\tsub r{var - 1}, r{valV - 1}, r4");
				Clamp(var);
			}
			else W($"\tmov r{var - 1}, #0");

			instance += 1;
		}

		void FuncAdsr(int var, string args)
		{
			var p = Parse("{:d}, {:d}, {:d}, {:d}, {:d}, {:d}, {:d}", args);
			if (p == null) throw Bad("adsr", args);

			adsrValues.Add(p.Select(Int).ToArray());
			int adsrInstance = adsrValues.Count - 1;
			string id = $"{instNr}_{instance}";

			W($"\tldr r4, [r10, #AK_OPINSTANCE+4*{instance}]\t; val");
			W($"\tldrb r14, [r10, #AK_OPINSTANCE+4*{instance + 1}]\t; mode");

			W($"\tcmp r14, #ADSR_Decay");
			W($"\tbeq ADSR_D_{id}");
			W($"\tcmp r14, #ADSR_Sustain");
			W($"\tbeq ADSR_S_{id}");
			W($"\tcmp r14, #ADSR_Release");
			W($"\tbeq ADSR_R_{id}\n");

			W($"ADSR_A_{id}:\t; Attack");
			W($"\tldr r5, [r10, #AK_ADSRVALUES+24*{adsrInstance}+4*0]\t; attackAmount");
			W($"\tadd r4, r4, r5");
			W($"\tldr r6, [r10, #AK_ADSRVALUES+24*{adsrInstance}+4*5]\t; peak");
			W($"\tcmp r4, r6");
			W($"\tmovge r4, r6");
			W($"\tmovge r14, #ADSR_Decay");
			W($"\tb ADSR_End_{id}\n");

			W($"ADSR_D_{id}:\t; Decay");
			W($"\tldr r5, [r10, #AK_ADSRVALUES+24*{adsrInstance}+4*1]\t; decayAmount");
			W($"\tsub r4, r4, r5");
			W($"\tldr r6, [r10, #AK_ADSRVALUES+24*{adsrInstance}+4*2]\t; sustainLevel");
			W($"\tcmp r4, r6");
			W($"\tmovle r4, r6");
			W($"\tmovle r14, #ADSR_Sustain");
			W($"\tb ADSR_End_{id}\n");

			W($"ADSR_S_{id}:\t; Sustain");
			W($"\tldr r5, [r10, #AK_OPINSTANCE+4*{instance + 2}]\t; SustainCounter");
			W($"\tadd r5, r5, #1");
			W($"\tstr r5, [r10, #AK_OPINSTANCE+4*{instance + 2}]\t; SustainCounter");
			W($"\tldr r6, [r10, #AK_ADSRVALUES+24*{adsrInstance}+4*3]\t; sustainLength");
			W($"\tcmp r5, r6");
			W($"\tmovgt r14, #ADSR_Release");
			W($"\tb ADSR_End_{id}\n");

			W($"ADSR_R_{id}:\t; Release");
			W($"\tldr r5, [r10, #AK_ADSRVALUES+24*{adsrInstance}+4*4]\t; releaseAmount");
			W($"\tsubs r4, r4, r5");
			W($"\tmovle r4, #0\n");

			W($"ADSR_End_{id}:");
			W($"\tstr r4, [r10, #AK_OPINSTANCE+4*{instance}]\t; val");
			W($"\tstrb r14, [r10, #AK_OPINSTANCE+4*{instance + 1}]\t; mode");
			W($"\tmov r{var - 1}, r4, asr #8");

			instance += 3;
		}

		// samplemixer(instance, rowlength, notes, (row, track, source, step, volume) * notes): every
		// rowlength samples the next of the 16 rows starts; a note restarts the voice of its track
		// (source -1 = silent) at position 0 with a step (16.16) and a volume 0..64. A voice plays
		// its source once. Output: sum of sample * volume of the voices, times 4, clamped.
		// Instance: row counter, row, 4 voices of address, length, position, step, volume.
		void FuncSampleMixer(int var, string args)
		{
			int[] v;
			try { v = args.Split(',').Select(Int).ToArray(); }
			catch (FormatException) { throw Bad("samplemixer", args); }
			if (v.Length < 3 || v.Length != 3 + v[2] * 5) throw Bad("samplemixer", args);
			int rowLength = v[1], notes = v[2], first = instance;
			string id = $"{instNr}_{labelNr++}";

			var rows = new SortedDictionary<int, List<int[]>>();
			var tracksUsed = new bool[4];
			for (int n = 0; n < notes; n++)
			{
				int[] note = v.Skip(3 + n * 5).Take(5).ToArray();
				if (note[1] < 0 || note[1] > 3) throw Bad("samplemixer: track", args);
				if (!rows.ContainsKey(note[0])) rows[note[0]] = new List<int[]>();
				rows[note[0]].Add(note);
				tracksUsed[note[1]] = true;
			}

			// next row: start the voices of its notes
			W($"\tldr r4, [r10, #AK_OPINSTANCE+4*{first}]\t; row counter");
			W("\tcmp r4, #0");
			W($"\tbne MixNoRow_{id}");
			W($"\tldr r5, [r10, #AK_OPINSTANCE+4*{first + 1}]\t; row");
			W("\tcmp r5, #16");
			W($"\tbhs MixNoRow_{id}");
			W("\tadd r6, r5, #1");
			W($"\tstr r6, [r10, #AK_OPINSTANCE+4*{first + 1}]");
			Const(4, rowLength, "row length");
			foreach (var row in rows)
			{
				W($"\tcmp r5, #{row.Key}");
				W($"\tbne MixRow{row.Key}_{id}");
				foreach (int[] note in row.Value)
				{
					int voice = first + 2 + note[1] * 5;
					if (note[2] < 0)
					{
						W("\tmov r6, #0");
						W($"\tstr r6, [r10, #AK_OPINSTANCE+4*{voice + 1}]\t; track {note[1] + 1} falls silent");
						continue;
					}
					W($"\tldr r6, [r10, #AK_SMPADDR+4*{note[2]}]");
					W($"\tstr r6, [r10, #AK_OPINSTANCE+4*{voice}]\t; track {note[1] + 1}: address");
					W($"\tldr r6, [r10, #AK_SMPLEN+4*{note[2]}]");
					W($"\tstr r6, [r10, #AK_OPINSTANCE+4*{voice + 1}]\t; length");
					W("\tmov r6, #0");
					W($"\tstr r6, [r10, #AK_OPINSTANCE+4*{voice + 2}]\t; position");
					Const(6, note[3], "step");
					W($"\tstr r6, [r10, #AK_OPINSTANCE+4*{voice + 3}]");
					W($"\tmov r6, #{note[4]}");
					W($"\tstr r6, [r10, #AK_OPINSTANCE+4*{voice + 4}]\t; volume");
				}
				W($"MixRow{row.Key}_{id}:");
			}
			W($"MixNoRow_{id}:");
			W("\tsub r4, r4, #1");
			W($"\tstr r4, [r10, #AK_OPINSTANCE+4*{first}]");

			// mix the voices
			W("\tmov r5, #0\t; sum");
			for (int t = 0; t < 4; t++)
			{
				if (!tracksUsed[t]) continue;
				W($"\tadd r14, r10, #AK_OPINSTANCE+4*{first + 2 + t * 5}\t; track {t + 1}");
				W("\tldmia r14, {r4, r6, r12}\t; address, length, position");
				W("\tcmp r6, r12, lsr #16");
				W("\tldrhib r6, [r4, r12, lsr #16]");
				W("\tmovls r6, #0\t; behind the end of the source");
				W("\tldrhi r4, [r14, #12]\t; step");
				W("\taddhi r12, r12, r4");
				W("\tstrhi r12, [r14, #8]");
				W("\tmov r6, r6, asl #24");
				W("\tmov r6, r6, asr #24");
				W("\tldr r4, [r14, #16]\t; volume");
				W("\tmla r5, r6, r4, r5");
			}
			W($"\tmov r{var - 1}, r5, asl #2");
			Clamp(var);

			instance += 22;
		}

		// v = bytebeat(gain, formula): the formula (Bytebeat.cs) is an unsigned 16 bit expression
		// over t = r7. Its value is built in r4 (r5 and the stack hold the left operands), the low
		// byte is the sample (centred), then the gain like osc_saw.
		void FuncBytebeat(int var, string args)
		{
			int comma = args.IndexOf(',');
			if (comma < 0) throw Bad("bytebeat", args);
			string gain = args.Substring(0, comma).Trim();
			string formula = args.Substring(comma + 1);
			Bytebeat.Node node;
			string error;
			if (!Bytebeat.TryParse(formula, out node, out error)) throw Bad("bytebeat formula (" + error + ")", formula);
			if (V(gain) == null && C(gain) == null) throw Bad("bytebeat: gain", gain);

			BytebeatExpr(node);
			W("\tmov r4, r4, asl #24");
			W("\teor r4, r4, #0x80000000");
			W("\tmov r4, r4, asr #16\t; the low byte, centred");
			VolVar(var, 5, V(gain), C(gain));
		}

		void Mask16()
		{
			W("\tmov r4, r4, lsl #16");
			W("\tmov r4, r4, lsr #16");
		}

		// the value of a node in r4 (0..65535)
		void BytebeatExpr(Bytebeat.Node n)
		{
			switch (n.Op)
			{
				case Bytebeat.Op.Const:
					if (n.Value <= 255) W($"\tmov r4, #{n.Value}");
					else
					{
						W($"\tmov r4, #0x{n.Value & 0xff00:x}");
						if ((n.Value & 255) != 0) W($"\torr r4, r4, #0x{n.Value & 255:x}");
					}
					return;
				case Bytebeat.Op.T:
					W("\tmov r4, r7, lsl #16");
					W("\tmov r4, r4, lsr #16\t; t");
					return;
				case Bytebeat.Op.Neg:
					BytebeatExpr(n.A);
					W("\trsb r4, r4, #0");
					Mask16();
					return;
				case Bytebeat.Op.Not:
					BytebeatExpr(n.A);
					W("\tmvn r4, r4");
					Mask16();
					return;
				case Bytebeat.Op.LogNot:
					BytebeatExpr(n.A);
					W("\tcmp r4, #0");
					W("\tmoveq r4, #1");
					W("\tmovne r4, #0");
					return;
				case Bytebeat.Op.Cond:
				{
					string id = $"{instNr}_{labelNr++}";
					BytebeatExpr(n.A);
					W("\tcmp r4, #0");
					W($"\tbeq BBElse_{id}");
					BytebeatExpr(n.B);
					W($"\tb BBEnd_{id}");
					W($"BBElse_{id}:");
					BytebeatExpr(n.C);
					W($"BBEnd_{id}:");
					return;
				}
			}

			// binary: a in r5, b in r4
			BytebeatExpr(n.A);
			if (n.B.Op == Bytebeat.Op.Const || n.B.Op == Bytebeat.Op.T)
			{
				W("\tmov r5, r4");
				BytebeatExpr(n.B);
			}
			else
			{
				W("\tstr r4, [sp, #-4]!");
				BytebeatExpr(n.B);
				W("\tldr r5, [sp], #4");
			}

			switch (n.Op)
			{
				case Bytebeat.Op.Mul: W("\tmul r4, r5, r4"); Mask16(); break;
				case Bytebeat.Op.Div: usedDivide = true; W("\tbl AK_Divide"); break;
				case Bytebeat.Op.Mod: usedDivide = true; W("\tbl AK_Divide"); W("\tmov r4, r5"); break;
				case Bytebeat.Op.Add: W("\tadd r4, r5, r4"); Mask16(); break;
				case Bytebeat.Op.Sub: W("\tsub r4, r5, r4"); Mask16(); break;
				case Bytebeat.Op.Shl:
				case Bytebeat.Op.Shr:
					// counts are taken & 63 and give 0 from 16 on
					W("\tand r4, r4, #63");
					W("\tcmp r4, #16");
					W(n.Op == Bytebeat.Op.Shl ? "\tmovlo r4, r5, lsl r4" : "\tmovlo r4, r5, lsr r4");
					W("\tmovhs r4, #0");
					if (n.Op == Bytebeat.Op.Shl) Mask16();
					break;
				case Bytebeat.Op.Lt: Compare("lo", "hs"); break;
				case Bytebeat.Op.Le: Compare("ls", "hi"); break;
				case Bytebeat.Op.Gt: Compare("hi", "ls"); break;
				case Bytebeat.Op.Ge: Compare("hs", "lo"); break;
				case Bytebeat.Op.Eq: Compare("eq", "ne"); break;
				case Bytebeat.Op.Ne: Compare("ne", "eq"); break;
				case Bytebeat.Op.And: W("\tand r4, r5, r4"); break;
				case Bytebeat.Op.Xor: W("\teor r4, r5, r4"); break;
				case Bytebeat.Op.Or: W("\torr r4, r5, r4"); break;
				case Bytebeat.Op.LogAnd:
					W("\tcmp r5, #0");
					W("\tcmpne r4, #0");
					W("\tmovne r4, #1");
					W("\tmoveq r4, #0");
					break;
				default:    // LogOr
					W("\torrs r4, r5, r4");
					W("\tmovne r4, #1");
					break;
			}
		}

		// unsigned comparison of a (r5) and b (r4): 1 or 0
		void Compare(string yes, string no)
		{
			W("\tcmp r5, r4");
			W($"\tmov{yes} r4, #1");
			W($"\tmov{no} r4, #0");
		}

		// ---- the instruments --------------------------------------------------------------------

		void WriteInstruments()
		{
			instNr = 0;
			buffers = 0;

			foreach (var inst in instruments)
			{
				instNr += 1;

				Console.WriteLine($"Instrument {instNr} '{inst.Name}'");

				W(";----------------------------------------------------------------------------");
				W($"; Instrument {instNr} - '{inst.Name}'");
				W(";----------------------------------------------------------------------------\n");

				if (instNr == 1) W("\tmov r4, #AK_MaxTempBuffers\t; buffers to clear");
				else W($"\tmov r4, #{buffers}\t; buffers to clear");
				W("\tbl AK_ResetVars");
				W("\tmov r7, #0\t; Sample byte count");

				W("\n\tAK_PROGRESS\n");
				W($"Inst{instNr}Loop:");

				instance = 0;
				dvalue = 0;
				buffers = 0;

				foreach (string command in inst.Commands)
				{
					var cmd = Parse("v{:d} = {}({})", command);
					if (cmd == null) throw Bad("node", command);
					int var = Int(cmd[0]);
					string args = cmd[2];

					W($"\t; {command};");
					switch (cmd[1])
					{
						case "add": FuncAdd(var, args); break;
						case "mul": FuncMul(var, args); break;
						case "osc_saw": FuncOscSaw(var, args); break;
						case "sh": FuncSh(var, args); break;
						case "osc_tri": FuncOscTri(var, args); break;
						case "enva": FuncEnva(var, args); break;
						case "envd": FuncEnvd(var, args); break;
						case "osc_sine": FuncOscSine(var, args); break;
						case "osc_pulse": FuncOscPulse(var, args); break;
						case "osc_noise": FuncOscNoise(var, args); break;
						case "clone": FuncClone(var, args); break;
						case "clone_reverse": FuncCloneReverse(var, args); break;
						case "ctrl": FuncCtrl(var, args); break;
						case "dly_cyc": FuncDlyCyc(var, args); break;
						case "cmb_flt_n": FuncCmbFltN(var, args); break;
						case "sv_flt_n": FuncSvFltN(var, args); break;
						case "chordgen": FuncChordgen(var, args); break;
						case "reverb": FuncReverb(var, args); break;
						case "imported_sample": FuncImportedSample(var, args); break;
						case "vol": FuncVol(var, args); break;
						case "distortion": FuncDistortion(var, args); break;
						case "onepole_flt": FuncOnepoleFlt(var, args); break;
						case "adsr": FuncAdsr(var, args); break;
						case "samplemixer": FuncSampleMixer(var, args); break;
						case "bytebeat": FuncBytebeat(var, args); break;
						default:
							Console.WriteLine($"WARNING: Unimplemented node command '{cmd[1]}'");
							W($"; TODO: Implement node command '{cmd[1]}'.");
							break;
					}

					W("");
				}

				maxInstances = Math.Max(maxInstances, instance);
				maxDvalues = Math.Max(maxDvalues, dvalue);
				maxBuffers = Math.Max(maxBuffers, buffers);

				if (buffers > 0) W($"\tsub r9, r9, #2048*4*{buffers}\t; reset temp buffer base.");

				W("\tmov r4, r0, asr #8");
				W("\tstrb r4, [r8], #1");
				W("\n\tAK_FINE_PROGRESS\n");
				W("\tadd r7, r7, #1");
				W($"\tldr r4, [r10, #AK_SMPLEN+4*{instNr - 1}]");
				W("\tcmp r7, r4");
				W($"\tblt Inst{instNr}Loop\n");

				if (inst.Loop) LoopGenerator(inst);
			}

			Console.WriteLine($"{instNr} total instruments.");
			Console.WriteLine($"{totalSmpLen} total sample size.");
			Console.WriteLine($"{totalExtSmpLen} external sample size.");

			W("; ============================================================================\n");

			W(".if AK_CLEAR_FIRST_2_BYTES");
			W("\t; Clear first 2 bytes of each sample");
			W("\tadr r4, AK_SmpAddr");
			W($"\tmov r7, #{instruments.Count}\t; Num instruments.");
			W("\tmov r0, #0");
			W(".0:");
			W("\tldr r6, [r4], #4");
			W("\tstrb r0, [r6]");
			W("\tstrb r0, [r6, #1]");
			W("\tsubs r7, r7, #1");
			W("\tbne .0");
			W(".endif\n");

			W("\tldr pc, [sp], #4\n");
		}

		// The end of the loop is crossfaded with the part in front of the loop start, as long as
		// possible: fade = min(repeat length, repeat offset), like the editor and the 68000 code
		// since V1.14 (akp2arc.py had the loop generator of the original editor):
		//   blend = (src1[n] * rampdown + src2[n] * rampup) >> 15, ramps in steps of (32767 << 8) / fade
		void LoopGenerator(Instrument inst)
		{
			W(";----------------------------------------------------------------------------");
			W($"; Instrument {instNr} - Loop Generator (Offset: {inst.RepeatOffset} Length: {inst.RepeatLength})");
			W(";----------------------------------------------------------------------------\n");

			int fade = Math.Min(inst.RepeatLength, inst.RepeatOffset);
			if (fade <= 0)
			{
				W("\t; no crossfade: the loop starts at the beginning of the sample\n");
				return;
			}
			int start = inst.RepeatOffset + inst.RepeatLength - fade;
			int delta = (32767 << 8) / fade;

			Const(7, fade, "samples to crossfade");
			W($"\tldr r6, [r10, #AK_SMPADDR+4*{instNr - 1}]");
			W($"\tadd r6, r6, #{start}\t; src1: the end of the loop");
			W($"\tsub r4, r6, #{inst.RepeatLength}\t; src2: the part in front of the loop");
			Const(5, delta, $"(32767<<8)/{fade}");
			W("\tmov r14, #0\t; rampup");
			W("\tmov r12, r11, lsl #8\t; rampdown");
			W($"LoopGen_{instNr}:");

			W("\tmov r3, r14, lsr #8");
			W("\tmov r2, r12, lsr #8");

			W("\tldrb r1, [r6]");
			W("\tmov r1, r1, asl #24");
			W("\tmov r1, r1, asr #24");

			W("\tldrb r0, [r4], #1");
			W("\tmov r0, r0, asl #24");
			W("\tmov r0, r0, asr #24");

			W("\tmul r0, r3, r0");
			W("\tmul r1, r2, r1");
			W("\tadd r0, r0, r1");
			W("\tmov r0, r0, asr #15");

			W("\tstrb r0, [r6], #1");

			W("\tadd r14, r14, r5");
			W("\tsub r12, r12, r5");
			W("\n\tAK_FINE_PROGRESS\n");
			W("\tsubs r7, r7, #1");
			W($"\tbne LoopGen_{instNr}\n");
		}

		// ---- variables --------------------------------------------------------------------------

		bool usedAdpcmSlots => extAdpcm.Any(a => a);

		void WriteVars()
		{
			W("; ============================================================================\n");

			W("AK_ResetVars:");

			W("\tmov r0, #0");
			W("\tmov r1, #0");
			W("\tmov r2, #0");
			W("\tmov r3, #0");

			W("\tmovs r4, r4, lsl #9\t; num_buffers * 2048 * 4 / 16 = num_buffers * 512");
			W("\tbeq .2");
			W("\tmov r6, r9\t; Clear temp buffers (delay loop).");
			W(".1:");
			W("\tstmia r6!, {r0-r3}");
			W("\tsubs r4, r4, #1");
			W("\tbne .1");

			W(".2:");
			W("\tadd r6, r10, #AK_OPINSTANCE");
			W($"\t.rept {maxInstances}\t; Max OpInstance values.");
			W("\tstr r0, [r6], #4");
			W("\t.endr");
			if (maxDvalues > 0)
			{
				W("\tmov r4, r11, lsl #16\t; 32767<<16");
				W($"\t.rept {maxDvalues}\t; Max envd values.");
				W("\tstr r4, [r6], #4");
				W("\t.endr");
			}
			else W("\t; No envd values to reset.");
			W("\tmov pc, lr\n");

			if (usedAdpcm)
			{
				// short AK_AdpcmNext(BYTE* data, int smp, LONG* state): Yamaha style 4 bit ADPCM, two
				// nibbles per byte, the first sample in the high nibble (see Adpcm.cs of the editor)
				W("; ============================================================================");
				W("; r6 = data, r7 = sample number, r12 = state (value, step - 127) -> r4 = 16 bit value; uses r4-r6");
				W("AK_AdpcmNext:");
				W("\tstr lr, [sp, #-4]!");
				W("\tldrb r4, [r6, r7, lsr #1]");
				W("\ttst r7, #1");
				W("\tmoveq r4, r4, lsr #4");
				W("\tand r4, r4, #15\t; nibble");
				W("\tldr r5, [r12, #4]");
				W("\tadd r5, r5, #127\t; step");
				W("\tand r6, r4, #7\t; m");
				W("\tadd r14, r6, r6");
				W("\tadd r14, r14, #1");
				W("\tmul r14, r5, r14");
				W("\tmov r14, r14, lsr #3\t; diff = ((2m + 1) * step) >> 3");
				W("\ttst r4, #8");
				W("\trsbne r14, r14, #0");
				W("\tldr r4, [r12]");
				W("\tadd r4, r4, r14");
				W("\tcmp r4, r11\t\t; #32767");
				W("\tmovgt r4, r11");
				W("\tcmn r4, r11\t\t; #-32768");
				W("\tmvnlt r4, r11");
				W("\tstr r4, [r12]");
				W("\tadr r14, AK_AdpcmSteps");
				W("\tldrb r14, [r14, r6]");
				W("\tmul r5, r14, r5");
				W("\tmov r5, r5, lsr #6\t; step = step * steps[m] >> 6, 127..24576");
				W("\tcmp r5, #24576");
				W("\tmovhi r5, #24576");
				W("\tcmp r5, #127");
				W("\tmovlo r5, #127");
				W("\tsub r5, r5, #127");
				W("\tstr r5, [r12, #4]");
				W("\tldr pc, [sp], #4");
				W("AK_AdpcmSteps:");
				W("\t.byte 57,57,57,57,77,102,128,153\n");
			}

			if (usedDivide)
			{
				// the ARM2 has no divide: 16 steps of shift and subtract
				W("; ============================================================================");
				W("; r5 = a, r4 = b (0..65535) -> r4 = a / b, r5 = a % b, both 0 for b = 0; uses r6, r12");
				W("AK_Divide:");
				W("\tmovs r6, r4, lsl #15");
				W("\tmov r4, #0");
				W("\tmoveq r5, #0");
				W("\tmoveq pc, lr");
				W("\tmov r12, #16");
				W("AK_DivideLoop:");
				W("\tcmp r5, r6");
				W("\tsubhs r5, r5, r6");
				W("\tadc r4, r4, r4");
				W("\tmov r6, r6, lsr #1");
				W("\tsubs r12, r12, #1");
				W("\tbne AK_DivideLoop");
				W("\tmov pc, lr\n");
			}

			W("; ============================================================================\n");

			W("AK_Vars:");
			W("AK_SmpLen:");
			for (int i = 0; i < instruments.Count; i++)
				W($"\t.long 0x{instruments[i].Length:x8}\t; Instrument {i + 1} Length");
			for (int i = instruments.Count + 1; i < 32; i++)
				W($"\t.long 0x00000000\t; Instrument {i} Length");

			W("AK_ExtSmpLen:");
			for (int i = 1; i <= extSampleLens.Length; i++)
				W($"\t.long 0x{extSampleLens[i - 1]:x8}\t; External Sample {i} Length" + (extAdpcm[i - 1] ? " (4 bit ADPCM)" : ""));

			if (usedAdpcmSlots)
			{
				// where the samples lie in the block: the delta coded ones first, the ADPCM ones after them
				W("AK_ExtSmpOffset:");
				int deltaOffset = 0, adpcmOffset = extDeltaLen;
				for (int i = 0; i < extSampleLens.Length; i++)
				{
					W($"\t.long {(extAdpcm[i] ? adpcmOffset : deltaOffset)}\t; External Sample {i + 1}");
					if (extAdpcm[i]) adpcmOffset += (extSampleLens[i] + 1) / 2; else deltaOffset += extSampleLens[i];
				}
			}

			W("AK_NoiseSeeds:");
			W("\t.long 0x67452301\n\t.long 0xefcdab89\n\t.long 0x00000000");

			W("AK_SmpAddr:");
			W("\t.skip AK_MaxInstruments*4");

			W("AK_ExtSmpAddr:");
			W("\t.skip AK_MaxExtSamples*4");

			W("AK_OpInstance:");
			W($"\t.skip {maxInstances}*4");

			W("AK_EnvDValue:\n\t; NB. Must follow AK_OpInstance!");
			if (maxDvalues > 0) W($"\t.skip {maxDvalues}*4");

			W("AK_ADSRValues:");
			foreach (int[] p in adsrValues)
			{
				W($"\t.long {p[1]}\t; attackAmount");
				W($"\t.long {p[2]}\t; decayAmount");
				W($"\t.long {p[3]}\t; sustainLevel");
				W($"\t.long {p[4]}\t; sustainLength");
				W($"\t.long {p[5]}\t; releaseAmount");
				W($"\t.long {p[6]}\t; peak");
			}

			W("\n; ============================================================================\n");

			W($".equ AK_MaxTempBuffers, {maxBuffers}");
			W($".equ AK_TempBufferSize, {maxBuffers * 2048 * 4}");

			W("\n; ============================================================================");
		}

		// ---- header -----------------------------------------------------------------------------

		void WriteHeader()
		{
			// Standard header.
			W("; ============================================================================");
			W("; Generated with Aklang2Asm V1.22 (-arm), by Dan/Lemon.");
			W("; ARM code generator: Aklang2Acorn.py v1.0, by kieran/Bitshifters 2024 (https://github.com/kieranhj/archieklang).");
			W("; Based on Alcatraz Amigaklang rendering core. (c) Jochen 'Virgill' Feldkötter 2020-2024.");
			W($"; Input Aklang script = '{source}'");
			W(";");
			W("; Define macro 'AK_PROGRESS' for whatever per-instrument progress callback you require (if any).");
			W("; Define macro 'AK_FINE_PROGRESS' for whatever per-byte progress callback you require (if any).");
			W("; Define symbol 'AK_CLEAR_FIRST_2_BYTES' to true to zero out the first two bytes of each sample, Amiga style.");
			W("; Call 'AK_Generate' with the registers listed below.");
			W("; ============================================================================\n");

			W(".equ AK_MaxInstruments,\t\t31");
			W($".equ AK_MaxExtSamples,\t\t{extSampleLens.Length}\n");

			W(".equ AK_LPF,\t\t\t\t0");
			W(".equ AK_HPF,\t\t\t\t1");
			W(".equ AK_BPF,\t\t\t\t2\n");

			W(".equ AK_CHORD1,\t\t\t\t0");
			W(".equ AK_CHORD2,\t\t\t\t1");
			W(".equ AK_CHORD3,\t\t\t\t2\n");

			W(".equ ADSR_Attack,\t\t\t0");
			W(".equ ADSR_Decay,\t\t\t1");
			W(".equ ADSR_Sustain,\t\t\t2");
			W(".equ ADSR_Release,\t\t\t3\n");

			W(".equ AK_SMPLEN,\t\t\t\t(AK_SmpLen-AK_Vars)");
			W(".equ AK_EXTSMPLEN,\t\t\t(AK_ExtSmpLen-AK_Vars)");
			W(".equ AK_NOISESEEDS,\t\t\t(AK_NoiseSeeds-AK_Vars)");
			W(".equ AK_SMPADDR,\t\t\t(AK_SmpAddr-AK_Vars)");
			W(".equ AK_EXTSMPADDR,\t\t\t(AK_ExtSmpAddr-AK_Vars)");
			W(".equ AK_OPINSTANCE,\t\t\t(AK_OpInstance-AK_Vars)");
			W(".equ AK_ENVDVALUE,\t\t\t(AK_EnvDValue-AK_Vars)");
			W(".equ AK_ADSRVALUES,\t\t\t(AK_ADSRValues-AK_Vars)\n");

			W($".equ AK_SMP_LEN,\t\t\t{totalSmpLen}");
			W($".equ AK_EXT_SMP_LEN,\t\t{totalExtSmpLen}");
			W($".equ AK_EXT_DELTA_LEN,\t\t{extDeltaLen}\t; delta coded part of the external samples (the rest is 4 bit ADPCM)");
			W(".equ AK_VERSION,\t\t\t123\n");

			W("; ============================================================================");
			W("; R8 = Sample Buffer Start Address (AK_SMP_LEN bytes)");
			W("; R9 = Temporary Work Buffer Address (AK_MaxTempBuffers * 2048 * 4 = AK_TempBufferSize bytes) (can be freed after sample rendering complete)");
			W("; R10 = External Samples Address (can be freed after sample rendering complete)");
			W("; ============================================================================\n");
			W("AK_Generate:");
			W("\tstr lr, [sp, #-4]!\n");

			W("\t; Create sample & external sample base addresses");
			W("\tadr r4, AK_SmpAddr");
			W("\tadr r5, AK_SmpLen");
			W("\tmov r7, #AK_MaxInstruments");
			W("\tmov r0, r8");
			W("SmpAdrLoop:");
			W("\tstr r0, [r4], #4");
			W("\tldr r1, [r5], #4");
			W("\tadd r0, r0, r1");
			W("\tsubs r7, r7, #1");
			W("\tbne SmpAdrLoop");
			W("\tmov r7, #AK_MaxExtSamples");
			W("\tadr r4, AK_ExtSmpAddr");
			if (usedAdpcmSlots)
			{
				// ADPCM samples take half the bytes and lie after the delta samples: a table of offsets
				W("\tadr r5, AK_ExtSmpOffset");
				W("ExSmpAdrLoop:");
				W("\tldr r1, [r5], #4");
				W("\tadd r0, r10, r1");
				W("\tstr r0, [r4], #4");
			}
			else
			{
				W("\tadr r5, AK_ExtSmpLen");
				W("\tmov r0, r10");
				W("ExSmpAdrLoop:");
				W("\tstr r0, [r4], #4");
				W("\tldr r1, [r5], #4");
				W("\tadd r0, r0, r1");
			}
			W("\tsubs r7, r7, #1");
			W("\tbne ExSmpAdrLoop\n");

			W(".if AK_EXT_DELTA_LEN > 0");
			W("\t; Convert external samples from stored deltas");
			W("\tmov r7, #AK_EXT_DELTA_LEN");
			W("\tmov r6, r10");
			W("\tmov r0, #0");
			W("DeltaLoop:");
			W("\tldrb r1, [r6]");
			W("\tmov r1, r1, asl #24");
			W("\tmov r1, r1, asr #24");
			W("\tadd r0, r0, r1");
			W("\tmov r0, r0, asl #24");
			W("\tmov r0, r0, asr #24");
			W("\tstrb r0, [r6], #1");
			W("\tsubs r7, r7, #1");
			W("\tbne DeltaLoop");
			W(".endif\n");

			W("; ============================================================================");
			W("; r0 = v1 (final sample value)");
			W("; r1 = v2");
			W("; r2 = v3");
			W("; r3 = v4");
			W("; r4 = temp");
			W("; r5 = temp");
			W("; r6 = temp");
			W("; r7 = Sample byte count");
			W("; r8 = Sample Buffer Start Address");
			W("; r9 = AK_MaxTempBuffers*2048 word (>=65536 byte) Temporary Work Buffer Address (can be freed after sample rendering complete)");
			W("; r10 = Base of AK_Vars");
			W("; r11 = 36767 (0x7fff)");
			W("; r12 = temp");
			W("; r14 = temp");
			W("; ============================================================================\n");

			W("\tadr r10, AK_Vars");
			W("\tmov r11, #32767\t; const\n");
		}
	}
}
