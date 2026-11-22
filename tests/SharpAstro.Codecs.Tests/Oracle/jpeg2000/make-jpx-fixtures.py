"""Write the JPX sample fixtures from the JP2 ones: Fixtures/jpeg2000/jpx-*.jpx.

A JPX file (ISO/IEC 15444-2 Annex M) as a JPX writer lays one out for a single Part 1 picture: the
signature box, a file type box whose brand is 'jpx ' and whose compatibility list is 'jpx ' and
'jpxb' without 'jp2 ' (so a Part 1-only reader is told it may not read it), a reader requirements box
(M.11.1, ML = 1 and no feature listed), then the JP2 header and the codestream unchanged. The samples
therefore decode to exactly the JP2 fixtures' pictures. Run from this directory:

    python make-jpx-fixtures.py
"""
import os
import struct

FIXTURES = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "Fixtures", "jpeg2000")


def box(box_type: bytes, body: bytes) -> bytes:
    return struct.pack(">I", 8 + len(body)) + box_type + body


for source, target in (("jp2-rgb-struct64.jp2", "jpx-rgb-struct64.jpx"), ("jp2-gray-struct64.jp2", "jpx-gray-struct64.jpx")):
    with open(os.path.join(FIXTURES, source), "rb") as f:
        data = f.read()
    signature = data[:12]
    if data[16:20] != b"ftyp":
        raise SystemExit(f"{source}: the file type box does not follow the signature")
    rest = data[12 + struct.unpack(">I", data[12:16])[0]:]
    file_type = box(b"ftyp", b"jpx " + struct.pack(">I", 0) + b"jpx " + b"jpxb")
    reader_requirements = box(b"rreq", bytes([1, 0, 0]) + struct.pack(">HH", 0, 0))
    with open(os.path.join(FIXTURES, target), "wb") as f:
        f.write(signature + file_type + reader_requirements + rest)
    print(target)
