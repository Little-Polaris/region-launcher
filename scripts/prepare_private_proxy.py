"""Import ONE local Clash node; never modify Clash or export credentials to source/packages."""
import argparse
import copy
import json
import os
from pathlib import Path
import ipaddress
import subprocess
import urllib.parse
import urllib.request
import yaml


def make_config(node, port=17890):
    node = copy.deepcopy(node)
    if node.get('dialer-proxy') or node.get('type') in ('direct', 'socks5', 'http'):
        raise ValueError('A self-contained remote node is required, not another local routing proxy.')
    node['name'] = 'RegionExit'
    return {
        'mixed-port': port, 'bind-address': '127.0.0.1', 'allow-lan': False,
        'mode': 'rule', 'ipv6': False, 'log-level': 'warning',
        'external-controller': '', 'tun': {'enable': False},
        'profile': {'store-selected': False, 'store-fake-ip': False},
        'dns': {
            'enable': True, 'listen': '127.0.0.153:53', 'ipv6': False, 'enhanced-mode': 'redir-host',
            'use-hosts': False, 'use-system-hosts': False, 'respect-rules': True,
            'default-nameserver': ['1.1.1.1'],
            'proxy-server-nameserver': ['https://1.1.1.1/dns-query'],
            'nameserver': ['https://1.1.1.1/dns-query#RegionExit'],
        },
        'proxies': [node], 'rules': ['MATCH,RegionExit'],
    }


def pin_node_server(node):
    node = copy.deepcopy(node)
    server = node['server']
    try:
        ipaddress.ip_address(server)
        return node
    except ValueError:
        pass
    # Bootstrap once through existing proxy; website names never use host DNS.
    url = 'https://cloudflare-dns.com/dns-query?' + urllib.parse.urlencode({'name': server, 'type': 'A'})
    result = subprocess.run(['curl.exe', '-sSf', '--proxy', 'socks5h://127.0.0.1:7890',
                             '--max-time', '20', '-H', 'accept: application/dns-json', url],
                            check=True, capture_output=True)
    answers = json.loads(result.stdout).get('Answer', [])
    addresses = [a['data'] for a in answers if a['type'] == 1 and ipaddress.ip_address(a['data']).is_global]
    if not addresses:
        raise ValueError('Remote bootstrap did not return a public IPv4 address')
    if node.get('tls'):
        node.setdefault('servername', server)
    if node.get('network') == 'ws':
        node.setdefault('ws-opts', {}).setdefault('headers', {}).setdefault('Host', server)
    node['server'] = addresses[0]
    return node


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--app', required=True, type=Path)
    parser.add_argument('--group', default='自动选择')
    args = parser.parse_args()
    base = Path(os.environ['APPDATA']) / 'clashmi/clashmi'
    patch = json.loads((base / 'service_core_patch_final.json').read_text(encoding='utf-8-sig'))
    req = urllib.request.Request('http://127.0.0.1:9090/proxies', headers={
        'Authorization': 'Bearer ' + patch['secret']})
    with urllib.request.urlopen(req, timeout=5) as response:
        proxies = json.load(response)['proxies']
    name = args.group
    seen = set()
    while proxies.get(name, {}).get('now'):
        if name in seen:
            raise ValueError('Proxy group cycle')
        seen.add(name)
        name = proxies[name]['now']
    if '美国' not in name:
        raise ValueError('Selected node is not labelled US; choose the intended US node first.')
    runtime = yaml.safe_load((base / 'service_core_runtime_profile.yaml').read_text(encoding='utf-8'))
    node = next(n for n in runtime['proxies'] if n['name'] == name)
    target = args.app.resolve() / 'data/private'
    target.mkdir(parents=True, exist_ok=True)
    config = make_config(pin_node_server(node))
    dest = target / 'proxy.json'
    if dest.exists():
        raise FileExistsError('Private config already exists; back it up before replacing it.')
    dest.write_text(json.dumps(config, ensure_ascii=False, indent=2), encoding='utf-8')
    print(json.dumps({'node': name, 'port': config['mixed-port'], 'config': str(dest)}, ensure_ascii=True))


if __name__ == '__main__':
    main()
