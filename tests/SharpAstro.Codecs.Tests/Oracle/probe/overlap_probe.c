/* Golden-vector generator for the Photo Overlap Transform (POT) port.
 * Calls jxrlib's real pre/post overlap functions on fixed inputs and prints
 * outputs, baked into PhotoOverlapTransformTests.cs. The HST and oddOddPre
 * primitives are static in jxrlib, so they're covered transitively via the
 * linkable wrappers (strPre4, the Stage1/Stage2 splits, strPost4_alternate).
 * Build via build_probe.sh overlap_probe.c. Throwaway dev tool. */
#include <stdio.h>

typedef int PixelI;
extern void strPre2(PixelI *, PixelI *);
extern void strPre2x2(PixelI *, PixelI *, PixelI *, PixelI *);
extern void strPre4(PixelI *, PixelI *, PixelI *, PixelI *);
extern void strPre4x4Stage1(PixelI *, int);
extern void strPre4x4Stage2Split(PixelI *, PixelI *);
extern void strPost2(PixelI *, PixelI *);
extern void strPost2_alternate(PixelI *, PixelI *);
extern void strPost2x2(PixelI *, PixelI *, PixelI *, PixelI *);
extern void strPost2x2_alternate(PixelI *, PixelI *, PixelI *, PixelI *);
extern void strPost4(PixelI *, PixelI *, PixelI *, PixelI *);
extern void strPost4_alternate(PixelI *, PixelI *, PixelI *, PixelI *);
extern void strPost4x4Stage1(PixelI *, int, int, int);
extern void strPost4x4Stage2Split(PixelI *, PixelI *);

/* Same deterministic fill on both sides; only accessed cells change. */
static int fill(int i) { return (i * 37 + 11) % 211 - 105; }

static void dump_cells(const char *tag, const PixelI *buf, const int *idx, int n)
{
    printf("%s", tag);
    for (int k = 0; k < n; k++) printf(" %d", buf[idx[k]]);
    printf("\n");
}

int main(void)
{
    /* --- scalar 2-arg --- */
    { PixelI a = 40, b = -13; strPre2(&a, &b);            printf("PRE2 %d %d\n", a, b); }
    { PixelI a = 40, b = -13; strPost2(&a, &b);           printf("POST2 %d %d\n", a, b); }
    { PixelI a = 40, b = -13; strPost2_alternate(&a, &b); printf("POST2ALT %d %d\n", a, b); }

    /* --- scalar 4-arg --- */
    { PixelI a=50,b=-20,c=33,d=-7; strPre2x2(&a,&b,&c,&d);            printf("PRE2x2 %d %d %d %d\n",a,b,c,d); }
    { PixelI a=50,b=-20,c=33,d=-7; strPost2x2(&a,&b,&c,&d);           printf("POST2x2 %d %d %d %d\n",a,b,c,d); }
    { PixelI a=50,b=-20,c=33,d=-7; strPost2x2_alternate(&a,&b,&c,&d); printf("POST2x2ALT %d %d %d %d\n",a,b,c,d); }
    { PixelI a=50,b=-20,c=33,d=-7; strPre4(&a,&b,&c,&d);             printf("PRE4 %d %d %d %d\n",a,b,c,d); }
    { PixelI a=50,b=-20,c=33,d=-7; strPost4(&a,&b,&c,&d);            printf("POST4 %d %d %d %d\n",a,b,c,d); }
    { PixelI a=50,b=-20,c=33,d=-7; strPost4_alternate(&a,&b,&c,&d);  printf("POST4ALT %d %d %d %d\n",a,b,c,d); }

    /* --- Stage 1 (256-cell buffer, p=0, iOffset=0) --- */
    {
        static const int s1[16] = {12,13,14,15, 20,21,22,23, 72,73,74,75, 80,81,82,83};
        PixelI buf[256]; for (int i=0;i<256;i++) buf[i]=fill(i);
        strPre4x4Stage1(buf, 0);
        dump_cells("PRESTAGE1", buf, s1, 16);
    }
    {
        static const int s1[16] = {12,13,14,15, 20,21,22,23, 72,73,74,75, 80,81,82,83};
        PixelI buf[256]; for (int i=0;i<256;i++) buf[i]=fill(i);
        strPost4x4Stage1(buf, 0, 0, 0);              /* iHPQP=0, bHPAbsent=0 -> no DC comp */
        dump_cells("POSTSTAGE1_NOCOMP", buf, s1, 16);
    }
    {
        static const int s1[16] = {12,13,14,15, 20,21,22,23, 72,73,74,75, 80,81,82,83};
        PixelI buf[256]; for (int i=0;i<256;i++) buf[i]=fill(i);
        strPost4x4Stage1(buf, 0, 0, 1);              /* bHPAbsent=1 -> DC comp fires */
        dump_cells("POSTSTAGE1_COMP", buf, s1, 16);
    }

    /* --- Stage 2 (512-cell buffer, p0=128, p1=256) --- */
    {
        static const int s2[16] = {32,48,96,112,128,144,160,176,192,208,224,240,256,272,320,336};
        PixelI buf[512]; for (int i=0;i<512;i++) buf[i]=fill(i);
        strPre4x4Stage2Split(buf+128, buf+256);
        dump_cells("PRESTAGE2", buf, s2, 16);
    }
    {
        static const int s2[16] = {32,48,96,112,128,144,160,176,192,208,224,240,256,272,320,336};
        PixelI buf[512]; for (int i=0;i<512;i++) buf[i]=fill(i);
        strPost4x4Stage2Split(buf+128, buf+256);
        dump_cells("POSTSTAGE2", buf, s2, 16);
    }
    return 0;
}
