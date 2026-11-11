"""Inspect APK contents and both ELF ABIs without running the application."""
import argparse
import hashlib
import json
from pathlib import Path
import struct
import zipfile

def verify(path):
    with zipfile.ZipFile(path) as apk:
        assert apk.testzip() is None, 'Corrupt ZIP member'
        names=set(apk.namelist())
        assert 'AndroidManifest.xml' in names and 'classes.dex' in names
        assert 'assets/licenses/Mihomo-GPL-3.0.txt' in names
        metadata='META-INF/com/android/build/gradle/app-metadata.properties'
        assert not any(x!=metadata and (x.lower().endswith(('.jks','.keystore','.log','.properties')) or 'profiles.json' in x.lower()) for x in names), 'Private file in APK'
        abis={}
        for abi,machine in [('arm64-v8a',183),('x86_64',62)]:
            native=apk.read(f'lib/{abi}/libregioncore.so')
            assert native[:6]==b'\x7fELF\x02\x01', 'Expected ELF64 little endian'
            assert struct.unpack_from('<H',native,18)[0]==machine, 'Wrong architecture'
            offset=struct.unpack_from('<Q',native,32)[0]
            width,count=struct.unpack_from('<HH',native,54)
            loads=[]
            for i in range(count):
                fields=struct.unpack_from('<IIQQQQQQ',native,offset+i*width)
                if fields[0]==1:
                    assert fields[7]>=16384 and fields[2]%16384==fields[3]%16384, 'Native library lacks 16 KiB alignment'
                    loads.append(fields[7])
            assert loads
            for symbol in ['JNI_OnLoad','attach','start','stop','version']:
                value=symbol if symbol=='JNI_OnLoad' else 'Java_io_regionlauncher_android_NativeCore_'+symbol
                assert value.encode()+b'\0' in native, f'Missing JNI symbol: {value}'
            abis[abi]={'bytes':len(native),'load_alignments':loads}
        with open(path,'rb') as stream: digest=hashlib.file_digest(stream,'sha256').hexdigest()
        return {'apk':Path(path).name,'sha256':digest,'abis':abis,'scope':'archive, ABI, JNI names and ELF alignment only; not runtime validation'}

if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('apk');args=parser.parse_args()
    print(json.dumps(verify(args.apk),ensure_ascii=False,indent=2))
