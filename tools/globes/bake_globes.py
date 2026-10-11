# bake_globes.py [--size 4096] [--normal-size 2048] [planet folder ...]   (default: every planet with an overlay)
#
# The proxy globe's textures (PlanetMesh), from the planet's OWN overlay cubemaps - the maps the real planet draws with
# from orbit (PlanetOverlayDefinition.OverlayColorMetal / OverlayNormalAO, sampled by direction in
# TriplanarCommonSampling.hlsli). The globe's material (PBREmissiveColoredPixel.hlsl, the MapPlanet state) reads them
# differently, so they are re-packed - never re-drawn:
#   <Folder>_globe_cm_<face>.dds  colour: the cubemap face's BC1 colour blocks copied bit for bit into BC3 with a zero
#                                 alpha block. The material takes metalness from the colour alpha - BC1's is 1 (full
#                                 metal: the tan, mirror-like globe); the overlay path forces metal 0 (output.CM.w = 0).
#   <Folder>_globe_nr_<face>.dds  normal + roughness: the overlay's normals are OBJECT space (the +X face points +X);
#                                 turned into the globe mesh's tangent space (PlanetMesh.AddFace: T along +s, B along +t
#                                 on the unit sphere; BRDF/Utils.hlsli RotateByNormalMap, Resolve8BitNormalMap's 127
#                                 midpoint), BC1 (alpha 1 = roughness 1, the overlay's own constant).
# Faces px nx py ny pz nz in the D3D cubemap layout (PlanetMesh.CubePoint), mip chains complete. Written to
# Assets/PlanetGlobes/<Folder>/ (gitignored: regenerated from the game install), compiled into Content by
# D:\aero\tools\build_mod_content.py like any asset.
import glob, json, os, struct, sys, uuid
import numpy as np

GAME = r"C:\Program Files (x86)\Steam\steamapps\common\SpaceEngineers2\GameData\Vanilla\Content\Procedural"
OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "Assets", "PlanetGlobes")
FACES = ["px", "nx", "py", "ny", "pz", "nz"]
BC1_UNORM, BC1_SRGB, BC3_SRGB, BC7_UNORM, BC7_SRGB = 71, 72, 78, 98, 99
def block_of(fmt): return 16 if fmt in (BC7_UNORM, BC7_SRGB, BC3_SRGB) else 8

# ── DDS ───────────────────────────────────────────────────────────────────────────────────────────────────────────

def read_dds(path):
    """(width, mips, faces, dxgi format or fourcc, data offset)."""
    with open(path, "rb") as f: h = f.read(148)
    w, mips = struct.unpack("<I", h[16:20])[0], max(1, struct.unpack("<I", h[28:32])[0])
    fourcc = h[84:88]
    cube = struct.unpack("<I", h[112:116])[0] & 0x200
    if fourcc == b"DX10":
        fmt, misc = struct.unpack("<I", h[128:132])[0], struct.unpack("<I", h[136:140])[0]
        return w, mips, 6 if (misc & 4) else 1, fmt, 148
    return w, mips, 6 if cube else 1, {b"DXT1": BC1_UNORM}.get(fourcc, fourcc), 128

