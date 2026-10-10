using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.IO;
using System.Threading.Tasks;

namespace Aklang2Asm
{
	class Program
	{
		static Dictionary<string, string> mulRightShifts;
		static Dictionary<string, string> mulLeftShifts;
		static string localLabel = string.Empty;

		static List<string> instrumentName = new List<string>();
		static List<string> instrumentLength = new List<string>();
		static List<int> instrumentLengthInt = new List<int>();
		static List<string> instrumentRepeatOffset = new List<string>();
		static List<string> instrumentRepeatLength = new List<string>();
		static List<string> instrumentLoop = new List<string>();
		static int sampleTotalLength = 0;
		static int fineProgressLength = 0;

		static List<string> externalSampleLength = new List<string>();
		static List<int> externalSampleLengthInt = new List<int>();
		static int externalSampleTotalLength = 0;		// bytes of the external sample block (deltas + ADPCM)
		// V1.15: up to 12 external samples, "<length>a" = 4 bit ADPCM (stored after the delta samples)
		static List<bool> externalSampleAdpcm = new List<bool>();
		static int externalSlots = 8;
		static int externalDeltaLength = 0;
		static bool externalAdpcm = false;
		static bool usedAdpcm = false;					// an imported_sample plays an ADPCM sample

		static int currentLargeBufferInstance = 0;
		static int maxLargeBufferInstance = 0;
		static int currentWordInstance = 0;
		static int maxWordInstance = 0;
		static int currentEnvDInstance = 0;
		static int maxEnvdInstance = 0;

		static bool usedNoise = false;

		static bool useProgress = false;
		static bool useFineProgress = false;
		static bool earlyPlay = false;			// -e: a sample can be played as soon as it is rendered
		static bool wide = false;				// -16: 16 bit samples (words) instead of 8 bit
		// -unz: 16 bit samples, one routine per instrument for the UNZ player of Platon42 (see UnzMap)
		static bool unz = false;
		static List<int> unzInstruments = new List<int>();				// instruments with a routine (1 based)
		static Dictionary<int, SortedSet<int>> unzDepends = new Dictionary<int, SortedSet<int>>();	// the samples they read

		// no key wait when started with redirected input (batch files, the AmigaKlang exe export)
		static void WaitKey()
		{
			if (!Console.IsInputRedirected)
				Console.ReadKey();
		}

		static void Main(string[] args)
		{
			mulRightShifts = new Dictionary<string, string>();
			mulRightShifts.Add("#64", "#1");
			mulRightShifts.Add("#32", "#2");
			mulRightShifts.Add("#16", "#3");
			mulRightShifts.Add("#8", "#4");
			mulRightShifts.Add("#4", "#5");
			mulRightShifts.Add("#2", "#6");
			mulRightShifts.Add("#1", "#7");

			mulLeftShifts = new Dictionary<string, string>();
			mulLeftShifts.Add("#256", "#8");
			mulLeftShifts.Add("#128", "#7");
			mulLeftShifts.Add("#64", "#6");
			mulLeftShifts.Add("#32", "#5");
			mulLeftShifts.Add("#16", "#4");
			mulLeftShifts.Add("#8", "#3");
			mulLeftShifts.Add("#4", "#2");
			mulLeftShifts.Add("#2", "#1");

			Console.WriteLine("AKlang2Asm");
			Console.WriteLine("");

			if (args.Length < 2)
			{
				Console.WriteLine("Usage: Aklang2Asm <script.txt> <output.asm> [-p|-pf] [-e] [-16|-unz]");
				Console.WriteLine("Press Any Key To Continue...");
				WaitKey();
				return;
			}

			useProgress = false;
			useFineProgress = false;
			earlyPlay = false;
			wide = false;

			for (int a = 2; a < args.Length; a++)
			{
				if ( args[a] == "-p" )
				{
					Console.WriteLine("Using Progress");
					useProgress = true;
				}
				else if ( args[a] == "-pf" )
				{
					Console.WriteLine("Using Fine Progress");
					useProgress = true;
					useFineProgress = true;
				}
				else if ( args[a] == "-e" )
				{
					Console.WriteLine("Early play: clearing the first word of each sample right after it is rendered");
					earlyPlay = true;
				}
				else if ( args[a] == "-16" )
				{
					Console.WriteLine("16 bit samples");
					wide = true;
				}
				else if ( args[a] == "-unz" )
				{
					Console.WriteLine("16 bit samples, one routine per instrument (UNZ player)");
					wide = true;
					unz = true;
				}
				else
				{
					Console.WriteLine("Unknown option!");
				}
			}

			if (wide && earlyPlay)
			{
				// early play is for the 8 bit player of the Amiga exe (Paula repeats the first word)
				Console.WriteLine("Error: -16 and -e cannot be used together");
				return;
			}
			if (unz && useProgress)
			{
				// the routines of -unz have no progress pointer (a3 is their scratch register)
				Console.WriteLine("Error: -unz and -p/-pf cannot be used together");
				return;
			}

			if (!File.Exists(args[0]))
			{
				Console.WriteLine("Could not find script file: " + args[0]);
				Console.WriteLine("Press Any Key To Continue...");
				WaitKey();
				return;
			}

			string instrumentsMain = File.ReadAllText(args[0]);

			List<string> instruments = instrumentsMain.Split('$').ToList();
			string externalSampleInfo = instruments[0];
			instruments.RemoveAt(0);

			externalSampleInfo = externalSampleInfo.Replace("\r", "");
			externalSampleInfo = externalSampleInfo.Replace("\n", "");
			externalSampleInfo = externalSampleInfo.Replace(" ", "");
			List<string> externalSampleLengths = externalSampleInfo.Split(',').ToList();

			for (int i = 0; i < externalSampleLengths.Count; i++)
			{
				string entry = externalSampleLengths[i];
				bool adpcm = entry.EndsWith("a");
				int length = int.Parse(adpcm ? entry.Substring(0, entry.Length - 1) : entry);
				externalSampleLength.Add(length.ToString());
				externalSampleLengthInt.Add(length);
				externalSampleAdpcm.Add(adpcm && length > 0);
				if (adpcm && length > 0)
				{
					externalAdpcm = true;
					externalSampleTotalLength += (length + 1) / 2;
				}
				else
				{
					externalDeltaLength += length;
					externalSampleTotalLength += length;
				}
			}
			externalSlots = Math.Max(8, externalSampleLengths.Count);
			if (externalSlots > 12)
			{
				Console.WriteLine("Error: more than 12 external samples");
				return;
			}

			string asmCode = string.Empty;

			asmCode += DividerCode();
			asmCode += ";\n";
			asmCode += "; Generated with Aklang2Asm V1.19, by Dan/Lemon. 2021-2024.\n";
			asmCode += ";\n";
			asmCode += "; Based on Alcatraz Amigaklang rendering core. (c) Jochen 'Virgill' Feldkötter 2020.\n";
			asmCode += ";\n";
			asmCode += "; What's new in V1.1?\n";
			asmCode += "; - Instance offsets fixed in ADSR operator\n";
			asmCode += "; - Incorrect shift direction fixed in OnePoleFilter operator\n";
			asmCode += "; - Loop Generator now correctly interleaved with instrument generation\n";
			asmCode += "; - Fine progress includes loop generation, and new AK_FINE_PROGRESS_LEN added\n";
			asmCode += "; - Reverb large buffer instance offsets were wrong, causing potential buffer overrun\n";
			asmCode += ";\n";
			asmCode += "; What's new in V1.11?\n";
			asmCode += "; - Added value clamping to mode 1 of one-pole filter\n";
			asmCode += ";\n";
			asmCode += "; What's new in V1.12?\n";
			asmCode += "; - Clone sample: optional transpose (-32768..32767 = -1..+1 octave, immediate or V1-V4)\n";
			asmCode += ";\n";
			asmCode += "; What's new in V1.13?\n";
			asmCode += "; - Sample mixer: plays a 4 track pattern of other instruments into a sample\n";
			asmCode += ";\n";
			asmCode += "; What's new in V1.14?\n";
			asmCode += "; - Loop generator: the loop may be longer than the part in front of it (crossfade =\n";
			asmCode += ";   min(repeat length, repeat offset)), constant step instead of divs (no overflow below 257)\n";
			asmCode += ";\n";
			asmCode += "; What's new in V1.15?\n";
			asmCode += "; - Up to 12 external samples; \"<length>a\" in the script = 4 bit ADPCM (Yamaha style),\n";
			asmCode += ";   stored after the delta samples and decoded while imported_sample plays it\n";
			asmCode += ";\n";
			asmCode += "; What's new in V1.16?\n";
			asmCode += "; - AK_TMP_LEN: size of the temporary work buffer (a1)\n";
			asmCode += "; - Option -e (early play): the first word of each sample is cleared right after it is\n";
			asmCode += ";   rendered, so a player can start before all samples are rendered\n";
			asmCode += ";\n";
			asmCode += "; What's new in V1.17?\n";
			asmCode += "; - bytebeat(gain, formula): an unsigned 16 bit formula over t (the sample number) makes\n";
			asmCode += ";   the sample, the low byte of the result is played (experimental)\n";
			asmCode += "; - AK_VERSION (117) for players that need a minimum version; unknown operators give a warning\n";
			asmCode += ";\n";
			asmCode += "; What's new in V1.18?\n";
			asmCode += "; - Option -16: AK_Generate renders 16 bit samples (signed words, AK_16BIT equ 1). The\n";
			asmCode += ";   sample buffer is twice as large (AK_SMP_LEN), clone, chord generator, sample mixer and\n";
			asmCode += ";   loop generator work on the 16 bit samples, the first word of a sample is not cleared.\n";
			asmCode += ";   External samples stay 8 bit. Lengths (AK_SmpLen, progress) count samples in both modes.\n";
			asmCode += ";\n";
			asmCode += "; What's new in V1.19?\n";
			asmCode += "; - Option -unz: 16 bit samples for the UNZ player of Platon42: one routine per instrument\n";
			asmCode += ";   (ak_generate_instrument_N), variables and sample addresses in the structure of the player\n";
			asmCode += ";\n";
			if (unz)
			{
				asmCode += UnzHeaderCode();
			}
			else
			{
			// (AK_Generate and its setup, not indented)
			asmCode += "; Call 'AK_Generate' with the following registers set:\n";
			asmCode += "; a0 = Sample Buffer Start Address (AK_SMP_LEN bytes)\n";
			asmCode += "; a1 = %LARGEBUFFSIZE% Bytes Temporary Work Buffer Address (can be freed after sample rendering complete)\n";
			asmCode += "; a2 = External Samples Address (need not be in chip memory, and can be freed after sample rendering complete)\n";
			asmCode += "; a3 = Rendering Progress Address (2 modes available... see below)\n";
			asmCode += ";\n";
			asmCode += "; AK_FINE_PROGRESS equ 0 = rendering progress as a byte (current instrument number)\n";
			asmCode += "; AK_FINE_PROGRESS equ 1 = rendering progress as a long (current sample byte)\n";
			asmCode += ";\n";
			asmCode += DividerCode();
			asmCode += "\n";
			asmCode += "AK_USE_PROGRESS			equ " + ( useProgress ? "1" : "0" ) +"\n";
			asmCode += "AK_FINE_PROGRESS		equ " + ( useFineProgress ? "1" : "0" ) +"\n";
			asmCode += "AK_FINE_PROGRESS_LEN	equ %FINEPROGRESSLEN%\n";
			asmCode += "AK_SMP_LEN				equ %SAMPLESIZE%\n";
			asmCode += "AK_EXT_SMP_LEN			equ " + externalSampleTotalLength.ToString() + "\n";
			asmCode += "AK_TMP_LEN				equ %LARGEBUFFSIZE%\n";
			asmCode += "AK_VERSION				equ 119\n";
			asmCode += "AK_16BIT				equ " + ( wide ? "1" : "0" ) + "		; 1 = 16 bit samples (signed words)\n";
			asmCode += "\n";
			asmCode += "AK_Generate:\n";
			asmCode += "\n";
			asmCode += "				lea		AK_Vars(pc),a5\n";
			asmCode += "\n";
			asmCode += "				ifne	AK_USE_PROGRESS\n";
			asmCode += "					ifeq	AK_FINE_PROGRESS\n";
			asmCode += "						move.b	#-1,(a3)\n";
			asmCode += "					else\n";
			asmCode += "						move.l	#0,(a3)\n";
			asmCode += "					endif\n";
			asmCode += "				endif\n";
			asmCode += "\n";
			asmCode += "				; Create sample & external sample base addresses\n";
			asmCode += "				lea		AK_SmpLen(a5),a6\n";
			asmCode += "				lea		AK_SmpAddr(a5),a4\n";
			asmCode += "				move.l	a0,d0\n";
			asmCode += "				moveq	#31-1,d7\n";
			asmCode += ".SmpAdrLoop		move.l	d0,(a4)+\n";
			if (wide)
				asmCode += "				add.l	(a6),d0			; 16 bit: two bytes per sample\n";
			asmCode += "				add.l	(a6)+,d0\n";
			asmCode += "				dbra	d7,.SmpAdrLoop\n";
			if (!externalAdpcm)
			{
				asmCode += "				move.l	a2,d0\n";
				asmCode += "				moveq	#" + externalSlots.ToString() + "-1,d7\n";
				asmCode += ".ExtSmpAdrLoop	move.l	d0,(a4)+\n";
				asmCode += "				add.l	(a6)+,d0\n";
				asmCode += "				dbra	d7,.ExtSmpAdrLoop\n";
			}
			else
			{
				// the delta samples come first, the ADPCM samples (half the size) after them
				int deltaOffset = 0, adpcmOffset = externalDeltaLength;
				for (int i = 0; i < externalSampleLengthInt.Count; i++)
				{
					int length = externalSampleLengthInt[i];
					if (length == 0) continue;
					int offset = externalSampleAdpcm[i] ? adpcmOffset : deltaOffset;
					if (externalSampleAdpcm[i]) adpcmOffset += (length + 1) / 2; else deltaOffset += length;
					if (offset < 32768)
						asmCode += "				lea		" + offset.ToString() + "(a2),a6\n";
					else
					{
						asmCode += "				move.l	a2,a6\n";
						asmCode += "				add.l	#" + offset.ToString() + ",a6\n";
					}
					asmCode += "				move.l	a6,AK_ExtSmpAddr+" + (i * 4).ToString() + "(a5)\n";
				}
			}
			asmCode += "\n";

			if (externalDeltaLength > 0)
			{
				asmCode += "				; Convert external samples from stored deltas\n";
				asmCode += "				move.l	a2,a6\n";
				if (!externalAdpcm)
					asmCode += "				move.w	#AK_EXT_SMP_LEN-1,d7\n";
				else
					asmCode += "				move.w	#" + externalDeltaLength.ToString() + "-1,d7\n";
				asmCode += "				moveq	#0,d0\n";
				asmCode += ".DeltaLoop		add.b	(a6),d0\n";
				asmCode += "				move.b	d0,(a6)+\n";
				asmCode += "				dbra	d7,.DeltaLoop\n";
				asmCode += "\n";
			}
			}


			// Iterate Instruments
			for (int i = 0; i < instruments.Count; i++)
			{
				instruments[i] = instruments[i].Replace("\r", "");
				instruments[i] = instruments[i].Replace("\n", "");

				List<string> instrument = instruments[i].Split('#').ToList();

				instrument[0] = instrument[0].Replace(" ", "");
				string[] instrumentInfo = instrument[0].Split(',');

				List<string> instrumentInstructions = instrument[1].Split(';').ToList();

				instrumentName.Add(instrumentInfo[0]);
				instrumentLength.Add(instrumentInfo[1]);
				instrumentLengthInt.Add(int.Parse(instrumentInfo[1]));
				sampleTotalLength += int.Parse(instrumentInfo[1]);
				fineProgressLength += int.Parse(instrumentInfo[1]);
				instrumentRepeatOffset.Add(instrumentInfo[2]);
				instrumentRepeatLength.Add(instrumentInfo[3]);
				instrumentLoop.Add(instrumentInfo[4]);

				// Remove empty operator slots
				for (int j = instrumentInstructions.Count - 1; j >= 0; j--)
				{
					if (string.IsNullOrEmpty(instrumentInstructions[j]))
					{
						instrumentInstructions.RemoveAt(j);
					}
				}

				if (instrumentInstructions.Count == 0 && unz)
				{
					// no routine and no memory for an empty instrument
					instrumentLengthInt[i] = 0;
				}
				else if (instrumentInstructions.Count == 0)
				{
					asmCode += DividerCode();
					asmCode += "; Empty Instrument\n";
					asmCode += DividerCode();
					asmCode += "\n";
					int tempInt = int.Parse(instrumentLength[i]);
					if (tempInt != 0)
					{
						int skip = wide ? tempInt * 2 : tempInt;			// bytes of the sample
						if (skip <= 8)
						{
							asmCode += "				addq.w	#" + skip.ToString() + ",a0\n";
						}
						else if (skip <= 32767)
						{
							asmCode += "				lea		" + skip.ToString() + "(a0),a0\n";
						}
						else
						{
							asmCode += "				add.l	#" + skip.ToString() + ",a0\n";
						}

						if (tempInt <= 8)
						{
							asmCode += "				ifne	AK_USE_PROGRESS\n";
							asmCode += "					ifeq	AK_FINE_PROGRESS\n";
							asmCode += "						addq.b	#1,(a3)\n";
							asmCode += "					else\n";
							asmCode += "						addq.l	#" + instrumentLength[i] + ",(a3)\n";
							asmCode += "					endif\n";
							asmCode += "				endif\n";
						}
						else
						{
							asmCode += "				ifne	AK_USE_PROGRESS\n";
							asmCode += "					ifeq	AK_FINE_PROGRESS\n";
							asmCode += "						addq.b	#1,(a3)\n";
							asmCode += "					else\n";
							asmCode += "						add.l	#" + instrumentLength[i] + ",(a3)\n";
							asmCode += "					endif\n";
							asmCode += "				endif\n";
						}
						asmCode += "\n";
					}
				}
				else
				{
					int instrumentStart = asmCode.Length;
					if (unz)
						asmCode += "ak_generate_instrument_" + (i + 1).ToString() + ":\n";
					asmCode += DividerCode();
					asmCode += "; Instrument " + (i + 1).ToString() + " - " + instrumentName[i] + "\n";
					asmCode += DividerCode();
					asmCode += "\n";
					if (unz)
						// the routines can be called in any order: each one clears its own delay buffers
						asmCode += "				moveq	#%UNZBUFFERS%,d0\n";
					else if (i == 0)
						asmCode += "				moveq	#%MAXLARGEBUFFERINSTANCES%,d0\n";
					else
						asmCode += "				moveq	#" + currentLargeBufferInstance.ToString() + ",d0\n";
					asmCode += "				bsr		AK_ResetVars\n";
					asmCode += "				moveq	#0,d7\n";
					asmCode += "				ifne	AK_USE_PROGRESS\n";
					asmCode += "					ifeq	AK_FINE_PROGRESS\n";
					asmCode += "						addq.b	#1,(a3)\n";
					asmCode += "					endif\n";
					asmCode += "				endif\n";

					asmCode += ".Inst" + (i + 1).ToString() + "Loop\n";

					currentLargeBufferInstance = 0;
					currentWordInstance = 0;
					currentEnvDInstance = 0;

					// Iterate operator steps within instrument
					for (int j = 0; j < instrumentInstructions.Count; j++)
					{
						string output = instrumentInstructions[j].Substring(0, 2);

						int varsStart = instrumentInstructions[j].IndexOf('(') + 1;
						int varsEnd = instrumentInstructions[j].IndexOf(')');

						string varsTemp = instrumentInstructions[j].Substring(varsStart, varsEnd - varsStart);
						varsTemp = varsTemp.Replace(" ", "");
						string[] inputs = varsTemp.Split(',');

						localLabel = (i + 1).ToString() + "_" + (j + 1).ToString();

						asmCode += "				; " + instrumentInstructions[j] + "\n";

						if (instrumentInstructions[j].Contains("bytebeat("))
						{
							asmCode += BytebeatOp(output, instrumentInstructions[j]);
						}
						else if (instrumentInstructions[j].Contains("vol("))
						{
							asmCode += Volume(output, inputs);
						}
						else if (instrumentInstructions[j].Contains("osc_saw("))
						{
							asmCode += Osc_Saw(output, inputs);
						}
						else if (instrumentInstructions[j].Contains("osc_tri("))
						{
							asmCode += Osc_Tri(output, inputs);
						}
						else if (instrumentInstructions[j].Contains("osc_sine("))
						{
							asmCode += Osc_Sine(output, inputs);
						}
						else if (instrumentInstructions[j].Contains("osc_pulse("))
						{
							asmCode += Osc_Pulse(output, inputs);
						}
						else if (instrumentInstructions[j].Contains("osc_noise("))
						{
							asmCode += Osc_Noise(output, inputs);
						}
						else if (instrumentInstructions[j].Contains("sh("))
						{
							asmCode += Sample_And_Hold(output, inputs);
						}
						else if (instrumentInstructions[j].Contains("envd("))
						{
							asmCode += Env_Decay(output, inputs);
						}
						else if (instrumentInstructions[j].Contains("enva("))
						{
							asmCode += Env_Attack(output, inputs);
						}
						else if (instrumentInstructions[j].Contains("mul("))
						{
							asmCode += Mul(output, inputs);
						}
						else if (instrumentInstructions[j].Contains("add("))
						{
							asmCode += Add(output, inputs);
						}
						else if (instrumentInstructions[j].Contains("ctrl("))
						{
							asmCode += Control(output, inputs);
						}
						else if (instrumentInstructions[j].Contains("dly_cyc("))
						{
							asmCode += Delay(output, inputs);
						}
						else if (instrumentInstructions[j].Contains("cmb_flt_n("))
						{
							asmCode += CombFilter(output, inputs);
						}
						else if (instrumentInstructions[j].Contains("reverb("))
						{
							asmCode += Reverb(output, inputs);
						}
						else if (instrumentInstructions[j].Contains("sv_flt_n("))
						{
							asmCode += SVFilter(output, inputs);
						}
						else if (instrumentInstructions[j].Contains("onepole_flt("))
						{
							asmCode += OnePoleFilter(output, inputs);
						}
						else if (instrumentInstructions[j].Contains("chordgen("))
						{
							asmCode += ChordGen(output, inputs);
						}
						else if (instrumentInstructions[j].Contains("clone("))
						{
							asmCode += Clone(output, inputs);
						}
						else if (instrumentInstructions[j].Contains("clone_reverse("))
						{
							asmCode += CloneReverse(output, inputs);
						}
						else if (instrumentInstructions[j].Contains("samplemixer("))
						{
							asmCode += SampleMixer(output, inputs);
						}
						else if (instrumentInstructions[j].Contains("imported_sample("))
						{
							asmCode += ImportedSample(output, inputs);
						}
						else if (instrumentInstructions[j].Contains("distortion("))
						{
							asmCode += Distortion(output, inputs);
						}
						else if (instrumentInstructions[j].Contains("adsr("))
						{
							asmCode += ADSR(output, inputs);
						}
						else
						{
							Console.WriteLine("Warning: unknown operator ignored: " + instrumentInstructions[j]);
						}

						asmCode += "\n";
					}

					if (wide)
					{
						asmCode += "				move.w	d0,(a0)+\n";
					}
					else
					{
						asmCode += "				asr.w	#8,d0\n";
						asmCode += "				move.b	d0,(a0)+\n";
					}
					asmCode += "				ifne	AK_USE_PROGRESS\n";
					asmCode += "					ifne	AK_FINE_PROGRESS\n";
					asmCode += "						addq.l	#1,(a3)\n";
					asmCode += "					endif\n";
					asmCode += "				endif\n";
					asmCode += "				addq.l	#1,d7\n";
					asmCode += "				cmp.l	AK_SmpLen+" + (i << 2).ToString() + "(a5),d7\n";
					asmCode += "				blt		.Inst" + (i + 1).ToString() + "Loop\n";
					asmCode += "\n";

					if (unz)
						asmCode = asmCode.Substring(0, instrumentStart) + asmCode.Substring(instrumentStart).Replace("%UNZBUFFERS%", currentLargeBufferInstance.ToString());

					if (instrumentLoop[i] == "Y" && !unz)
					{
						asmCode += "				movem.l a0-a1,-(sp)	;Stash sample base address & large buffer address for loop generator\n";
					}

					if (currentLargeBufferInstance > maxLargeBufferInstance)
					{
						maxLargeBufferInstance = currentLargeBufferInstance;
					}
					if (currentWordInstance > maxWordInstance)
					{
						maxWordInstance = currentWordInstance;
					}
					if (currentEnvDInstance > maxEnvdInstance)
					{
						maxEnvdInstance = currentEnvDInstance;
					}

					if ( instrumentLoop[i] == "Y" )
                    {
						if (instrumentLoop[i] == "Y")
						{
							asmCode += "\n";
							asmCode += DividerCode();
							asmCode += "; Instrument " + (i + 1).ToString() +
									   " - Loop Generator (Offset: " + instrumentRepeatOffset[i] + " Length: " + instrumentRepeatLength[i] + "\n";
							asmCode += DividerCode();
							asmCode += "\n";
							asmCode += LoopGenerator(instrumentRepeatLength[i], instrumentRepeatOffset[i], i.ToString());
							asmCode += "\n";
							if (!unz)
							{
								asmCode += "				movem.l (sp)+,a0-a1	;Restore sample base address & large buffer address after loop generator\n";
								asmCode += "\n";
							}
						}
					}

					if (earlyPlay)
					{
						// one shot samples repeat their first word: clear it now, not only at the end,
						// so the music can already play this sample while the next ones are rendered
						asmCode += "				move.l	AK_SmpAddr+" + (i << 2).ToString() + "(a5),a6\n";
						asmCode += "				clr.w	(a6)		; early play\n";
						asmCode += "\n";
					}

					if (unz)
					{
						// the routine ends here; the samples it reads have to be rendered before it
						if (instrumentLoop[i] != "Y" && asmCode.EndsWith("\n\n"))
							asmCode = asmCode.Substring(0, asmCode.Length - 1);
						var sources = new SortedSet<int>();
						foreach (Match m in Regex.Matches(asmCode.Substring(instrumentStart), @"AK_SmpAddr\+(\d+)"))
						{
							int source = int.Parse(m.Groups[1].Value) / 4 + 1;
							if (source != i + 1) sources.Add(source);
						}
						unzInstruments.Add(i + 1);
						unzDepends[i + 1] = sources;
						asmCode += "				rts\n";
						asmCode += "\n";
					}

				}
			}

			if ((maxWordInstance & 1) == 1)
			{
				maxWordInstance += 1;
			}


			asmCode += "\n";

			asmCode += DividerCode();
			asmCode += "\n";

			if (!wide && !unz)
			{
				// (16 bit samples stay complete: they are not played by Paula, which repeats the first word)
				asmCode += "				; Clear first 2 bytes of each sample\n";
				asmCode += "				lea		AK_SmpAddr(a5),a6\n";
				asmCode += "				moveq	#0,d0\n";
				asmCode += "				moveq	#31-1,d7\n";
				asmCode += ".SmpClrLoop		move.l	(a6)+,a4\n";
				asmCode += "				move.b	d0,(a4)+\n";
				asmCode += "				move.b	d0,(a4)+\n";
				asmCode += "				dbra	d7,.SmpClrLoop\n";
			}

			if (!unz)
			{
				asmCode += "\n";
				asmCode += "				rts\n";

				asmCode += "\n";
				asmCode += DividerCode();
				asmCode += "\n";
			}
			asmCode += ClearVarsCode();
			if (usedAdpcm)
			{
				asmCode += "\n";
				asmCode += AdpcmCode();
			}
			asmCode += "\n";
			asmCode += DividerCode();
			asmCode += "\n";
			if (unz)
			{
				asmCode = UnzMap(asmCode);
				asmCode += UnzVarsCode();
				asmCode += "\n";
				asmCode += DividerCode();
				asmCode += UnzGeneratorsCode();
			}
			else
			{
				asmCode += VarsCode();
				asmCode += "\n";
				asmCode += DividerCode();
			}


			asmCode = asmCode.Replace("%SAMPLESIZE%", (wide ? sampleTotalLength * 2 : sampleTotalLength).ToString());
			asmCode = asmCode.Replace("%FINEPROGRESSLEN%", fineProgressLength.ToString());

			asmCode = asmCode.Replace("%LARGEBUFFSIZE%", (maxLargeBufferInstance * 4096).ToString());
			asmCode = asmCode.Replace("%MAXLARGEBUFFERINSTANCES%", maxLargeBufferInstance.ToString());

			asmCode = asmCode.Replace("%MAXWORDINSTANCES%", maxWordInstance.ToString());
			asmCode = asmCode.Replace("%MAXENVDINSTANCES%", maxEnvdInstance.ToString());

			File.WriteAllText(args[1], asmCode);

			Console.WriteLine("Press Any Key To Continue...");
			WaitKey();
		}




