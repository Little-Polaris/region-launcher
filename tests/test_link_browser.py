"""Exercise real ShellExecute hooks with Chrome/Edge fixtures and an outside profile owner."""
import os
from pathlib import Path
import shutil
import subprocess
import uuid

ROOT = Path(__file__).resolve().parents[1]
BIN = ROOT / 'build/service-bin'
OUT = ROOT / 'build/test-results/link-browser'


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    saved = OUT / 'saved profile 中文'
    temp = OUT / 'temporary profile 中文'
    saved.mkdir(exist_ok=True); temp.mkdir(exist_ok=True)
    env = os.environ.copy(); env['LINK_TEST_OUTPUT'] = str(OUT / 'browser-args.txt')
    for name in ('chrome.exe', 'msedge.exe'):
        browser = OUT / name
        shutil.copy2(ROOT / 'build/LinkProbe.exe', browser)
        for api in ('w', 'a', 'exw', 'exa'):
            for kind in ('saved', 'temporary', 'foreign', 'inside'):
                owner = None
                if kind == 'foreign':
                    owner = subprocess.Popen([str(browser), '--owner', str(saved)], stdout=subprocess.PIPE, text=True)
                    assert owner.stdout.readline().strip() == 'OWNER_READY'
                try:
                    url = 'https://example.invalid/oauth?code=test&value="quoted"'
                    config = OUT / 'session.ini'
                    arguments = (['--inside-owner', str(saved)] if kind == 'inside' else []) + [api, url]
                    config.write_text(f'''[session]
Job=Local\\RegionLauncher.{uuid.uuid4().hex}
Application={ROOT / 'build/LinkProbe.exe'}
Arguments={subprocess.list2cmdline(arguments)}
Locale=en-US
Region=US
Geo=244
TimeZone=Pacific Standard Time
AutoDst=1
LinkBrowser_ENABLED=1
LinkBrowser_EXE={browser}
LinkBrowser_PROFILE={saved if kind != 'temporary' else ''}
LinkBrowser_PROFILE_NAME=Profile 2
LinkBrowser_TEMP={temp}
''', encoding='utf-16')
                    result = subprocess.run([str(BIN / 'RegionSession.exe'), '--locale-test', str(config)], env=env, capture_output=True, text=True, timeout=25)
                    assert result.returncode == 0, result.stdout + result.stderr
                    args = Path(env['LINK_TEST_OUTPUT']).read_text(encoding='utf-8-sig').splitlines()
                    assert args[:2] == ['locale=en-US', 'inside=1'], args
                    assert args[-1] == url, args
                    reused = kind in ('saved', 'inside')
                    assert '--user-data-dir=' + str(saved if reused else temp) in args, args
                    private = '--inprivate' if name == 'msedge.exe' else '--incognito'
                    assert (private in args) == (not reused), args
                    assert ('--profile-directory=Profile 2' in args) == reused, args
                    assert url not in result.stdout + result.stderr, 'Authorization URL leaked into logs'
                    if owner: assert owner.poll() is None, 'Outside owner was affected'
                finally:
                    if owner: owner.terminate(); owner.wait(timeout=5)
        config.write_text(config.read_text(encoding='utf-16').replace(str(browser), str(OUT / 'missing.exe')), encoding='utf-16')
        missing = subprocess.run([str(BIN / 'RegionSession.exe'), '--locale-test', str(config)], env=env, capture_output=True, text=True, timeout=20)
        assert missing.returncode != 0 and 'no external fallback' in missing.stderr, missing
    print('PASS: Chrome/Edge arguments; four Shell APIs; Unicode paths; saved/temporary/foreign profiles; scoped child; missing browser fails closed; URL not logged')


if __name__ == '__main__': main()
