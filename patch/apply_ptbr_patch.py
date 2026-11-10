#!/usr/bin/env python3
"""
apply_ptbr_patch.py - The Legend of Zelda: The Wind Waker HD (PT-BR)
A completely self-contained patcher requiring only Python standard library.
Applies Portuguese (Brazil) translation (5,040 messages across 68 archives)
and synthesizes accented font glyphs onto permanent_2d_UsEnglish.pack.
"""

import os
import sys
import json
import struct
import argparse
import time
import re

# ---------------------------------------------------------
# SARC Archive Handling
# ---------------------------------------------------------

def parse_sarc(b: bytes):
    e = ">" if b[6:8] == b"\xfe\xff" else "<"
    hl = struct.unpack_from(e + "H", b, 4)[0]
    doff = struct.unpack_from(e + "I", b, 0xC)[0]
    n = struct.unpack_from(e + "H", b, hl + 6)[0]
    sfnt = hl + 12 + 16 * n
    members = {}
    for i in range(n):
        h, attr, s, t = struct.unpack_from(e + "IIII", b, hl + 12 + 16 * i)
        name = "%08x" % h
        if attr & 0x01000000:
            off = sfnt + 8 + (attr & 0xFFFF) * 4
            name = b[off:b.index(b"\0", off)].decode("ascii", errors="replace")
        members[name] = b[doff + s : doff + t]
    return members

def rebuild_inner_sarc(orig_sarc: bytes, new_member_bytes: bytes) -> bytes:
    e = ">" if orig_sarc[6:8] == b"\xfe\xff" else "<"
    doff = struct.unpack_from(e + "I", orig_sarc, 0x0C)[0]
    hdr = bytearray(orig_sarc[:doff])
    total_size = doff + len(new_member_bytes)
    struct.pack_into(e + "I", hdr, 0x08, total_size)
    struct.pack_into(e + "I", hdr, 32 + 12, len(new_member_bytes))
    return bytes(hdr) + new_member_bytes

def rebuild_main_pack_sarc(orig_pack_bytes: bytes, replaced_members: dict) -> bytes:
    e = ">" if orig_pack_bytes[6:8] == b"\xfe\xff" else "<"
    hl = struct.unpack_from(e + "H", orig_pack_bytes, 4)[0]
    doff = struct.unpack_from(e + "I", orig_pack_bytes, 0x0C)[0]
    n = struct.unpack_from(e + "H", orig_pack_bytes, hl + 6)[0]
    sfnt = hl + 12 + 16 * n

    hdr = bytearray(orig_pack_bytes[:doff])
    data_chunks = bytearray()

    for i in range(n):
        entry_off = hl + 12 + 16 * i
        h, attr, s, t = struct.unpack_from(e + "IIII", orig_pack_bytes, entry_off)
        name = "%08x" % h
        if attr & 0x01000000:
            noff = sfnt + 8 + (attr & 0xFFFF) * 4
            name = orig_pack_bytes[noff:orig_pack_bytes.index(b"\0", noff)].decode("ascii")

        if i > 0:
            pad_len = (-len(data_chunks)) % 0x2000
            if pad_len:
                data_chunks += b"\x00" * pad_len

        new_s = len(data_chunks)
        body = replaced_members.get(name, orig_pack_bytes[doff + s : doff + t])
        data_chunks += body
        new_t = len(data_chunks)

        struct.pack_into(e + "II", hdr, entry_off + 8, new_s, new_t)

    total_size = doff + len(data_chunks)
    struct.pack_into(e + "I", hdr, 0x08, total_size)
    return bytes(hdr) + bytes(data_chunks)

# ---------------------------------------------------------
# Yaz0 Compression & Decompression
# ---------------------------------------------------------

def decompress_yaz0(b: bytes) -> bytes:
    if len(b) < 16 or b[:4] != b"Yaz0":
        return b""
    n = struct.unpack_from(">I", b, 4)[0]
    out, at = bytearray(), 16
    try:
        while len(out) < n:
            code = b[at]
            at += 1
            for bit in range(7, -1, -1):
                if len(out) >= n:
                    break
                if code & (1 << bit):
                    out.append(b[at])
                    at += 1
                else:
                    first, second = b[at], b[at + 1]
                    at += 2
                    distance = ((first & 15) << 8 | second) + 1
                    length = first >> 4
                    if not length:
                        length = b[at] + 18
                        at += 1
                    else:
                        length += 2
                    if distance > len(out):
                        return b""
                    for _ in range(min(length, n - len(out))):
                        out.append(out[-distance])
    except IndexError:
        return b""
    return bytes(out)

def compress_yaz0(data: bytes) -> bytes:
    n = len(data)
    out = bytearray(b"Yaz0" + struct.pack(">I", n) + b"\x00" * 8)
    pos = 0
    while pos < n:
        code_pos = len(out)
        out.append(0)
        code = 0
        for bit in range(7, -1, -1):
            if pos >= n:
                break
            max_len = min(0x111, n - pos)
            best_len = 0
            best_dist = 0
            if max_len >= 3 and pos > 0:
                win_start = max(0, pos - 0x1000)
                idx = data.rfind(data[pos:pos+3], win_start, pos)
                tries = 0
                while idx != -1 and tries < 16:
                    ml = 3
                    while ml < max_len and data[idx + ml] == data[pos + ml]:
                        ml += 1
                    if ml > best_len:
                        best_len = ml
                        best_dist = pos - idx
                        if best_len == max_len:
                            break
                    if idx <= win_start:
                        break
                    idx = data.rfind(data[pos:pos+best_len], win_start, idx + best_len - 1)
                    tries += 1
            if best_len >= 3:
                dist = best_dist - 1
                if best_len < 0x12:
                    out.append(((best_len - 2) << 4) | (dist >> 8))
                    out.append(dist & 0xFF)
                else:
                    out.append(dist >> 8)
                    out.append(dist & 0xFF)
                    out.append(best_len - 0x12)
                pos += best_len
            else:
                code |= (1 << bit)
                out.append(data[pos])
                pos += 1
        out[code_pos] = code
    return bytes(out)

