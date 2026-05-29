using SharpAstro.Jxr;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Phase-4c tests for the scan-adaptive block coder (T.832 §8.7.18.4
/// DECODE_BLOCK_ADAPTIVE). Verifies that round-tripping a 16-position
/// raster block through encode → decode reproduces the input bit-exact,
/// AND that the encoder's and decoder's AdaptiveScan states stay in
/// lock-step so multi-block sequences also round-trip.
/// </summary>
public sealed class JxrBlockAdaptiveTests
{
    private static BlockCodingContext FreshContext() => new()
    {
        FirstIndex = AdaptiveVlc.InitializeTable2(),
        Index0     = AdaptiveVlc.InitializeTable2(),
        Index1     = AdaptiveVlc.InitializeTable2(),
        AbsLevel0  = AdaptiveVlc.InitializeTable1(),
        AbsLevel1  = AdaptiveVlc.InitializeTable1(),
    };

    private static void RoundTripBlock(int[] block)
    {
        var encCtx = FreshContext();
        var encScan = AdaptiveScan.ForHpHorizontal();
        var w = new BitWriter();
        BlockAdaptive.Encode(w, ref encCtx, encScan, block);

        var decCtx = FreshContext();
        var decScan = AdaptiveScan.ForHpHorizontal();
        var r = new BitReader(w.AsSpan());
        var decoded = new int[16];
        BlockAdaptive.Decode(ref r, ref decCtx, decScan, decoded);

        for (var i = 1; i <= 15; i++)
            decoded[i].ShouldBe(block[i], $"position {i}");
    }

    [Fact]
    public void SingleNonZero_AtBlockPosition1_RoundTrips()
    {
        var block = new int[16];
        block[1] = 42;
        RoundTripBlock(block);
    }

    [Fact]
    public void SingleNonZero_AtBlockPosition15_RoundTrips()
    {
        var block = new int[16];
        block[15] = -7;
        RoundTripBlock(block);
    }

    [Fact]
    public void FullDenseBlock_AllNonZero_RoundTrips()
    {
        var block = new int[16];
        for (var i = 1; i <= 15; i++) block[i] = i * (i & 1) == 0 ? -i : i;
        RoundTripBlock(block);
    }

    [Fact]
    public void SparseBlock_VariousPositions_RoundTrips()
    {
        var block = new int[16];
        block[3] = 5;
        block[7] = -12;
        block[11] = 100;
        RoundTripBlock(block);
    }

    [Fact]
    public void EmptyBlock_NoBitsEmitted()
    {
        var encCtx = FreshContext();
        var encScan = AdaptiveScan.ForHpHorizontal();
        var w = new BitWriter();
        var n = BlockAdaptive.Encode(w, ref encCtx, encScan, new int[16]);
        n.ShouldBe(0);
        w.BitPosition.ShouldBe(0, "empty block emits nothing; caller signals via CBPHP");
    }

    [Fact]
    public void SequentialBlocks_ScanStateStaysInSync()
    {
        // Encode 5 random blocks with one shared scan state, then decode
        // with another shared scan state. If both scans don't update
        // identically, the second-or-later block's bits decode at different
        // positions and the comparison fails.
        var rng = new Random(0xABBA);
        var blocks = new int[5][];
        for (var b = 0; b < 5; b++)
        {
            blocks[b] = new int[16];
            for (var i = 1; i <= 15; i++)
                blocks[b][i] = rng.Next(0, 4) == 0 ? rng.Next(-64, 65) : 0;
            // Ensure at least one non-zero so we actually encode something.
            blocks[b][rng.Next(1, 16)] = rng.Next(1, 64);
        }

        var encCtx = FreshContext();
        var encScan = AdaptiveScan.ForHpHorizontal();
        var w = new BitWriter();
        foreach (var blk in blocks)
            BlockAdaptive.Encode(w, ref encCtx, encScan, blk);

        var decCtx = FreshContext();
        var decScan = AdaptiveScan.ForHpHorizontal();
        var r = new BitReader(w.AsSpan());
        for (var b = 0; b < 5; b++)
        {
            var decoded = new int[16];
            BlockAdaptive.Decode(ref r, ref decCtx, decScan, decoded);
            for (var i = 1; i <= 15; i++)
                decoded[i].ShouldBe(blocks[b][i], $"block {b} pos {i}");
        }
    }

    [Fact]
    public void RandomBlocks_FullSweep_BothScanDirections()
    {
        // Sweep both HP horizontal and HP vertical scan paths. Adaptive
        // updates differ between them (different initial scan orders), so
        // exercise both.
        foreach (var useVertical in new[] { false, true })
        {
            var rng = new Random(0xCABBA + (useVertical ? 1 : 0));
            for (var trial = 0; trial < 100; trial++)
            {
                var block = new int[16];
                var anyNonZero = false;
                for (var i = 1; i <= 15; i++)
                {
                    block[i] = rng.Next(0, 3) == 0 ? rng.Next(-100, 101) : 0;
                    if (block[i] != 0) anyNonZero = true;
                }
                if (!anyNonZero) block[rng.Next(1, 16)] = rng.Next(1, 100);

                var encCtx = FreshContext();
                var encScan = useVertical ? AdaptiveScan.ForHpVertical() : AdaptiveScan.ForHpHorizontal();
                var w = new BitWriter();
                BlockAdaptive.Encode(w, ref encCtx, encScan, block);

                var decCtx = FreshContext();
                var decScan = useVertical ? AdaptiveScan.ForHpVertical() : AdaptiveScan.ForHpHorizontal();
                var r = new BitReader(w.AsSpan());
                var decoded = new int[16];
                BlockAdaptive.Decode(ref r, ref decCtx, decScan, decoded);

                for (var i = 1; i <= 15; i++)
                    decoded[i].ShouldBe(block[i], $"vertical={useVertical} trial={trial} pos={i}");
            }
        }
    }
}
