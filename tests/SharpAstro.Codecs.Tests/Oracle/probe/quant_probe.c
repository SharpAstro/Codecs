/* Golden-vector generator for the quantization port. Calls jxrlib's real
 * remapQP / QUANT / QUANT_Mulless on fixed inputs and prints outputs, baked into
 * QuantizationTests.cs. Mirrors the CWMIQuantizer layout (U8 iIndex; I32 iQP,
 * iOffset, iMan, iExp) without WMP_OPT_QT (undefined in the probe build).
 * Build via build_probe.sh quant_probe.c. Throwaway dev tool. */
#include <stdio.h>

typedef struct { unsigned char iIndex; int iQP; int iOffset; int iMan; int iExp; } Q;
extern void remapQP(Q *, int iShift, int bScaledArith);
extern int QUANT(int v, int o, int man, int exp);
extern int QUANT_Mulless(int v, int o, int r);

static int quant_one(const Q *q, int v)
{
    return q->iMan == 0 ? QUANT_Mulless(v, q->iOffset, q->iExp)
                        : QUANT(v, q->iOffset, q->iMan, q->iExp);
}

int main(void)
{
    int idxs[] = {0,1,2,3,4,5,8,15,16,17,31,32,33,47,48,49,64,80,127,200,255};
    int nidx = (int)(sizeof(idxs) / sizeof(idxs[0]));

    for (int k = 0; k < nidx; k++) {
        Q q; q.iIndex = (unsigned char)idxs[k];
        remapQP(&q, 1 /*SHIFTZERO*/, 0 /*bScaledArith=false*/);
        printf("REMAP %d %d %u %d %d\n", idxs[k], q.iQP, (unsigned)q.iMan, q.iExp, q.iOffset);
    }
    for (int k = 0; k < nidx; k++) {
        Q q; q.iIndex = (unsigned char)idxs[k];
        remapQP(&q, 1, 1 /*bScaledArith=true*/);
        printf("REMAPS %d %d %u %d %d\n", idxs[k], q.iQP, (unsigned)q.iMan, q.iExp, q.iOffset);
    }

    int testv[] = {0,1,5,-5,100,-100,1000,-1000,12345,-12345};
    int nv = (int)(sizeof(testv) / sizeof(testv[0]));
    int qidx[] = {1,2,3,4,8,16,33,64};
    int nq = (int)(sizeof(qidx) / sizeof(qidx[0]));
    for (int qi = 0; qi < nq; qi++) {
        Q q; q.iIndex = (unsigned char)qidx[qi];
        remapQP(&q, 1, 0);
        for (int vi = 0; vi < nv; vi++) {
            int v = testv[vi];
            int out = quant_one(&q, v);
            printf("QUANT %d %d %d %d\n", qidx[qi], v, out, out * q.iQP); /* idx v quantized dequantized */
        }
    }
    return 0;
}