		static string Volume(string output, string[] inputs)
		{
			//inline short vol(short val, UBYTE gain)
			//{
			//    return mulsw(val, gain) >> 7;
			//}

			// instance = 0-15
			// val = V1-v4
			// step = Immediate or V1-v4

			string outReg = RemapVarToRegisterOrImmediate(output);
			string val = RemapVarToRegisterOrImmediate(inputs[0]);
			string gain = RemapVarToRegisterOrImmediate(inputs[1]);
			string gainShift = mulRightShifts.ContainsKey(gain) ? mulRightShifts[gain] : string.Empty;

			string code = string.Empty;

			if (outReg != val)
				code += "				move.w	@VAL,@OR\n";

			if (gain != "#128")
			{
				if (mulRightShifts.ContainsKey(gain))
					code += "				asr.w	@GS,@OR\n";
				else
				{
					if (!gain.Contains("#"))
					{
						code += "				move.w	@GN,@TR1\n";
						code += "				and.w	#255,@TR1\n";
					}

					code += "				muls	@TR1,@OR\n";
					code += "				asr.l	#7,@OR\n";
				}
			}

			if (!gain.Contains("#"))
				code = code.Replace("@TR1", "d4");
			else
				code = code.Replace("@TR1", gain);

			code = code.Replace("@VAL", val);
			code = code.Replace("@GN", gain);
			code = code.Replace("@GS", gainShift);
			code = code.Replace("@OR", outReg);

			return code;
		}

		static string Osc_Saw(string output, string[] inputs)
		{
			//short osc_saw(BYTE instance, short freq, UBYTE gain)
			//{
			//    counter_saw[instance] += freq;
			//    return vol(counter_saw[instance], gain);
			//}

			// instance = 0-15
			// freq = Immediate or V1-v4
			// gain = Immediate or V1-v4

			string outReg = RemapVarToRegisterOrImmediate(output);

			//string instance = GetInstanceOffset(inputs[0], 2);
			int instanceVal = (currentWordInstance * 2);
			string instance = instanceVal.ToString();

			string freq = RemapVarToRegisterOrImmediate(inputs[1]);
			string gain = RemapVarToRegisterOrImmediate(inputs[2]);
			string gainShift = mulRightShifts.ContainsKey(gain) ? mulRightShifts[gain] : string.Empty;

			string code = string.Empty;

			code += "				add.w	@FR,AK_OpInstance+@IN(a5)\n";

			if (gain == "#128")
			{
				code += "				move.w	AK_OpInstance+@IN(a5),@OR\n";
			}
			else
			{

				if (mulRightShifts.ContainsKey(gain))
				{
					code += "				move.w	AK_OpInstance+@IN(a5),@OR\n";
					code += "				asr.w	@GS,@OR\n";
				}
				else
				{
					if (!gain.Contains("#"))
					{
						code += "				move.w	@GN,@TR1\n";
						code += "				and.w	#255,@TR1\n";
					}

					code += "				move.w	AK_OpInstance+@IN(a5),@OR\n";
					code += "				muls	@TR1,@OR\n";
					code += "				asr.l	#7,@OR\n";
				}
			}

			if (!gain.Contains("#"))
				code = code.Replace("@TR1", "d4");
			else
				code = code.Replace("@TR1", gain);

			code = code.Replace("@IN", instance);
			code = code.Replace("@FR", freq);
			code = code.Replace("@GN", gain);
			code = code.Replace("@GS", gainShift);
			code = code.Replace("@OR", outReg);

			currentWordInstance += 1;

			return code;
		}

		static string Osc_Tri(string output, string[] inputs)
		{
			//short osc_tri(BYTE instance, short freq, UBYTE gain)
			//{
			//   short buf = counter_tri[instance] += freq; if (buf < 0) buf = 65535 - buf;
			//    buf -= 16384; buf <<= 1;
			//    return vol(buf, gain);
			//}

			// instance = 0-15
			// freq = Immediate or V1-v4
			// gain = Immediate or V1-v4

			string outReg = RemapVarToRegisterOrImmediate(output);

			//string instance = GetInstanceOffset(inputs[0], 2);
			int instanceVal = (currentWordInstance * 2);
			string instance = instanceVal.ToString();

			string freq = RemapVarToRegisterOrImmediate(inputs[1]);
			string gain = RemapVarToRegisterOrImmediate(inputs[2]);
			string gainShift = mulRightShifts.ContainsKey(gain) ? mulRightShifts[gain] : string.Empty;

			string code = string.Empty;

			code += "				add.w	@FR,AK_OpInstance+@IN(a5)\n";
			if (!gain.Contains("#"))
			{
				code += "				move.w	@GN,@TR1\n";
				code += "				and.w	#255,@TR1\n";
			}
			code += "				move.w	AK_OpInstance+@IN(a5),@OR\n";
			code += "				bge.s	.TriNoInvert_" + localLabel + "\n";
			code += "				not.w	@OR\n";
			code += ".TriNoInvert_" + localLabel + "\n";
			code += "				sub.w	#16384,@OR\n";
			code += "				add.w	@OR,@OR\n";

			if (gain != "#128")
			{
				if (mulRightShifts.ContainsKey(gain))
				{
					code += "				asr.w	@GS,@OR\n";
				}
				else
				{
					code += "				muls	@TR1,@OR\n";
					code += "				asr.l	#7,@OR\n";
				}
			}

			if (!gain.Contains("#"))
				code = code.Replace("@TR1", "d4");
			else
				code = code.Replace("@TR1", gain);

			code = code.Replace("@IN", instance);
			code = code.Replace("@FR", freq);
			code = code.Replace("@GN", gain);
			code = code.Replace("@GS", gainShift);
			code = code.Replace("@OR", outReg);

			currentWordInstance += 1;

			return code;
		}

