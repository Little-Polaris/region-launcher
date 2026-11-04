from pathlib import Path

root = Path(__file__).resolve().parents[1]
core = root / 'vendor/proxybridge'
header = core / 'ProxyBridge.h'
text = header.read_text(encoding='utf-8')
needle = 'PROXYBRIDGE_API BOOL ProxyBridge_Start(void);'
replacement = '''// Region Launcher: the owner supplies a process-instance membership predicate.
typedef BOOL (*ProcessScopeCallback)(DWORD pid);
PROXYBRIDGE_API void ProxyBridge_SetProcessScope(ProcessScopeCallback callback);
PROXYBRIDGE_API BOOL ProxyBridge_Start(void);'''
assert text.count(needle) == 1
header.write_text(text.replace(needle, replacement), encoding='utf-8')
rules = core / 'pb_rules.c'
text = rules.read_text(encoding='utf-8')
text = text.replace('#include "pb_internal.h"', '''#include "pb_internal.h"

static ProcessScopeCallback region_scope = NULL;
PROXYBRIDGE_API void ProxyBridge_SetProcessScope(ProcessScopeCallback callback) {
    region_scope = callback;
}
''', 1)
needle = '    if (!get_process_name_from_pid(pid, process_name, sizeof(process_name)))'
assert text.count(needle) == 2
text = text.replace(needle, '''    // A job object identifies this launch, independently of executable name/path.
    if (region_scope != NULL && !region_scope(pid)) return RULE_ACTION_DIRECT;
''' + needle)
# Missing/unsupported proxy configurations never silently fall back for selected processes.
text = text.replace("return RULE_ACTION_DIRECT;  // No proxy configured", "return RULE_ACTION_BLOCK;  // No proxy configured")
text = text.replace("if (cfg == NULL || cfg->host[0] == '\\0' || cfg->port == 0)\n            return RULE_ACTION_DIRECT;", "if (cfg == NULL || cfg->host[0] == '\\0' || cfg->port == 0)\n            return RULE_ACTION_BLOCK;")
text = text.replace('if (is_udp && cfg->type == PROXY_TYPE_HTTP)\n            return RULE_ACTION_DIRECT;', 'if (is_udp && cfg->type == PROXY_TYPE_HTTP)\n            return RULE_ACTION_BLOCK;')
rules.write_text(text, encoding='utf-8')
main = core / 'ProxyBridge.c'
text = main.read_text(encoding='utf-8')
needle = '    g_fw_thread = CreateThread(NULL, 0, fw_ensure_thread, NULL, 0, NULL);'
assert text.count(needle) == 1
text = text.replace(needle, '    // Region Launcher does not add persistent host firewall exceptions.\n    g_fw_thread = NULL;')
main.write_text(text, encoding='utf-8')
