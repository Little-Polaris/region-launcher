// Exercise the same Node DNS lookup + loopback listener used by desktop sign-in callbacks.
const assert = require('node:assert/strict');
const dns = require('node:dns').promises;
const http = require('node:http');

(async () => {
  for (const name of ['localhost', 'LOCALHOST.', 'callback.localhost']) {
    const addresses = await dns.lookup(name, { all: true });
    assert.deepEqual(addresses, [{ address: '127.0.0.1', family: 4 }]);
  }
  await assert.rejects(dns.lookup('localhost', { family: 6 }));
  const server = http.createServer((req, res) => res.end('local-callback-ok'));
  await new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(0, 'localhost', resolve);
  });
  try {
    const body = await new Promise((resolve, reject) => {
      const req = http.get(`http://localhost:${server.address().port}/callback`, res => {
        let body = '';
        res.on('data', chunk => body += chunk);
        res.on('end', () => resolve(body));
      });
      req.on('error', reject);
      req.setTimeout(5000, () => req.destroy(new Error('local callback timeout')));
    });
    assert.equal(body, 'local-callback-ok');
    console.log('PASS: Node localhost lookup, IPv4 callback listener and HTTP round trip; IPv6 absent');
  } finally { await new Promise(resolve => server.close(resolve)); }
})().catch(error => { console.error(error); process.exitCode = 1; });