		static string Osc_Pulse(string output, string[] inputs)
		{
			//short osc_pulse(BYTE instance, short freq, UBYTE gain, UBYTE dutycycle)
			//{
			//	short buf = counter_pulse[instance] += freq;
			//	if (buf < (dutycycle - 63) << 9) buf = -32768; else buf = 32767;
			//	return vol(buf, gain);
			//}

			// instance = 0-15
			// freq = Immediate or V1-v4
			// gain = Immediate or V1-v4
			// dutycycle = Immediate or V1-v4

			string outReg = RemapVarToRegisterOrImmediate(output);

			//string instance = GetInstanceOffset(inputs[0], 2);
			int instanceVal = (currentWordInstance * 2);
			string instance = instanceVal.ToString();

			string freq = RemapVarToRegisterOrImmediate(inputs[1]);
			string gain = RemapVarToRegisterOrImmediate(inputs[2]);
			string gainShift = mulRightShifts.ContainsKey(gain) ? mulRightShifts[gain] : string.Empty;
			string dutyCycle = RemapVarToRegisterOrImmediate(inputs[3]);

			string code = string.Empty;

			code += "				add.w	@FR,AK_OpInstance+@IN(a5)\n";

			if (!gain.Contains("#"))
			{
				code += "				move.w	@GN,@TR2\n";
				code += "				and.w	#255,@TR2\n";
			}

			if (dutyCycle.Contains("#"))
			{
				int dutyCycleInt = int.Parse(dutyCycle.Replace("#", ""));
				if (dutyCycleInt != 63)
				{
					int index = dutyCycle.IndexOf("#");
					code += "				cmp.w	#((" + dutyCycle.Remove(index, 1) + "-63)<<9),AK_OpInstance+@IN(a5)\n";
				}
			}
			else
			{
				if (dutyCycle == gain)
				{
					code += "				move.w	@TR2,@TR1\n";
				}
				else
				{
					code += "				move.w	@DC,@TR1\n";
					code += "				and.w	#255,@TR1\n";
				}
				code += "				sub.w	#63,@TR1\n";
				code += "				asl.w	#8,@TR1\n";
				code += "				add.w	@TR1,@TR1\n";
				code += "				cmp.w	AK_OpInstance+@IN(a5),@TR1\n";
			}
			code += "				slt		@OR\n";
			code += "				ext.w	@OR\n";
			code += "				eor.w	#$7fff,@OR\n";

			if (gain != "#128")
			{
				if (mulRightShifts.ContainsKey(gain))
				{
					code += "				asr.w	@GS,@OR\n";
				}
				else
				{
					code += "				muls	@TR2,@OR\n";
					code += "				asr.l	#7,@OR\n";
				}
			}

			code = code.Replace("@TR1", "d4");

			if (!gain.Contains("#"))
				code = code.Replace("@TR2", "d5");
			else
				code = code.Replace("@TR2", gain);

			code = code.Replace("@IN", instance);
			code = code.Replace("@FR", freq);
			code = code.Replace("@DC", dutyCycle);
			code = code.Replace("@GN", gain);
			code = code.Replace("@GS", gainShift);
			code = code.Replace("@OR", outReg);

			currentWordInstance += 1;

			return code;
		}

		static string Osc_Sine(string output, string[] inputs)
		{
			//short osc_sine(BYTE instance, short freq, UBYTE gain)
			//{
			//    int temp; short res; short buf = counter_sine[instance] += freq;
			//    buf -= 16384; temp = mulsw(buf, 32767 - abs(buf)); res = temp >> 16; res <<= 3;
			//    return vol(res, gain);
			//}

			// instance = 0-15
			// freq = Immediate or V1-v4
			// gain = Immediate or V1-v4

			string outReg = RemapVarToRegisterOrImmediate(output);

			//string instance = GetInstanceOffset(inputs[0], 2);
			int instanceVal = (currentWordInstance * 2);
			string instance = instanceVal.ToString();

			string freq = RemapVarToRegisterOrImmediate(inputs[1]);
			string gain = RemapVarToRegisterOrImmediate(inputs[2]);
			string gainShift = mulRightShifts.ContainsKey(gain) ? mulRightShifts[gain] : string.Empty;

			string code = string.Empty;

			code += "				add.w	@FR,AK_OpInstance+@IN(a5)\n";
			if (!gain.Contains("#"))
			{
				code += "				move.w	@GN,@TR1\n";
				code += "				and.w	#255,@TR1\n";
			}
			code += "				move.w	AK_OpInstance+@IN(a5),@OR\n";
			code += "				sub.w	#16384,@OR\n";
			code += "				move.w	@OR,d5\n";
			code += "				bge.s	.SineNoAbs_" + localLabel + "\n";
			code += "				neg.w	d5\n";
			code += ".SineNoAbs_" + localLabel + "\n";
			code += "				move.w	#32767,d6\n";
			code += "				sub.w	d5,d6\n";
			code += "				muls	d6,@OR\n";
			code += "				swap	@OR\n";
			code += "				asl.w	#3,@OR\n";

			if (gain != "#128")
			{
				if (mulRightShifts.ContainsKey(gain))
				{
					code += "				asr.w	@GS,@OR\n";
				}
				else
				{
					code += "				muls	@TR1,@OR\n";
					code += "				asr.l	#7,@OR\n";
				}
			}

			if (!gain.Contains("#"))
				code = code.Replace("@TR1", "d4");
			else
				code = code.Replace("@TR1", gain);

			code = code.Replace("@IN", instance);
			code = code.Replace("@FR", freq);
			code = code.Replace("@GN", gain);
			code = code.Replace("@GS", gainShift);
			code = code.Replace("@OR", outReg);

			currentWordInstance += 1;

			return code;
		}

		static string Osc_Noise(string output, string[] inputs)
		{
			//short osc_noise(UBYTE gain)
			//{
			//   static int g_x1 = 0x67452301; static int g_x2 = 0xefcdab89; static int g_x3 = 0;
			//    g_x1 ^= g_x2;
			//    g_x3 += g_x2;
			//    g_x2 += g_x1;
			//    short buf = g_x3;
			//    return vol(buf, gain);
			//}

			// instance = 0-15
			// gain = Immediate or V1-v4

			string outReg = RemapVarToRegisterOrImmediate(output);
			string gain = RemapVarToRegisterOrImmediate(inputs[0]);
			string gainShift = mulRightShifts.ContainsKey(gain) ? mulRightShifts[gain] : string.Empty;

			string code = string.Empty;

			code += "				move.l	AK_NoiseSeeds+0(a5),d4\n";
			code += "				move.l	AK_NoiseSeeds+4(a5),d5\n";
			code += "				eor.l	d5,d4\n";
			code += "				move.l	d4,AK_NoiseSeeds+0(a5)\n";
			code += "				add.l	d5,AK_NoiseSeeds+8(a5)\n";
			code += "				add.l	d4,AK_NoiseSeeds+4(a5)\n";
			if (!gain.Contains("#"))
			{
				code += "				move.w	@GN,@TR1\n";
				code += "				and.w	#255,@TR1\n";
			}
			code += "				move.w	AK_NoiseSeeds+10(a5),@OR\n";

			if (gain != "#128")
			{
				if (mulRightShifts.ContainsKey(gain))
				{
					code += "				asr.w	@GS,@OR\n";
				}
				else
				{
					code += "				muls	@TR1,@OR\n";
					code += "				asr.l	#7,@OR\n";
				}
			}

			if (!gain.Contains("#"))
				code = code.Replace("@TR1", "d4");
			else
				code = code.Replace("@TR1", gain);

			code = code.Replace("@GN", gain);
			code = code.Replace("@GS", gainShift);
			code = code.Replace("@OR", outReg);

			usedNoise = true;

			return code;
		}

		static string Sample_And_Hold(string output, string[] inputs)
		{
			//short sh(BYTE instance, short val1, UBYTE step)
			//{
			//	short step2 = mulsw(step, step) >> 2;
			//	if (counter_sh[instance] == 0)
			//		buffer_sh[instance] = val1;
			//	if (counter_sh[instance] == step2)
			//		counter_sh[instance] = -1;
			//	counter_sh[instance] += 1;
			//	return buffer_sh[instance];
			//}

			//short sh(BYTE instance, short val1, UBYTE step)
			//{
			//	short step2 = mulsw(step, step) >> 2;
			//	counter_sh[instance]--;
			//	if (counter_sh[instance] < 0)
			//	{
			//		buffer_sh[instance] = val1;
			//		counter_sh[instance] = step2;
			//	}
			//	return buffer_sh[instance];
			//}

			// instance = 0-15
			// val = Immediate or V1-v4
			// step = Immediate or V1-v4

			string outReg = RemapVarToRegisterOrImmediate(output);
			// string instance = GetInstanceOffset(inputs[0], 2);
			int instance1Val = (currentWordInstance * 2);
			string instance1 = instance1Val.ToString();
			int instance2Val = (currentWordInstance * 2) + 2;
			string instance2 = instance2Val.ToString();
			string val = RemapVarToRegisterOrImmediate(inputs[1]);
			string step = RemapVarToRegisterOrImmediate(inputs[2]);
			string step2 = string.Empty;
			bool stepImmediate = step.Contains("#");

			string code = string.Empty;

			if (!stepImmediate)
			{
				code += "				move.w	@STR,d4\n";
				code += "				and.w	#255,d4\n";
				code += "				muls	d4,d4\n";
				code += "				asr.l	#2,d4\n";
			}
			else
			{
				int tempStep = int.Parse(inputs[2]);
				step2 = "#" + ((tempStep * tempStep) >> 2).ToString();
			}

			code += "				sub.w	#1,AK_OpInstance+@IN1(a5)\n";
			code += "				bge.s	.SHNoStore_" + localLabel + "\n";
			code += "				move.w	@VL,AK_OpInstance+@IN2(a5)\n";
			if (!stepImmediate)
				code += "				move.w	d4,AK_OpInstance+@IN1(a5)\n";
			else
				code += "				move.w	@STI,AK_OpInstance+@IN1(a5)\n";
			code += ".SHNoStore_" + localLabel + "\n";
			code += "				move.w	AK_OpInstance+@IN2(a5),@OR\n";

			code = code.Replace("@IN1", instance1);
			code = code.Replace("@IN2", instance2);
			code = code.Replace("@STR", step);
			code = code.Replace("@STI", step2);
			code = code.Replace("@VL", val);
			code = code.Replace("@OR", outReg);

			currentWordInstance += 2;

			return code;
		}


		static string Env_Decay(string output, string[] inputs)
		{
			//short envd(BYTE instance, BYTE decay, BYTE sustain, UBYTE gain)
			//{
			//	int sustain32 = sustain << 24;
			//  int decay32 = decay32Table[decay];
			//  int buf32 = counter_envd[instance];
			//  buf32 -= decay32;
			//  if( buf32 <= sustain32 )
			//     buf32 = sustain32;
			//  counter_envd[instance] = buf32;
			//  short buf2 = buf>>16;
			//	return vol(buf2, gain);
			//z}

			// instance = 0-15
			// attack = Immediate
			// sustain = Immediate
			// gain = Immediate or V1-V4

			string outReg = RemapVarToRegisterOrImmediate(output);

			//string instance = GetInstanceOffset(inputs[0], 4);
			int instanceVal = (currentEnvDInstance * 4);
			string instance = instanceVal.ToString();

			string decay32 = GetDecayValue(int.Parse(inputs[1]));
			int sustain32Val = (int.Parse(inputs[2]) << 24);
			string sustain32 = "#" + (int.Parse(inputs[2]) << 24).ToString();
			string gain = RemapVarToRegisterOrImmediate(inputs[3]);
			string gainShift = mulRightShifts.ContainsKey(gain) ? mulRightShifts[gain] : string.Empty;

			string code = string.Empty;

			if (!gain.Contains("#"))
			{
				code += "				move.w	@GN,@TR1\n";
				code += "				and.w	#255,@TR1\n";
			}
			code += "				move.l	AK_EnvDValue+@IN(a5),d5\n";
			code += "				move.l	d5,@OR\n";
			code += "				swap	@OR\n";
			code += "				sub.l	@DV,d5\n";
			if (sustain32 != "#0")
			{
				code += "				cmp.l	@SV,d5\n";
			}
			code += "				bgt.s   .EnvDNoSustain_" + localLabel + "\n"; // Need to look at this in future.. potential signed/unsigned issues on large decays?
			if (sustain32Val <= 127 && sustain32Val >= -128)
				code += "				moveq	@SV,d5\n";
			else
				code += "				move.l	@SV,d5\n";
			code += ".EnvDNoSustain_" + localLabel + "\n";
			code += "				move.l	d5,AK_EnvDValue+@IN(a5)\n";

			if (gain != "#128")
			{
				if (mulRightShifts.ContainsKey(gain))
				{
					code += "				asr.w	@GS,@OR\n";
				}
				else
				{
					code += "				muls	@TR1,@OR\n";
					code += "				asr.l	#7,@OR\n";
				}
			}

			if (!gain.Contains("#"))
				code = code.Replace("@TR1", "d4");
			else
				code = code.Replace("@TR1", gain);

			code = code.Replace("@IN", instance);
			code = code.Replace("@GN", gain);
			code = code.Replace("@GS", gainShift);
			code = code.Replace("@OR", outReg);
			code = code.Replace("@DV", decay32);
			code = code.Replace("@SV", sustain32);

			currentEnvDInstance += 1;

			return code;
		}

		static string Env_Attack(string output, string[] inputs)
		{
			//short enva(BYTE instance, BYTE attack, BYTE sustain, UBYTE gain)
			//{
			//	int sustain32 = sustain << 24;
			//  int buf32 = counter_enva[instance];
			//  int attack32 = decay32Table[decay];
			//  buf32 += attack32;
			//  if( buf32 > 32767<<16 )
			//     buf32 = 32767<<16;
			//  counter_enva[instance] = buf32
			//  short buf2 = buf>>16;
			//	return vol(buf2, gain);
			//}

			// instance = 0-15
			// attack = Immediate
			// sustain = Immediate
			// gain = Immediate or V1-V4

			string outReg = RemapVarToRegisterOrImmediate(output);

			//string instance = GetInstanceOffset(inputs[0], 4);
			int instanceVal = (currentWordInstance * 2);
			string instance = instanceVal.ToString();

			string attack32 = GetDecayValue(int.Parse(inputs[1]));
			string sustain32 = "#" + (int.Parse(inputs[2]) << 24).ToString();
			string gain = RemapVarToRegisterOrImmediate(inputs[3]);
			string gainShift = mulRightShifts.ContainsKey(gain) ? mulRightShifts[gain] : string.Empty;

			string code = string.Empty;

			if (!gain.Contains("#"))
			{
				code += "				move.w	@GN,@TR1\n";
				code += "				and.w	#255,@TR1\n";
			}
			code += "				move.l	AK_OpInstance+@IN(a5),d5\n";
			code += "				move.l	d5,@OR\n";
			code += "				swap	@OR\n";
			code += "				add.l	@AV,d5\n";
			code += "				bvc.s   .EnvANoMax_" + localLabel + "\n";
			code += "				move.l	#32767<<16,d5\n";
			code += ".EnvANoMax_" + localLabel + "\n";
			code += "				move.l	d5,AK_OpInstance+@IN(a5)\n";

			if (gain != "#128")
			{
				if (mulRightShifts.ContainsKey(gain))
				{
					code += "				asr.w	@GS,@OR\n";
				}
				else
				{
					code += "				muls	@TR1,@OR\n";
					code += "				asr.l	#7,@OR\n";
				}
			}

			if (!gain.Contains("#"))
				code = code.Replace("@TR1", "d4");
			else
				code = code.Replace("@TR1", gain);

			code = code.Replace("@IN", instance);
			code = code.Replace("@GN", gain);
			code = code.Replace("@GS", gainShift);
			code = code.Replace("@OR", outReg);
			code = code.Replace("@AV", attack32);
			code = code.Replace("@SV", sustain32);

			currentWordInstance += 2;

			return code;
		}

		static string Mul(string output, string[] inputs)
		{
			//inline short mul(short val1, short val2)
			//{
			//    return mulsw(val1, val2) >> 15;
			//}

			// val1 = Immediate or V1-V4
			// val2 = Immediate or V1-V4

			string outReg = RemapVarToRegisterOrImmediate(output);
			string val1 = RemapVarToRegisterOrImmediate(inputs[0]);
			string val2 = RemapVarToRegisterOrImmediate(inputs[1]);

			string code = string.Empty;

			if (outReg == val1)
			{
				code += "				muls	@V2,@OR\n";
			}
			else if (outReg == val2)
			{
				code += "				muls	@V1,@OR\n";
			}
			else
			{
				code += "				move.w	@V1,@OR\n";
				code += "				muls	@V2,@OR\n";
			}

			code += "				add.l	@OR,@OR\n";
			code += "				swap	@OR\n";

			code = code.Replace("@OR", outReg);
			code = code.Replace("@V1", val1);
			code = code.Replace("@V2", val2);

			return code;
		}

		static string Add(string output, string[] inputs)
		{
			//inline short add(short val1, short val2)
			//{
			//    return clamp(val1 + val2);
			//}

			// val1 = Immediate or V1-V4
			// val2 = Immediate or V1-V4

			string outReg = RemapVarToRegisterOrImmediate(output);
			string val1 = RemapVarToRegisterOrImmediate(inputs[0]);
			string val2 = RemapVarToRegisterOrImmediate(inputs[1]);

			string code = string.Empty;

			if (outReg == val1)
			{
				code += "				add.w	@V2,@OR\n";
			}
			else if (outReg == val2)
			{
				code += "				add.w	@V1,@OR\n";
			}
			else
			{
				code += "				move.w	@V1,@OR\n";
				code += "				add.w	@V2,@OR\n";
			}

			code += "				bvc.s	.AddNoClamp_" + localLabel + "\n";
			code += "				spl		@OR\n";
			code += "				ext.w	@OR\n";
			code += "				eor.w	#$7fff,@OR\n";
			code += ".AddNoClamp_" + localLabel + "\n";

			code = code.Replace("@OR", outReg);
			code = code.Replace("@V1", val1);
			code = code.Replace("@V2", val2);

			return code;
		}


		static string Control(string output, string[] inputs)
		{
			//inline BYTE ctrl(short val)
			//{
			//	return (val >> 9) + 64;
			//}

			// val = V1-V4

			string outReg = RemapVarToRegisterOrImmediate(output);
			string input = RemapVarToRegisterOrImmediate(inputs[0]);

			string code = string.Empty;

			if (outReg != input)
			{
				code += "				move.w	@IR,@OR\n";
			}

			code += "				moveq	#9,d4\n";
			code += "				asr.w	d4,@OR\n";
			code += "				add.w	#64,@OR\n";

			code = code.Replace("@OR", outReg);
			code = code.Replace("@IR", input);

			return code;
		}