# ---------------------------------------------------------
# MSBT Parser & Rebuilder
# ---------------------------------------------------------

def encode_msbt_tokens(serialized: str, endian: str) -> bytes:
    """Restore serialized MSBT controls, including group, type and parameter length."""
    encoding = "utf-16-be" if endian == ">" else "utf-16-le"
    out = bytearray()
    cursor = 0

    def append_text(text):
        if any(token in text for token in ("{{", "}}", "[0x0E:", "\x00", "\x0e")):
            raise ValueError("Malformed or raw MSBT control in translated text")
        out.extend(text.encode(encoding))

    for match in re.finditer(r"\{\{TAG:(\d+):(\d+):([0-9a-fA-F]*)\}\}", serialized):
        append_text(serialized[cursor:match.start()])
        group, kind = int(match[1]), int(match[2])
        params = bytes.fromhex(match[3])
        if max(group, kind, len(params)) > 0xffff:
            raise ValueError("MSBT control exceeds 16-bit field")
        out.extend(struct.pack(endian + "HHHH", 0x000E, group, kind, len(params)))
        out.extend(params)
        cursor = match.end()
    append_text(serialized[cursor:])
    out.extend(b"\x00\x00")
    return bytes(out)

def parse_msbt_file(raw_bytes: bytes) -> dict:
    endian = ">" if raw_bytes[8:10] == b"\xfe\xff" else "<"
    num_sections = struct.unpack_from(endian + "H", raw_bytes, 0x0E)[0]

    sections = []
    messages = []
    lbl1_labels = []

    pos = 0x20
    for _ in range(num_sections):
        magic = raw_bytes[pos:pos+4].decode("ascii")
        sec_size = struct.unpack_from(endian + "I", raw_bytes, pos+4)[0]
        hdr_rest = raw_bytes[pos+8:pos+16]
        body = raw_bytes[pos+16:pos+16+sec_size]
        sections.append({
            "magic": magic,
            "hdr_rest": hdr_rest,
            "body": body,
            "pad_byte": raw_bytes[pos+16+sec_size : pos+16+sec_size+1] or b"\xab"
        })

        if magic == "LBL1":
            num_groups = struct.unpack_from(endian + "I", body, 0)[0]
            for g in range(num_groups):
                count, grp_off = struct.unpack_from(endian + "II", body, 4 + g * 8)
                cur = grp_off
                for _ in range(count):
                    slen = body[cur]
                    lbl = body[cur + 1 : cur + 1 + slen].decode("ascii")
                    idx = struct.unpack_from(endian + "I", body, cur + 1 + slen)[0]
                    lbl1_labels.append((idx, lbl))
                    cur += 1 + slen + 4
        pos = pos + 16 + sec_size
        pos += (16 - (pos % 16)) % 16

    lbl1_labels.sort(key=lambda x: x[0])
    for idx, lbl in lbl1_labels:
        messages.append({"index": idx, "label": lbl})

    return {
        "endian": endian,
        "header_prefix": raw_bytes[:0x12],
        "header_suffix": raw_bytes[0x16:0x20],
        "sections": sections,
        "messages": messages
    }

def build_msbt_file(parsed_msbt: dict, replacement_dict: dict) -> bytes:
    endian = parsed_msbt["endian"]
    messages = parsed_msbt["messages"]
    num_msgs = len(messages)

    encoded_msgs = []
    for m in messages:
        lbl = m["label"]
        txt = replacement_dict[lbl]
        encoded_msgs.append(encode_msbt_tokens(txt, endian))

    header_size = 4 + 4 * num_msgs
    cur_off = header_size
    offs_table = bytearray(struct.pack(endian + "I", num_msgs))
    msg_blob = bytearray()
    for em in encoded_msgs:
        offs_table += struct.pack(endian + "I", cur_off)
        msg_blob += em
        cur_off += len(em)
    new_txt2_body = bytes(offs_table + msg_blob)

    sec_blobs = bytearray()
    for sec in parsed_msbt["sections"]:
        magic = sec["magic"]
        body = new_txt2_body if magic == "TXT2" else sec["body"]
        sec_hdr = magic.encode("ascii") + struct.pack(endian + "I", len(body)) + sec["hdr_rest"]
        sec_blobs += sec_hdr + body
        rem = (16 - (len(sec_blobs) % 16)) % 16
        sec_blobs += sec["pad_byte"] * rem

    total_size = 0x20 + len(sec_blobs)
    file_hdr = parsed_msbt["header_prefix"] + struct.pack(endian + "I", total_size) + parsed_msbt["header_suffix"]
    return bytes(file_hdr + sec_blobs)

# ---------------------------------------------------------
# BFFNT Font Patching & BC4 Encoding
# ---------------------------------------------------------

