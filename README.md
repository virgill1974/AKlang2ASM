# AKlang2Asm

*Author: Dan / Lemon*

AKlang2Asm converts an AmigaKlang patch script into a fully expanded Motorola 68000 assembly routine.  The generated routine (labelled `AK_Generate`) renders every instrument of a patch into raw sample memory and mirrors the rendering logic of the original AmigaKlang tool, including loop generation, delays, comb filters, reverb, distortion, ADSR, clone/import operators, etc.

## Features

- Parses AmigaKlang script files and emits ready-to-assemble 68k source code.
- Supports progress reporting (`-p` byte-level per instrument, `-pf` fine progress per sample byte).
- Renders 8 bit samples or, with `-16`, 16 bit samples (V1.18).
- `-unz` (V1.19): 16 bit samples with one routine per instrument, for the UNZ player of Platon42.
- Automatically calculates sample lengths, loop offsets, large buffer requirements, and maximum operator instance counts.
- Converts embedded external samples from delta encoding back to absolute form.
- Generates helper data (`AK_Vars`, decay tables, buffer declarations) so the output can be dropped into a demo/mod production without extra glue code.

## Build

This is a classic .NET console project.

```bash
# Restore and build with the modern SDK
cd AKlang2ASM
dotnet build

# …or use MSBuild/Visual Studio on Windows
msbuild Aklang2Asm.sln
```

The compiled executable will land in `bin/<config>/<target>/Aklang2Asm.exe`.

## Usage

```bash
Aklang2Asm <script.txt> <output.asm> [-p|-pf] [-e] [-16|-unz]
```

- `script.txt` — AmigaKlang export script (see format below).
- `output.asm` — Destination file for the generated 68k assembly.
- `-p` — (optional) enable coarse progress (current instrument number stored via `a3`).
- `-pf` — (optional) enable fine progress (current sample byte stored via `a3`).
- `-e` — (optional, V1.16) early play: the first word of each sample is cleared right after
  the sample is rendered (not only at the end), so a replayer can start while the later samples
  are still being rendered. Samples that clone/chord/mix an earlier one then see two zero bytes
  at its start.
- `-16` — (optional, V1.18) 16 bit samples, see below. Not together with `-e`.
- `-unz` — (optional, V1.19) 16 bit samples, one routine per instrument, see below. Not together
  with `-p`, `-pf` or `-e`.

If no option is supplied, progress writes are disabled. The header also defines `AK_TMP_LEN`,
the size of the temporary work buffer passed in `a1`. V1.16 does not wait for a key press when
its input is redirected (`aklang2asm ... <NUL` in batch files).

## 16 bit samples (`-16`, V1.18)

With `-16` the generated `AK_Generate` renders every instrument as signed 16 bit words (big
endian) instead of bytes. Without the switch the code is the same as in V1.17.

- `v1` is written as a word (`move.w d0,(a0)+`); the 8 bit code writes its high byte.
- A sample of `length` samples takes `2 * length` bytes; `AK_SMP_LEN` (the size of the sample
  buffer in `a0`) is twice the sum of the lengths. The lengths and loop offsets of the script
  stay in samples.
- `clone`, `clone_reverse` (also with transpose), `chordgen`, `samplemixer` and the loop
  generator read and write words, so nothing of the 16 bit is lost on the way:
  `chordgen` sums the halved words of its voices in 32 bit and clamps, `samplemixer` sums
  `sample * volume` of its voices and divides the sum by 64 once, the loop generator does its
  crossfade on words.
- `imported_sample` (also ADPCM) is unchanged: imported samples are 8 bit, `<< 8`.
- Nothing is cleared at the start of a sample (the 8 bit code clears its first two bytes for
  ProTracker).
- `-e` (early play of the Amiga player, which plays 8 bit) is rejected together with `-16`.

The header defines `AK_16BIT equ 1` (`0` without the switch), so a player can check what it
got. The AmigaKlang editor renders the same samples for "Export Samples to XM" and writes this
source with "Export ASM Source (16 bit)".

## One routine per instrument (`-unz`, V1.19)

