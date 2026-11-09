#!/usr/bin/env bash
# Fetch HEVC conformance bitstreams (JCT-VC, ITU-T H.265.1) that are NOT committed to this repo (issue #66).
#
# They exercise what x265 cannot write (tiles, dependent slices, PCM, CRA/BLA pictures, unequal luma and
# chroma bit depths, the range-extension tools) and cross-check what it can. They are third-party data
# with no licence stated for redistribution here, and this repo is Unlicense, so like the Canon files they
# are downloaded on demand into Fixtures/hevc-conformance/ (git-ignored, copied to the test output by the
# Fixtures/** glob) and the tests that use them skip when they are absent. Only each stream's first picture
# is decoded, and the oracle is ffmpeg's decode of that picture: the md5s that ship with the streams cover
# whole sequences, mostly inter-coded, which this decoder does not read.
#
# Each zip is pinned by SHA-256, taken at pin time (2026-11-09). A mismatch means the upstream file changed,
# and a test whose input can change underneath it proves nothing, so the script stops.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="$HERE/Fixtures/hevc-conformance"
BASE="https://www.itu.int/wftp3/av-arch/jctvc-site/bitstream_exchange/draft_conformance"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$OUT"
command -v python3 > /dev/null || { echo "[hevc conformance] need python3 (to read the zips)" >&2; exit 1; }

