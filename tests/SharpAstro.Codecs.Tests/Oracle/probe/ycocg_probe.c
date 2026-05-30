/* Golden-vector generator for the color transform. Runs jxrlib's exact _CC /
 * _ICC / _CC_CMYK / _ICC_CMYK macros (copied verbatim from strenc.c / strdec.c)
 * through clang so the authoritative C shift/precedence semantics pin the C#
 * port. Build via build_probe.sh ycocg_probe.c. Throwaway dev tool. */
#include <stdio.h>

#define _CC(r, g, b)        (b -= r, r += ((b + 1) >> 1) - g, g += ((r + 0) >> 1))
#define _ICC(r, g, b)       (g -= ((r + 0) >> 1), r -= ((b + 1) >> 1) - g, b += r)
#define _CC_CMYK(c, m, y, k) (y -= c, c += ((y + 1) >> 1) - m, m += (c >> 1) - k, k += ((m + 1) >> 1))
#define _ICC_CMYK(c, m, y, k) (k -= ((m + 1) >> 1), m -= (c >> 1) - k, c -= ((y + 1) >> 1) - m, y += c)

int main(void)
{
    int tv[][3] = { {100,150,200}, {0,0,0}, {255,0,128}, {47,20,49}, {200,100,50} };
    for (int i = 0; i < 5; i++)
    {
        int r = tv[i][0], g = tv[i][1], b = tv[i][2];
        _CC(r, g, b);
        printf("CC %d %d %d %d %d %d\n", tv[i][0], tv[i][1], tv[i][2], r, g, b);
    }
    {
        int c = 200, m = 50, y = 180, k = 30;
        _CC_CMYK(c, m, y, k);
        printf("CCK 200 50 180 30 %d %d %d %d\n", c, m, y, k);
    }
    return 0;
}