		static string Delay(string output, string[] inputs)
		{
			//short dly_cyc(BYTE instance, short val, short delay, UBYTE gain)
			//{
			//	if (delay > 2047) delay = 2047;
			//	static short i[16];
			//  buffern[instance][i[instance]] = vol(val, gain);
			//  i[instance]++;
			//	if (i[instance] >= delay)
			//		i[instance] = 0;
			//	return buffern[instance][i[instance]];
			//}

			// instance = 0-15
			// val = V1-V4
			// delay = Immediate or V1-V4
			// gain = Immediate or V1-V4


			string outReg = RemapVarToRegisterOrImmediate(output);

			//string instance = GetInstanceOffset(inputs[0], 2);
			int instanceVal = (currentWordInstance * 2);
			string instance = instanceVal.ToString();

			//string instance2 = GetInstanceOffset(inputs[0], 4096);
			//int instance2Val = int.Parse(instance2);
			int instance2Val = (currentLargeBufferInstance * 4096);
			string instance2 = instance2Val.ToString();

			string val = RemapVarToRegisterOrImmediate(inputs[1]);
			string delay = RemapVarToRegisterOrImmediate(inputs[2]);
			bool delayImmediate = delay.Contains("#");
			string gain = RemapVarToRegisterOrImmediate(inputs[3]);
			string gainShift = mulRightShifts.ContainsKey(gain) ? mulRightShifts[gain] : string.Empty;

			string code = string.Empty;

			code += "				move.w	@VL,d4\n";

			if (gain != "#128")
			{
				if (mulRightShifts.ContainsKey(gain))
				{
					code += "				asr.w	@GS,@d4\n";
				}
				else if (gain.Contains("#"))
				{
					code += "				muls	@GN,d4\n";
					code += "				asr.l	#7,d4\n";
				}
				else
				{
					code += "				move.w	@GN,d5\n";
					code += "				and.w	#255,d5\n";
					code += "				muls	d5,d4\n";
					code += "				asr.l	#7,d4\n";
				}
			}

			if (instance2Val == 0)
			{
				code += "				move.l	a1,a4\n";
			}
			else if (instance2Val < 32767)
			{
				code += "				lea		@IN2(a1),a4\n";
			}
			else
			{
				code += "				move.l	a1,a4\n";
				code += "				add.l	#@IN2,a4\n";
			}
			code += "				move.w	AK_OpInstance+@IN1(a5),d5\n";
			code += "				move.w	d4,(a4,d5.w)\n";

			if (delayImmediate)
			{
				code += "				addq.w	#2,d5\n";
				code += "				cmp.w	@DL<<1,d5\n";
				code += "				blt.s	.NoDelayReset_" + localLabel + "\n";
				code += "				moveq	#0,d5\n";
				code += ".NoDelayReset_" + localLabel + "\n";
				code += "				move.w  d5,AK_OpInstance+@IN1(a5)\n";
				code += "				move.w	(a4,d5.w),@OR\n";
			}
			else
			{
				code += "				move.w	@DL,d6\n";
				code += "               cmp.w	#2047,d6\n";
				code += "				ble.s	.NoClampDelay_" + localLabel + "\n";
				code += "				move.w  #2047,d6\n";
				code += ".NoClampDelay_" + localLabel + "\n";
				code += "				add.w	d6,d6\n";
				code += "				addq.w	#2,d5\n";
				code += "				cmp.w	d6,d5\n";
				code += "				blt.s	.NoDelayReset_" + localLabel + "\n";
				code += "				moveq	#0,d5\n";
				code += ".NoDelayReset_" + localLabel + "\n";
				code += "				move.w  d5,AK_OpInstance+@IN1(a5)\n";
				code += "				move.w	(a4,d5.w),@OR\n";
			}

			code = code.Replace("@IN1", instance);
			code = code.Replace("@IN2", instance2);
			code = code.Replace("@VL", val);
			code = code.Replace("@DL", delay);
			code = code.Replace("@GN", gain);
			code = code.Replace("@GS", gainShift);
			code = code.Replace("@OR", outReg);

			currentLargeBufferInstance += 1;
			currentWordInstance += 1;

			return code;
		}

		static string CombFilter(string output, string[] inputs)
		{
			//short cmb_flt_n(BYTE instance, short val, short delay, UBYTE feedback, UBYTE gain)
			//{
			//	if (delay > 2047) delay = 2047;
			//	static short i[16];
			//	short output = buffern[instance][i[instance]] = add(val, (vol(buffern[instance][i[instance]], feedback)));
			//  i[instance]++;
			//  if ( [instance] >= delay)
			//		i[instance] = 0;
			//	return vol(output, gain);
			//}

			// instance = 0-15
			// val = V1-V4
			// delay = Immediate or V1-V4
			// feedback = Immediate or V1-V4
			// gain = Immediate or V1-V4


			string outReg = RemapVarToRegisterOrImmediate(output);

			//string instance = GetInstanceOffset(inputs[0], 2);
			int instanceVal = (currentWordInstance * 2);
			string instance = instanceVal.ToString();

			//string instance2 = GetInstanceOffset(inputs[0], 4096);
			//int instance2Val = int.Parse(instance2);
			int instance2Val = (currentLargeBufferInstance * 4096);
			string instance2 = instance2Val.ToString();

			string val = RemapVarToRegisterOrImmediate(inputs[1]);
			string delay = RemapVarToRegisterOrImmediate(inputs[2]);
			bool delayImmediate = delay.Contains("#");
			string feedback = RemapVarToRegisterOrImmediate(inputs[3]);
			string feedbackShift = mulRightShifts.ContainsKey(feedback) ? mulRightShifts[feedback] : string.Empty;
			string gain = RemapVarToRegisterOrImmediate(inputs[4]);
			string gainShift = mulRightShifts.ContainsKey(gain) ? mulRightShifts[gain] : string.Empty;

			string code = string.Empty;

			if (instance2Val == 0)
			{
				code += "				move.l	a1,a4\n";
			}
			else if (instance2Val < 32767)
			{
				code += "				lea		@IN2(a1),a4\n";
			}
			else
			{
				code += "				move.l	a1,a4\n";
				code += "				add.l	#@IN2,a4\n";
			}
			code += "				move.w	AK_OpInstance+@IN1(a5),d5\n";
			code += "				move.w	(a4,d5.w),d4\n";

			if (feedback != "#128")
			{
				if (mulRightShifts.ContainsKey(feedback))
				{
					code += "				asr.w	@FS,d4\n";
				}
				else if (feedback.Contains("#"))
				{
					code += "				muls	@FB,d4\n";
					code += "				asr.l	#7,d4\n";
				}
				else
				{
					code += "				move.w	@FB,d6\n";
					code += "				and.w	#255,d6\n";
					code += "				muls	d6,d4\n";
					code += "				asr.l	#7,d4\n";
				}
			}

			code += "				add.w	@VL,d4\n";
			code += "				bvc.s	.CombAddNoClamp_" + localLabel + "\n";
			code += "				spl		d4\n";
			code += "				ext.w	d4\n";
			code += "				eor.w	#$7fff,d4\n";
			code += ".CombAddNoClamp_" + localLabel + "\n";
			code += "				move.w	d4,(a4,d5.w)\n";

			if (delayImmediate)
			{
				code += "				addq.w	#2,d5\n";
				code += "				cmp.w	@DL<<1,d5\n";
				code += "				blt.s	.NoCombReset_" + localLabel + "\n";
				code += "				moveq	#0,d5\n";
				code += ".NoCombReset_" + localLabel + "\n";
				code += "				move.w  d5,AK_OpInstance+@IN1(a5)\n";
			}
			else
			{
				code += "				move.w	@DL,d6\n";
				code += "               cmp.w	#2047,d6\n";
				code += "				ble.s	.NoClampComb_" + localLabel + "\n";
				code += "				move.w  #2047,d6\n";
				code += ".NoClampComb_" + localLabel + "\n";
				code += "				add.w	d6,d6\n";
				code += "				addq.w	#2,d5\n";
				code += "				cmp.w	d6,d5\n";
				code += "				blt.s	.NoCombReset_" + localLabel + "\n";
				code += "				moveq	#0,d5\n";
				code += ".NoCombReset_" + localLabel + "\n";
				code += "				move.w  d5,AK_OpInstance+@IN1(a5)\n";
			}

			if (gain != "#128")
			{
				if (mulRightShifts.ContainsKey(gain))
				{
					code += "				move.w	d4,@OR\n";
					code += "				asr.w	@GS,@OR\n";
				}
				else if (gain.Contains("#"))
				{
					code += "				move.w	d4,@OR\n";
					code += "				muls	@GN,@OR\n";
					code += "				asr.l	#7,@OR\n";
				}
				else
				{
					code += "				move.w	@GN,d5\n";
					code += "				and.w	#255,d5\n";
					code += "				move.w	d4,@OR\n";
					code += "				muls	d5,@OR\n";
					code += "				asr.l	#7,@OR\n";
				}
			}
			else
				code += "				move.w	d4,@OR\n";

			code = code.Replace("@IN1", instance);
			code = code.Replace("@IN2", instance2);
			code = code.Replace("@VL", val);
			code = code.Replace("@DL", delay);
			code = code.Replace("@GN", gain);
			code = code.Replace("@GS", gainShift);
			code = code.Replace("@FB", feedback);
			code = code.Replace("@FS", feedbackShift);
			code = code.Replace("@OR", outReg);

			currentLargeBufferInstance += 1;
			currentWordInstance += 1;

			return code;
		}


		static string Reverb(string output, string[] inputs)
		{
			//short cmb_flt_n(BYTE instance, short val, short delay, UBYTE feedback, UBYTE gain)
			//{
			//	if (delay > 2047) delay = 2047;
			//	static short i[16];
			//	short output = buffern[instance][i[instance]] = add(val, (vol(buffern[instance][i[instance]], feedback)));
			//  i[instance]++;
			//  if ( [instance] >= delay)
			//		i[instance] = 0;
			//	return vol(output, gain);
			//}

			/*short reverb(short val, UBYTE feedback, UBYTE gain)
			{
				int buf = cmb_flt_n(0, val, 557, feedback, gain);
				buf += cmb_flt_n(1, val, 593, feedback, gain);
				buf += cmb_flt_n(2, val, 641, feedback, gain);
				buf += cmb_flt_n(3, val, 677, feedback, gain);
				buf += cmb_flt_n(4, val, 709, feedback, gain);
				buf += cmb_flt_n(5, val, 743, feedback, gain);
				buf += cmb_flt_n(6, val, 787, feedback, gain);
				buf += cmb_flt_n(7, val, 809, feedback, gain);
				return clamp(buf);
			}*/

			// val = V1-V4
			// feedback = Immediate or V1-V4
			// gain = Immediate or V1-V4


			string outReg = RemapVarToRegisterOrImmediate(output);
			string val = RemapVarToRegisterOrImmediate(inputs[0]);
			string feedback = RemapVarToRegisterOrImmediate(inputs[1]);
			string feedbackShift = mulRightShifts.ContainsKey(feedback) ? mulRightShifts[feedback] : string.Empty;
			string gain = RemapVarToRegisterOrImmediate(inputs[2]);
			string gainShift = mulRightShifts.ContainsKey(gain) ? mulRightShifts[gain] : string.Empty;

			int[] ReverbDelayTable = new int[8] { 557, 593, 641, 677, 709, 743, 787, 809 };

			string code = string.Empty;

			code += "				move.l	d7,-(sp)\n";
			code += "				sub.l	a6,a6\n";

			for (int i = 0; i < 8; i++)
			{

				//string instance = GetInstanceOffset(i.ToString(), 2);
				int instanceVal = (currentWordInstance * 2);
				string instance = instanceVal.ToString();

				//string instance2 = GetInstanceOffset(i.ToString(), 4096);
				//int instance2Val = int.Parse(instance2);
				int instance2Val = (currentLargeBufferInstance * 4096);
				string instance2 = instance2Val.ToString();

				string delay = "#" + ReverbDelayTable[i].ToString();

				if (instance2Val == 0)
				{
					code += "				move.l	a1,a4\n";
				}
				else if (instance2Val < 32767)
				{
					code += "				lea		@IN2(a1),a4\n";
				}
				else
				{
					code += "				move.l	a1,a4\n";
					code += "				add.l	#@IN2,a4\n";
				}
				code += "				move.w	AK_OpInstance+@IN1(a5),d5\n";
				code += "				move.w	(a4,d5.w),d4\n";

				if (feedback != "#128")
				{
					if (mulRightShifts.ContainsKey(feedback))
					{
						code += "				asr.w	@FS,d4\n";
					}
					else if (feedback.Contains("#"))
					{
						code += "				muls	@FB,d4\n";
						code += "				asr.l	#7,d4\n";
					}
					else
					{
						code += "				move.w	@FB,d6\n";
						code += "				and.w	#255,d6\n";
						code += "				muls	d6,d4\n";
						code += "				asr.l	#7,d4\n";
					}
				}

				code += "				add.w	@VL,d4\n";
				code += "				bvc.s	.ReverbAddNoClamp_" + localLabel + "_" + i.ToString() + "\n";
				code += "				spl		d4\n";
				code += "				ext.w	d4\n";
				code += "				eor.w	#$7fff,d4\n";
				code += ".ReverbAddNoClamp_" + localLabel + "_" + i.ToString() + "\n";
				code += "				move.w	d4,(a4,d5.w)\n";

				code += "				addq.w	#2,d5\n";
				code += "				cmp.w	@DL<<1,d5\n";
				code += "				ble.s	.NoReverbReset_" + localLabel + "_" + i.ToString() + "\n";
				code += "				moveq	#0,d5\n";
				code += ".NoReverbReset_" + localLabel + "_" + i.ToString() + "\n";
				code += "				move.w  d5,AK_OpInstance+@IN1(a5)\n";

				if (gain != "#128")
				{
					if (mulRightShifts.ContainsKey(gain))
					{
						code += "				move.w	d4,d7\n";
						code += "				asr.w	@GS,d7\n";
					}
					else if (gain.Contains("#"))
					{
						code += "				move.w	d4,d7\n";
						code += "				muls	@GN,d7\n";
						code += "				asr.l	#7,d7\n";
					}
					else
					{
						code += "				move.w	@GN,d5\n";
						code += "				and.w	#255,d5\n";
						code += "				move.w	d4,d7\n";
						code += "				muls	d5,d7\n";
						code += "				asr.l	#7,d7\n";
					}
					code += "				add.w	d7,a6\n";
				}
				else
				{
					code += "				add.w	d4,a6\n";
				}

				code = code.Replace("@IN1", instance);
				code = code.Replace("@IN2", instance2);
				code = code.Replace("@VL", val);
				code = code.Replace("@DL", delay);
				code = code.Replace("@GN", gain);
				code = code.Replace("@GS", gainShift);
				code = code.Replace("@FB", feedback);
				code = code.Replace("@FS", feedbackShift);
				code = code.Replace("@OR", outReg);

				currentLargeBufferInstance += 1;
				currentWordInstance += 1;
			}

			code += "				move.l	a6,d7\n";
			code += "				cmp.l	#32767,d7\n";
			code += "				ble.s	.NoReverbMax_" + localLabel + "\n";
			code += "				move.w	#32767,d7\n";
			code += "				bra.s	.NoReverbMin_" + localLabel + "\n";
			code += ".NoReverbMax_" + localLabel + "\n";
			code += "				cmp.l	#-32768,d7\n";
			code += "				bge.s	.NoReverbMin_" + localLabel + "\n";
			code += "				move.w	#-32768,d7\n";
			code += ".NoReverbMin_" + localLabel + "\n";
			code += "				move.w	d7,@OR\n";
			code += "				move.l	(sp)+,d7\n";

			code = code.Replace("@OR", outReg);
			return code;
		}

