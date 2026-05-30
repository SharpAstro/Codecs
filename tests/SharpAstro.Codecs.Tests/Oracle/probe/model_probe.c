/* Golden-vector generator for the model-bits port. Drives jxrlib's real (linkable)
 * UpdateModelMB with controlled Laplacian-mean inputs and dumps the FLC width /
 * state evolution. CAdaptiveModel layout: { int FlcState[2]; int FlcBits[2]; int band; }.
 * band: DC=1, LP=2, AC=3. cf: Y_ONLY=0, YUV444=3. UpdateModelMB mutates the mean
 * array in place, so a fresh copy is passed each step.
 * Build via build_probe.sh model_probe.c. Throwaway dev tool. */
#include <string.h>
#include <stdio.h>

typedef struct { int m_iFlcState[2]; int m_iFlcBits[2]; int m_band; } AM;
extern void UpdateModelMB(int cf, int iChannels, int *iLaplacianMean, AM *pModel);

static void step(AM *m, int cf, int ch, int lm0, int lm1, const char *tag)
{
    int lm[2] = { lm0, lm1 };
    UpdateModelMB(cf, ch, lm, m);
    printf("%s FlcBits=[%d,%d] FlcState=[%d,%d]\n",
           tag, m->m_iFlcBits[0], m->m_iFlcBits[1], m->m_iFlcState[0], m->m_iFlcState[1]);
}

static AM make(int band, int init)
{
    AM m;
    memset(&m, 0, sizeof(m));
    m.m_band = band;
    m.m_iFlcBits[0] = m.m_iFlcBits[1] = init;
    return m;
}

int main(void)
{
    /* DC band, YUV444, 3 channels (init 8). raw mean 1 -> weighted high -> up; 0 -> down. */
    {
        AM m = make(1, 8);
        step(&m, 3, 3, 1, 1, "DC_up1");
        step(&m, 3, 3, 1, 1, "DC_up2");
        step(&m, 3, 3, 1, 1, "DC_up3");
        step(&m, 3, 3, 0, 0, "DC_dn1");
        step(&m, 3, 3, 0, 0, "DC_dn2");
        step(&m, 3, 3, 0, 0, "DC_dn3");
        step(&m, 3, 3, 20, 20, "DC_mid");
    }
    /* LP band, YUV444, 3 channels (init 4). */
    {
        AM m = make(2, 4);
        step(&m, 3, 3, 30, 30, "LP_a");
        step(&m, 3, 3, 30, 30, "LP_b");
        step(&m, 3, 3, 0, 0, "LP_c");
        step(&m, 3, 3, 200, 200, "LP_d");
    }
    /* AC band, YUV444, 3 channels (init 0, chroma >>4). */
    {
        AM m = make(3, 0);
        step(&m, 3, 3, 100, 100, "AC_a");
        step(&m, 3, 3, 100, 100, "AC_b");
        step(&m, 3, 3, 0, 0, "AC_c");
    }
    /* DC band, Y_ONLY (cf=0): only channel 0 updates. */
    {
        AM m = make(1, 8);
        step(&m, 0, 1, 1, 999, "YDC_a");
        step(&m, 0, 1, 0, 999, "YDC_b");
    }
    return 0;
}
