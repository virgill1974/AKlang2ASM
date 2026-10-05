# AKlang2Asm

*Author: Dan / Lemon*

AKlang2Asm converts an AmigaKlang patch script into a fully expanded Motorola 68000 assembly routine.  The generated routine (labelled `AK_Generate`) renders every instrument of a patch into raw sample memory and mirrors the rendering logic of the original AmigaKlang tool, including loop generation, delays, comb filters, reverb, distortion, ADSR, clone/import operators, etc.

## Features

- Parses AmigaKlang script files and emits ready-to-assemble 68k source code.
- Supports progress reporting (`-p` byte-level per instrument, `-pf` fine progress per sample byte).
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
Aklang2Asm <script.txt> <output.asm> [-p|-pf]
```

- `script.txt` — AmigaKlang export script (see format below).
- `output.asm` — Destination file for the generated 68k assembly.
- `-p` — (optional) enable coarse progress (current instrument number stored via `a3`).
- `-pf` — (optional) enable fine progress (current sample byte stored via `a3`).

If no option is supplied, progress writes are disabled.

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
`samplemixer`.

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

Loop generator (V1.14): the last `min(repeat length, repeat offset)` samples of the loop are
crossfaded with the samples in front of the loop start, so the loop may be longer than the part
in front of it (offset 0 = no crossfade). Loops that are not longer than that part are generated
exactly as before. The crossfade step is a constant (no `divs`, which overflowed for loops
shorter than 257 samples), which also saves 6 bytes per loop.

## Output overview

The generated `output.asm` contains:

- Header banner describing the tool version (currently 1.15) and usage of `AK_Generate`.
- Constant section with `AK_USE_PROGRESS`, `AK_FINE_PROGRESS`, buffer sizes (`AK_SMP_LEN`, `AK_EXT_SMP_LEN`, etc.).
- The `AK_Generate` routine, which:
  - Computes sample and external sample base addresses.
  - Optional progress writes via `a3`.
  - Per-instrument rendering loops (`movem/add/asr/...` sequences for each operator).
  - Loop generator code when `loopFlag == Y`.
  - Delta decoding of imported samples when present.
  - Final two-byte clearing pass for all samples.
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