		static string SVFilter(string output, string[] inputs)
		{
			//short sv_flt_n(BYTE instance, short val, short cutoff, UBYTE resonance, BYTE mode)
			//{
			//	short* buffer = filterBuffer + (instance << 2);
			//	short lpf = buffer[0]; short hpf = buffer[1]; short bpf = buffer[2];
			//	lpf = clamp(lpf + (mulsw(bpf >> 7, cutoff)));
			//	hpf = clamp(val - lpf - (mulsw(bpf >> 7, resonance)));
			//	bpf = clamp(bpf + (mulsw(hpf >> 7, cutoff)));
			//	buffer[0] = lpf; buffer[1] = hpf; buffer[2] = bpf;
			//	switch (mode)
			//	{
			//		case 0: return lpf; break;
			//		case 1: return hpf; break;
			//		case 2: return bpf; break;
			//		case 3: return clamp(hpf + hpf); break;
			//	}
			//	return 0;
			//}

			// instance = 0-15
			// val = V1-V4
			// cutoff = Immediate or V1-V4
			// resonance = Immediate or V1-V4
			// mode = Immediate (chooses return code path)

			string outReg = RemapVarToRegisterOrImmediate(output);

			//string instance = GetInstanceOffset(inputs[0], 8);
			int instanceVal = (currentWordInstance * 2);
			string instance = instanceVal.ToString();

			string val = RemapVarToRegisterOrImmediate(inputs[1]);
			string cutoff = RemapVarToRegisterOrImmediate(inputs[2]);
			string cutoffShift = mulLeftShifts.ContainsKey(cutoff) ? mulLeftShifts[cutoff] : string.Empty;
			string resonance = RemapVarToRegisterOrImmediate(inputs[3]);
			string resonanceShift = mulLeftShifts.ContainsKey(resonance) ? mulLeftShifts[resonance] : string.Empty;
			string mode = inputs[4];

			string code = string.Empty;

			//////////////////////////
			//code += "				; LPF STAGE: lpf = clamp(lpf + (mulsw(bpf>>7, cutoff)))\n";
			code += "				move.w	AK_OpInstance+AK_BPF+@IN(a5),d5\n";
			code += "				asr.w	#7,d5\n";
			code += "				move.w	d5,d6\n";

			if (mulLeftShifts.ContainsKey(cutoff))
			{
				code += "				asl.w	@CS,d5\n";
			}
			else if (cutoff.Contains("#"))
			{
				code += "				muls	@CO,d5\n";
			}
			else
			{
				code += "				muls	@CO,d5\n";
			}
			code += "				move.w	AK_OpInstance+AK_LPF+@IN(a5),d4\n";
			code += "				add.w	d5,d4\n";
			code += "				bvc.s	.NoClampLPF_" + localLabel + "\n";
			code += "				spl		d4\n";
			code += "				ext.w	d4\n";
			code += "				eor.w	#$7fff,d4\n";
			code += ".NoClampLPF_" + localLabel + "\n";
			code += "				move.w	d4,AK_OpInstance+AK_LPF+@IN(a5)\n";

			//////////////////////////
			//code += "				; HPF STAGE: hpf = clamp(val - lpf - (mulsw(bpf>>7, resonance)));\n";
			if (mulLeftShifts.ContainsKey(resonance))
			{
				code += "				asl.w	@RS,d6\n";
				code += "				ext.l	d6\n";
			}
			else if (resonance.Contains("#"))
			{
				code += "				muls	@RE,d6\n";
			}
			else
			{
				code += "				move.w	@RE,d5\n";
				code += "				and.w	#255,d5\n";
				code += "				muls	d5,d6\n";
			}
			code += "				move.w	@VL,d5\n";
			code += "				ext.l	d5\n";
			code += "				ext.l	d4\n";
			code += "				sub.l	d4,d5\n";
			code += "				sub.l	d6,d5\n";
			code += "				cmp.l	#32767,d5\n";
			code += "				ble.s	.NoClampMaxHPF_" + localLabel + "\n";
			code += "				move.w	#32767,d5\n";
			code += "				bra.s	.NoClampMinHPF_" + localLabel + "\n";
			code += ".NoClampMaxHPF_" + localLabel + "\n";
			code += "				cmp.l	#-32768,d5\n";
			code += "				bge.s	.NoClampMinHPF_" + localLabel + "\n";
			code += "				move.w	#-32768,d5\n";
			code += ".NoClampMinHPF_" + localLabel + "\n";
			code += "				move.w	d5,AK_OpInstance+AK_HPF+@IN(a5)\n";

			//////////////////////////
			//code += "				; BPF STAGE: bpf = clamp(bpf + (mulsw(hpf>>7, cutoff)))\n";

			code += "				asr.w	#7,d5\n";
			if (mulLeftShifts.ContainsKey(cutoff))
			{
				code += "				asl.w	@CS,d5\n";
			}
			else if (cutoff.Contains("#"))
			{
				code += "				muls	@CO,d5\n";
			}
			else
			{
				code += "				muls	@CO,d5\n";
			}

			code += "				add.w	AK_OpInstance+AK_BPF+@IN(a5),d5\n";
			code += "				bvc.s	.NoClampBPF_" + localLabel + "\n";
			code += "				spl		d5\n";
			code += "				ext.w	d5\n";
			code += "				eor.w	#$7fff,d5\n";
			code += ".NoClampBPF_" + localLabel + "\n";
			code += "				move.w	d5,AK_OpInstance+AK_BPF+@IN(a5)\n";


			if (mode == "0")
			{
				code += "				move.w	AK_OpInstance+AK_LPF+@IN(a5),@OR\n";
			}
			else if (mode == "1")
			{
				code += "				move.w	AK_OpInstance+AK_HPF+@IN(a5),@OR\n";
			}
			else if (mode == "2")
			{
				code += "				move.w	d5,@OR\n";
			}
			else if (mode == "3")
			{
				code += "				move.w	AK_OpInstance+AK_HPF+@IN(a5),@OR\n";
				code += "				add.w	@OR,@OR\n";
				code += "				bvc.s	.NoClampMode3_" + localLabel + "\n";
				code += "				spl		@OR\n";
				code += "				ext.w	@OR\n";
				code += "				eor.w	#$7fff,@OR\n";
				code += ".NoClampMode3_" + localLabel + "\n";
			}

			code = code.Replace("@IN", instance);
			code = code.Replace("@VL", val);
			code = code.Replace("@CO", cutoff);
			code = code.Replace("@CS", cutoffShift);
			code = code.Replace("@RE", resonance);
			code = code.Replace("@RS", resonanceShift);
			code = code.Replace("@OR", outReg);

			currentWordInstance += 3;

			return code;
		}

		static string OnePoleFilter(string output, string[] inputs)
		{
			//short onepole_flt(BYTE instance, short val, BYTE cutoff, BYTE mode)
			//{
			//	short* buffer = filterBuffer + (instance << 2);
			//	short pole = buffer[3];
			//	pole = clamp(pole - (mulsw(pole >> 7, cutoff)) + (mulsw(val >> 7, cutoff)));
			//	buffer[3] = pole;
			//	switch (mode)
			//	{
			//		case 0: return pole; break;
			//		case 1: return (val - pole); break;
			//	}
			//	return 0;
			//}

			// instance = 0-15
			// val = V1-V4
			// cutoff = Immediate or V1-V4
			// mode = Immediate (chooses return code path)

			string outReg = RemapVarToRegisterOrImmediate(output);

			int instanceVal = (currentWordInstance * 2);
			string instance = instanceVal.ToString();

			string val = RemapVarToRegisterOrImmediate(inputs[1]);
			string cutoff = RemapVarToRegisterOrImmediate(inputs[2]);
			string cutoffShift = mulLeftShifts.ContainsKey(cutoff) ? mulLeftShifts[cutoff] : string.Empty;
			string mode = inputs[3];

			string code = string.Empty;

			//////////////////////////
			code += "				move.w	AK_OpInstance+@IN(a5),d5\n";
			code += "				move.w	d5,d6\n";
			code += "				ext.l	d6\n";
			code += "				asr.w	#7,d5\n";

			if (mulLeftShifts.ContainsKey(cutoff))
			{
				code += "				asl.w	@CS,d5\n";
				code += "				ext.l	d5\n";
			}
			else if (cutoff.Contains("#"))
			{
				code += "				muls	@CO,d5\n";
			}
			else
			{
				code += "				move.w	@CO,d4\n";
				code += "				and.w	#255,d4\n";
				code += "				muls	d4,d5\n";
			}

			code += "				sub.l	d5,d6\n";

			code += "				move.w	@VL,d5\n";
			code += "				asr.w	#7,d5\n";

			if (mulLeftShifts.ContainsKey(cutoff))
			{
				code += "				asl.w	@CS,d5\n";
				code += "				ext.l	d5\n";
			}
			else if (cutoff.Contains("#"))
			{
				code += "				muls	@CO,d5\n";
			}
			else
			{
				code += "				move.w	@CO,d4\n";
				code += "				and.w	#255,d4\n";
				code += "				muls	d4,d5\n";
			}

			code += "				add.l	d6,d5\n";


			code += "				cmp.l	#32767,d5\n";
			code += "				ble.s	.NoClampMaxOPF_" + localLabel + "\n";
			code += "				move.w	#32767,d5\n";
			code += "				bra.s	.NoClampMinOPF_" + localLabel + "\n";
			code += ".NoClampMaxOPF_" + localLabel + "\n";
			code += "				cmp.l	#-32768,d5\n";
			code += "				bge.s	.NoClampMinOPF_" + localLabel + "\n";
			code += "				move.w	#-32768,d5\n";
			code += ".NoClampMinOPF_" + localLabel + "\n";
			code += "				move.w	d5,AK_OpInstance+@IN(a5)\n";

			if (mode == "0")
			{
				code += "				move.w	d5,@OR\n";
			}
			else if (mode == "1")
			{
				if (val != outReg)
				{
					code += "				move.w	@VL,@OR\n";
				}
				code += "				sub.w	d5,@OR\n";

				code += "				bvc.s	.NoClampOPFMode1_" + localLabel + "\n";
				code += "				spl		@OR\n";
				code += "				ext.w	@OR\n";
				code += "				eor.w	#$7fff,@OR\n";
				code += ".NoClampOPFMode1_" + localLabel + "\n";

			}

			code = code.Replace("@IN", instance);
			code = code.Replace("@VL", val);
			code = code.Replace("@CO", cutoff);
			code = code.Replace("@CS", cutoffShift);
			code = code.Replace("@OR", outReg);

			currentWordInstance += 1;

			return code;
		}

		static string Distortion(string output, string[] inputs)
		{
			//short distortion(int val, UBYTE gain)
			//{
			//	int temp;
			//	short res;
			//	val = clamp((val * gain) >> 5);
			//  val >>= 1;
			//	temp = mulsw(val, 32767 - abs(val));
			//	res = temp >> 16;
			//	res <<= 3;
			//	return res;
			//}

			// val = v1-v4
			// gain = Immediate or V1-v4

			string outReg = RemapVarToRegisterOrImmediate(output);
			string val = RemapVarToRegisterOrImmediate(inputs[0]);
			string gain = RemapVarToRegisterOrImmediate(inputs[1]);
			string gainShift = mulLeftShifts.ContainsKey(gain) ? mulLeftShifts[gain] : string.Empty;

			string code = string.Empty;

			code += "				move.w	@VL,d5\n";
			if (!gain.Contains("#"))
			{
				code += "				move.w	@GN,d4\n";
				code += "				and.w	#255,d4\n";
				code += "				muls	d4,d5\n";
				code += "				asr.l	#5,d5\n";
			}
			else
			{
				if (mulLeftShifts.ContainsKey(gain))
				{
					code += "				ext.l	d5\n";
					code += "				asl.l	@GS,d5\n";
					code += "				asr.l	#5,d5\n";
				}
				else
				{
					code += "				muls	@GN,d5\n";
					code += "				asr.l	#5,d5\n";
				}
			}

			code += "				cmp.l	#32767,d5\n";
			code += "				ble.s	.NoClampMaxDist_" + localLabel + "\n";
			code += "				move.w	#32767,d5\n";
			code += "				bra.s	.NoClampMinDist_" + localLabel + "\n";
			code += ".NoClampMaxDist_" + localLabel + "\n";
			code += "				cmp.l	#-32768,d5\n";
			code += "				bge.s	.NoClampMinDist_" + localLabel + "\n";
			code += "				move.w	#-32768,d5\n";
			code += ".NoClampMinDist_" + localLabel + "\n";
			code += "				asr.w	#1,d5\n";
			code += "				move.w	d5,@OR\n";
			code += "				bge.s	.DistNoAbs_" + localLabel + "\n";
			code += "				neg.w	d5\n";
			code += ".DistNoAbs_" + localLabel + "\n";
			code += "				move.w	#32767,d6\n";
			code += "				sub.w	d5,d6\n";
			code += "				muls	d6,@OR\n";
			code += "				swap	@OR\n";
			code += "				asl.w	#3,@OR\n";

			code = code.Replace("@VL", val);
			code = code.Replace("@GN", gain);
			code = code.Replace("@GS", gainShift);
			code = code.Replace("@OR", outReg);

			return code;
		}

		static string ADSR(string output, string[] inputs)
		{
			//short adsr(BYTE instance, int attackAmount, int decayAmount, int sustainLevel, int sustainLength, int releaseAmount, int peak)
			//{
			//	int val = ADSR_Value[instance];
			//	switch (ADSR_Mode[instance])
			//	{
			//		case 0:         // Attack
			//			val += attackAmount; if (val >= peak) { val = peak; ADSR_Mode[instance] = 1; }
			//			break;
			//		case 1:         // Decay
			//			val -= decayAmount; if (val <= sustainLevel) { val = sustainLevel; ADSR_Mode[instance] = 2; }
			//			break;
			//		case 2:         // Sustain
			//			ADSR_SustainCounter[instance]++; if (ADSR_SustainCounter[instance] > sustainLength) { ADSR_Mode[instance] = 3; }
			//			break;
			//		case 3:        // Release
			//			val -= releaseAmount; if (val < 0) { val = 0; }
			//			break;
			//	}
			//	ADSR_Value[instance] = val;
			//	return val >> 8;
			//}

			int instanceVal = (currentWordInstance * 2);
			string instance1 = instanceVal.ToString();
			string instance2 = (instanceVal + 4).ToString();
			string instance3 = (instanceVal + 6).ToString();

			string outReg = RemapVarToRegisterOrImmediate(output);
			string attackAmount = RemapVarToRegisterOrImmediate(inputs[1]);
			string decayAmount = RemapVarToRegisterOrImmediate(inputs[2]);
			string sustainLevel = RemapVarToRegisterOrImmediate(inputs[3]);
			string sustainLength = RemapVarToRegisterOrImmediate(inputs[4]);
			string releaseAmount = RemapVarToRegisterOrImmediate(inputs[5]);
			string peakValue = RemapVarToRegisterOrImmediate(inputs[6]);

			string code = string.Empty;

			code += "				move.l	AK_OpInstance+@IN1(a5),@OR\n";
			code += "				move.w	AK_OpInstance+@IN2(a5),d4\n";
			code += "				beq.s	.ADSR_A_" + localLabel + "\n";
			code += "				subq.w	#1,d4\n";
			code += "				beq.s	.ADSR_D_" + localLabel + "\n";
			code += "				subq.w	#1,d4\n";
			code += "				beq.s	.ADSR_S_" + localLabel + "\n";
			code += ".ADSR_R_" + localLabel + "\n";
			code += "				sub.l	@RA,@OR\n";
			code += "				bge.s	.ADSR_End_" + localLabel + "\n";
			code += "				moveq	#0,@OR\n";
			code += "				bra.s	.ADSR_End_" + localLabel + "\n";
			code += ".ADSR_A_" + localLabel + "\n";
			code += "				add.l	@AA,@OR\n";
			code += "				cmp.l	@PV,@OR\n";
			code += "				blt.s	.ADSR_End_" + localLabel + "\n";
			code += "				move.l	@PV,@OR\n";
			code += "				move.w	#1,AK_OpInstance+@IN2(a5)\n";
			code += "				bra.s	.ADSR_End_" + localLabel + "\n";
			code += ".ADSR_D_" + localLabel + "\n";
			code += "				sub.l	@DA,@OR\n";
			code += "				cmp.l	@SLEV,@OR\n";
			code += "				bgt.s	.ADSR_End_" + localLabel + "\n";
			code += "				move.l	@SLEV,@OR\n";
			code += "				move.l	@SLEN,AK_OpInstance+@IN3(a5)\n";
			code += "				move.w	#2,AK_OpInstance+@IN2(a5)\n";
			code += "				bra.s	.ADSR_End_" + localLabel + "\n";
			code += ".ADSR_S_" + localLabel + "\n";
			code += "				subq.l	#1,AK_OpInstance+@IN3(a5)\n";
			code += "				bge.s	.ADSR_End_" + localLabel + "\n";
			code += "				move.w	#3,AK_OpInstance+@IN2(a5)\n";
			code += ".ADSR_End_" + localLabel + "\n";
			code += "				move.l	@OR,AK_OpInstance+@IN1(a5)\n";
			code += "				asr.l	#8,@OR\n";

			code = code.Replace("@IN1", instance1);
			code = code.Replace("@IN2", instance2);
			code = code.Replace("@IN3", instance3);
			code = code.Replace("@AA", attackAmount);
			code = code.Replace("@DA", decayAmount);
			code = code.Replace("@SLEV", sustainLevel);
			code = code.Replace("@SLEN", sustainLength);
			code = code.Replace("@RA", releaseAmount);
			code = code.Replace("@PV", peakValue);
			code = code.Replace("@OR", outReg);

			currentWordInstance += 5;

			return code;
		}



