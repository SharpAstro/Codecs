/* Golden-vector generator for the adaptive-Huffman table-index state machine.
 * Drives jxrlib's real AdaptDiscriminant with controlled discriminant values and
 * dumps the resulting table index / bounds / discriminants. Replicates the
 * CAdaptiveHuffman layout (common.h) exactly so field offsets match.
 * Build via build_probe.sh adapthuff_probe.c. Throwaway dev tool. */
#include <string.h>
#include <stdio.h>

typedef int Bool;
typedef struct {
    int m_iNSymbols;
    const int *m_pTable;
    const int *m_pDelta, *m_pDelta1;
    int m_iTableIndex;
    const short *m_hufDecTable;
    Bool m_bInitialize;
    int m_iDiscriminant, m_iDiscriminant1;
    int m_iUpperBound;
    int m_iLowerBound;
} AH;

extern void AdaptDiscriminant(AH *);

static void step(AH *h, int disc, int disc1, const char *tag)
{
    h->m_iDiscriminant = disc;
    h->m_iDiscriminant1 = disc1;
    AdaptDiscriminant(h);
    printf("%s t=%d disc=%d disc1=%d lo=%d hi=%d\n",
           tag, h->m_iTableIndex, h->m_iDiscriminant, h->m_iDiscriminant1,
           h->m_iLowerBound, h->m_iUpperBound);
}

int main(void)
{
    /* iSym = 12: dual discriminant, 5 tables, init index = 1 */
    {
        AH h; memset(&h, 0, sizeof(h)); h.m_iNSymbols = 12;
        step(&h, 0, 0, "S12_0");        /* init */
        step(&h, 0, 100, "S12_1");      /* dH>hi -> t++ */
        step(&h, 0, 100, "S12_2");
        step(&h, 0, 100, "S12_3");      /* reach t=4 (max) */
        step(&h, 0, 100, "S12_4");      /* pinned at max */
        step(&h, 100, 5, "S12_5");      /* no change; clamp disc to 64 */
        step(&h, -100, 0, "S12_6");     /* dL<lo -> t-- */
        step(&h, -100, 0, "S12_7");
        step(&h, -100, 0, "S12_8");
        step(&h, -100, 0, "S12_9");     /* reach t=0 */
        step(&h, -100, 0, "S12_10");    /* pinned at min */
    }
    /* iSym = 5: single discriminant, 2 tables, init index = 0 */
    {
        AH h; memset(&h, 0, sizeof(h)); h.m_iNSymbols = 5;
        step(&h, 0, 0, "S5_0");         /* init */
        step(&h, 100, 0, "S5_1");       /* dH=disc>hi -> t++ (max) */
        step(&h, 100, 0, "S5_2");       /* pinned */
        step(&h, -100, 0, "S5_3");      /* dL<lo -> t-- */
        step(&h, -100, 0, "S5_4");      /* pinned at 0 */
    }
    return 0;
}
