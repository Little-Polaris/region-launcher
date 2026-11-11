package io.regionlauncher.android;

import org.junit.Test;
import org.json.*;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.util.*;
import static org.junit.Assert.*;

public class ConfigurationTest {
    private static JSONObject node()throws Exception{return new JSONObject("{\"name\":\"fixture\",\"type\":\"socks5\",\"server\":\"proxy.example\",\"port\":1080}");}
    @Test public void configHasOneProxyAndNoBypass()throws Exception{
        JSONObject c=new JSONObject(ProxyConfig.build(node(),18291));
        assertEquals("MATCH,RegionExit",c.getJSONArray("rules").getString(0));assertEquals(1,c.getJSONArray("rules").length());
        assertFalse(c.getBoolean("ipv6"));assertFalse(c.getBoolean("allow-lan"));assertEquals("127.0.0.1",c.getString("bind-address"));
        assertFalse(c.getJSONObject("tun").getBoolean("auto-route"));assertFalse(c.getJSONObject("tun").getBoolean("auto-redirect"));
        assertTrue(c.getJSONObject("dns").getJSONArray("nameserver").getString(0).endsWith("#RegionExit"));
        assertEquals(1,c.getJSONArray("proxies").length());
    }
    @Test public void rejectsAlternateDialer()throws Exception{
        JSONObject n=node().put("dialer-proxy","DIRECT");assertThrows(IOException.class,()->ProxyConfig.build(n,18291));
    }
    @Test public void ignoresSubscriptionRoutingRules()throws Exception{
        JSONArray n=Subscriptions.parse("proxies:\n  - {name: test, type: socks5, server: proxy.example, port: 1080}\nrules: [MATCH,DIRECT]\n");
        assertEquals(1,n.length());assertEquals("MATCH,RegionExit",new JSONObject(ProxyConfig.build(n.getJSONObject(0),18291)).getJSONArray("rules").getString(0));
    }
    @Test public void parsesMultipleShareLinksAndReality()throws Exception{
        String links="vless://11111111-1111-1111-1111-111111111111@proxy.example:443?security=reality&pbk=public&sid=01&fp=chrome#fixture\nhy2://secret@second.example:443#second";
        JSONArray nodes=Subscriptions.parse(Base64.getEncoder().encodeToString(links.getBytes(StandardCharsets.UTF_8)));
        assertEquals(2,nodes.length());assertEquals("public",nodes.getJSONObject(0).getJSONObject("reality-opts").getString("public-key"));assertEquals("hysteria2",nodes.getJSONObject(1).getString("type"));
    }
    @Test public void rejectsUnsafeYamlAndDuplicateFields(){
        assertThrows(Exception.class,()->Subscriptions.parse("proxies: !!java.net.URL [https://example.com]"));
        assertThrows(Exception.class,()->Subscriptions.parse("proxies:\n - {name: x, type: socks5, server: one, server: two, port: 1080}"));
    }
    @Test public void parsesAuthenticatedLocalProxyWithoutLosingUsername()throws Exception{
        JSONObject n=Subscriptions.parse("socks5://user:p%2Bss@127.0.0.1:1080#local").getJSONObject(0);
        assertEquals("user",n.getString("username"));assertEquals("p+ss",n.getString("password"));assertEquals(1080,n.getInt("port"));
        assertThrows(IOException.class,()->Subscriptions.parse("socks5://127.0.0.1"));
    }
    @Test public void decodesChunkBoundariesInsideIp()throws Exception{
        String raw="HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n6\r\nip=203\r\n9\r\n.0.113.7\n\r\n0\r\n\r\n";
        assertEquals("ip=203.0.113.7\n",new String(HttpResponse.read(new ByteArrayInputStream(raw.getBytes(StandardCharsets.US_ASCII))),StandardCharsets.US_ASCII));
    }
    @Test public void rejectsTruncatedOrAmbiguousResponse(){
        for(String s:List.of("HTTP/1.1 200 OK\r\nContent-Length: 9\r\n\r\nshort","HTTP/1.1 200 OK\r\nContent-Length: 1\r\nTransfer-Encoding: chunked\r\n\r\n0\r\n\r\n"))assertThrows(IOException.class,()->HttpResponse.read(new ByteArrayInputStream(s.getBytes(StandardCharsets.US_ASCII))));
    }
    @Test public void rejectsMismatchOrUnprovenEnvironment(){
        var proxy=List.of(new Health.Result("probe",true,"203.0.113.1",10,""));
        assertThrows(IOException.class,()->Health.match(proxy,List.of(new Health.Result("probe",true,"198.51.100.1",10,""))));
        assertThrows(IOException.class,()->Health.match(proxy,List.of(new Health.Result("probe",false,"",10,"timeout"))));
    }
    @Test public void onlyAcceptsExplicitExitAddressFields()throws Exception{
        assertEquals("203.0.113.7",Health.extractIp("my.ip.cn","<p>ip：<span>203.0.113.7</span> 归属地：测试</p>"));
        assertEquals("203.0.113.7",Health.extractIp("www.cloudflare.com","h=www.cloudflare.com\nip=203.0.113.7\n"));
        assertThrows(IOException.class,()->Health.extractIp("my.ip.cn","请访问 203.0.113.7 联系客服"));
        assertThrows(IOException.class,()->Health.extractIp("www.cloudflare.com","ip=203.0.113.7\nip=198.51.100.1\n"));
        assertThrows(IOException.class,()->Health.extractIp("www.cloudflare.com","ip=999.0.113.7\n"));
    }
}
