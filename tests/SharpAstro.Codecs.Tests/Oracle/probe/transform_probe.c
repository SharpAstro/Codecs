/* One-off golden-vector generator for the Photo Core Transform port.
 * Calls jxrlib's real strDCT4x4Stage1 / strDCT4x4SecondStage on fixed inputs
 * and prints the outputs, which get baked into PhotoCoreTransformTests.cs as
 * known-answer vectors. Compile via Oracle/probe/build_probe.sh. Throwaway dev
 * tool; not part of any build. */
#include <stdio.h>

typedef int PixelI;
extern void strDCT4x4Stage1(PixelI *);
extern void strIDCT4x4Stage1(PixelI *);
extern void strDCT4x4SecondStage(PixelI *);
extern void strIDCT4x4Stage2(PixelI *);

static void print16(const char *tag, const PixelI *p)
{
    printf("%s", tag);
    for (int i = 0; i < 16; i++) printf(" %d", p[i]);
    printf("\n");
}

static const int kStride16[16] = {
    0, 16, 32, 48, 64, 80, 96, 112, 128, 144, 160, 176, 192, 208, 224, 240
};

int main(void)
{
    /* Stage 1 — ramp 0..15 */
    {
        PixelI p[16];
        for (int i = 0; i < 16; i++) p[i] = i;
        strDCT4x4Stage1(p);
        print16("S1_ramp", p);
    }
    /* Stage 1 — flat DC (all 100) */
    {
        PixelI p[16];
        for (int i = 0; i < 16; i++) p[i] = 100;
        strDCT4x4Stage1(p);
        print16("S1_dc100", p);
    }
    /* Stage 1 — mixed signs */
    {
        PixelI p[16] = { 5, -3, 7, 2, -8, 4, 0, 11, 6, -1, 9, -4, 3, 8, -2, 1 };
        strDCT4x4Stage1(p);
        print16("S1_mix", p);
    }
    /* Stage 2 — mixed values on the 16 strided super-DC positions */
    {
        PixelI p[256];
        for (int i = 0; i < 256; i++) p[i] = 0;
        for (int k = 0; k < 16; k++) p[kStride16[k]] = (k + 1) * 7 - 50;
        strDCT4x4SecondStage(p);
        printf("S2_mix");
        for (int k = 0; k < 16; k++) printf(" %d", p[kStride16[k]]);
        printf("\n");
    }
    return 0;
}