`-unz` writes the 16 bit code in the form the UNZ player of Platon42 uses (until now made from
the 8 bit output of V1.1 by his `ExemusicConv.kt`). [UNZ_PLAYER.md](UNZ_PLAYER.md) has the
details and the differences to the converted files. In short:

- There is no `AK_Generate`. Every instrument with operators has a routine
  `ak_generate_instrument_N` (`a0` = its sample, `a1` = work buffer of `AK_TMP_MEMORY_SIZE` bytes,
  `a6` = data of the player), which changes `d0-d7/a0/a1/a3/a4`. There is no progress.
- The player keeps the variables (`AK_NoiseSeeds`, `AK_OpInstance`, `AK_EnvDValue`: the lines in
  the `IF 0` block) and the tables `pd_VirtInstTable` (addresses of the 31 samples) and
  `pd_ExtSampleTable` (addresses of the external samples) in its structure.
- `AK_SamLengths` has the lengths in bytes (`dc.l $…*2`), `ak_delta_decode_extsamples` decodes
  the external samples (`a0`), `AK_SampleGenerators` (in `IF 0`) lists the routines and the
  samples they depend on.
- The samples need not lie one behind the other and the routines can be called in any order
  that renders the sources first: each routine clears its own delay buffers, a reversed clone
  finds the end of its source by its length, and a chord generator voice is silent behind the
  end of its source (`AK_Generate` reads the memory behind it there).
- The operator code is the code of `-16`. Without `-unz` the output is the same as in V1.18.

## Script format

The script is a plain-text file that the Windows AmigaKlang tool can export. At a high level:

1. **External sample lengths** — first line (before the first `$`) contains comma-separated integer lengths for up to eight (V1.15: twelve) imported samples. Example:
   ```
   1024,0,0,2048,0,0,0,0
   ```
   V1.15: a length with an `a` (e.g. `2280a`) marks a 4 bit ADPCM sample (Yamaha style:
   adaptive step, two nibbles per byte, first sample in the high nibble). The external sample
   block then holds the delta coded samples first and the ADPCM samples (half the length,
   rounded up) after them, both in slot order; `AK_EXT_SMP_LEN` is the size of the whole block.
   ADPCM samples are decoded by `imported_sample` while it plays them (shared routine
   `AK_AdpcmNext`, ~90 bytes, 2 words of instance data per use), so they need no extra memory.
   Scripts with eight lengths and no `a` produce the same code as before.
2. **Instrument blocks** — each block is separated by `$`. Inside a block:
   ```
   <name>,<length>,<loopOffset>,<loopLength>,<loopFlag>#
   op1;
   op2;
   ...
   ```
   - `length`, `loopOffset`, `loopLength` are decimal integers (samples).
   - `loopFlag` is `Y` or `N`.
   - Each operator `op` mirrors an AmigaKlang node call, e.g. `v1=osc_saw(0,v2,#80)` or `v3=envd(0,#12,#40,#64)`.

Supported operators (see `Program.cs` for exact parameter ordering):

`vol`, `osc_saw`, `osc_tri`, `osc_sine`, `osc_pulse`, `osc_noise`, `sh` (sample & hold),
`enva`, `envd`, `mul`, `add`, `ctrl`, `dly_cyc`, `cmb_flt_n`, `reverb`, `sv_flt_n`,
`onepole_flt`, `chordgen`, `clone`, `clone_reverse`, `imported_sample`, `distortion`, `adsr`,
`samplemixer`, `bytebeat`.

`clone(smp,<instrument>,<offset>[,<transpose>])` and `clone_reverse(...)` take an optional
transpose: a number or `v1`..`v4`, -32768 = one octave down (half speed), 0 = original,
32767 = one octave up (double speed), linear in each half. The read position advances by
`65536 + (t < 0 ? t : 2*t)` (16.16) per sample, so a modulated transpose bends the pitch.
Without it (or with 0) the generated code is the same as in V1.11.