# name | directory | sha256 of the zip
STREAMS=(
    "ADJUST_IPRED_ANGLE_A_RExt_Mitsubishi_2|RExt|8c915b0554265ccd6fb1347b41788006df9e7c66349fdeaa142d953340b12997"
    "Bitdepth_A_RExt_Sony_1|RExt|34967e2047d59b0f3e96e7501712a056c01f369501d812b170c717ce1d417368"
    "Bitdepth_B_RExt_Sony_1|RExt|6f5f16377e9287ced1ad06a9493249ed1d61e393d779b06e777a92e14b6c6eb6"
    "CCP_10bit_RExt_QCOM_1|RExt|190ab9b4a9aadee32ecfb639223156a3dee721dbba064c28574e176ce07ff032"
    "CCP_8bit_RExt_QCOM_1|RExt|e5a0216c23f3816107441366b7743160bd0e6a39ce15b14a9fe1a3ffea38bf88"
    "CONFWIN_A_Sony_1|HEVC_v1|c3c823ffb9e526f5d9506ba2f1239b1f0323572850789230d762f4e4678fc298"
    "DBLK_A_SONY_3|HEVC_v1|ce18cd42375359b9ff293d12a95793cc6b56eb75d899ad012c68e25dfa54dfbf"
    "DBLK_B_SONY_3|HEVC_v1|cfdfab2f022aa13d218ba96c1315626c27b68e4f38107f05cbe158f39ab4e409"
    "DBLK_D_VIXS_2|HEVC_v1|1b9c3dec3685760cac19fd8667510b34fdeaaccfb5b137803ea859233a2bdd7e"
    "DSLICE_A_HHI_5|HEVC_v1|c41c1a1e1e700af201eaf4cb0d11d4361b41ba0828e80c78d1835d1de187f80c"
    "DSLICE_B_HHI_5|HEVC_v1|993b933ac6801899b6f04ae5dd2c20fbc2498fd9710a924611e66de2f690716b"
    "DSLICE_C_HHI_5|HEVC_v1|6836566f0a4465775b4757b84d7a89f453b635eba2b941ae07132f7c7268c116"
    "ENTP_C_Qualcomm_1|HEVC_v1|6cfdfe2f6ba8e1a860909550e5a486e7bf712fed00674869f45c1d82d05833f5"
    "EXTPREC_MAIN_444_16_INTRA_8BIT_RExt_Sony_1|RExt|7ebc6db0e9bfbceeef1f168843e824cdd2b3a672c9574eceea9ada8f41a37a37"
    "GENERAL_10b_422_RExt_Sony_1|RExt|c22c21e0dbe5630fbb009b93799eb1caf7f0f7b445135684846194ef9713fe4d"
    "GENERAL_12b_400_RExt_Sony_1|RExt|8524fa46aee7066ca77f08092f799572809b01e0d5f35c73286cbd0d414d0bf3"
    "GENERAL_8b_400_RExt_Sony_1|RExt|64d8eda9ecd8a86acbd9d91fe6e83927a8a155e8d13c891770f1d0d2a1792980"
    "GENERAL_8b_420_RExt_Sony_1|RExt|a3121dd9a59902b4517a395e91a48337d9fc11f4caf60208dbdf9c43fdd6c2db"
    "GENERAL_8b_444_RExt_Sony_2|RExt|dd810f2b7e47e2dd4f516599b2a366084ad47f1af01fe2763caa9ab9ca1233c0"
    "ipcm_A_NEC_3|HEVC_v1|89a71f4b9b7e22c481378f83bacc5aac6c8f204999691d3fa21f2804337e84c9"
    "IPCM_A_RExt_NEC_2|RExt|cc78df0aa70ed8536b002b48ea7feacf72ab703bba7e5286c290d02d1eb51308"
    "ipcm_B_NEC_3|HEVC_v1|36486e9b9cf21ccdd2f1c49cfd6e2806915b732b84e94940553ef501fd197ce6"
    "IPCM_B_RExt_NEC|RExt|b248e35a32f76f3384ddfb3ea7727b7d088ae977b3c649e152cca06149f546ce"
    "ipcm_C_NEC_3|HEVC_v1|12da972f8fcf2d75825535022b7a437d95b1c8272af0b738dc651f34e94c2175"
    "ipcm_D_NEC_3|HEVC_v1|4ef6cdc8fd330d339e1e9e9e04487ba84a21e40a02d111b2ee10c28549bddc94"
    "ipcm_E_NEC_2|HEVC_v1|1d6f8f5d1f08c9bc3f12179bcd6b28a56a7e18500f2c1dfd9799d8b407e78607"
    "NUT_A_ericsson_5|HEVC_v1|caabf3005c37b5ad7497b110f30f04ffb603fc154250058edc23da95b7c73d88"
    "PERSIST_RPARAM_A_RExt_Sony_3|RExt|8237dfe09f09e9ad390c04a30fa57d66f26ab75b9da90c6b482ba1330dcdbebb"
    "PICSIZE_C_Bossen_1|HEVC_v1|d9a3e925af1c1eabc634bdb04a8cc0a48076956b92e1c22e65a89e154309d2ee"
    "PICSIZE_D_Bossen_1|HEVC_v1|bb63815e8817b5a58aefaf235a7b144b3664e4cc3b4f7b7807a9371222356f67"
    "QMATRIX_A_RExt_Sony_1|RExt|45123e91b5d0dc9bffc09735ec532bc23a32bbcdecc1a0c7972301fef09be065"
    "RAP_A_docomo_6|HEVC_v1|5031bac0b1520ed826bf3112d000e59c76677c01dee706738af0d206cde031be"
    "RAP_B_Bossen_2|HEVC_v1|162e16fa34535085f58a3128782a820ae41430281508ab0a7ef036a0d7cf4707"
    "SAO_A_MediaTek_4|HEVC_v1|e0b5c8f4ab4fb592971f06469db630d9e7ef72b4d37d729604e2fdaa9df706c9"
    "SAO_H_Parabola_1|HEVC_v1|c823ce5b11e759970b0e5996f05f8d2e8bd0fa65b461f40dd74df1d9a74ac7c6"
    "SDH_A_Orange_4|HEVC_v1|ddf41877f10b47bad946712954e4a4b35cae435bac187d8c404c6bcfcb4acad0"
    "SLIST_A_Sony_5|HEVC_v1|c51997394e58293b6bedbc5493056fee1b0e7fd41e266468d6a4664b1699b568"
    "STRUCT_A_Samsung_7|HEVC_v1|56a83b66b9e20dbfb20b196797c8136a9c19d3df39d4c1142e0b96e0159b617b"
    "TILES_A_Cisco_2|HEVC_v1|bd63079966105a3bdac764979cb82b708f586cc7c34859a1d466e18cc6a5c345"
    "TILES_B_Cisco_1|HEVC_v1|75b3c9b2981cda59d497750322cde7ca23d6cef39fa9a8e11f36926758b7f83b"
    "TSUNEQBD_A_MAIN10_Technicolor_2|HEVC_v1|615696a911a824988da0d883ab573fb673fd72bf45275d75549abc763602f814"
    "WAVETILES_RExt_Sony_2|RExt|9d78d6b831b3be52f19cc74f5b6c9fd2cabf7d2573741db9a8ad7d0346da30cb"
    "WPP_AND_TILE_AND_CABAC_BYPASS_ALIGN_1_HIGH_TP_444_14BIT_RExt_Apple_2|RExt|a852b1501950e9ce50827fd7e65043511115ffc6aca0e5e68a1b6b25932172b9"
    "WPP_D_ericsson_MAIN_2|HEVC_v1|04c166df6e6d68584910fce77055f6c16428fc5c907bb1befd10bbf40c16b939"
    "WPP_HIGH_TP_444_8BIT_RExt_Apple_2|RExt|fdfd79b04a4faec48e7536e4ffcf692b84b2c5945253a9e8edfa382816b27335"
)

for entry in "${STREAMS[@]}"; do
    IFS='|' read -r name dir sha <<< "$entry"
    target="$OUT/$name.bit"
    if [ -f "$target" ]; then echo "[hevc conformance] $name ok (present)"; continue; fi
    curl -sSfL --max-time 600 "$BASE/$dir/$name.zip" -o "$WORK/$name.zip"
    if ! echo "$sha *$WORK/$name.zip" | sha256sum -c - > /dev/null 2>&1; then
        echo "[hevc conformance] SHA-256 mismatch for $name.zip; refusing to use it." >&2
        exit 1
    fi
    # The one bitstream in the zip (.bit or .bin; a few zips also carry decoded .yuv or traces).
    python3 - "$WORK/$name.zip" "$target.part" << 'PY'
import re, sys, zipfile
archive = zipfile.ZipFile(sys.argv[1])
streams = [n for n in archive.namelist() if re.search(r"\.(bit|bin)$", n, re.I)]
if len(streams) != 1:
    sys.exit(f"expected one bitstream in {sys.argv[1]}, found {streams}")
open(sys.argv[2], "wb").write(archive.read(streams[0]))
PY
    mv "$target.part" "$target"
    echo "[hevc conformance] $name ok"
done
