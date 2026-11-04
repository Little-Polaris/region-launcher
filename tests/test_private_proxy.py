import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('setup_proxy', Path(__file__).parents[1] / 'scripts/prepare_private_proxy.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class DedicatedConfigTest(unittest.TestCase):
    def test_only_remote_exit_and_dns(self):
        original = {'name': 'US', 'type': 'vless', 'server': 'example.com', 'uuid': 'fixture'}
        c = module.make_config(original)
        self.assertEqual(c['rules'], ['MATCH,RegionExit'])
        self.assertEqual(c['dns']['nameserver'], ['https://1.1.1.1/dns-query#RegionExit'])
        self.assertEqual(c['bind-address'], '127.0.0.1')
        self.assertFalse(c['tun']['enable'])
        self.assertEqual(len(c['proxies']), 1)
        self.assertEqual(original['name'], 'US')

    def test_no_routing_proxy_as_remote_node(self):
        with self.assertRaises(ValueError):
            module.make_config({'type': 'socks5', 'server': '127.0.0.1'})


if __name__ == '__main__':
    unittest.main()
