# Aklang2Asm `-unz`: 16 bit samples for the UNZ player

*For Platon42. Aklang2Asm V1.21, AmigaKlang ImGui editor V1.5.*

`-unz` makes Aklang2Asm write the source that `ExemusicConv.kt` made out of the 8 bit output of
V1.1: 16 bit samples, one routine per instrument, the variables and sample addresses in the
structure of the player. No post-processing is needed any more, and the operators added since
V1.1 work (sample mixer, clone with transpose, long loops, ADPCM imports, bytebeat).

```
Aklang2Asm script.txt exemusic.asm -unz
```

In the editor: **Export → Export ASM Source (16 bit)** writes `exe_creator\exemusic16.asm`
this way. `-p`, `-pf` and `-e` cannot be combined with `-unz`.

## Interface

It is the interface of the converted file, label for label:

| | |
|---|---|
| `ak_generate_instrument_N` | renders instrument N and returns with `rts`. One routine per instrument that has operators. |
| `a0` | address of the sample of this instrument: signed 16 bit words, big endian |
| `a1` | work buffer of `AK_TMP_MEMORY_SIZE` bytes. It need not be cleared. |
| `a6` | player data, see below |
| changed | `d0-d7/a0/a1/a3/a4`. `a2`, `a5` and `a6` are not touched. |
| `ak_delta_decode_extsamples` | `a0` = external samples. Call it once before the first routine. Always present (just `rts` without external samples). |
| `AK_SamLengths` | 31 instrument lengths, then the lengths of the external samples, as `dc.l $…*2` (bytes of a 16 bit sample). An instrument without operators has `0`. |
| `AK_TMP_MEMORY_SIZE`, `AK_EXT_SMP_LEN` | as before. New: `AK_VERSION equ 121`, `AK_16BIT equ 1`. |
| `AK_SampleGenerators` | the template in `IF 0 … ENDC`: number and routine of every instrument, with `; Depending on …`, then the negated numbers and `0`. |

### Player data (`a6`)

```
pd_VirtInstTable    rs.l 31     ; address of the sample of each instrument
pd_ExtSampleTable   rs.l 8      ; address of each external sample (12 if the patch uses more than 8)
AK_NoiseSeeds       rs.l 3      ; only if the patch uses osc_noise; start: $67452301, $efcdab89, 0
AK_OpInstance       rs.w n      ; n and m are in the "IF 0 ; move to pd structure!" block
AK_EnvDValue        rs.l m
```

The three `AK_` lines are written into the `IF 0 ; move to pd structure!` block at the end of
the file, as before. The routines reset `AK_OpInstance` and `AK_EnvDValue` themselves; the
noise seeds are only read and advanced.

A routine reads the samples it depends on through `pd_VirtInstTable` (clone, chord generator,
sample mixer), so they have to be rendered before it. The loop generator reads the address of
its own sample from `pd_VirtInstTable` too (as in the converted file), not from `a0`.

### Example

```
ak_generate_instrument_21:
;----------------------------------------------------------------------------
; Instrument 21 - Virgill-Bass
;----------------------------------------------------------------------------

				moveq	#1,d0
				bsr		AK_ResetVars
				moveq	#0,d7
.Inst21Loop
				; v1 = clone(smp,5, 726)
				move.l	d7,d6
				add.l	#726,d6
				moveq	#0,d0
				cmp.l	#6016,d6
				bge.s	.NoClone_21_1
				move.l	pd_VirtInstTable+5*4(a6),a4
				add.l	d6,a4
				move.w	(a4,d6.l),d0
.NoClone_21_1
				...
				move.w	d0,(a0)+
				addq.l	#1,d7
				cmp.l	#6144,d7
				blt		.Inst21Loop
				rts
```

## Compared with the output of `ExemusicConv.kt`

Tested with your two files of "pagoda" (`exemusic_orig.asm`, the converted `exemusic.asm`): the
script of the song was rebuilt from the comments of the original, compiled with `-unz`, and both
sources were run on an emulated 68000 with the same player data, external samples and noise seeds.

**Same:** the global labels and their order, `AK_SamLengths`, the `Depending on` comments, the
sizes of `AK_OpInstance` (18 words) and `AK_EnvDValue` (1 long). **18 of the 21 instruments are
bit-identical**, including the imports, the noise, reverb, comb and state variable filters, ADSR,
distortion and all ten loop generators.

**Different, on purpose:**