`samplemixer(<instance>,<rowlength>,<notes>,<row>,<track>,<source>,<step>,<volume>,...)` mixes
a sequencer pattern of the editor (4 tracks, 16 rows): every `rowlength` samples the next row
starts; each of the `notes` entries (resolved by the editor) restarts the voice of its track
with a source instrument (0-based, -1 = the track falls silent), a step in 16.16 (65536 = C-3)
and a volume 0..64. A voice plays its source once; the output is the sum of
`sample * volume` of all voices, times 4, clamped to 16 bit.

`bytebeat(<gain>,<formula>)` (V1.17, experimental) computes a formula over `t`, the sample
number, e.g. `v1 = bytebeat(128, t*(t>>5|t>>8));`. The formula is the rest of the line up to the
last `)`. Numbers 0..65535 (or `0x..`), `t`, parentheses, unary `- ~ !` and the C operators
`* / % + - << >> < <= > >= == != & ^ | && || ?:` with C precedence, at most 120 characters.
Everything is unsigned 16 bit: overflow wraps, `x/0` and `x%0` are 0, shift counts are taken
`& 63` and give 0 from 16 on, comparisons are unsigned and give 0 or 1. The low byte of the result
is the sample (centered), then the gain like `osc_saw` (128 = 1:1, number or `v1`..`v4`).
The formula is compiled into `.w` code on `d4` (`d5`/`d6` and the stack as scratch, `t` = `d7`):
`mulu.w` for `*` and `divu.w` with a zero extended dividend for `/` and `%` (can't overflow).
`Bytebeat.cs` holds parser and semantics; the AmigaKlang editor uses the same file, so the
editor preview and the Amiga render the same samples.

V1.17 also defines `AK_VERSION equ 117` (V1.18: `118`, V1.19: `119`) in the header (players can check for a
minimum version) and prints a warning for script lines with an unknown operator instead of silently
skipping them.

Loop generator (V1.14): the last `min(repeat length, repeat offset)` samples of the loop are
crossfaded with the samples in front of the loop start, so the loop may be longer than the part
in front of it (offset 0 = no crossfade). Loops that are not longer than that part are generated
exactly as before. The crossfade step is a constant (no `divs`, which overflowed for loops
shorter than 257 samples), which also saves 6 bytes per loop.

## Output overview

The generated `output.asm` contains:

- Header banner describing the tool version (currently 1.19) and usage of `AK_Generate`.
- Constant section with `AK_USE_PROGRESS`, `AK_FINE_PROGRESS`, buffer sizes (`AK_SMP_LEN`, `AK_EXT_SMP_LEN`, etc.).
- The `AK_Generate` routine, which:
  - Computes sample and external sample base addresses.
  - Optional progress writes via `a3`.
  - Per-instrument rendering loops (`movem/add/asr/...` sequences for each operator).
  - Loop generator code when `loopFlag == Y`.
  - Delta decoding of imported samples when present.
  - Final two-byte clearing pass for all samples (not with `-16`).
- Support routines `AK_ResetVars`, `AK_ClearVars`, decay tables, RNG seeds, ADSR state, and other persistent variables.

Registers expected on entry (summarized from the header comments):

| Register | Meaning |
|----------|---------|
| `a0` | Base address of the instrument sample buffer |
| `a1` | Scratch buffer (`%LARGEBUFFSIZE%` bytes) |
| `a2` | External samples buffer |
| `a3` | Progress pointer (depends on `AK_USE_PROGRESS`) |

## Notes & tips

- External samples are stored delta-encoded in the script export. The tool restores them automatically before mixing.
- `AK_Vars` holds per-instrument metadata (`AK_SmpLen`, `AK_SmpAddr`, etc.); you must preserve it between calls if you reuse buffers.
- When using fine progress, `AK_FINE_PROGRESS_LEN` equals the sum of all instrument lengths and loop-generated bytes.
- The generated code expects a 68000 assembler that understands standard Motorola syntax (e.g., vasm, asm-pro, Devpac).

## License

No explicit license was provided in the original project archive. Please consult Jochen "Virgill" Feldkötter / Dan of Lemon for redistribution terms if you intend to publish the generated code or the tool itself.
