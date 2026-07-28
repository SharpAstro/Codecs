namespace SharpAstro.Codecs.Tests;

/// <summary>
/// Resolves the jxrlib reference binaries (<c>JxrEncApp.exe</c> /
/// <c>JxrDecApp.exe</c>) that the JXR oracle tests validate against, and gates
/// on their presence through <see cref="OracleGate"/>.
/// <para>
/// This replaces a guard that used to be copy-pasted into every JXR oracle test:
/// </para>
/// <code>
/// var encApp = FindOracle("JxrEncApp.exe");
/// if (encApp is null) { _out.WriteLine("... not found — skipping"); return; }
/// </code>
/// <para>
/// That <c>return</c> makes the test <b>pass</b>. On a dev box with
/// <c>Oracle/bin/</c> populated it is invisible; in CI, where nothing built the
/// binaries, it meant ~38 oracle methods reported success while executing none
/// of their assertions — and byte-exactness against <c>JxrEncApp</c> is the
/// strongest guarantee this repo makes about the JXR port. A missing oracle now
/// skips visibly, or fails outright under <c>REQUIRE_ORACLES=1</c>.
/// </para>
/// <para>
/// Build the binaries with <c>bash tests/SharpAstro.Codecs.Tests/Oracle/build.sh</c>
/// (clang; works on Windows and Linux). They are git-ignored.
/// </para>
/// </summary>
internal static class JxrOracle
{
    private const string EncName = "JxrEncApp.exe";
    private const string DecName = "JxrDecApp.exe";

    private static readonly Lazy<string?> EncPath = new(() => Find(EncName));
    private static readonly Lazy<string?> DecPath = new(() => Find(DecName));

    private const string HowToBuild =
        "Build it with `bash tests/SharpAstro.Codecs.Tests/Oracle/build.sh` (needs clang and git).";

    /// <summary>True when both reference binaries are present.</summary>
    public static bool IsAvailable => EncPath.Value is not null && DecPath.Value is not null;

    /// <summary>
    /// Returns the path to <c>JxrEncApp.exe</c>, or skips/fails the calling test
    /// when it is absent. Never returns null.
    /// </summary>
    public static string RequireEncApp()
    {
        OracleGate.RequireOrSkip(EncPath.Value is not null, EncName, HowToBuild);
        return EncPath.Value!;
    }

    /// <summary>
    /// Returns the path to <c>JxrDecApp.exe</c>, or skips/fails the calling test
    /// when it is absent. Never returns null.
    /// </summary>
    public static string RequireDecApp()
    {
        OracleGate.RequireOrSkip(DecPath.Value is not null, DecName, HowToBuild);
        return DecPath.Value!;
    }

    /// <summary>Walk up from the test output directory to find Oracle/bin/&lt;exe&gt;.</summary>
    private static string? Find(string exe)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var direct = Path.Combine(dir.FullName, "Oracle", "bin", exe);
            if (File.Exists(direct)) return direct;
            var nested = Path.Combine(dir.FullName, "tests", "SharpAstro.Codecs.Tests", "Oracle", "bin", exe);
            if (File.Exists(nested)) return nested;
        }
        return null;
    }
}