		static string ChordGen(string output, string[] inputs)
		{
			//short chordgen(int sample, int instrument, BYTE n1, BYTE n2, BYTE n3, UBYTE shift)
			//{
			//	int buf = *(BYTE*)(BaseAdr + sample) << 7;
			//	if (n1 == 1 || n2 == 1 || n3 == 1) buf += *(BYTE*)(shift + BaseAdr + ((mulsw(sample, 271)) >> 8)) << 7;
			//	if (n1 == 2 || n2 == 2 || n3 == 2) buf += *(BYTE*)(shift + BaseAdr + ((mulsw(sample, 287)) >> 8)) << 7;
			//	if (n1 == 3 || n2 == 3 || n3 == 3) buf += *(BYTE*)(shift + BaseAdr + ((mulsw(sample, 304)) >> 8)) << 7;
			//	if (n1 == 4 || n2 == 4 || n3 == 4) buf += *(BYTE*)(shift + BaseAdr + ((mulsw(sample, 322)) >> 8)) << 7;
			//	if (n1 == 5 || n2 == 5 || n3 == 5) buf += *(BYTE*)(shift + BaseAdr + ((mulsw(sample, 342)) >> 8)) << 7;
			//	if (n1 == 6 || n2 == 6 || n3 == 6) buf += *(BYTE*)(shift + BaseAdr + ((mulsw(sample, 362)) >> 8)) << 7;
			//	if (n1 == 7 || n2 == 7 || n3 == 7) buf += *(BYTE*)(shift + BaseAdr + ((mulsw(sample, 383)) >> 8)) << 7;
			//	if (n1 == 8 || n2 == 8 || n3 == 8) buf += *(BYTE*)(shift + BaseAdr + ((mulsw(sample, 203)) >> 7)) << 7;
			//	if (n1 == 9 || n2 == 9 || n3 == 9) buf += *(BYTE*)(shift + BaseAdr + ((mulsw(sample, 215)) >> 7)) << 7;
			//	if (n1 == 10 || n2 == 10 || n3 == 10) buf += *(BYTE*)(shift + BaseAdr + ((mulsw(sample, 228)) >> 7)) << 7;
			//	if (n1 == 11 || n2 == 11 || n3 == 11) buf += *(BYTE*)(shift + BaseAdr + ((mulsw(sample, 483)) >> 8)) << 7;
			//	if (n1 == 12 || n2 == 12 || n3 == 12) buf += *(BYTE*)(shift + BaseAdr + (sample << 1)) << 7;
			//	return clamp(buf);
			//}


			// instance = immediate 0-15
			// instrument = Immediate 0-31
			// note1 = Immediate 0-12
			// note2 = Immediate 0-12
			// note3 = Immediate 0-12
			// shift = Immediate or V1-V4

			string outReg = RemapVarToRegisterOrImmediate(output);

			//string instance = GetInstanceOffset(inputs[0], 4);
			int instanceVal = (currentWordInstance * 2);
			string instance = instanceVal.ToString();

			string basesample = GetInstanceOffset(inputs[1], 4);
			string note1 = GetChordValue(int.Parse(inputs[2]));
			string note2 = GetChordValue(int.Parse(inputs[3]));
			string note3 = GetChordValue(int.Parse(inputs[4]));
			string shift = RemapVarToRegisterOrImmediate(inputs[5]);
			int shiftInt = shift.Contains("#") ? int.Parse(inputs[5]) : 0;

			string code = string.Empty;

			if (wide)
				return ChordGenWide(outReg, instance, basesample, note1, note2, note3, shift, shiftInt);

			code += "				move.l	AK_SmpAddr+@BS(a5),a4\n";
			code += "				move.b	(a4,d7.l),d6\n";
			//code += "				ext.w	d6\n";
			//code += "				ext.l	d6\n";
			//code += "				asl.l	#7,d6\n";
			code += "				ext.w	d6\n";

			if (!shift.Contains("#"))
			{
				code += "				move.w	@SH,d4\n";
				code += "				and.w	#255,d4\n";
				code += "				add.w	d4,a4\n";
			}
			else
			{
				if (shiftInt != 0)
				{
					if (shiftInt <= 8)
						code += "				addq.w	@SH,a4\n";
					else
						code += "				add.w	@SH,a4\n";
				}
			}

			code += "				moveq	#0,d4\n";

			if (note1 != "#0")
			{
				code += "				move.w	AK_OpInstance+AK_CHORD1+@IN(a5),d4\n";
				code += "				add.l	@N1,AK_OpInstance+AK_CHORD1+@IN(a5)\n";
				code += "				move.b	(a4,d4.l),d5\n";
				code += "				ext.w	d5\n";
				//code += "				ext.l	d5\n";
				//code += "				asl.l	#7,d5\n";
				//code += "				add.l	d5,d6\n";
				code += "				add.w	d5,d6\n";
			}
			if (note2 != "#0" && note2 != note1)
			{
				code += "				move.w	AK_OpInstance+AK_CHORD2+@IN(a5),d4\n";
				code += "				add.l	@N2,AK_OpInstance+AK_CHORD2+@IN(a5)\n";
				code += "				move.b	(a4,d4.l),d5\n";
				code += "				ext.w	d5\n";
				//code += "				ext.l	d5\n";
				//code += "				asl.l	#7,d5\n";
				//code += "				add.l	d5,d6\n";
				code += "				add.w	d5,d6\n";
			}
			if (note3 != "#0" && note3 != note1 && note3 != note2)
			{
				code += "				move.w	AK_OpInstance+AK_CHORD3+@IN(a5),d4\n";
				code += "				add.l	@N3,AK_OpInstance+AK_CHORD3+@IN(a5)\n";
				code += "				move.b	(a4,d4.l),d5\n";
				code += "				ext.w	d5\n";
				//code += "				ext.l	d5\n";
				//code += "				asl.l	#7,d5\n";
				//code += "				add.l	d5,d6\n";
				code += "				add.w	d5,d6\n";
			}

			/*code += "				cmp.l	#32767,d6\n";
			code += "				blt.s	.NoClampMaxChord_" + localLabel + "\n";
			code += "				move.l	#32767,d6\n";
			code += "				bra.s	.NoClampMinChord_" + localLabel + "\n";
			code += ".NoClampMaxChord_" + localLabel + "\n";
			code += "				cmp.l	#-32768,d6\n";
			code += "				bge.s	.NoClampMinChord_" + localLabel + "\n";
			code += "				move.l	#-32768,d6\n";
			code += ".NoClampMinChord_" + localLabel + "\n";*/

			/*code += "				asl.w	#7,d6\n";
			code += "				bvc.s	.NoClampChord_" + localLabel + "\n";
			code += "				spl		d6\n";
			code += "				ext.w	d6\n";
			code += "				eor.w	#$7fff,d6\n";
			code += ".NoClampChord_" + localLabel + "\n";*/

			code += "				move.w	#255,d5\n";
			code += "				cmp.w	d5,d6\n";
			code += "				blt.s	.NoClampMaxChord_" + localLabel + "\n";
			code += "				move.w	d5,d6\n";
			code += "				bra.s	.NoClampMinChord_" + localLabel + "\n";
			code += ".NoClampMaxChord_" + localLabel + "\n";
			code += "				not.w	d5\n";
			code += "				cmp.w	d5,d6\n";
			code += "				bge.s	.NoClampMinChord_" + localLabel + "\n";
			code += "				move.w	d5,d6\n";
			code += ".NoClampMinChord_" + localLabel + "\n";

			code += "				asl.w	#7,d6\n";
			code += "				move.w	d6,@OR\n";


			code = code.Replace("@OR", outReg);
			code = code.Replace("@IN", instance);
			code = code.Replace("@BS", basesample);
			code = code.Replace("@SH", shift);
			code = code.Replace("@N1", note1);
			code = code.Replace("@N2", note2);
			code = code.Replace("@N3", note3);

			currentWordInstance += 6;

			return code;
		}

		// 16 bit samples (-16): half of every voice is summed in 32 bit and limited to 16 bit, the
		// positions (16.16 counters) are the same as for 8 bit samples
		static string ChordGenWide(string outReg, string instance, string basesample, string note1, string note2, string note3, string shift, int shiftInt)
		{
			string code = string.Empty;

			// -unz: the samples of the player are not one behind the other, so a voice that has passed
			// the end of the source is silent (AK_Generate reads the memory behind the source there).
			// a6 = length of the source - shift, the limit of the voice positions
			if (unz)
			{
				code += "				move.l	AK_SmpAddr+@BS(a5),a4\n";
				code += "				move.l	AK_SmpLen+@BS(a5),a6\n";
				code += "				moveq	#0,d6\n";
				code += "				cmp.l	a6,d7\n";
				code += "				bge.s	.ChordEnd0_" + localLabel + "\n";
				code += "				add.l	d7,a4\n";
				code += "				move.w	(a4,d7.l),d6\n";
				code += "				asr.w	#1,d6\n";
				code += "				ext.l	d6\n";
				code += "				sub.l	d7,a4\n";
				code += ".ChordEnd0_" + localLabel + "\n";
			}
			else
			{
				code += "				move.l	AK_SmpAddr+@BS(a5),a4\n";
				code += "				add.l	d7,a4\n";
				code += "				move.w	(a4,d7.l),d6\n";
				code += "				asr.w	#1,d6\n";
				code += "				ext.l	d6\n";
				code += "				move.l	AK_SmpAddr+@BS(a5),a4\n";
			}

			if (!shift.Contains("#"))
			{
				code += "				move.w	@SH,d4\n";
				code += "				and.w	#255,d4\n";
				code += "				add.w	d4,a4\n";
				code += "				add.w	d4,a4\n";
				if (unz)
					code += "				sub.w	d4,a6\n";
			}
			else if (shiftInt != 0)
			{
				if (shiftInt * 2 <= 8)
					code += "				addq.w	#" + (shiftInt * 2).ToString() + ",a4\n";
				else
					code += "				add.w	#" + (shiftInt * 2).ToString() + ",a4\n";
				if (unz)
					code += "				sub.w	#" + shiftInt.ToString() + ",a6\n";
			}

			string[] notes = { note1, note2, note3 };
			for (int n = 0; n < 3; n++)
			{
				if (notes[n] == "#0") continue;
				if (n >= 1 && notes[n] == notes[0]) continue;
				if (n == 2 && notes[n] == notes[1]) continue;
				string counter = "AK_OpInstance+AK_CHORD" + (n + 1).ToString() + "+@IN(a5)";
				code += "				moveq	#0,d4\n";
				code += "				move.w	" + counter + ",d4\n";
				code += "				add.l	" + notes[n] + "," + counter + "\n";
				if (unz)
				{
					code += "				cmp.l	a6,d4\n";
					code += "				bge.s	.ChordEnd" + (n + 1).ToString() + "_" + localLabel + "\n";
				}
				code += "				add.l	d4,d4\n";
				code += "				move.w	(a4,d4.l),d5\n";
				code += "				asr.w	#1,d5\n";
				code += "				ext.l	d5\n";
				code += "				add.l	d5,d6\n";
				if (unz)
					code += ".ChordEnd" + (n + 1).ToString() + "_" + localLabel + "\n";
			}

			code += "				cmp.l	#32767,d6\n";
			code += "				ble.s	.NoClampMaxChord_" + localLabel + "\n";
			code += "				move.w	#32767,d6\n";
			code += "				bra.s	.NoClampMinChord_" + localLabel + "\n";
			code += ".NoClampMaxChord_" + localLabel + "\n";
			code += "				cmp.l	#-32768,d6\n";
			code += "				bge.s	.NoClampMinChord_" + localLabel + "\n";
			code += "				move.w	#-32768,d6\n";
			code += ".NoClampMinChord_" + localLabel + "\n";
			code += "				move.w	d6,@OR\n";

			code = code.Replace("@OR", outReg);
			code = code.Replace("@IN", instance);
			code = code.Replace("@BS", basesample);
			code = code.Replace("@SH", shift);

			currentWordInstance += 6;

			return code;
		}

		// reads the sample at a4 + index (+ the samples in "before": "" or "-1") of an instrument rendered
		// before as a 16 bit value. 8 bit: the byte << 8. 16 bit (-16): the word; the 68000 has no scaled
		// index, so the index is added to a4 first.
		static string ReadSample(string index, string before, string target)
		{
			string code = string.Empty;
			if (wide)
			{
				code += "				add.l	" + index + ",a4\n";
				code += "				move.w	" + (before == "" ? "" : (int.Parse(before) * 2).ToString()) + "(a4," + index + ".l)," + target + "\n";
			}
			else
			{
				code += "				move.b	" + before + "(a4," + index + ".l)," + target + "\n";
				code += "				asl.w	#8," + target + "\n";
			}
			return code;
		}

		static string Clone(string output, string[] inputs)
		{
			//short clone(int smp, int instrument, int offset)
			//{
			//	BYTE val = (smp + offset) < SmpLength[instrument] ? *(BYTE*)(BaseAdr[instrument] + smp + offset) : 0;
			//	return val<<8;
			//}

			// sample = d7
			// instrument = Immediate 0-31
			// offset = immediate
			// transpose = optional, Immediate or V1-V4 (see CloneTranspose)

			if (inputs.Length > 3 && inputs[3] != "0")
				return CloneTranspose(output, inputs, false);

			string outReg = RemapVarToRegisterOrImmediate(output);
			string basesample = GetInstanceOffset(inputs[1], 4);
			string offset = RemapVarToRegisterOrImmediate(inputs[2]);
			int offsetInt = offset.Contains("#") ? int.Parse(inputs[2]) : 0;

			string code = string.Empty;

			if (offsetInt > 0)
			{
				code += "				move.l	d7,d6\n";
				if (offsetInt <= 8)
					code += "				addq.l	@OS,d6\n";
				else
					code += "				add.l	@OS,d6\n";
			}
			code += "				moveq	#0,@OR\n";
			if (offsetInt > 0)
				code += "				cmp.l	AK_SmpLen+@BS(a5),d6\n";
			else
				code += "				cmp.l	AK_SmpLen+@BS(a5),d7\n";
			code += "				bge.s	.NoClone_" + localLabel + "\n";
			code += "				move.l	AK_SmpAddr+@BS(a5),a4\n";
			code += ReadSample(offsetInt > 0 ? "d6" : "d7", "", "@OR");
			code += ".NoClone_" + localLabel + "\n";

			code = code.Replace("@OR", outReg);
			code = code.Replace("@BS", basesample);
			code = code.Replace("@OS", offset);

			return code;
		}

		static string CloneReverse(string output, string[] inputs)
		{
			//short clone(int smp, int instrument, int offset)
			//{
			//	BYTE val = (smp + offset) < SmpLength[instrument] ? *(BYTE*)(BaseAdr[instrument+1] - (smp + offset)) : 0;
			//	return val<<8;
			//}

			// sample = d7
			// instrument = Immediate 0-31
			// offset = immediate
			// transpose = optional, Immediate or V1-V4 (see CloneTranspose)

			if (inputs.Length > 3 && inputs[3] != "0")
				return CloneTranspose(output, inputs, true);

			string outReg = RemapVarToRegisterOrImmediate(output);
			string basesample = GetInstanceOffset(inputs[1], 4);
			string offset = RemapVarToRegisterOrImmediate(inputs[2]);
			int offsetInt = offset.Contains("#") ? int.Parse(inputs[2]) : 0;

			string code = string.Empty;

			code += "				move.l	d7,d6\n";
			if (offsetInt > 0)
			{
				if (offsetInt <= 8)
					code += "				addq.l	@OS,d6\n";
				else
					code += "				add.l	@OS,d6\n";
			}
			code += "				moveq	#0,@OR\n";
			code += "				cmp.l	AK_SmpLen+@BS(a5),d6\n";
			code += "				bge.s	.NoCloneR_" + localLabel + "\n";
			code += "				move.l	AK_SmpAddr+@BS+4(a5),a4\n";
			code += "				neg.l	d6\n";
			code += ReadSample("d6", "-1", "@OR");
			code += ".NoCloneR_" + localLabel + "\n";

			code = code.Replace("@OR", outReg);
			code = code.Replace("@BS", basesample);
			code = code.Replace("@OS", offset);

			return code;
		}

		static string CloneTranspose(string output, string[] inputs, bool reverse)
		{
			//short clone(int smp, int instrument, int offset, short transpose)
			//{
			//	// pos = read position in 16.16 (one long instance, cleared per instrument)
			//	ULONG p = (pos >> 16) + offset;
			//	if (p >= SmpLength[instrument]) return 0;	// pos stops at the end of the source
			//	pos += 65536 + (transpose < 0 ? transpose : 2 * transpose);	// -32768 = half speed, 0 = original, 32767 = double speed
			//	BYTE val = forward ? *(BYTE*)(BaseAdr[instrument] + p) : *(BYTE*)(BaseAdr[instrument+1] - 1 - p);
			//	return val<<8;
			//}

			// instrument = Immediate 0-31
			// offset = immediate
			// transpose = Immediate or V1-V4

			string outReg = RemapVarToRegisterOrImmediate(output);
			string basesample = GetInstanceOffset(inputs[1], 4);
			string offset = RemapVarToRegisterOrImmediate(inputs[2]);
			int offsetInt = offset.Contains("#") ? int.Parse(inputs[2]) : 0;
			string transpose = RemapVarToRegisterOrImmediate(inputs[3]);

			int instanceVal = (currentWordInstance * 2);
			string instance = instanceVal.ToString();

			string code = string.Empty;

			// step first: the output register may be the transpose variable
			if (!transpose.Contains("#"))
			{
				code += "				move.w	@TR,d5\n";
				code += "				ext.l	d5\n";
				code += "				bmi.s	.CloneTrNeg_" + localLabel + "\n";
				code += "				add.l	d5,d5\n";
				code += ".CloneTrNeg_" + localLabel + "\n";
				code += "				add.l	#65536,d5\n";
			}
			code += "				move.l	AK_OpInstance+@IN(a5),d6\n";
			code += "				clr.w	d6\n";
			code += "				swap	d6\n";
			if (offsetInt > 0)
			{
				if (offsetInt <= 8)
					code += "				addq.l	@OS,d6\n";
				else
					code += "				add.l	@OS,d6\n";
			}
			code += "				moveq	#0,@OR\n";
			code += "				cmp.l	AK_SmpLen+@BS(a5),d6\n";
			code += "				bge.s	.NoCloneT_" + localLabel + "\n";
			if (!transpose.Contains("#"))
				code += "				add.l	d5,AK_OpInstance+@IN(a5)\n";
			else
			{
				int t = int.Parse(inputs[3]);
				int step = 65536 + (t < 0 ? t : 2 * t);
				code += "				add.l	#" + step.ToString() + ",AK_OpInstance+@IN(a5)\n";
			}
			if (!reverse)
			{
					code += "				move.l	AK_SmpAddr+@BS(a5),a4\n";
					code += ReadSample("d6", "", "@OR");
				}
				else
				{
					code += "				move.l	AK_SmpAddr+@BS+4(a5),a4\n";
					code += "				neg.l	d6\n";
					code += ReadSample("d6", "-1", "@OR");
				}
			code += ".NoCloneT_" + localLabel + "\n";

			code = code.Replace("@OR", outReg);
			code = code.Replace("@BS", basesample);
			code = code.Replace("@OS", offset);
			code = code.Replace("@TR", transpose);
			code = code.Replace("@IN", instance);

			currentWordInstance += 2;

			return code;
		}

