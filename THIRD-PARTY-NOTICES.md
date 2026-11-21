# Third-party notices

The packages in this repository are released under the Unlicense (`UNLICENSE`), except for the
portions listed here, which keep their own licences.

## jxl-oxide (in `SharpAstro.Jxl`)

Parts of `SharpAstro.Jxl` are ported from [jxl-oxide](https://github.com/tirr-c/jxl-oxide), a JPEG XL
decoder written in Rust by Wonwoo Choi and contributors, dual-licensed MIT OR Apache-2.0. This
repository uses it under the MIT licence. The ported code is chiefly the lossy (VarDCT) decoder
(`JxlVarDctDecoder`, `JxlVarDctInverse`, `JxlRestorationFilters`, `JxlColorOutput`) and individual
routines elsewhere in the package; each file that carries ported code says so at its top and cites the
jxl-oxide routine at each place. Ported from jxl-oxide commit
`c0cc4c7ea57c1207f38ff2970d94757470613be4`.

```
Copyright (c) Wonwoo Choi and the jxl-oxide contributors

Permission is hereby granted, free of charge, to any
person obtaining a copy of this software and associated
documentation files (the "Software"), to deal in the
Software without restriction, including without
limitation the rights to use, copy, modify, merge,
publish, distribute, sublicense, and/or sell copies of
the Software, and to permit persons to whom the Software
is furnished to do so, subject to the following
conditions:

The above copyright notice and this permission notice
shall be included in all copies or substantial portions
of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF
ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED
TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A
PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT
SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY
CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR
IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
DEALINGS IN THE SOFTWARE.
```
