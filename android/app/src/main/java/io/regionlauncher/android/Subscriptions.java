package io.regionlauncher.android;

import org.json.*;
import org.yaml.snakeyaml.Yaml;
import org.yaml.snakeyaml.LoaderOptions;
import org.yaml.snakeyaml.constructor.SafeConstructor;
import java.io.*;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.util.*;

final class Subscriptions {
    static final int LIMIT=4*1024*1024;
    static String fetch(String source) throws Exception {
        URI uri=new URI(source.trim());
        if (!"https".equalsIgnoreCase(uri.getScheme()) || uri.getUserInfo()!=null) throw new IOException("订阅地址需要使用 HTTPS");
        HttpURLConnection connection=(HttpURLConnection)uri.toURL().openConnection(Proxy.NO_PROXY);
        connection.setConnectTimeout(15000); connection.setReadTimeout(15000);
        connection.setRequestProperty("User-Agent","ClashMeta/RegionLauncherAndroid");
        connection.setInstanceFollowRedirects(false);
        try {
            if (connection.getResponseCode()!=200) throw new IOException("订阅服务器返回 HTTP "+connection.getResponseCode());
            try(InputStream input=connection.getInputStream()) {
                byte[] value=input.readNBytes(LIMIT+1);
                if(value.length>LIMIT) throw new IOException("订阅文件超过 4 MiB");
                return new String(value,StandardCharsets.UTF_8);
            }
        } finally {connection.disconnect();}
    }
    static JSONArray parse(String content) throws Exception {
        if(content.length()>LIMIT) throw new IOException("订阅文件过大");
        content=content.trim();
        if(!content.contains(":") && !content.startsWith("{")) content=decode(content);
        JSONArray result=new JSONArray();
        if(content.startsWith("[") || content.startsWith("{")) {
            Object value=new JSONTokener(content).nextValue();
            JSONArray array=value instanceof JSONArray?(JSONArray)value:((JSONObject)value).optJSONArray("proxies");
            if(array==null) array=new JSONArray().put(value);
            for(int i=0;i<array.length();i++) add(result,array.getJSONObject(i));
        } else if(content.contains("proxies:")) {
            LoaderOptions options=new LoaderOptions(); options.setCodePointLimit(LIMIT); options.setMaxAliasesForCollections(30); options.setAllowDuplicateKeys(false);
            Object value=new Yaml(new SafeConstructor(options)).load(content);
            if(!(value instanceof Map<?,?> map) || !(map.get("proxies") instanceof List<?> nodes)) throw new IOException("订阅中没有节点列表");
            for(Object item:nodes) { if(!(item instanceof Map<?,?> nodeMap)) throw new IOException("节点格式无效"); add(result,new JSONObject(nodeMap)); }
        } else {
            for(String line:content.split("\\R")) if(!line.isBlank()) add(result,link(line.trim()));
        }
        if(result.length()==0) throw new IOException("没有找到可用节点");
        if(result.length()>2000) throw new IOException("节点过多，请使用较小的订阅");
        return result;
    }
    static void add(JSONArray result,JSONObject node) throws Exception { ProxyConfig.validate(node); result.put(node); }
    static String decode(String value) { return new String(Base64.getDecoder().decode(value.replace('-','+').replace('_','/').replaceAll("\\s+","")),StandardCharsets.UTF_8); }
    static String unescape(String value) { return URLDecoder.decode(value.replace("+","%2B"),StandardCharsets.UTF_8); }
    static JSONObject link(String value) throws Exception {
        if(value.startsWith("vmess://")) {
            JSONObject src=new JSONObject(decode(value.substring(8)));
            JSONObject dst=new JSONObject().put("name",src.optString("ps","VMess")).put("type","vmess").put("server",src.getString("add"))
                .put("port",Integer.parseInt(src.get("port").toString())).put("uuid",src.getString("id")).put("alterId",src.optInt("aid",0)).put("cipher",src.optString("scy","auto"));
            if("tls".equals(src.optString("tls"))) dst.put("tls",true).put("servername",src.optString("sni",src.optString("host")));
            transport(dst,src.optString("net","tcp"),src.optString("path","/"),src.optString("host",""),src.optString("path",""));
            return dst;
        }
        URI uri=new URI(value); String scheme=uri.getScheme();
        if(scheme==null) throw new IOException("无法识别节点链接");
        scheme=scheme.equals("hy2")?"hysteria2":scheme;
        Map<String,String> query=new HashMap<>();
        if(uri.getRawQuery()!=null) for(String pair:uri.getRawQuery().split("&")) { String[] p=pair.split("=",2);query.put(unescape(p[0]),p.length==2?unescape(p[1]):""); }
        String auth=uri.getRawUserInfo()==null?"":unescape(uri.getRawUserInfo());
        if(uri.getHost()==null) throw new IOException("节点链接没有有效服务器地址");
        JSONObject node=new JSONObject().put("type",scheme).put("server",uri.getHost()).put("port",uri.getPort()>0?uri.getPort():443)
            .put("name",uri.getRawFragment()==null?scheme:unescape(uri.getRawFragment()));
        switch(scheme) {
            case "socks5", "http" -> {String[] a=auth.split(":",2);if(!auth.isEmpty()){node.put("username",a[0]);node.put("password",a.length==2?a[1]:"");}if(uri.getPort()<1)throw new IOException("本地或普通代理链接必须指定端口");}
            case "vless" -> node.put("uuid",auth).put("flow",query.getOrDefault("flow",""));
            case "tuic" -> {String[] a=auth.split(":",2);if(a.length!=2)throw new IOException("TUIC 缺少密码");node.put("uuid",a[0]).put("password",a[1]);}
            case "ss" -> {String[] a=(auth.contains(":")?auth:decode(auth)).split(":",2);if(a.length!=2)throw new IOException("Shadowsocks 参数无效");node.put("cipher",a[0]).put("password",a[1]);}
            default -> node.put("password",auth);
        }
        if(query.containsKey("sni"))node.put("sni",query.get("sni")).put("servername",query.get("sni"));
        if(query.containsKey("alpn"))node.put("alpn",new JSONArray(Arrays.asList(query.get("alpn").split(","))));
        if("tls".equals(query.get("security")) || "reality".equals(query.get("security"))) node.put("tls",true);
        if(query.containsKey("fp"))node.put("client-fingerprint",query.get("fp"));
        if("reality".equals(query.get("security")))node.put("reality-opts",new JSONObject().put("public-key",query.getOrDefault("pbk","")).put("short-id",query.getOrDefault("sid","")));
        if(query.containsKey("obfs"))node.put("obfs",query.get("obfs")).put("obfs-password",query.getOrDefault("obfs-password",""));
        if(query.containsKey("insecure")&&!(query.get("insecure").equals("0")||query.get("insecure").equalsIgnoreCase("false")))throw new IOException("此节点要求跳过证书验证，请核对服务端证书或导入明确的 Clash 配置");
        transport(node,query.getOrDefault("type","tcp"),query.getOrDefault("path","/"),query.getOrDefault("host",""),query.getOrDefault("serviceName",""));
        return node;
    }
    static void transport(JSONObject node,String type,String path,String host,String service) throws Exception {
        if(type.equals("tcp") || type.isEmpty())return;
        node.put("network",type);
        if(type.equals("ws"))node.put("ws-opts",new JSONObject().put("path",path).put("headers",new JSONObject().put("Host",host)));
        else if(type.equals("grpc"))node.put("grpc-opts",new JSONObject().put("grpc-service-name",service));
        else throw new IOException("此分享链接的传输类型尚不支持，请导入 Clash 节点配置："+type);
    }
}