		static string SampleMixer(string output, string[] inputs)
		{
			//short samplemixer(int instance, int rowlength, int notes, (row, track, source, step, volume) * notes)
			//{
			//	every rowlength samples the next of the 16 rows starts. A note (re)starts the voice of its
			//	track: source instrument (-1 = the track falls silent), position 0, step (16.16, 65536 = C-3)
			//	and volume 0..64. A voice plays its source once.
			//	sum = 0;
			//	for each voice: if ((pos >> 16) < SmpLength[source]) { sum += *(BYTE*)(BaseAdr[source] + (pos >> 16)) * volume; pos += step; }
			//	return clamp(sum * 4);
			//}

			// instance = Immediate (slot, unused)
			// rowlength = Immediate 1..65535
			// notes = Immediate, followed by 5 immediates per note (resolved by the editor)

			string outReg = RemapVarToRegisterOrImmediate(output);
			int rowLength = int.Parse(inputs[1]);
			int notes = int.Parse(inputs[2]);

			// instance: row counter.w, row.w, 4 voices of 20 bytes: address.l, length.l, position.l, step.l, volume.w, pad.w
			int instanceVal = (currentWordInstance * 2);
			string Var(int offset) => "AK_OpInstance+" + (instanceVal + offset).ToString() + "(a5)";
			const int counter = 0, row = 2;
			int Voice(int track) => 4 + track * 20;

			var rows = new SortedDictionary<int, List<int[]>>();
			bool[] tracksUsed = new bool[4];
			for (int n = 0; n < notes; n++)
			{
				int k = 3 + n * 5;
				int[] note = { int.Parse(inputs[k]), int.Parse(inputs[k + 1]), int.Parse(inputs[k + 2]), int.Parse(inputs[k + 3]), int.Parse(inputs[k + 4]) };
				if (!rows.ContainsKey(note[0])) rows[note[0]] = new List<int[]>();
				rows[note[0]].Add(note);
				tracksUsed[note[1]] = true;
			}

			string code = string.Empty;

			// next row: start the voices of its notes
			code += "				tst.w	" + Var(counter) + "\n";
			code += "				bne.w	.MixNoRow_" + localLabel + "\n";
			code += "				move.w	" + Var(row) + ",d4\n";
			code += "				cmp.w	#16,d4\n";
			code += "				bhs.w	.MixNoRow_" + localLabel + "\n";
			code += "				addq.w	#1," + Var(row) + "\n";
			code += "				move.w	#" + rowLength.ToString() + "," + Var(counter) + "\n";
			foreach (var r in rows)
			{
				code += "				cmp.w	#" + r.Key.ToString() + ",d4\n";
				code += "				bne.w	.MixRow" + r.Key.ToString() + "_" + localLabel + "\n";
				foreach (var note in r.Value)
				{
					int v = Voice(note[1]);
					if (note[2] < 0)
					{
						code += "				clr.l	" + Var(v + 4) + "\n";
						continue;
					}
					code += "				move.l	AK_SmpAddr+" + (note[2] * 4).ToString() + "(a5)," + Var(v) + "\n";
					code += "				move.l	AK_SmpLen+" + (note[2] * 4).ToString() + "(a5)," + Var(v + 4) + "\n";
					code += "				clr.l	" + Var(v + 8) + "\n";
					code += "				move.l	#" + note[3].ToString() + "," + Var(v + 12) + "\n";
					code += "				move.w	#" + note[4].ToString() + "," + Var(v + 16) + "\n";
				}
				code += ".MixRow" + r.Key.ToString() + "_" + localLabel + "\n";
			}
			code += ".MixNoRow_" + localLabel + "\n";
			code += "				subq.w	#1," + Var(counter) + "\n";

			// mix the voices
			code += "				moveq	#0,d5\n";
			for (int t = 0; t < 4; t++)
			{
				if (!tracksUsed[t]) continue;
				int v = Voice(t);
				code += "				move.l	" + Var(v + 8) + ",d6\n";
				code += "				clr.w	d6\n";
				code += "				swap	d6\n";
				code += "				cmp.l	" + Var(v + 4) + ",d6\n";
				code += "				bge.s	.MixVoice" + t.ToString() + "_" + localLabel + "\n";
				code += "				move.l	" + Var(v) + ",a4\n";
				if (wide)
				{
					code += "				add.l	d6,a4\n";
					code += "				move.w	(a4,d6.l),d4\n";
				}
				else
				{
					code += "				move.b	(a4,d6.l),d4\n";
					code += "				ext.w	d4\n";
				}
				code += "				muls	" + Var(v + 16) + ",d4\n";
				code += "				add.l	d4,d5\n";
				code += "				move.l	" + Var(v + 12) + ",d6\n";
				code += "				add.l	d6," + Var(v + 8) + "\n";
				code += ".MixVoice" + t.ToString() + "_" + localLabel + "\n";
			}

			// * 4 (16 bit samples: / 64), clamp to 16 bit
			code += wide ? "				asr.l	#6,d5\n" : "				asl.l	#2,d5\n";
			code += "				cmp.l	#32767,d5\n";
			code += "				ble.s	.MixNoMax_" + localLabel + "\n";
			code += "				move.w	#32767,d5\n";
			code += "				bra.s	.MixDone_" + localLabel + "\n";
			code += ".MixNoMax_" + localLabel + "\n";
			code += "				cmp.l	#-32768,d5\n";
			code += "				bge.s	.MixDone_" + localLabel + "\n";
			code += "				move.w	#-32768,d5\n";
			code += ".MixDone_" + localLabel + "\n";
			code += "				move.w	d5,@OR\n";

			code = code.Replace("@OR", outReg);

			currentWordInstance += 42;

			return code;
		}

		static string ImportedSample(string output, string[] inputs)
		{
			//short clone(int smp, int instrument)
			//{
			//	BYTE val = (smp < SmpLength[instrument] ) ? *(BYTE*)(ImpBaseAdr[instrument] + smp) : 0;
			//	return val<<8;
			//}

			// sample = d7
			// basesample = Immediate 0-11

			string outReg = RemapVarToRegisterOrImmediate(output);
			string basesample = GetInstanceOffset(inputs[1], 4);
			int slot = int.Parse(inputs[1]);
			if (slot < externalSampleAdpcm.Count && externalSampleAdpcm[slot])
				return ImportedSampleAdpcm(outReg, basesample);

			string code = string.Empty;

			code += "				moveq	#0,@OR\n";
			code += "				cmp.l	AK_ExtSmpLen+@BS(a5),d7\n";
			code += "				bge.s	.NoCloneIS_" + localLabel + "\n";
			code += "				move.l	AK_ExtSmpAddr+@BS(a5),a4\n";
			code += "				move.b	(a4,d7.l),@OR\n";
			code += "				asl.w	#8,@OR\n";
			code += ".NoCloneIS_" + localLabel + "\n";

			code = code.Replace("@OR", outReg);
			code = code.Replace("@BS", basesample);

			return code;
		}

		static string ImportedSampleAdpcm(string outReg, string basesample)
		{
			//short imported_sample(int smp, int sample)		// 4 bit ADPCM, decoded while playing
			//{
			//	if (smp >= ImpLength[sample]) return 0;
			//	return AK_AdpcmNext(ImpBaseAdr[sample], smp, state) & 0xff00;	// state: value, step (per instance)
			//}

			int instanceVal = (currentWordInstance * 2);
			usedAdpcm = true;

			string code = string.Empty;

			code += "				moveq	#0,@OR\n";
			code += "				cmp.l	AK_ExtSmpLen+@BS(a5),d7\n";
			code += "				bge.s	.NoAdpcmIS_" + localLabel + "\n";
			code += "				move.l	AK_ExtSmpAddr+@BS(a5),a4\n";
			code += "				lea		AK_OpInstance+@IN(a5),a6\n";
			code += "				bsr		AK_AdpcmNext\n";
			code += "				move.w	d6,@OR\n";
			code += "				clr.b	@OR\n";
			code += ".NoAdpcmIS_" + localLabel + "\n";

			code = code.Replace("@OR", outReg);
			code = code.Replace("@BS", basesample);
			code = code.Replace("@IN", instanceVal.ToString());

			currentWordInstance += 2;

			return code;
		}

		static string AdpcmCode()
		{
			//short AK_AdpcmNext(BYTE* data, int smp, WORD* state)
			//{
			//	// Yamaha style 4 bit ADPCM: two nibbles per byte, the first sample in the high nibble
			//	int nibble = smp & 1 ? data[smp >> 1] & 15 : data[smp >> 1] >> 4;
			//	int m = nibble & 7, step = state[1] + 127;
			//	int diff = ((2 * m + 1) * step) >> 3;
			//	state[0] = clamp16(state[0] + (nibble & 8 ? -diff : diff));
			//	state[1] = clamp(step * steps[m] >> 6, 127, 24576) - 127;		// cleared state = start
			//	return state[0];
			//}
			// in: a4 = data, d7 = sample, a6 = state (value.w, step-127.w)
			// out: d6.w = 16 bit sample; uses d4/d5/a4

			string code = string.Empty;

			code += "AK_AdpcmNext:\n";
			code += "				move.l	d7,d4\n";
			code += "				lsr.l	#1,d4\n";
			code += "				move.b	(a4,d4.l),d4\n";
			code += "				btst	#0,d7\n";
			code += "				bne.s	.AdpcmLo\n";
			code += "				lsr.b	#4,d4\n";
			code += ".AdpcmLo		moveq	#7,d6\n";
			code += "				and.w	d4,d6\n";
			code += "				move.w	2(a6),d5\n";
			code += "				add.w	#127,d5\n";
			code += "				add.w	d6,d6\n";
			code += "				addq.w	#1,d6\n";
			code += "				mulu	d5,d6\n";
			code += "				lsr.l	#3,d6\n";
			code += "				btst	#3,d4\n";
			code += "				beq.s	.AdpcmPos\n";
			code += "				neg.l	d6\n";
			code += ".AdpcmPos		move.w	(a6),a4\n";
			code += "				add.l	a4,d6\n";
			code += "				cmp.l	#32767,d6\n";
			code += "				ble.s	.AdpcmNoMax\n";
			code += "				move.w	#32767,d6\n";
			code += "				bra.s	.AdpcmValue\n";
			code += ".AdpcmNoMax		cmp.l	#-32768,d6\n";
			code += "				bge.s	.AdpcmValue\n";
			code += "				move.w	#-32768,d6\n";
			code += ".AdpcmValue		move.w	d6,(a6)\n";
			code += "				and.w	#7,d4\n";
			code += "				move.b	.AdpcmSteps(pc,d4.w),d4\n";
			code += "				mulu	d4,d5\n";
			code += "				lsr.l	#6,d5\n";
			code += "				cmp.l	#24576,d5\n";
			code += "				bls.s	.AdpcmNoStepMax\n";
			code += "				move.w	#24576,d5\n";
			code += ".AdpcmNoStepMax	cmp.w	#127,d5\n";
			code += "				bhs.s	.AdpcmStepOk\n";
			code += "				moveq	#127,d5\n";
			code += ".AdpcmStepOk	sub.w	#127,d5\n";
			code += "				move.w	d5,2(a6)\n";
			code += "				rts\n";
			code += ".AdpcmSteps		dc.b	57,57,57,57,77,102,128,153\n";
			code += "\n";

			return code;
		}

		static string LoopGenerator(string repeat_length, string repeat_offset, string instrument)
		{
			//void loopgen(WORD repeat_length, WORD repeat_offset, void* BaseAdr)
			//{
			//	// the end of the loop is crossfaded with the part in front of the loop start, as long
			//	// as possible (V1.14: the loop may be longer than the part in front of it)
			//	int fade = min(repeat_length, repeat_offset);
			//	int smp;
			//	BYTE* src1 = BaseAdr + repeat_offset + repeat_length - fade;
			//	BYTE* src2 = src1 - repeat_length;
			//	int delta = (32767 << 8) / fade;		// constant, no divs (overflowed below 257)
			//	int rampup = 0;
			//	int rampdown = 32767 << 8;
			//	for (smp = 0; smp < fade; smp++)
			//	{
			//		short a = (rampup >> 8);
			//		short b = (rampdown >> 8);
			//		BYTE s1 = src1[smp];
			//		BYTE s2 = src2[smp];
			//		BYTE blend = (mulsw(s1, b) + mulsw(s2, a)) >> 15;
			//		src1[smp] = blend;
			//		rampup += delta;
			//		rampdown -= delta;
			//	}
			//}

			string instr = GetInstanceOffset(instrument, 4);		

			int repLenInt = int.Parse(repeat_length);
			int repOffInt = int.Parse(repeat_offset);
			int fade = Math.Min(repLenInt, repOffInt);
			if (fade <= 0)
				return "				; no crossfade: the loop starts at the beginning of the sample\n";
			int start = repOffInt + repLenInt - fade;
			int delta = (32767 << 8) / fade;

			fineProgressLength += fade;

			string code = string.Empty;

			code += fade <= 127 ? "				moveq	#@FD,d7\n" : "				move.l	#@FD,d7\n";
			code += "				move.l	AK_SmpAddr+@IN(a5),a0\n";
			// 16 bit samples: the offsets in bytes are twice as large
			int startBytes = wide ? start * 2 : start, repLenBytes = wide ? repLenInt * 2 : repLenInt;
			if (startBytes < 32768)
				code += "				lea		@ST(a0),a0\n";
			else
				code += "				add.l	#@ST,a0\n";
			if (repLenBytes <= 32768)
				code += "				lea		-@RL(a0),a1\n";
			else
			{
				code += "				move.l	a0,a1\n";
				code += "				sub.l	#@RL,a1\n";
			}
			code += "				moveq	#0,d4\n";
			code += "				move.l	#32767<<8,d5\n";
			code += "				move.l	#@DT,d6\n";
			code += ".LoopGen_" + instrument + "\n";
			code += "				move.l	d4,d2\n";
			code += "				asr.l	#8,d2\n";
			code += "				move.l	d5,d3\n";
			code += "				asr.l	#8,d3\n";
			if (wide)
			{
				code += "				move.w	(a0),d0\n";
				code += "				move.w	(a1)+,d1\n";
			}
			else
			{
				code += "				move.b	(a0),d0\n";
				code += "				move.b	(a1)+,d1\n";
				code += "				ext.w	d0\n";
				code += "				ext.w	d1\n";
			}
			code += "				muls	d3,d0\n";
			code += "				muls	d2,d1\n";
			code += "				add.l	d1,d0\n";
			code += "				add.l	d0,d0\n";
			code += "				swap	d0\n";
			code += wide ? "				move.w	d0,(a0)+\n" : "				move.b	d0,(a0)+\n";
			code += "				add.l	d6,d4\n";
			code += "				sub.l	d6,d5\n";
			code += "\n";
			code += "				ifne	AK_USE_PROGRESS\n";
			code += "					ifne	AK_FINE_PROGRESS\n";
			code += "						addq.l	#1,(a3)\n";
			code += "					endif\n";
			code += "				endif\n";
			code += "\n";
			code += "				subq.l	#1,d7\n";
			code += "				bne.s	.LoopGen_" + instrument + "\n";

			code = code.Replace("@IN", instr);
			code = code.Replace("@FD", fade.ToString());
			code = code.Replace("@ST", startBytes.ToString());
			code = code.Replace("@RL", repLenBytes.ToString());
			code = code.Replace("@DT", delta.ToString());

			return code;
		}



		// v1 = bytebeat(gain, formula) (V1.17): the formula (see Bytebeat.cs) is an unsigned 16 bit
		// expression over t = d7.w. Its value is built in d4 (d5/d6 scratch, the left operands of
		// nested parts on the stack), the low byte is the sample, then the gain like osc_saw.
		static string BytebeatOp(string output, string line)
		{
			int open = line.IndexOf('(');
			int close = line.LastIndexOf(')');
			string args = line.Substring(open + 1, close - open - 1);
			int comma = args.IndexOf(',');
			string gain = RemapVarToRegisterOrImmediate(args.Substring(0, comma).Trim());
			string formula = args.Substring(comma + 1);
			Bytebeat.Node node;
			string error;
			if (!Bytebeat.TryParse(formula, out node, out error))
			{
				Console.WriteLine("Error in bytebeat formula '" + formula + "': " + error);
				Environment.Exit(1);
			}

			string outReg = RemapVarToRegisterOrImmediate(output);
			bytebeatLabel = 0;
			string code = BytebeatExpr(node);
			code += BB("lsl.w\t#8,d4");
			code += BB("eor.w\t#$8000,d4");
			if (gain == "#128")
			{
				code += BB("move.w\td4," + outReg);
			}
			else if (mulRightShifts.ContainsKey(gain))
			{
				code += BB("asr.w\t" + mulRightShifts[gain] + ",d4");
				code += BB("move.w\td4," + outReg);
			}
			else
			{
				if (gain.Contains("#"))
				{
					code += BB("muls\t" + gain + ",d4");
				}
				else
				{
					code += BB("move.w\t" + gain + ",d5");
					code += BB("and.w\t#255,d5");
					code += BB("muls\td5,d4");
				}
				code += BB("asr.l\t#7,d4");
				code += BB("move.w\td4," + outReg);
			}
			return code;
		}

		static int bytebeatLabel;

		static string BB(string instruction)
		{
			return "\t\t\t\t" + instruction + "\n";
		}

		static string BytebeatLabel(string name)
		{
			return ".BB" + name + "_" + localLabel + "_" + (bytebeatLabel++).ToString();
		}

		// operand of a constant or t, null for other nodes
		static string BytebeatOperand(Bytebeat.Node n)
		{
			if (n.Op == Bytebeat.Op.Const) return "#" + n.Value.ToString();
			if (n.Op == Bytebeat.Op.T) return "d7";
			return null;
		}