def tiled_address(x, y, pitch, bpp, mode, swizzle):
    b = lambda v, i: (v >> i) & 1
    bits = (b(x, 0), b(y, 0), b(x, 1), b(x, 2), b(y, 1), b(y, 2))
    pix = sum(v << i for i, v in enumerate(bits)) * bpp // 8
    pipe = ((y >> 3) ^ (x >> 3)) & 1
    bank = (y >> 4) & 3
    bank = ((bank >> 1) | (bank << 1)) & 3
    bank = (bank ^ (x >> 3)) & 3
    bank_pipe = ((pipe + 2 * bank) ^ (((swizzle >> 8) & 1) + 2 * ((swizzle >> 9) & 3))) % 8
    pipe, bank = bank_pipe % 2, bank_pipe // 2
    macro_bytes = 32 * 16 * bpp // 8
    off = ((((x // 32) + (pitch // 32) * (y // 16)) * macro_bytes) >> 3) + pix
    off = ((off & ~0xFF) << 3) | (off & 0xFF)
    return off | (pipe << 8) | (bank << 9)

def bc3_alpha(block):
    a0, a1 = block[0], block[1]
    bits = int.from_bytes(block[2:8], "little")
    if a0 > a1:
        pal = [a0, a1] + [((7 - i) * a0 + i * a1) // 7 for i in range(1, 7)]
    else:
        pal = [a0, a1] + [((5 - i) * a0 + i * a1) // 5 for i in range(1, 5)] + [0, 255]
    return [pal[(bits >> (3 * i)) & 7] for i in range(16)]

def encode_bc4_block(pixels_16):
    """Encode 16 8-bit alpha values (4x4 block) into an 8-byte BC4 block."""
    a0 = max(pixels_16)
    a1 = min(pixels_16)
    if a0 == a1:
        return bytes([a0, a1, 0, 0, 0, 0, 0, 0])
    # 8-value interpolated palette (a0 > a1)
    pal = [a0, a1] + [((7 - i) * a0 + i * a1) // 7 for i in range(1, 7)]
    bits = 0
    for i, px in enumerate(pixels_16):
        best_idx = min(range(8), key=lambda k: abs(pal[k] - px))
        bits |= (best_idx << (3 * i))
    return bytes([a0, a1]) + bits.to_bytes(6, "little")

def decode_sheet(sheet_bytes: bytes, w: int = 1024, h: int = 1024, swizzle: int = 0) -> bytearray:
    wb, hb = w // 4, h // 4
    img = bytearray(w * h)
    for by in range(hb):
        for bx in range(wb):
            at = tiled_address(bx, by, wb, 64, 4, swizzle)
            alpha = bc3_alpha(sheet_bytes[at:at+8])
            for i, a in enumerate(alpha):
                x, y = bx * 4 + (i & 3), by * 4 + (i >> 2)
                img[y * w + x] = a
    return img

def encode_sheet_region(sheet_bytes: bytearray, img: bytearray, x0: int, y0: int, x1: int, y1: int, w: int = 1024, swizzle: int = 0):
    """Re-encode only the modified 4x4 blocks covering [x0..x1, y0..y1] into sheet_bytes."""
    wb = w // 4
    bx0, bx1 = max(0, x0 // 4), min(wb - 1, x1 // 4)
    by0, by1 = max(0, y0 // 4), min((w // 4) - 1, y1 // 4)
    for by in range(by0, by1 + 1):
        for bx in range(bx0, bx1 + 1):
            px16 = []
            for i in range(16):
                x, y = bx * 4 + (i & 3), by * 4 + (i >> 2)
                px16.append(img[y * w + x])
            blk = encode_bc4_block(px16)
            at = tiled_address(bx, by, wb, 64, 4, swizzle)
            sheet_bytes[at:at+8] = blk

def parse_bffnt_maps(b: bytes):
    off = 0x14
    cwdh_off = None
    cmap_offs = []
    while off < len(b):
        mag = b[off:off+4].decode("ascii")
        sz = struct.unpack_from(">I", b, off+4)[0]
        if mag == "TGLP":
            cw, ch, nsh, maxw, ssz, base, fmt, cols, rows, sw, sh, doff = struct.unpack_from(">BBBB I HHHHHH I", b, off+8)
            tglp = {"off": off, "sz": sz, "cw": cw, "ch": ch, "nsh": nsh, "ssz": ssz, "cols": cols, "rows": rows, "sw": sw, "sh": sh, "doff": doff}
        elif mag == "CWDH":
            cwdh_off = off
            s_idx, e_idx, nxt = struct.unpack_from(">HHI", b, off+8)
            widths = [struct.unpack_from(">bBB", b, off+16+3*k) for k in range(e_idx - s_idx + 1)]
        elif mag == "CMAP":
            cmap_offs.append(off)
        off += sz

    cp_to_glyph = {}
    scan_pairs = []
    for coff in cmap_offs:
        c0, c1, method, res, nxt = struct.unpack_from(">HHHHI", b, coff+8)
        doff = coff + 20
        if method == 0:
            idx0 = struct.unpack_from(">H", b, doff)[0]
            for cp in range(c0, c1 + 1):
                cp_to_glyph[cp] = idx0 + (cp - c0)
        elif method == 2:
            cnt = struct.unpack_from(">H", b, doff)[0]
            for k in range(cnt):
                cp, g = struct.unpack_from(">HH", b, doff + 2 + 4 * k)
                cp_to_glyph[cp] = g
                scan_pairs.append((cp, g))
    return tglp, cwdh_off, widths, cmap_offs, cp_to_glyph, scan_pairs

def get_cell_rect(tglp, glyph_idx):
    cells_per_sheet = tglp["cols"] * tglp["rows"]
    sheet_idx = glyph_idx // cells_per_sheet
    rem = glyph_idx % cells_per_sheet
    row = rem // tglp["cols"]
    col = rem % tglp["cols"]
    pitch_x = tglp["cw"] + 1
    pitch_y = tglp["ch"] + 1
    x0 = col * pitch_x
    # Top of row 0 is at y_top = 0 in standard top-down, which is y_gl = 1023 - y_top in bottom-up sheet
    return sheet_idx, x0, row * pitch_y, tglp["cw"], tglp["ch"]

def extract_cell_topdown(sheets_decoded, tglp, glyph_idx):
    sh_idx, x0, y0_td, cw, ch = get_cell_rect(tglp, glyph_idx)
    img = sheets_decoded[sh_idx]
    w = tglp["sw"]
    cell = [[0] * cw for _ in range(ch)]
    for dy in range(ch):
        y_gl = (tglp["sh"] - 1) - (y0_td + dy)
        for dx in range(cw):
            cell[dy][dx] = img[y_gl * w + (x0 + dx)]
    return cell

def write_cell_topdown(sheets_raw, sheets_decoded, tglp, glyph_idx, cell):
    sh_idx, x0, y0_td, cw, ch = get_cell_rect(tglp, glyph_idx)
    img = sheets_decoded[sh_idx]
    w = tglp["sw"]
    y_gl_min = (tglp["sh"] - 1) - (y0_td + ch - 1)
    y_gl_max = (tglp["sh"] - 1) - y0_td
    for dy in range(ch):
        y_gl = (tglp["sh"] - 1) - (y0_td + dy)
        for dx in range(cw):
            img[y_gl * w + (x0 + dx)] = cell[dy][dx]
    swizzle = tglp.get('sheet_swizzles', [0] * tglp['nsh'])[sh_idx]
    encode_sheet_region(sheets_raw[sh_idx], img, x0, y_gl_min, x0 + cw - 1, y_gl_max, w, swizzle)

def extract_accent_cap(base_cell, accented_cell, max_y):
    """Extract pixels in accented_cell above max_y where accented_cell > base_cell."""
    ch = len(base_cell)
    cw = len(base_cell[0])
    cap = [[0] * cw for _ in range(ch)]
    for y in range(min(ch, max_y)):
        for x in range(cw):
            diff = accented_cell[y][x] - base_cell[y][x]
            if diff > 24:
                cap[y][x] = accented_cell[y][x]
    return cap

def shift_and_overlay(target_cell, cap_cell, dx=0, dy=0):
    ch = len(target_cell)
    cw = len(target_cell[0])
    out = [row[:] for row in target_cell]
    for y in range(ch):
        for x in range(cw):
            v = cap_cell[y][x]
            if v > 0:
                ny, nx = y + dy, x + dx
                if 0 <= ny < ch and 0 <= nx < cw:
                    if v > out[ny][nx]:
                        out[ny][nx] = v
    return out

def patch_cking_main(b: bytes) -> bytes:
    tglp, cwdh_off, widths, cmap_offs, cp_to_glyph, scan_pairs = parse_bffnt_maps(b)
    # Runtime LatteAddrLib tile mode 4: slice rotation = 2; bank XOR is page & 3.
    tglp['sheet_swizzles'] = [(page % 4) * 0x200 for page in range(tglp['nsh'])]
    # Decode all sheets
    sheets_raw = []
    sheets_decoded = []
    for s in range(tglp["nsh"]):
        soff = tglp["doff"] + s * tglp["ssz"]
        raw = bytearray(b[soff:soff+tglp["ssz"]])
        sheets_raw.append(raw)
        sheets_decoded.append(decode_sheet(raw, tglp["sw"], tglp["sh"], tglp['sheet_swizzles'][s]))

    # Extract reference cells in CKingMain:
    # Existing in CKingMain: a, e, i, o, u, c, A, E, I, O, U, C, á, à, â, é, ê, í, ó, ç, Á, É, Î, ~
    cell_a = extract_cell_topdown(sheets_decoded, tglp, cp_to_glyph[ord("a")])
    cell_e = extract_cell_topdown(sheets_decoded, tglp, cp_to_glyph[ord("e")])
    cell_o = extract_cell_topdown(sheets_decoded, tglp, cp_to_glyph[ord("o")])
    cell_u = extract_cell_topdown(sheets_decoded, tglp, cp_to_glyph[ord("u")])
    cell_c = extract_cell_topdown(sheets_decoded, tglp, cp_to_glyph[ord("c")])
    cell_A = extract_cell_topdown(sheets_decoded, tglp, cp_to_glyph[ord("A")])
    cell_E = extract_cell_topdown(sheets_decoded, tglp, cp_to_glyph[ord("E")])
    cell_I = extract_cell_topdown(sheets_decoded, tglp, cp_to_glyph[ord("I")])
    cell_O = extract_cell_topdown(sheets_decoded, tglp, cp_to_glyph[ord("O")])
    cell_U = extract_cell_topdown(sheets_decoded, tglp, cp_to_glyph[ord("U")])
    cell_C = extract_cell_topdown(sheets_decoded, tglp, cp_to_glyph[ord("C")])

    cell_a_acute = extract_cell_topdown(sheets_decoded, tglp, cp_to_glyph[ord("á")])
    cell_a_grave = extract_cell_topdown(sheets_decoded, tglp, cp_to_glyph[ord("à")])
    cell_a_circ  = extract_cell_topdown(sheets_decoded, tglp, cp_to_glyph[ord("â")])
    cell_c_ced   = extract_cell_topdown(sheets_decoded, tglp, cp_to_glyph[ord("ç")])
    cell_A_acute = extract_cell_topdown(sheets_decoded, tglp, cp_to_glyph[ord("Á")])
    cell_I_circ  = extract_cell_topdown(sheets_decoded, tglp, cp_to_glyph[ord("Î")])
    cell_tilde   = extract_cell_topdown(sheets_decoded, tglp, cp_to_glyph[ord("~")])
    cell_ord_m   = extract_cell_topdown(sheets_decoded, tglp, cp_to_glyph[ord("º")])

    # Extract diacritic caps
    cap_acute_lo = extract_accent_cap(cell_a, cell_a_acute, 32)
    cap_grave_lo = extract_accent_cap(cell_a, cell_a_grave, 32)
    cap_circ_lo  = extract_accent_cap(cell_a, cell_a_circ, 32)
    cap_acute_hi = extract_accent_cap(cell_A, cell_A_acute, 26)
    cap_circ_hi  = extract_accent_cap(cell_I, cell_I_circ, 26)

    # Cedilla from ç (bottom rows >= 45)
    ch, cw = tglp["ch"], tglp["cw"]
    cap_cedilla = [[0] * cw for _ in range(ch)]
    for y in range(45, ch):
        for x in range(cw):
            if cell_c_ced[y][x] > cell_c[y][x] + 24:
                cap_cedilla[y][x] = cell_c_ced[y][x]

    # Tilde cap: scale down ~ slightly in height and place at y=10..24 (for lowercase) and y=4..18 (for uppercase)
    cap_tilde_lo = [[0] * cw for _ in range(ch)]
    cap_tilde_hi = [[0] * cw for _ in range(ch)]
    # Find bounding box of ~
    ty_coords = [y for y in range(ch) for x in range(cw) if cell_tilde[y][x] > 32]
    tx_coords = [x for y in range(ch) for x in range(cw) if cell_tilde[y][x] > 32]
    if ty_coords and tx_coords:
        ty0, ty1 = min(ty_coords), max(ty_coords)
        tx0, tx1 = min(tx_coords), max(tx_coords)
        for y in range(ty0, ty1 + 1):
            for x in range(tx0, tx1 + 1):
                v = cell_tilde[y][x]
                ny_lo = 13 + int((y - ty0) * 0.75)
                nx_lo = x - 2
                if 0 <= ny_lo < ch and 0 <= nx_lo < cw:
                    cap_tilde_lo[ny_lo][nx_lo] = max(cap_tilde_lo[ny_lo][nx_lo], v)
                ny_hi = 6 + int((y - ty0) * 0.75)
                nx_hi = x
                if 0 <= ny_hi < ch and 0 <= nx_hi < cw:
                    cap_tilde_hi[ny_hi][nx_hi] = max(cap_tilde_hi[ny_hi][nx_hi], v)

    # Define the missing PT-BR characters to synthesize: (char, base_char, composite_cell)
    new_specs = [
        ("ã", "a", shift_and_overlay(cell_a, cap_tilde_lo, dx=0, dy=0)),
        ("õ", "o", shift_and_overlay(cell_o, cap_tilde_lo, dx=1, dy=0)),
        ("ô", "o", shift_and_overlay(cell_o, cap_circ_lo,  dx=1, dy=0)),
        ("ú", "u", shift_and_overlay(cell_u, cap_acute_lo, dx=1, dy=0)),
        ("À", "A", shift_and_overlay(cell_A, cap_grave_lo, dx=2, dy=-6)),
        ("Â", "A", shift_and_overlay(cell_A, cap_circ_hi,  dx=5, dy=0)),
        ("Ã", "A", shift_and_overlay(cell_A, cap_tilde_hi, dx=1, dy=0)),
        ("Ç", "C", shift_and_overlay(cell_C, cap_cedilla,  dx=3, dy=0)),
        ("Ê", "E", shift_and_overlay(cell_E, cap_circ_hi,  dx=4, dy=0)),
        ("Í", "I", shift_and_overlay(cell_I, cap_acute_hi, dx=-4, dy=0)),
        ("Ó", "O", shift_and_overlay(cell_O, cap_acute_hi, dx=2, dy=0)),
        ("Ô", "O", shift_and_overlay(cell_O, cap_circ_hi,  dx=7, dy=0)),
        ("Õ", "O", shift_and_overlay(cell_O, cap_tilde_hi, dx=2, dy=0)),
        ("Ú", "U", shift_and_overlay(cell_U, cap_acute_hi, dx=2, dy=0)),
        ("ª", "º", cell_ord_m),
    ]

    for ch_str, base_ch, synth_cell in new_specs:
        cp = ord(ch_str)
        if cp in cp_to_glyph:
            continue
        new_gidx = len(widths)
        base_gidx = cp_to_glyph[ord(base_ch)]
        widths.append(widths[base_gidx])
        write_cell_topdown(sheets_raw, sheets_decoded, tglp, new_gidx, synth_cell)
        scan_pairs.append((cp, new_gidx))
        cp_to_glyph[cp] = new_gidx

    scan_pairs.sort(key=lambda pair: pair[0])

    # Rebuild the BFFNT file with updated TGLP sheets, CWDH, and CMAP blocks
    out = bytearray(b[:0x14])
    # Copy FINF (0x14..0x34)
    finf = bytearray(b[0x14:0x34])
    out += finf
    # Copy TGLP header + updated sheets
    tglp_blob = bytearray(b[tglp["off"] : tglp["off"] + tglp["sz"]])
    for s in range(tglp["nsh"]):
        rel_s = (tglp["doff"] - tglp["off"]) + s * tglp["ssz"]
        tglp_blob[rel_s : rel_s + tglp["ssz"]] = sheets_raw[s]
    out += tglp_blob

    # Rebuild CWDH block at new_cwdh_off
    new_cwdh_off = len(out)
    cwdh_body = bytearray(struct.pack(">HHI", 0, len(widths) - 1, 0))
    for l_w, g_w, c_w in widths:
        cwdh_body += struct.pack(">bBB", l_w, g_w, c_w)
    while len(cwdh_body) % 4 != 0:
        cwdh_body.append(0)
    cwdh_block = b"CWDH" + struct.pack(">I", 8 + len(cwdh_body)) + cwdh_body
    out += cwdh_block

    # Rebuild the 3 direct CMAP blocks + 1 scan CMAP block
    first_cmap_off = len(out)
    for i in range(3):
        coff = cmap_offs[i]
        cblock = bytearray(b[coff : coff + 24])
        next_ptr = (len(out) + 24 + 8)
        struct.pack_into(">I", cblock, 16, next_ptr)
        out += cblock

    # Build final scan CMAP block
    scan_body = bytearray(struct.pack(">HHHHIH", 0x0000, 0xFFFF, 2, 0, 0, len(scan_pairs)))
    for cp, g in scan_pairs:
        scan_body += struct.pack(">HH", cp, g)
    while len(scan_body) % 4 != 0:
        scan_body.append(0)
    out += b"CMAP" + struct.pack(">I", 8 + len(scan_body)) + scan_body

    # Update FINF pointers (at out[0x14..0x34]) and FFNT total size (at out[0x0C..0x10])
    struct.pack_into(">I", out, 0x14 + 0x18, new_cwdh_off + 8)
    struct.pack_into(">I", out, 0x14 + 0x1C, first_cmap_off + 8)
    struct.pack_into(">I", out, 0x0C, len(out))
    return bytes(out)

def patch_cking_main_l(b: bytes) -> bytes:
    """Add PT-BR accented glyphs to CKingMainL.bffnt (271/288 cells used, 17 free slots)."""
    tglp, cwdh_off, widths, cmap_offs, cp_to_glyph, scan_pairs = parse_bffnt_maps(b)
    # Runtime texture arrays rotate bank bits for every slice, not just odd pages.
    # Read reference glyphs and write new glyphs through the same runtime layout.
    tglp['sheet_swizzles'] = [(page % 4) * 0x200 for page in range(tglp['nsh'])]
    sheets_raw = []
    sheets_decoded = []
    for s in range(tglp["nsh"]):
        soff = tglp["doff"] + s * tglp["ssz"]
        raw = bytearray(b[soff:soff+tglp["ssz"]])
        sheets_raw.append(raw)
        sheets_decoded.append(decode_sheet(raw, tglp["sw"], tglp["sh"], tglp['sheet_swizzles'][s]))

    cell_a = extract_cell_topdown(sheets_decoded, tglp, cp_to_glyph[ord("a")])
    cell_e = extract_cell_topdown(sheets_decoded, tglp, cp_to_glyph[ord("e")])
    cell_i = extract_cell_topdown(sheets_decoded, tglp, cp_to_glyph[ord("i")])
    cell_o = extract_cell_topdown(sheets_decoded, tglp, cp_to_glyph[ord("o")])
    cell_u = extract_cell_topdown(sheets_decoded, tglp, cp_to_glyph[ord("u")])
    cell_c = extract_cell_topdown(sheets_decoded, tglp, cp_to_glyph[ord("c")])
    cell_A = extract_cell_topdown(sheets_decoded, tglp, cp_to_glyph[ord("A")])
    cell_E = extract_cell_topdown(sheets_decoded, tglp, cp_to_glyph[ord("E")])
    cell_I = extract_cell_topdown(sheets_decoded, tglp, cp_to_glyph[ord("I")])
    cell_O = extract_cell_topdown(sheets_decoded, tglp, cp_to_glyph[ord("O")])
    cell_U = extract_cell_topdown(sheets_decoded, tglp, cp_to_glyph[ord("U")])
    cell_C = extract_cell_topdown(sheets_decoded, tglp, cp_to_glyph[ord("C")])

    cell_e_acute = extract_cell_topdown(sheets_decoded, tglp, cp_to_glyph[ord("é")])
    cell_E_acute = extract_cell_topdown(sheets_decoded, tglp, cp_to_glyph[ord("É")])
    cell_tilde   = extract_cell_topdown(sheets_decoded, tglp, cp_to_glyph[ord("~")])
    cell_caret   = extract_cell_topdown(sheets_decoded, tglp, cp_to_glyph[ord("^")])
    cell_comma   = extract_cell_topdown(sheets_decoded, tglp, cp_to_glyph[ord(",")])

    cap_acute_lo = extract_accent_cap(cell_e, cell_e_acute, 56)
    cap_acute_hi = extract_accent_cap(cell_E, cell_E_acute, 44)

    ch, cw = tglp["ch"], tglp["cw"]
    cap_tilde_lo = [[0] * cw for _ in range(ch)]
    cap_tilde_hi = [[0] * cw for _ in range(ch)]
    ty_coords = [y for y in range(ch) for x in range(cw) if cell_tilde[y][x] > 32]
    tx_coords = [x for y in range(ch) for x in range(cw) if cell_tilde[y][x] > 32]
    if ty_coords and tx_coords:
        ty0, ty1 = min(ty_coords), max(ty_coords)
        tx0, tx1 = min(tx_coords), max(tx_coords)
        for y in range(ty0, ty1 + 1):
            for x in range(tx0, tx1 + 1):
                v = cell_tilde[y][x]
                ny_lo = 22 + int((y - ty0) * 0.75)
                if 0 <= ny_lo < ch and 0 <= x - 3 < cw:
                    cap_tilde_lo[ny_lo][x - 3] = max(cap_tilde_lo[ny_lo][x - 3], v)
                ny_hi = 10 + int((y - ty0) * 0.75)
                if 0 <= ny_hi < ch and 0 <= x < cw:
                    cap_tilde_hi[ny_hi][x] = max(cap_tilde_hi[ny_hi][x], v)

    cap_circ_lo = [[0] * cw for _ in range(ch)]
    cap_circ_hi = [[0] * cw for _ in range(ch)]
    cy_coords = [y for y in range(ch) for x in range(cw) if cell_caret[y][x] > 32]
    cx_coords = [x for y in range(ch) for x in range(cw) if cell_caret[y][x] > 32]
    if cy_coords and cx_coords:
        cy0, cy1 = min(cy_coords), max(cy_coords)
        cx0, cx1 = min(cx_coords), max(cx_coords)
        for y in range(cy0, cy1 + 1):
            for x in range(cx0, cx1 + 1):
                v = cell_caret[y][x]
                ny_lo = 18 + int((y - cy0) * 0.65)
                if 0 <= ny_lo < ch and 0 <= x < cw:
                    cap_circ_lo[ny_lo][x] = max(cap_circ_lo[ny_lo][x], v)
                ny_hi = 8 + int((y - cy0) * 0.65)
                if 0 <= ny_hi < ch and 0 <= x < cw:
                    cap_circ_hi[ny_hi][x] = max(cap_circ_hi[ny_hi][x], v)

    # Dotless i for í
    cell_i_dotless = [row[:] for row in cell_i]
    for y in range(48):
        for x in range(cw):
            cell_i_dotless[y][x] = 0

    # Up to 17 most-needed PT-BR chars for large banners
    new_specs = [
        ("á", "a", shift_and_overlay(cell_a, cap_acute_lo, dx=0, dy=0)),
        ("ã", "a", shift_and_overlay(cell_a, cap_tilde_lo, dx=0, dy=0)),
        ("â", "a", shift_and_overlay(cell_a, cap_circ_lo,  dx=0, dy=0)),
        ("ê", "e", shift_and_overlay(cell_e, cap_circ_lo,  dx=0, dy=0)),
        ("í", "i", shift_and_overlay(cell_i_dotless, cap_acute_lo, dx=-10, dy=0)),
        ("ó", "o", shift_and_overlay(cell_o, cap_acute_lo, dx=2, dy=0)),
        ("ô", "o", shift_and_overlay(cell_o, cap_circ_lo,  dx=2, dy=0)),
        ("õ", "o", shift_and_overlay(cell_o, cap_tilde_lo, dx=2, dy=0)),
        ("ú", "u", shift_and_overlay(cell_u, cap_acute_lo, dx=2, dy=0)),
        ("ç", "c", shift_and_overlay(cell_c, cell_comma,   dx=10, dy=4)),
        ("Á", "A", shift_and_overlay(cell_A, cap_acute_hi, dx=2, dy=0)),
        ("Ã", "A", shift_and_overlay(cell_A, cap_tilde_hi, dx=2, dy=0)),
        ("Í", "I", shift_and_overlay(cell_I, cap_acute_hi, dx=-6, dy=0)),
        ("Ó", "O", shift_and_overlay(cell_O, cap_acute_hi, dx=4, dy=0)),
        ("Ô", "O", shift_and_overlay(cell_O, cap_circ_hi,  dx=4, dy=0)),
        ("Ú", "U", shift_and_overlay(cell_U, cap_acute_hi, dx=4, dy=0)),
        ("Ç", "C", shift_and_overlay(cell_C, cell_comma,   dx=14, dy=4)),
    ]

    for ch_str, base_ch, synth_cell in new_specs:
        cp = ord(ch_str)
        if cp in cp_to_glyph or len(widths) >= tglp["nsh"] * tglp["cols"] * tglp["rows"]:
            continue
        new_gidx = len(widths)
        base_gidx = cp_to_glyph[ord(base_ch)]
        widths.append(widths[base_gidx])
        write_cell_topdown(sheets_raw, sheets_decoded, tglp, new_gidx, synth_cell)
        scan_pairs.append((cp, new_gidx))
        cp_to_glyph[cp] = new_gidx

    scan_pairs.sort(key=lambda pair: pair[0])

    out = bytearray(b[:0x14])
    finf = bytearray(b[0x14:0x34])
    out += finf
    tglp_blob = bytearray(b[tglp["off"] : tglp["off"] + tglp["sz"]])
    for s in range(tglp["nsh"]):
        rel_s = (tglp["doff"] - tglp["off"]) + s * tglp["ssz"]
        tglp_blob[rel_s : rel_s + tglp["ssz"]] = sheets_raw[s]
    out += tglp_blob

    new_cwdh_off = len(out)
    cwdh_body = bytearray(struct.pack(">HHI", 0, len(widths) - 1, 0))
    for l_w, g_w, c_w in widths:
        cwdh_body += struct.pack(">bBB", l_w, g_w, c_w)
    while len(cwdh_body) % 4 != 0:
        cwdh_body.append(0)
    cwdh_block = b"CWDH" + struct.pack(">I", 8 + len(cwdh_body)) + cwdh_body
    out += cwdh_block

    first_cmap_off = len(out)
    for i in range(len(cmap_offs) - 1):
        coff = cmap_offs[i]
        csz = struct.unpack_from(">I", b, coff + 4)[0]
        cblock = bytearray(b[coff : coff + csz])
        next_ptr = (len(out) + csz + 8)
        struct.pack_into(">I", cblock, 16, next_ptr)
        out += cblock

    scan_body = bytearray(struct.pack(">HHHHIH", 0x0000, 0xFFFF, 2, 0, 0, len(scan_pairs)))
    for cp, g in scan_pairs:
        scan_body += struct.pack(">HH", cp, g)
    while len(scan_body) % 4 != 0:
        scan_body.append(0)
    out += b"CMAP" + struct.pack(">I", 8 + len(scan_body)) + scan_body

    struct.pack_into(">I", out, 0x14 + 0x18, new_cwdh_off + 8)
    struct.pack_into(">I", out, 0x14 + 0x1C, first_cmap_off + 8)
    struct.pack_into(">I", out, 0x0C, len(out))
    return bytes(out)


def main():
    parser = argparse.ArgumentParser(description="Apply Wind Waker HD PT-BR Translation Patch")
    parser.add_argument("--input", "-i", required=True,
                        help="Path to original permanent_2d_UsEnglish.pack")
    parser.add_argument("--patch", "-p", default=os.path.join(os.path.dirname(__file__), "ptbr_patch.json"),
                        help="Path to ptbr_patch.json")
    parser.add_argument("--output", "-o", default=os.path.join(os.path.dirname(__file__), "..", "mods", "wind-waker-ptbr", "content", "Common", "Pack", "permanent_2d_UsEnglish.pack"),
                        help="Path to output permanent_2d_UsEnglish.pack")
    args = parser.parse_args()

    t_start = time.time()
    print("=" * 65)
    print("  The Legend of Zelda: The Wind Waker HD - Tradução PT-BR")
    print("  Standalone Binary Patcher (v1.0.6)")
    print("=" * 65)

    if not os.path.exists(args.input):
        print(f"ERRO: Arquivo original não encontrado: {args.input}", file=sys.stderr)
        sys.exit(1)

    if not os.path.exists(args.patch):
        print(f"ERRO: Arquivo de patch não encontrado: {args.patch}", file=sys.stderr)
        sys.exit(1)

    print(f"Lendo pacote original: {args.input}")
    with open(args.input, "rb") as f:
        orig_pack_bytes = f.read()
    print(f"Tamanho original: {len(orig_pack_bytes):,} bytes")

    print(f"Lendo definições do patch: {args.patch}")
    with open(args.patch, "r", encoding="utf-8") as f:
        patch_info = json.load(f)

    archives = patch_info["archives"]
    print(f"Arquivos no patch: {len(archives)} arquivos MSBT ({patch_info['metadata']['total_messages']:,} mensagens)")

    outer_members = parse_sarc(orig_pack_bytes)
    replaced_members = {}

    # 1. Patch MSBT archives
    for i, (arch_name, msg_dict) in enumerate(archives.items(), 1):
        if arch_name not in outer_members:
            print(f"AVISO: Arquivo {arch_name} não encontrado no pacote original!", file=sys.stderr)
            continue
        orig_szs = outer_members[arch_name]
        orig_inner_sarc = decompress_yaz0(orig_szs)
        inner_members = parse_sarc(orig_inner_sarc)
        msbt_name = list(inner_members.keys())[0]
        parsed_msbt = parse_msbt_file(inner_members[msbt_name])

        rebuilt_msbt = build_msbt_file(parsed_msbt, msg_dict)
        rebuilt_sarc = rebuild_inner_sarc(orig_inner_sarc, rebuilt_msbt)
        rebuilt_szs = compress_yaz0(rebuilt_sarc)
        replaced_members[arch_name] = rebuilt_szs

        if i % 15 == 0 or i == len(archives):
            print(f"  [{i:02d}/{len(archives)}] Injetado {arch_name} ({len(msg_dict)} mensagens)")

    # 2. Patch fonts
    print("Sintetizando glifos acentuados nas fontes do jogo...")
    if "CKingMain_bffnt.szs" in outer_members:
        orig_sarc = decompress_yaz0(outer_members["CKingMain_bffnt.szs"])
        sarc_members = parse_sarc(orig_sarc)
        bffnt_name = list(sarc_members.keys())[0]
        patched_bffnt = patch_cking_main(sarc_members[bffnt_name])
        rebuilt_sarc = rebuild_inner_sarc(orig_sarc, patched_bffnt)
        replaced_members["CKingMain_bffnt.szs"] = compress_yaz0(rebuilt_sarc)
        print("  CKingMain_bffnt.szs: Glifos acentuados reconstruídos com integridade estrutural.")

    if "CKingMainL_bffnt.szs" in outer_members:
        orig_sarc = decompress_yaz0(outer_members["CKingMainL_bffnt.szs"])
        sarc_members = parse_sarc(orig_sarc)
        bffnt_name = list(sarc_members.keys())[0]
        patched_bffnt = patch_cking_main_l(sarc_members[bffnt_name])
        rebuilt_sarc = rebuild_inner_sarc(orig_sarc, patched_bffnt)
        replaced_members["CKingMainL_bffnt.szs"] = compress_yaz0(rebuilt_sarc)
        print("  CKingMainL_bffnt.szs: Glifos acentuados reconstruídos com integridade estrutural.")

    # 3. Assemble new pack
    print("Reempacotando permanent_2d_UsEnglish.pack final...")
    final_pack = rebuild_main_pack_sarc(orig_pack_bytes, replaced_members)
    print(f"Tamanho do novo pacote: {len(final_pack):,} bytes")

    os.makedirs(os.path.dirname(args.output), exist_ok=True)
    with open(args.output, "wb") as f:
        f.write(final_pack)
    print(f"Arquivo gerado com sucesso em: {args.output}")

    elapsed = time.time() - t_start
    print("=" * 65)
    print(f"Patch aplicado e verificado com sucesso em {elapsed:.2f} segundos!")
    print("=" * 65)

if __name__ == "__main__":
    main()

