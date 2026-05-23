using SharpAstro.Jxr;
using Shouldly;

namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Tests for PROFILE_LEVEL_INFO (T.832 §8.5) — verifies the (profile, level,
/// reserved, last_flag) record layout, single- and multi-entry termination,
/// and that the structure ends naturally on a byte boundary.
/// </summary>
public sealed class JxrProfileLevelInfoTests
{
    [Fact]
    public void Single_MainL4_RoundTrips()
    {
        var info = ProfileLevelInfo.Single(JxrProfile.Main, JxrLevel.L4);
        var w = new BitWriter();
        info.Write(w);

        // 32 bits per entry → exactly 4 bytes for a single entry.
        w.ByteCount.ShouldBe(4);
        (w.BitPosition % 8).ShouldBe(0);

        var r = new BitReader(w.AsSpan());
        var read = ProfileLevelInfo.Read(ref r);
        read.Entries.Count.ShouldBe(1);
        read.Entries[0].ProfileIdc.ShouldBe(JxrProfile.Main);
        read.Entries[0].LevelIdc.ShouldBe(JxrLevel.L4);
    }

    [Fact]
    public void Single_Advanced_HdrTarget_RoundTrips()
    {
        // The HDR/float path needs Advanced profile.
        var info = ProfileLevelInfo.Single(JxrProfile.Advanced, JxrLevel.L4);
        var w = new BitWriter();
        info.Write(w);
        var r = new BitReader(w.AsSpan());
        var read = ProfileLevelInfo.Read(ref r);
        read.Entries[0].ProfileIdc.ShouldBe(JxrProfile.Advanced);
    }

    [Fact]
    public void Multiple_Entries_LastFlagTerminates()
    {
        var info = new ProfileLevelInfo
        {
            Entries =
            {
                new ProfileLevelInfo.Entry(JxrProfile.SubBaseline, JxrLevel.L1),
                new ProfileLevelInfo.Entry(JxrProfile.Baseline,    JxrLevel.L2),
                new ProfileLevelInfo.Entry(JxrProfile.Main,        JxrLevel.L4),
            },
        };
        var w = new BitWriter();
        info.Write(w);
        w.ByteCount.ShouldBe(12, "three 4-byte records");

        var r = new BitReader(w.AsSpan());
        var read = ProfileLevelInfo.Read(ref r);
        read.Entries.Count.ShouldBe(3);
        read.Entries[0].ProfileIdc.ShouldBe(JxrProfile.SubBaseline);
        read.Entries[1].ProfileIdc.ShouldBe(JxrProfile.Baseline);
        read.Entries[2].ProfileIdc.ShouldBe(JxrProfile.Main);
    }

    [Fact]
    public void Empty_Write_Throws()
    {
        var info = new ProfileLevelInfo();
        var w = new BitWriter();
        var threw = false;
        try { info.Write(w); }
        catch (InvalidOperationException) { threw = true; }
        threw.ShouldBeTrue();
    }

    [Fact]
    public void LastFlagBit_AtCorrectPosition()
    {
        // Verify the bit-level layout: LAST_FLAG is bit 31 of each 32-bit
        // record (i.e. the LSB of the 4th byte) per T.832 8.5.
        var info = ProfileLevelInfo.Single(0x42, 0x84);
        var w = new BitWriter();
        info.Write(w);
        var bytes = w.AsSpan();
        bytes[0].ShouldBe((byte)0x42, "PROFILE_IDC = bits 0..7");
        bytes[1].ShouldBe((byte)0x84, "LEVEL_IDC = bits 8..15");
        // bytes[2]..[3] hold RESERVED_L (15 bits) and LAST_FLAG (1 bit).
        // For a single-entry record LAST_FLAG = 1 → the LSB of byte 3 = 1.
        (bytes[3] & 1).ShouldBe(1, "LAST_FLAG bit");
    }

    [Fact]
    public void NonLastEntry_LastFlagIsZero()
    {
        var info = new ProfileLevelInfo
        {
            Entries =
            {
                new ProfileLevelInfo.Entry(0x10, 0x20),
                new ProfileLevelInfo.Entry(0x30, 0x40),
            },
        };
        var w = new BitWriter();
        info.Write(w);
        var bytes = w.AsSpan();
        // Record 0 ends at byte 3, LAST_FLAG (bit 31) must be 0.
        (bytes[3] & 1).ShouldBe(0);
        // Record 1 ends at byte 7, LAST_FLAG must be 1.
        (bytes[7] & 1).ShouldBe(1);
    }

    [Fact]
    public void ByteAligned_AfterWrite()
    {
        var info = ProfileLevelInfo.Single(JxrProfile.Main, JxrLevel.L4);
        var w = new BitWriter();
        info.Write(w);
        (w.BitPosition % 8).ShouldBe(0);
    }
}
