"""Package only a checked, signed Android build; preserve Windows delivery and private data."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import xml.etree.ElementTree as ET
import zipfile
from importlib.util import spec_from_file_location, module_from_spec

ROOT=Path(__file__).resolve().parents[2]
ANDROID=ROOT/'android'

def run(command):
    return subprocess.check_output([str(x) for x in command],cwd=ROOT,stderr=subprocess.STDOUT,text=True,encoding='utf-8',errors='replace')

def package(java,sdk):
    assert not run(['git','status','--porcelain','--','android']).strip(), 'Commit Android sources before packaging'
    revision=run(['git','rev-parse','HEAD']).strip()
    apk=ANDROID/'app/build/outputs/apk/release/app-release.apk'
    spec=spec_from_file_location('verify_apk',ANDROID/'scripts/verify-apk.py');module=module_from_spec(spec);spec.loader.exec_module(module)
    manifest=module.verify(apk)
    signature=run([java/'bin/java.exe','--enable-native-access=ALL-UNNAMED','-jar',sdk/'build-tools/36.0.0/lib/apksigner.jar','verify','--verbose','--print-certs',apk])
    assert 'Verifies' in signature
    badging=run([sdk/'build-tools/36.0.0/aapt2.exe','dump','badging',apk])
    assert "package: name='io.regionlauncher.android'" in badging and 'application-debuggable' not in badging
    reports=list((ANDROID/'app/build/test-results/testDebugUnitTest').glob('TEST-*.xml'))
    assert reports, 'Unit test report is missing'
    count=0
    for report in reports:
        suite=ET.parse(report).getroot();assert int(suite.attrib.get('failures',0))==0 and int(suite.attrib.get('errors',0))==0
        count+=int(suite.attrib['tests'])
    for source in (ANDROID/'app/src/main').rglob('*'):
        if source.is_file():assert source.stat().st_mtime<=apk.stat().st_mtime, f'APK predates {source.name}'
    with zipfile.ZipFile(apk) as archive:
        for abi in manifest['abis']:
            assert archive.read(f'lib/{abi}/libregioncore.so')==(ANDROID/f'app/src/main/jniLibs/{abi}/libregioncore.so').read_bytes(), 'APK has a stale native library'
    metadata=json.loads((apk.parent/'output-metadata.json').read_text(encoding='utf-8'))
    manifest.update(apk='RegionLauncher-Android-dev.apk',source_commit=revision,version=metadata['elements'][0]['versionName'],unit_tests=count,runtime_verified=False,
        runtime_limit='No attached device; software emulator crashed before Android boot. X300 verification pending.',
        signature=signature.strip().splitlines())
    out=ROOT.parent.parent/'outputs/region-launcher-unified/packages/android';out.mkdir(parents=True,exist_ok=True)
    with tempfile.TemporaryDirectory(dir=ROOT/'build',prefix='android-package-') as temporary:
        stage=Path(temporary)
        shutil.copy2(apk,stage/'RegionLauncher-Android-dev.apk')
        shutil.copy2(ANDROID/'INSTALL.md',stage/'使用说明.md')
        shutil.copy2(ANDROID/'LICENSE',stage/'LICENSE.txt')
        (stage/'build-manifest.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf-8')
        packed=stage/'RegionLauncher-Android-dev.zip'
        with zipfile.ZipFile(packed,'w',zipfile.ZIP_DEFLATED,compresslevel=6) as archive:
            for name in ['RegionLauncher-Android-dev.apk','使用说明.md','LICENSE.txt','build-manifest.json']:archive.write(stage/name,name)
        with zipfile.ZipFile(packed) as archive:assert archive.testzip() is None and len(archive.namelist())==4
        for name in ['RegionLauncher-Android-dev.apk','RegionLauncher-Android-dev.zip','build-manifest.json']:
            pending=out/(name+'.tmp');shutil.copy2(stage/name,pending);pending.replace(out/name)
    print(json.dumps({'apk':str(out/'RegionLauncher-Android-dev.apk'),'zip':str(out/'RegionLauncher-Android-dev.zip'),'sha256':manifest['sha256'],'source_commit':revision,'unit_tests':count,'runtime_verified':False},ensure_ascii=False,indent=2))

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--java-home',required=True,type=Path);p.add_argument('--sdk',required=True,type=Path);a=p.parse_args();package(a.java_home,a.sdk)
