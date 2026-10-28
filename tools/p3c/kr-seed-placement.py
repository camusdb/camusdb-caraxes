#!/usr/bin/env python3
# Predicts where Kahuna seeds a key-range space (1 + xxHash64(group) % P) and where hash routing places it
# (1 + jump hash of two xxHash32 of the group), for the groups given as a comma-separated argument, over P = 2..8.
# Validated 2026-10-28 against cluster-facts.json of the kr146-fanout runs. Usage: python3 tools/p3c/kr-seed-placement.py "1:1,1:2,1:3"
import struct, sys
M64=(1<<64)-1; M32=(1<<32)-1
P1,P2,P3,P4,P5=11400714785074694791,14029467366897019727,1609587929392839161,9650029242287828579,2870177450012600261
def rotl64(x,r): return ((x<<r)|(x>>(64-r)))&M64
def rotl32(x,r): return ((x<<r)|(x>>(32-r)))&M32
def xxh64(data,seed=0):
    n=len(data); i=0
    if n>=32:
        v1=(seed+P1+P2)&M64; v2=(seed+P2)&M64; v3=seed&M64; v4=(seed-P1)&M64
        def rnd(acc,inp): return (rotl64((acc+inp*P2)&M64,31)*P1)&M64
        while i+32<=n:
            a,b,c,d=struct.unpack_from('<QQQQ',data,i); v1=rnd(v1,a);v2=rnd(v2,b);v3=rnd(v3,c);v4=rnd(v4,d); i+=32
        h=(rotl64(v1,1)+rotl64(v2,7)+rotl64(v3,12)+rotl64(v4,18))&M64
        def mrg(h,v): v=rnd(0,v); return ((h^v)*P1+P4)&M64
        h=mrg(h,v1);h=mrg(h,v2);h=mrg(h,v3);h=mrg(h,v4)
    else: h=(seed+P5)&M64
    h=(h+n)&M64
    while i+8<=n:
        k=struct.unpack_from('<Q',data,i)[0]; k=(rotl64((k*P2)&M64,31)*P1)&M64; h=((rotl64(h^k,27)*P1)+P4)&M64; i+=8
    if i+4<=n:
        k=struct.unpack_from('<I',data,i)[0]; h=((rotl64(h^((k*P1)&M64),23)*P2)+P3)&M64; i+=4
    while i<n:
        h=(rotl64(h^((data[i]*P5)&M64),11)*P1)&M64; i+=1
    h^=h>>33; h=(h*P2)&M64; h^=h>>29; h=(h*P3)&M64; h^=h>>32
    return h
Q1,Q2,Q3,Q4,Q5=2654435761,2246822519,3266489917,668265263,374761393
def xxh32(data,seed=0):
    n=len(data); i=0
    if n>=16:
        v1=(seed+Q1+Q2)&M32; v2=(seed+Q2)&M32; v3=seed&M32; v4=(seed-Q1)&M32
        def rnd(acc,inp): return (rotl32((acc+inp*Q2)&M32,13)*Q1)&M32
        while i+16<=n:
            a,b,c,d=struct.unpack_from('<IIII',data,i); v1=rnd(v1,a);v2=rnd(v2,b);v3=rnd(v3,c);v4=rnd(v4,d); i+=16
        h=(rotl32(v1,1)+rotl32(v2,7)+rotl32(v3,12)+rotl32(v4,18))&M32
    else: h=(seed+Q5)&M32
    h=(h+n)&M32
    while i+4<=n:
        k=struct.unpack_from('<I',data,i)[0]; h=(rotl32((h+k*Q3)&M32,17)*Q4)&M32; i+=4
    while i<n:
        h=(rotl32((h+data[i]*Q5)&M32,11)*Q1)&M32; i+=1
    h^=h>>15; h=(h*Q2)&M32; h^=h>>13; h=(h*Q3)&M32; h^=h>>16
    return h
def jump(key,nb):
    b=-1; j=0
    while j<nb:
        b=j; key=(key*2862933555777941757+1)&M64
        j=int((b+1)*(2147483648.0/float((key>>33)+1)))
    return b
def consistent(s,nb):
    d=s.encode(); h1=xxh32(d,0xAAAAAAAA); h2=xxh32(d,0x55555555); return jump((h1<<32)|h2,nb)
def seed_kr(group,P): return 1+ (xxh64(group.encode())% P)
def seed_hash(group,P): return 1+ consistent(group,P)
groups=sys.argv[1].split(',')
for P in range(2,9):
    kr=[seed_kr(g,P) for g in groups]; hs=[seed_hash(g,P) for g in groups]
    print(f"P={P}  key-range {kr}  distinct {len(set(kr))}   hash {hs}  distinct {len(set(hs))}")