		// code that leaves the value of the node in d4.w (same semantics as Bytebeat.Eval)
		static string BytebeatExpr(Bytebeat.Node n)
		{
			string code;
			switch (n.Op)
			{
				case Bytebeat.Op.Const:
					return BB(n.Value < 128 ? "moveq\t#" + n.Value.ToString() + ",d4" : "move.w\t#" + n.Value.ToString() + ",d4");
				case Bytebeat.Op.T:
					return BB("move.w\td7,d4");
				case Bytebeat.Op.Neg:
					return BytebeatExpr(n.A) + BB("neg.w\td4");
				case Bytebeat.Op.Not:
					return BytebeatExpr(n.A) + BB("not.w\td4");
				case Bytebeat.Op.LogNot:
					return BytebeatExpr(n.A) + BB("tst.w\td4") + BB("seq\td4") + BB("and.w\t#1,d4");
				case Bytebeat.Op.Cond:
					{
						string other = BytebeatLabel("Else"), end = BytebeatLabel("End");
						code = BytebeatExpr(n.A) + BB("tst.w\td4") + BB("beq\t" + other);
						code += BytebeatExpr(n.B) + BB("bra\t" + end);
						code += other + "\n" + BytebeatExpr(n.C) + end + "\n";
						return code;
					}
			}

			// binary: the left side in d4, the right side as operand (#value, d7) or in d5
			string src = BytebeatOperand(n.B);
			code = BytebeatExpr(n.A);
			if (src == null)
			{
				code += BB("move.w\td4,-(sp)");
				code += BytebeatExpr(n.B);
				code += BB("move.w\td4,d5");
				code += BB("move.w\t(sp)+,d4");
				src = "d5";
			}
			bool imm = src.StartsWith("#");
			int value = imm ? n.B.Value : 0;
			switch (n.Op)
			{
				case Bytebeat.Op.Add: return code + BB("add.w\t" + src + ",d4");
				case Bytebeat.Op.Sub: return code + BB("sub.w\t" + src + ",d4");
				case Bytebeat.Op.Mul: return code + BB("mulu.w\t" + src + ",d4");
				case Bytebeat.Op.And: return code + BB("and.w\t" + src + ",d4");
				case Bytebeat.Op.Or: return code + BB("or.w\t" + src + ",d4");
				case Bytebeat.Op.Xor: return code + BB("eor.w\t" + src + ",d4");
				case Bytebeat.Op.Shl:
				case Bytebeat.Op.Shr:
					{
						string shift = n.Op == Bytebeat.Op.Shl ? "lsl.w\t" : "lsr.w\t";
						if (!imm) return code + BB(shift + src + ",d4");        // count = register mod 64
						int count = value & 63;
						if (count == 0) return code;
						if (count >= 16) return code + BB("moveq\t#0,d4");
						while (count > 8)
						{
							code += BB(shift + "#8,d4");
							count -= 8;
						}
						return code + BB(shift + "#" + count.ToString() + ",d4");
					}
				case Bytebeat.Op.Div:
				case Bytebeat.Op.Mod:
					{
						// 16 bit dividend zero extended: divu.w can't overflow; x/0 = x%0 = 0
						string result = (n.Op == Bytebeat.Op.Mod ? BB("swap\td6") : "") + BB("move.w\td6,d4");
						string divide = BB("moveq\t#0,d6") + BB("move.w\td4,d6") + BB("divu.w\t" + src + ",d6") + result;
						if (imm) return code + (value == 0 ? BB("moveq\t#0,d4") : divide);
						string zero = BytebeatLabel("Div0"), end = BytebeatLabel("DivEnd");
						code += BB("tst.w\t" + src) + BB("beq.s\t" + zero) + divide + BB("bra.s\t" + end);
						code += zero + "\n" + BB("moveq\t#0,d4") + end + "\n";
						return code;
					}
				case Bytebeat.Op.LogAnd:
				case Bytebeat.Op.LogOr:
					{
						bool and = n.Op == Bytebeat.Op.LogAnd;
						if (imm)
						{
							// known right side: x && 0 = 0, x || 1 = 1, otherwise the left side decides
							if (and && value == 0) return code + BB("moveq\t#0,d4");
							if (!and && value != 0) return code + BB("moveq\t#1,d4");
							return code + BB("tst.w\td4") + BB("sne\td4") + BB("and.w\t#1,d4");
						}
						code += BB("tst.w\td4") + BB("sne\td6") + BB("tst.w\t" + src) + BB("sne\td4");
						return code + BB((and ? "and.b\t" : "or.b\t") + "d6,d4") + BB("and.w\t#1,d4");
					}
				default:
					{
						// comparisons, unsigned (d4 - src)
						string set;
						switch (n.Op)
						{
							case Bytebeat.Op.Lt: set = "scs"; break;
							case Bytebeat.Op.Le: set = "sls"; break;
							case Bytebeat.Op.Gt: set = "shi"; break;
							case Bytebeat.Op.Ge: set = "scc"; break;
							case Bytebeat.Op.Eq: set = "seq"; break;
							default: set = "sne"; break;
						}
						return code + BB("cmp.w\t" + src + ",d4") + BB(set + "\td4") + BB("and.w\t#1,d4");
					}
			}
		}

		static string GetInstanceOffset(string value, int offsetSize)
		{
			int instance = 0;
			if (int.TryParse(value, out instance))
			{
				return (instance * offsetSize).ToString();
			}
			return "0";
		}

		static string RemapVarToRegisterOrImmediate(string value)
		{
			if (value == "v1")
				return "d0";
			else if (value == "v2")
				return "d1";
			else if (value == "v3")
				return "d2";
			else if (value == "v4")
				return "d3";
			else if (value == "smp")
				return "d7";

			return "#" + value;
		}

		static string GetDecayValue(int index)
		{
			int[] decayTable = {    32767, 32767, 32767, 16384, 10922, 8192, 6553, 4681,
									3640, 2978, 2520, 2048, 1724, 1489, 1310, 1129,
									992, 885, 799, 712, 642, 585, 537, 489,
									448, 414, 385, 356, 330, 309, 289, 270,
									254, 239, 225, 212, 201, 190, 181, 171,
									163, 155, 148, 141, 134, 129, 123, 118,
									113, 108, 104, 100, 96, 93, 89, 86,
									83, 80, 77, 75, 72, 70, 68, 65,
									63, 61, 60, 58, 56, 54, 53, 51,
									50, 49, 47, 46, 45, 44, 43, 41,
									40, 39, 38, 38, 37, 36, 35, 34,
									33, 33, 32, 31, 30, 30, 29, 29,
									28, 27, 27, 26, 26, 25, 25, 24,
									24, 23, 23, 22, 22, 22, 21, 21,
									20, 20, 20, 19, 19, 19, 18, 18,
									18, 17, 17, 17, 17, 16, 16, 16 };

			int decayValue = decayTable[index] << 8;
			return "#" + decayValue.ToString();
		}

		static string GetChordValue(int index)
		{
			int[] chordTable = { 0 << 8, 271 << 8, 287 << 8, 304 << 8, 322 << 8, 342 << 8,
								 362 << 8, 383 << 8, 406 << 8, 430 << 8, 456 << 8, 483 << 8, 512 << 8 };

			int chordValue = chordTable[index];
			return "#" + chordValue.ToString();
		}

		static string VarsCode()
		{
			string code = string.Empty;

			code += "				rsreset\n";
			code += "AK_LPF			rs.w	1\n";
			code += "AK_HPF			rs.w	1\n";
			code += "AK_BPF			rs.w	1\n";
			code += "				rsreset\n";
			code += "AK_CHORD1		rs.l	1\n";
			code += "AK_CHORD2		rs.l	1\n";
			code += "AK_CHORD3		rs.l	1\n";
			code += "				rsreset\n";
			code += "AK_SmpLen		rs.l	31\n";
			code += "AK_ExtSmpLen	rs.l	" + externalSlots.ToString() + "\n";
			if (usedNoise)
				code += "AK_NoiseSeeds	rs.l	3\n";
			code += "AK_SmpAddr		rs.l	31\n";
			code += "AK_ExtSmpAddr	rs.l	" + externalSlots.ToString() + "\n";
			code += "AK_OpInstance	rs.w    %MAXWORDINSTANCES%\n";
			code += "AK_EnvDValue	rs.l	%MAXENVDINSTANCES%\n";
			code += "AK_VarSize		rs.w	0\n";

			code += "\n";
			code += "AK_Vars:\n";

			for (int i = 0; i < 31; i++)
			{
				if (i < instrumentLength.Count)
					code += "				dc.l	$" + instrumentLengthInt[i].ToString("x8") + "		; Instrument " + (i + 1).ToString() + " Length \n";
				else
					code += "				dc.l	$00000000		; Instrument " + (i + 1).ToString() + " Length \n";
			}

			for (int i = 0; i < externalSlots; i++)
			{
				if (i < externalSampleLength.Count)
					code += "				dc.l	$" + externalSampleLengthInt[i].ToString("x8") + "		; External Sample " + (i + 1).ToString() + " Length \n";
				else
					code += "				dc.l	$00000000		; External Sample " + (i + 1).ToString() + " Length \n";
			}

			if (usedNoise)
			{
				code += "				dc.l	$67452301		; AK_NoiseSeed1\n";
				code += "				dc.l	$efcdab89		; AK_NoiseSeed2\n";
				code += "				dc.l	$00000000		; AK_NoiseSeed3\n";
			}
			code += "				ds.b	AK_VarSize-AK_SmpAddr\n";

			return code;
		}
		static string ClearVarsCode()
		{
			string code = string.Empty;

			code += "AK_ResetVars:\n";
			code += "				moveq   #0,d1\n";
			code += "				moveq   #0,d2\n";
			code += "				moveq   #0,d3\n";
			if (maxLargeBufferInstance > 0)
			{
				code += "				move.w  d0,d7\n";
				code += "				beq.s	.NoClearDelay\n";
				code += "				lsl.w	#8,d7\n";       // * (4096 / 4) >> 2
				code += "				subq.w	#1,d7\n";
				code += "				move.l  a1,a6\n";
				code += ".ClearDelayLoop\n";
				code += "				move.l  d1,(a6)+\n";
				code += "				move.l  d1,(a6)+\n";
				code += "				move.l  d1,(a6)+\n";
				code += "				move.l  d1,(a6)+\n";
				code += "				dbra	d7,.ClearDelayLoop\n";
				code += ".NoClearDelay\n";
			}
			code += "				moveq   #0,d0\n";
			if (maxWordInstance > 0 || maxEnvdInstance > 0)
				code += "				lea		AK_OpInstance(a5),a6\n";
			if (maxWordInstance > 0)
			{
				for (int i = 0; i < maxWordInstance / 2; i++)
					code += "				move.l	d0,(a6)+\n";
			}
			if (maxEnvdInstance > 0)
			{
				if (maxEnvdInstance == 1)
					code += "				move.l  #32767<<16,(a6)+\n";
				else
				{
					code += "				move.l  #32767<<16,d6\n";
					for (int i = 0; i < maxEnvdInstance; i++)
						code += "				move.l	d6,(a6)+\n";
				}
			}

			code += "				rts\n";

			return code;
		}
		// ---- -unz: the output for the UNZ player of Platon42 -------------------------------------
		// His ExemusicConv.kt made 16 bit code of the 8 bit output of V1.1; -unz writes that form: no
		// AK_Generate and no progress, a routine per instrument instead (a0 = its sample, a1 = work
		// buffer), the variables and the tables of the sample addresses in the structure of the player
		// (a6), a3 as scratch register. The operator code is the code of -16.

		static string UnzHeaderCode()
		{
			string code = string.Empty;

			code += "; Option -unz: 16 bit samples (signed words), one routine per instrument.\n";
			code += "; Call 'ak_generate_instrument_N' with the following registers set:\n";
			code += "; a0 = Address of the sample of this instrument (2 bytes per sample, see AK_SamLengths)\n";
			code += "; a1 = %LARGEBUFFSIZE% Bytes Temporary Work Buffer Address (need not be cleared)\n";
			code += "; a6 = Player data: pd_VirtInstTable (addresses of the 31 samples), pd_ExtSampleTable\n";
			code += ";      (addresses of the " + externalSlots.ToString() + " external samples) and the variables at the end of this file\n";
			code += "; d0-d7/a0/a1/a3/a4 are changed, a2/a5/a6 are not used. A routine reads the samples\n";
			code += "; it depends on (see AK_SampleGenerators): they have to be rendered before it.\n";
			code += "; Call 'ak_delta_decode_extsamples' once with a0 = External Samples Address.\n";
			code += ";\n";
			if (externalSampleTotalLength > 0)
			{
				// (the player sets pd_ExtSampleTable: the delta samples come first, the ADPCM samples after them)
				code += "; External samples (offset in the block of AK_EXT_SMP_LEN bytes):\n";
				int deltaOffset = 0, adpcmOffset = externalDeltaLength;
				for (int i = 0; i < externalSampleLengthInt.Count; i++)
				{
					int length = externalSampleLengthInt[i];
					if (length == 0) continue;
					bool adpcm = externalSampleAdpcm[i];
					int bytes = adpcm ? (length + 1) / 2 : length;
					code += "; " + (i + 1).ToString() + ": offset " + (adpcm ? adpcmOffset : deltaOffset).ToString() + ", " + bytes.ToString() + " bytes" +
							(adpcm ? " (4 bit ADPCM, " + length.ToString() + " samples)" : "") + "\n";
					if (adpcm) adpcmOffset += bytes; else deltaOffset += bytes;
				}
				code += ";\n";
			}
			code += DividerCode();
			code += "\n";
			code += "AK_TMP_MEMORY_SIZE		equ %LARGEBUFFSIZE%\n";
			code += "\n";
			code += "AK_EXT_SMP_LEN			equ " + externalSampleTotalLength.ToString() + "\n";
			code += "AK_VERSION				equ 119\n";
			code += "AK_16BIT				equ 1		; 16 bit samples (signed words)\n";
			code += "\n";
			code += "ak_delta_decode_extsamples:\n";
			if (externalDeltaLength > 0)
			{
				code += "				; Convert external samples from stored deltas\n";
				if (!externalAdpcm)
					code += "				move.w	#AK_EXT_SMP_LEN-1,d7\n";
				else
					code += "				move.w	#" + externalDeltaLength.ToString() + "-1,d7\n";
				code += "				moveq	#0,d0\n";
				code += ".DeltaLoop		add.b	(a0),d0\n";
				code += "				move.b	d0,(a0)+\n";
				code += "				dbra	d7,.DeltaLoop\n";
			}
			code += "				rts\n";
			code += "\n";

			return code;
		}

		static int UnzLength(List<int> lengths, string offset)
		{
			int index = int.Parse(offset) / 4;
			return index < lengths.Count ? lengths[index] : 0;
		}

		// the generated code in the form of the player: see above. Comment lines stay as they are.
		static string UnzMap(string code)
		{
			const string notInName = @"(?<![\w$.])";
			var lines = new List<string>();
			int progress = 0;
			foreach (string source in code.Split('\n'))
			{
				string line = source;
				string text = line.Trim();
				// no progress
				if (progress > 0)
				{
					if (text.StartsWith("ifne") || text.StartsWith("ifeq")) progress++;
					else if (text == "endif") progress--;
					continue;
				}
				if (text.StartsWith("ifne") && text.EndsWith("AK_USE_PROGRESS"))
				{
					progress = 1;
					continue;
				}
				if (!text.StartsWith(";"))
				{
					if (Regex.IsMatch(line, notInName + @"a[23](?!\w)"))
						throw new InvalidOperationException("-unz: a2/a3 is used by the generated code: " + text);
					// a3 is the scratch register, a6 points to the variables
					line = Regex.Replace(line, notInName + @"a6(?!\w)", "a3");
					line = Regex.Replace(line, notInName + @"a5(?!\w)", "a6");
					// the end of a source sample (reverse clone): the samples of the player are not
					// one behind the other, so it is the start plus two bytes per sample
					line = Regex.Replace(line, @"^(\s*)move\.l\tAK_SmpAddr\+(\d+)\+4\(a6\),(a\d)$", m =>
						m.Groups[1].Value + "move.l\tAK_SmpAddr+" + m.Groups[2].Value + "(a6)," + m.Groups[3].Value + "\n" +
						m.Groups[1].Value + "add.l\t#" + (UnzLength(instrumentLengthInt, m.Groups[2].Value) * 2).ToString() + "," + m.Groups[3].Value);
					// addresses from the tables of the player, lengths (in samples) as numbers
					line = Regex.Replace(line, @"AK_SmpAddr\+(\d+)\(a6\)", m => "pd_VirtInstTable+" + (int.Parse(m.Groups[1].Value) / 4).ToString() + "*4(a6)");
					line = Regex.Replace(line, @"AK_ExtSmpAddr\+(\d+)\(a6\)", m => "pd_ExtSampleTable+" + (int.Parse(m.Groups[1].Value) / 4).ToString() + "*4(a6)");
					line = Regex.Replace(line, @"AK_SmpLen\+(\d+)\(a6\)", m => "#" + UnzLength(instrumentLengthInt, m.Groups[1].Value).ToString());
					line = Regex.Replace(line, @"AK_ExtSmpLen\+(\d+)\(a6\)", m => "#" + UnzLength(externalSampleLengthInt, m.Groups[1].Value).ToString());
					if (line.Contains("AK_SmpAddr") || line.Contains("AK_SmpLen") || line.Contains("AK_ExtSmp"))
						throw new InvalidOperationException("-unz: not mapped: " + text);
				}
				lines.Add(line);
			}
			return string.Join("\n", lines);
		}

		// the variables are part of the structure of the player, the lengths are in bytes
		static string UnzVarsCode()
		{
			string code = string.Empty;

			code += "				rsreset\n";
			code += "AK_LPF			rs.w	1\n";
			code += "AK_HPF			rs.w	1\n";
			code += "AK_BPF			rs.w	1\n";
			code += "				rsreset\n";
			code += "AK_CHORD1		rs.l	1\n";
			code += "AK_CHORD2		rs.l	1\n";
			code += "AK_CHORD3		rs.l	1\n";
			code += "\n";
			code += "; The player data (a6) holds pd_VirtInstTable (rs.l 31), pd_ExtSampleTable (rs.l " + externalSlots.ToString() + ") and\n";
			code += "; these variables." + (usedNoise ? " AK_NoiseSeeds start with $67452301, $efcdab89, $00000000." : "") + "\n";
			code += "				IF	0	; move to pd structure!\n";
			if (usedNoise)
				code += "AK_NoiseSeeds	rs.l	3\n";
			code += "AK_OpInstance	rs.w    %MAXWORDINSTANCES%\n";
			code += "AK_EnvDValue	rs.l	%MAXENVDINSTANCES%\n";
			code += "				ENDC\n";
			code += "\n";
			code += "AK_SamLengths:\n";
			for (int i = 0; i < 31; i++)
			{
				int length = i < instrumentLengthInt.Count ? instrumentLengthInt[i] : 0;
				code += "				dc.l	$" + length.ToString("x8") + "*2		; Instrument " + (i + 1).ToString() + " Length \n";
			}
			for (int i = 0; i < externalSlots; i++)
			{
				int length = i < externalSampleLengthInt.Count ? externalSampleLengthInt[i] : 0;
				code += "				dc.l	$" + length.ToString("x8") + "*2		; External Sample " + (i + 1).ToString() + " Length \n";
			}

			return code;
		}

		// a template of the render list of the player: the routines and the samples they read
		static string UnzGeneratorsCode()
		{
			string code = string.Empty;

			code += "				IF	0	; copy to where you want to have it!\n";
			code += "AK_SampleGenerators:\n";
			foreach (int number in unzInstruments)
			{
				code += "				dc.w	" + number.ToString() + ",ak_generate_instrument_" + number.ToString() + "-AK_SampleGenerators";
				if (unzDepends[number].Count > 0)
					code += "	; Depending on " + string.Join(", ", unzDepends[number]);
				code += "\n";
			}
			for (int i = 0; i < unzInstruments.Count; i += 8)
				code += "				dc.w	" + string.Join(",", unzInstruments.Skip(i).Take(8).Select(n => (-n).ToString())) + "\n";
			code += "				dc.w	0\n";
			code += "				ENDC\n";

			return code;
		}

		static string DividerCode()
		{
			return ";----------------------------------------------------------------------------\n";
		}
	}
}