def level_bytes(w, block):
    b = max(1, (w + 3) // 4)
    return b * b * block

def surface(path, face, mip):
    """The raw blocks (BC1 or BC7) of one face's mip."""
    w, mips, faces, fmt, off = read_dds(path)
    bs = block_of(fmt)
    per_face = sum(level_bytes(w >> m, bs) for m in range(mips))
    start = off + face * per_face + sum(level_bytes(w >> m, bs) for m in range(mip))
    with open(path, "rb") as f:
        f.seek(start)
        return f.read(level_bytes(w >> mip, bs))

def bc7_decode(buf, w, fmt):
    """float32 (w, w, 3) 0..255 of BC7 blocks (Pillow's decoder, on a one-surface DDS made for it)."""
    import io
    from PIL import Image
    hdr = struct.pack("<4sIIIIIII", b"DDS ", 124, 0x1 | 0x2 | 0x4 | 0x1000 | 0x80000, w, w, len(buf), 0, 1) + b"\0" * 44
    hdr += struct.pack("<II4s5I", 32, 0x4, b"DX10", 0, 0, 0, 0, 0) + struct.pack("<5I", 0x1000, 0, 0, 0, 0)
    hdr += struct.pack("<5I", fmt, 3, 0, 1, 0)
    im = Image.open(io.BytesIO(hdr + buf)); im.load()
    return np.asarray(im.convert("RGB")).astype(np.float32)

def decode(path, face, mip):
    """A face's mip as float RGB 0..255, whatever its format (BC1 or BC7)."""
    w, mips, faces, fmt, off = read_dds(path)
    buf = surface(path, face, mip)
    return bc7_decode(buf, w >> mip, fmt) if fmt in (BC7_UNORM, BC7_SRGB) else bc1_decode(buf, w >> mip)

def write_dds(path, w, fmt, levels):
    write_meta(path, os.path.basename(path))
    flags = 0x1 | 0x2 | 0x4 | 0x1000 | 0x20000 | 0x80000
    h = struct.pack("<4sIIIIIII", b"DDS ", 124, flags, w, w, len(levels[0]), 0, len(levels)) + b"\0" * 44
    h += struct.pack("<II4s5I", 32, 0x4, b"DX10", 0, 0, 0, 0, 0)
    h += struct.pack("<5I", 0x1000 | 0x400000 | 0x8, 0, 0, 0, 0)
    h += struct.pack("<5I", fmt, 3, 0, 1, 0)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(h)
        for l in levels: f.write(l)

# ── BC1 ───────────────────────────────────────────────────────────────────────────────────────────────────────────

def bc1_decode(buf, w):
    """float32 (w, w, 3) in 0..255."""
    nb = max(1, w // 4)
    a = np.frombuffer(buf, np.uint8).reshape(nb * nb, 8)
    c0 = a[:, 0].astype(np.uint32) | a[:, 1].astype(np.uint32) << 8
    c1 = a[:, 2].astype(np.uint32) | a[:, 3].astype(np.uint32) << 8
    def rgb(c): return np.stack([(c >> 11 & 31) * 255 / 31, (c >> 5 & 63) * 255 / 63, (c & 31) * 255 / 31], 1).astype(np.float32)
    p0, p1 = rgb(c0), rgb(c1); four = (c0 > c1)[:, None]
    pal = np.stack([p0, p1, np.where(four, (2 * p0 + p1) / 3, (p0 + p1) / 2), np.where(four, (p0 + 2 * p1) / 3, 0)], 1)
    idx = a[:, 4:8].copy().view("<u4")[:, 0]
    sel = np.stack([(idx >> (2 * i)) & 3 for i in range(16)], 1)
    px = pal[np.arange(nb * nb)[:, None], sel]
    out = px.reshape(nb, nb, 4, 4, 3).transpose(0, 2, 1, 3, 4).reshape(nb * 4, nb * 4, 3)
    return out[:w, :w]

def bc1_encode(img):
    """img float (w, w, 3) 0..255 -> BC1 blocks (4-colour mode: alpha 1). Endpoints: the block's extremes along its
    principal axis, nearest-palette indices."""
    w = img.shape[0]
    if w < 4: img = np.pad(img, ((0, 4 - w), (0, 4 - w), (0, 0)), mode="edge"); w = 4
    nb = w // 4
    px = img.reshape(nb, 4, nb, 4, 3).transpose(0, 2, 1, 3, 4).reshape(nb * nb, 16, 3)
    mean = px.mean(1, keepdims=True)
    d = px - mean
    cov = np.einsum("nki,nkj->nij", d, d)
    axis = np.ones((nb * nb, 3), np.float32)
    for _ in range(6):   # power iteration
        axis = np.einsum("nij,nj->ni", cov, axis)
        axis /= np.maximum(np.linalg.norm(axis, axis=1, keepdims=True), 1e-9)
    proj = np.einsum("nki,ni->nk", d, axis)
    lo = mean[:, 0] + axis * proj.min(1, keepdims=True)
    hi = mean[:, 0] + axis * proj.max(1, keepdims=True)
    def q(c):
        c = np.clip(c, 0, 255)
        r, g, b = np.round(c[:, 0] * 31 / 255), np.round(c[:, 1] * 63 / 255), np.round(c[:, 2] * 31 / 255)
        return (r.astype(np.uint32) << 11) | (g.astype(np.uint32) << 5) | b.astype(np.uint32)
    c0, c1 = q(hi), q(lo)
    sw = c0 < c1
    c0, c1 = np.where(sw, c1, c0), np.where(sw, c0, c1)
    def rgb(c): return np.stack([(c >> 11 & 31) * 255 / 31, (c >> 5 & 63) * 255 / 63, (c & 31) * 255 / 31], 1).astype(np.float32)
    p0, p1 = rgb(c0), rgb(c1)
    pal = np.stack([p0, p1, (2 * p0 + p1) / 3, (p0 + 2 * p1) / 3], 1)
    dist = ((px[:, :, None, :] - pal[:, None, :, :]) ** 2).sum(3)
    sel = dist.argmin(2).astype(np.uint32)
    sel[c0 == c1] = 0   # (a flat block: c0 == c1 would read as 3-colour mode; index 0 is its colour either way)
    idx = np.zeros(nb * nb, np.uint32)
    for i in range(16): idx |= sel[:, i] << (2 * i)
    out = np.zeros((nb * nb, 8), np.uint8)
    out[:, 0], out[:, 1], out[:, 2], out[:, 3] = c0 & 255, c0 >> 8, c1 & 255, c1 >> 8
    out[:, 4:8] = idx.view(np.uint8).reshape(-1, 4)
    return out.tobytes()

def bc1_to_bc3(buf):
    """Each BC1 colour block behind an all-zero BC3 alpha block (alpha 0). BC3 always reads its colour block in
    4-colour mode: exact for c0 > c1, and for c0 == c1 every palette entry is that one colour. A true 3-colour BC1
    block (c0 < c1: a half-way entry, no 4-colour equivalent) is decoded and re-encoded - the game's overlays have
    none (Verdure: 0.6% blocks with c0 == c1, none below)."""
    a = np.frombuffer(buf, np.uint8).reshape(-1, 8).copy()
    c0 = a[:, 0].astype(np.uint32) | a[:, 1].astype(np.uint32) << 8
    c1 = a[:, 2].astype(np.uint32) | a[:, 3].astype(np.uint32) << 8
    bad = np.nonzero(c0 < c1)[0]
    for k in bad:
        a[k] = np.frombuffer(bc1_encode(bc1_decode(a[k].tobytes(), 4)), np.uint8)
    out = np.zeros((len(a), 16), np.uint8)
    out[:, 8:] = a
    return out.tobytes(), len(bad)

# ── geometry (PlanetMesh.CubePoint: the D3D cubemap layout) ───────────────────────────────────────────────────────

def cube_point(f, s, t):
    a, b = 2 * s - 1, 2 * t - 1
    o = np.ones_like(a)
    return [np.stack(v, -1) for v in (
        (o, -b, -a), (-o, -b, a), (a, o, b), (a, -o, -b), (a, -b, o), (-a, -b, -o))][f]

def cube_dirs(f):
    """d(CubePoint)/ds and /dt of face f (constant)."""
    z = np.zeros(1); p = cube_point(f, z, z)[0]
    return cube_point(f, z + 1, z)[0] - p, cube_point(f, z, z + 1)[0] - p

def tangent_normals(nao_path, face, size):
    """The face's object-space overlay normals at `size`, in the globe's tangent space: float (size, size, 3)."""
    w, mips = read_dds(nao_path)[:2]
    mip = int(round(np.log2(w / size)))
    if mip < 0: mip = 0
    src = decode(nao_path, face, mip)
    if src.shape[0] != size:   # (a source smaller than asked: nearest up-sample)
        k = size // src.shape[0]; src = src.repeat(k, 0).repeat(k, 1)
    n = src / 255 * 2 - 1   # (SampleCubemapNormalOverlay: plain *2-1, length = AO)
    n /= np.maximum(np.linalg.norm(n, axis=2, keepdims=True), 1e-6)
    c = (np.arange(size) + 0.5) / size
    s, t = np.meshgrid(c, c)   # row = t, column = s
    N0 = cube_point(face, s, t); N0 /= np.linalg.norm(N0, axis=2, keepdims=True)
    ds, dt = cube_dirs(face)
    T = ds - N0 * (N0 @ ds)[..., None]; T /= np.linalg.norm(T, axis=2, keepdims=True)
    B = np.cross(N0, T)
    B *= np.sign((B @ dt))[..., None]   # B along +t, as the mesh's tangent w makes it
    return np.stack([(n * T).sum(2), (n * B).sum(2), (n * N0).sum(2)], 2)

def encode_normal(tn):
    """Tangent-space unit normals -> 0..255 texel values (Resolve8BitNormalMap's inverse)."""
    x = (tn[..., 0] + 1) * 127; y = (tn[..., 1] + 1) * 127; z = (tn[..., 2] + 1) * 127.5
    return np.stack([x, y, z], 2)

# ── planets ───────────────────────────────────────────────────────────────────────────────────────────────────────

def overlays():
    """{folder: (colour cubemap, normal cubemap)} from the planets' PlanetOverlayDefinitions."""
    meta_index = {}
    for m in glob.glob(os.path.join(GAME, "*", "Planets", "*", "Maps", "Overlay", "*.dds")):
        meta_index[os.path.basename(m).lower()] = m
    found = {}
    for d in glob.glob(os.path.join(GAME, "*", "Planets", "*", "Data", "*.def")):
        try: t = open(d, encoding="utf-8-sig").read()
        except OSError: continue
        if "PlanetOverlayDefinitionObjectBuilder" not in t: continue
        folder = d.replace("\\", "/").split("/Planets/")[1].split("/")[0]
        # the cubemaps themselves: the Overlay folder's cube textures - the colour sRGB, the normal linear (BC1 or BC7)
        cm = nao = None
        for p in glob.glob(os.path.join(os.path.dirname(os.path.dirname(d)), "Maps", "Overlay", "*.dds")):
            w, mips, faces, fmt, _ = read_dds(p)
            if faces != 6: continue
            if fmt in (BC1_SRGB, BC7_SRGB): cm = p
            elif fmt in (BC1_UNORM, BC7_UNORM): nao = p
        if cm: found[folder] = (cm, nao)
    return found

META_VER = "2.4.0.16"

def write_meta(path, name=None):
    """The content pipeline's metafile: a file copied as is (P_DEFAULT, as the engine's own .dds), or a folder.
    Asset ids are stable (a hash of the path) so a re-bake keeps them."""
    rel = os.path.relpath(path, os.path.dirname(OUT)).replace("\\", "/").lower()
    items = []
    if name:
        items = [{"$Type": "VRage:Keen.VRage.ContentPipeline.Project.Structure.FileInfoMetaDataItem", "FileHash": ""},
                 {"$Type": "VRage:Keen.VRage.ContentPipeline.Project.Features.AssetProcessing.AssetProcessingMetaDataItem",
                  "ProcessorInfo": {"Version": 0, "Identifier": "P_DEFAULT"},
                  "ContentFiles": {"MainOutput": {"FileHash": "", "FileName": name}, "AdditionalOutputs": []},
                  "Problems": [], "DependenciesHashes": [], "AssetHash": "", "OverrideDetachedState": True, "IgnoredErrors": "None"}]
    m = {"$Bundles": {"System.Runtime": "1.0.0.0", "VRage": META_VER},
         "$Type": "VRage:Keen.VRage.ContentPipeline.Metafiles.MetaData",
         "$Value": {"AssetID": str(uuid.uuid5(uuid.NAMESPACE_URL, "orbitalmod/" + rel)), "InternalItems": items}}
    with open(path + ".meta", "w", encoding="utf-8", newline="\n") as f: json.dump(m, f, indent=2)

def bake(folder, cm, nao, size, nsize):
    out = os.path.join(OUT, folder)
    os.makedirs(out, exist_ok=True)
    write_meta(os.path.join(os.path.dirname(OUT), "planetglobes"))
    write_meta(os.path.join(OUT, folder.lower()))
    w, mips, _, cfmt, _ = read_dds(cm)
    top = max(0, int(round(np.log2(w / size))))
    fixed = 0
    for f, name in enumerate(FACES):
        levels = []
        if cfmt == BC7_SRGB:
            # BC7 carries its own alpha (the game's: metal ~0 already): the blocks copied as they are
            for m in range(top, mips): levels.append(surface(cm, f, m))
            write_dds(os.path.join(out, f"{folder}_globe_cm_{name}.dds"), w >> top, BC7_SRGB, levels)
        else:
            for m in range(top, mips):
                b, n = bc1_to_bc3(surface(cm, f, m)); levels.append(b); fixed += n
            write_dds(os.path.join(out, f"{folder}_globe_cm_{name}.dds"), w >> top, BC3_SRGB, levels)
        if nao:
            tn = tangent_normals(nao, f, nsize)
            levels, cur = [], tn
            while True:
                levels.append(bc1_encode(encode_normal(cur / np.maximum(np.linalg.norm(cur, axis=2, keepdims=True), 1e-6))))
                if cur.shape[0] == 1: break
                k = cur.shape[0] // 2
                cur = cur.reshape(k, 2, k, 2, 3).mean((1, 3))
            write_dds(os.path.join(out, f"{folder}_globe_nr_{name}.dds"), nsize, BC1_UNORM, levels)
        print(f"  {folder} {name}: colour {w >> top}, normal {nsize if nao else '-'}", flush=True)
    print(f"{folder}: {len(FACES)} faces from {os.path.basename(cm)}{' + ' + os.path.basename(nao) if nao else ''}"
          f" ({fixed} 3-colour block(s) re-encoded)")

def main(argv):
    size, nsize, want = 4096, 2048, []
    i = 0
    while i < len(argv):
        if argv[i] == "--size": size = int(argv[i + 1]); i += 2
        elif argv[i] == "--normal-size": nsize = int(argv[i + 1]); i += 2
        else: want.append(argv[i]); i += 1
    found = overlays()
    if not want or want == ["all"]: want = sorted(found)
    for folder in want:
        if folder not in found: print(f"{folder}: no overlay cubemap (have: {', '.join(sorted(found))})"); continue
        bake(folder, *found[folder], size, nsize)

if __name__ == "__main__":
    main(sys.argv[1:])
