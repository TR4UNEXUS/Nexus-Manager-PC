"""Build a synthetic NSP for interface tests, using ephemeral test keys only.

Usage: python make_fixture.py <empty fixture directory>. Requires cryptography.
Contains generated data only. It is not an executable game or an ES-valid ticket.
"""
import hashlib
import os
from pathlib import Path
import struct
import sys
from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes

root = Path(sys.argv[1])
root.mkdir(exist_ok=True, parents=True)
if any(root.iterdir()):
    raise ValueError("Use an empty fixture directory")
key = os.urandom(32)
(root / "fixture.keys").write_text("header_key = " + key.hex() + "\n")
title = 0x0100000000023000
rights = title.to_bytes(8, "big") + bytes(7) + b"\x01"
digest = lambda b: hashlib.sha256(b).digest()

def pfs(files):
    names, entries, payload = bytearray(), bytearray(), bytearray()
    for name, data in files:
        entries += struct.pack("<QQII", len(payload), len(data), len(names), 0)
        names += name.encode() + b"\0"
        payload += data
    return struct.pack("<4sIII", b"PFS0", len(files), len(names), 0) + entries + names + payload

def nca(kind, data, fs, has_rights=False, cartridge=False):
    header = bytearray(3072)
    data += bytes(-len(data) % 512)
    struct.pack_into("<4sBBBBQQ", header, 512, b"NCA3", 0, kind, 0, 0, len(header)+len(data), title)
    header[0x204]=int(cartridge)
    if has_rights:
        header[0x230:0x240] = rights
    header[0x400:0x600] = fs
    header[0x280:0x2a0] = digest(fs)
    struct.pack_into("<II",header,0x240,6,6+len(data)//512)
    encrypted = bytearray()
    for i in range(6):
        ctx=Cipher(algorithms.AES(key),modes.XTS(i.to_bytes(16,"big"))).encryptor()
        encrypted += ctx.update(bytes(header[i*512:(i+1)*512]))+ctx.finalize()
    return encrypted + data

program = nca(0, b"nexus-pc-test-content"*13000, bytes(512), True)
h = digest(program)
cnmt = bytearray(32+16+2*56)
struct.pack_into("<QIBBHH",cnmt,0,title,0,0x80,0,16,2)
cnmt[48:80],cnmt[80:96] = h,h[:16]
struct.pack_into("<I",cnmt,96,len(program))
cnmt[102] = 1
delta = nca(4,b"optional patch delivery data",bytes(512))
dh=digest(delta)
cnmt[104:136],cnmt[136:152]=dh,dh[:16]
struct.pack_into("<I",cnmt,152,len(delta))
cnmt[158]=6
section = pfs([("Application.cnmt",cnmt)])
fs = bytearray(512)
struct.pack_into("<HBBB", fs,0,2,1,2,1)
block=digest(section)
fs[8:40]=digest(block)
struct.pack_into("<IIQQQQ",fs,40,4096,2,0,32,512,len(section))
meta=nca(1,block+bytes(480)+section,fs)
ticket=bytearray(0x2c0)
struct.pack_into("<I",ticket,0,0x10004)
ticket[0x280]=2
ticket[0x2a0:0x2b0]=rights
files=[(h[:16].hex()+".nca",program),(digest(meta)[:16].hex()+".cnmt.nca",meta),
       (rights.hex()+".tik",ticket),(rights.hex()+".cert",b"synthetic certificate for framing tests")]
(root/"fixture.nsp").write_bytes(pfs(files))
# Independently authored, rights-free game content with its own consistent manifest.
for name,cartridge in [("ticketless.nsp",False),("cartridge-media.nsp",True)]:
    free_program=nca(0,b"nexus-pc-ticketless-asset"*12000,bytes(512),cartridge=cartridge)
    free_hash=digest(free_program)
    free_manifest=bytearray(cnmt[:104]);struct.pack_into("<H",free_manifest,16,1)
    free_manifest[48:80]=free_hash;free_manifest[80:96]=free_hash[:16]
    struct.pack_into("<I",free_manifest,96,len(free_program))
    free_section=pfs([("Application.cnmt",free_manifest)])
    free_layer=digest(free_section);free_fs=bytearray(fs)
    free_fs[8:40]=digest(free_layer);struct.pack_into("<Q",free_fs,72,len(free_section))
    free_meta=nca(1,free_layer+bytes(480)+free_section,free_fs,cartridge=cartridge)
    (root/name).write_bytes(pfs([(free_hash[:16].hex()+".nca",free_program),(digest(free_meta)[:16].hex()+".cnmt.nca",free_meta)]))
(root/"with-delta.nsp").write_bytes(pfs(files+[(dh[:16].hex()+".nca",delta)]))
title += 0x2000
struct.pack_into("<Q",cnmt,0,title)
section=pfs([("Application.cnmt",cnmt)])
block=digest(section)
fs[8:40]=digest(block)
second=nca(1,block+bytes(480)+section,fs)
files[1]=(digest(second)[:16].hex()+".cnmt.nca",second)
(root/"shared.nsp").write_bytes(pfs(files))

# A patch and two distinct add-ons for the first game. None has to copy
# shared data a second time. No commercial files are used in these tests.
family=0x0100000000023000
extra=[(h[:16].hex()+".nca",program),*files[-2:]]
for title,kind,version in [(family+0x800,0x81,65536),(family+0x1001,0x82,0),(family+0x1002,0x82,3)]:
    ext=24 if kind==0x81 else 16
    manifest=bytearray(32+ext+56)
    struct.pack_into("<QIBBHH",manifest,0,title,version,kind,0,ext,1)
    struct.pack_into("<Q",manifest,32,family)
    manifest[32+ext:]=cnmt[48:104]
    data=pfs([("Content.cnmt",manifest)])
    layer=digest(data)
    fs[8:40]=digest(layer)
    struct.pack_into("<Q",fs,72,len(data))
    n=nca(1,layer+bytes(480)+data,fs)
    extra.append((digest(n)[:16].hex()+".cnmt.nca",n))
(root/"extras.nsp").write_bytes(pfs(extra))

