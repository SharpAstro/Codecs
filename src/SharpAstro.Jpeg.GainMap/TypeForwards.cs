using System.Runtime.CompilerServices;

// Types that moved into SharpAstro.GainMap keep their full names, so code compiled against an earlier
// SharpAstro.Jpeg.GainMap still binds. (SrgbTransfer was internal, so it needs no forward.)
[assembly: TypeForwardedTo(typeof(SharpAstro.Jpeg.GainMapMetadata))]
