/* Golden-vector generator for the prediction port. Calls jxrlib's linkable
 * getACPredMode on fixed DC-block patterns. CWMIMBInfo begins with
 * iBlockDC[MAX_CHANNELS][16]; the [1]/[2] strides are fixed at 16 ints, so a
 * {int iBlockDC[3][16];} prefix gives the right addresses for channels 0..2.
 * Build via build_probe.sh predict_probe.c. Throwaway dev tool. */
#include <string.h>
#include <stdio.h>

typedef struct { int iBlockDC[3][16]; } MB;
extern int getACPredMode(MB *, int cf);

static int run(int cf, const int *y, const int *u, const int *v)
{
    MB mb;
    memset(&mb, 0, sizeof(mb));
    for (int i = 0; i < 16; i++) { mb.iBlockDC[0][i] = y[i]; mb.iBlockDC[1][i] = u[i]; mb.iBlockDC[2][i] = v[i]; }
    return getACPredMode(&mb, cf);
}

int main(void)
{
    int z[16] = {0};
    int hY[16] = {0}; hY[1] = 30; hY[2] = 20; hY[3] = 10; hY[4] = 1; hY[8] = 1; hY[12] = 1;   // strong row
    int vY[16] = {0}; vY[1] = 1;  vY[2] = 1;  vY[3] = 1;  vY[4] = 30; vY[8] = 20; vY[12] = 10; // strong col
    int bY[16] = {0}; bY[1] = 10; bY[2] = 10; bY[3] = 10; bY[4] = 10; bY[8] = 10; bY[12] = 10; // balanced
    int u1[16] = {0}; u1[1] = 5; u1[4] = 20;
    int v1[16] = {0}; v1[1] = 5; v1[4] = 20;

    printf("AC hY cf0 %d\n", run(0, hY, z, z));
    printf("AC vY cf0 %d\n", run(0, vY, z, z));
    printf("AC bY cf0 %d\n", run(0, bY, z, z));
    printf("AC hY cf3 %d\n", run(3, hY, u1, v1));
    printf("AC vY cf3 %d\n", run(3, vY, u1, v1));
    return 0;
}