1. **Clone offset.** The three other instruments (11, 15 and 21) use `clone` with an offset. The
   script kept the offset of the 8 bit code (`add.l #726,d6`) while `d7` counts bytes, so the
   clone started at **half** the offset of the patch. `-unz` uses the offset of the patch, like
   the editor and the 8 bit code. With the offsets halved in the script (1752 → 876, 1530 → 765,
   726 → 363) all 21 instruments are bit-identical to the converted file. So these three
   instruments will sound as they do in the editor now, not as in the current player.
2. **Work buffer.** The converted code clears the delay buffers the *previous* instrument used
   (`moveq #8,d0` in front of instrument 4 because instrument 3 has a reverb). That is only right
   in the order 1, 2, 3 … and with all eight buffers cleared at the start. With `-unz` every
   routine clears the buffers it uses itself: any order that respects the dependencies works, and
   the buffer may be dirty.
3. **Imported samples.** The script read them with `move.w d7,d3 / lsr.w #1,d3 / move.b (a4,d3.w)`:
   that destroys V4 and fails from 32768 samples on. `-unz` reads with `(a4,d7.l)` and no extra
   register.
4. **`d7` counts samples**, not bytes (bytebeat, ADPCM and the imports need the sample number), and
   the lengths in the code are numbers (`cmp.l #6144,d7`) instead of `AK_SamLengths+n*4(pc)`.
   `AK_SamLengths` itself is unchanged, in bytes.
5. **Chord generator.** Every voice is halved and then summed in 32 bit, like the editor (the
   script summed first: at most 2 of 65536 different). A voice that has passed the end of its
   source is silent; the old code went on reading the memory behind the sample.
6. **Reverse clone** takes the end of the source as start + 2 × length, so the samples need not
   lie one behind the other.
7. **Instruments without operators** have no routine and length 0 (the script only did that for
   length 2).

## What is new compared with V1.1

- **Sample mixer**: plays a pattern of the editor into a sample (depends on its source instruments).
- **Clone / clone reverse with transpose**, also modulated by V1–V4.
- **Loop generator**: the loop may be longer than the part in front of it.
- **Bytebeat**: a formula over the sample number, bit-identical with the editor.
- **Up to 12 external samples**, and **4 bit ADPCM** imports. The header of the generated file
  lists where each external sample lies in the block:
  ```
  ; External samples (offset in the block of AK_EXT_SMP_LEN bytes):
  ; 1: offset 23974, 1140 bytes (4 bit ADPCM, 2280 samples)
  ; 2: offset 0, 6936 bytes
  ; 3: offset 6936, 3866 bytes
  ```
  V1.20: an ADPCM sample is rendered with the full 16 bit value of the decoder (the low byte is
  no longer cleared); the editor does the same for "Export Samples to XM". Delta coded imports
  stay 8 bit.
  The delta coded samples come first (in slot order), the ADPCM samples after them, each with
  half the bytes. `ak_delta_decode_extsamples` only decodes the delta part; ADPCM samples are
  decoded while `imported_sample` plays them. `pd_ExtSampleTable` has to point to these offsets.
  The entries of `AK_SamLengths` are samples × 2 for both kinds, so with ADPCM imports the
  addresses cannot be summed up from that table.

## Things to know

- **Noise and order.** The noise seeds run on from instrument to instrument. If the player
  renders in another order than 1 … 31, instruments with `osc_noise` (and what is cloned from
  them) get other noise than in the editor. Everything else does not depend on the order.
- **The editor** renders the same 16 bit samples for "Export Samples to XM". For patches made of
  exact operators the 68000 code and the editor are bit-identical; oscillators, filters and
  envelopes differ by rounding as they always did.
- `-16` (without `-unz`) still writes one `AK_Generate` for all instruments into one buffer.

## Questions

1. ~~Does the player read `AK_SamLengths`, or only the generated code?~~ Answered: the player
   reads it, so the table stays as it is (the generated code itself uses numbers).
2. `AK_SampleGenerators`: your converted file has no line of negated numbers, the script writes
   one. Which form do you want?
3. Do you render in another order than 1 … 31? Then say if noise should be the same as in the
   editor: each routine could set its own seeds.
4. Is the place of the variables (`IF 0` block, to be moved by hand) fine, or would an include
   file with the `rs` lines be easier?
5. Anything in the player that relied on the halved clone offsets or on the work buffer being
   cleared in front of instrument 1?
