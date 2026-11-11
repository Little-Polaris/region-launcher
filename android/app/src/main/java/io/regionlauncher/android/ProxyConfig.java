package io.regionlauncher.android;

import org.json.*;
import java.io.IOException;
import java.util.Set;

final class ProxyConfig {
    static final String ADDRESS="10.151.0.1", DNS="10.151.0.2";
    static final Set<String> TYPES=Set.of("hysteria2","vless","vmess","trojan","ss","tuic","anytls","socks5","http");
    static void validate(JSONObject node) throws Exception {
        if (!TYPES.contains(node.optString("type"))) throw new IOException("不支持的节点协议："+node.optString("type"));
        if (node.optString("server").isBlank() || node.optInt("port")<1 || node.optInt("port")>65535) throw new IOException("节点地址或端口无效");
        if (node.has("dialer-proxy") || node.has("interface-name") || node.has("routing-mark")) throw new IOException("节点包含额外转发或网卡规则，请使用独立节点");
    }
    static String build(JSONObject supplied, int port) throws Exception {
        validate(supplied);
        JSONObject node=new JSONObject(supplied.toString()).put("name","RegionExit").put("udp",true);
        JSONObject dns=new JSONObject().put("enable",true).put("ipv6",false).put("use-hosts",false).put("use-system-hosts",false)
            .put("enhanced-mode","redir-host").put("respect-rules",true)
            .put("nameserver",new JSONArray().put("https://1.1.1.1/dns-query#RegionExit"))
            .put("proxy-server-nameserver",new JSONArray().put("https://1.1.1.1/dns-query"));
        JSONObject tun=new JSONObject().put("enable",false).put("stack","gvisor").put("device","tun0")
            .put("auto-route",false).put("auto-redirect",false).put("auto-detect-interface",false).put("mtu",1500)
            .put("inet4-address",new JSONArray().put(ADDRESS+"/30"))
            .put("dns-hijack",new JSONArray().put("any:53").put("tcp://any:53"));
        return new JSONObject().put("mixed-port",port).put("bind-address","127.0.0.1").put("allow-lan",false)
            .put("ipv6",false).put("mode","rule").put("log-level","warning").put("find-process-mode","off")
            .put("external-controller","").put("dns",dns).put("tun",tun)
            .put("proxies",new JSONArray().put(node)).put("rules",new JSONArray().put("MATCH,RegionExit")).toString();
    }
}
