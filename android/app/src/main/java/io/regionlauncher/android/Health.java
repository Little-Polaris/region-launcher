package io.regionlauncher.android;

import java.io.*;
import java.net.*;
import java.nio.charset.StandardCharsets;
import java.util.*;
import java.util.concurrent.*;
import java.util.regex.*;
import javax.net.ssl.*;

final class Health {
    record Result(String host, boolean ok, String ip, long millis, String error) {}
    private static final String[][] SITES={{"www.cloudflare.com","/cdn-cgi/trace"},{"my.ip.cn","/"}};
    static List<Result> check(Integer socksPort,Journal log) throws Exception {
        ExecutorService workers=Executors.newFixedThreadPool(2);
        try {
            List<Future<Result>> tasks=new ArrayList<>();
            for(String[] site:SITES) tasks.add(workers.submit(()->request(site[0],site[1],socksPort)));
            List<Result> results=new ArrayList<>();
            for(Future<Result> task:tasks) {
                Result r=task.get(25,TimeUnit.SECONDS);results.add(r);
                log.write((socksPort==null?"[环境内] ":"[指定代理] ")+r.host()+" "+(r.ok()?"HTTPS 200 出口="+r.ip():"失败 "+r.error())+" 耗时="+r.millis()+"ms");
            }
            return results;
        } finally {workers.shutdownNow();}
    }
    static boolean usable(List<Result> values) {return values.stream().anyMatch(Result::ok);}
    static void match(List<Result> proxy,List<Result> inside) throws IOException {
        boolean matched=false;
        for(Result a:proxy)for(Result b:inside) if(a.host().equals(b.host())&&a.ok()&&b.ok()) {
            if(!a.ip().equals(b.ip()))throw new IOException("代理与环境内出口不一致，未启动应用");matched=true;
        }
        if(!matched)throw new IOException("环境内联网未验证通过，未启动应用");
    }
    static Result request(String host,String path,Integer port) {
        long start=System.nanoTime();
        try {
            byte[] body;
            if(port!=null) {
                try(Socket raw=new Socket(new Proxy(Proxy.Type.SOCKS,new InetSocketAddress("127.0.0.1",port)))) {
                    raw.setSoTimeout(10000);raw.connect(InetSocketAddress.createUnresolved(host,443),10000);
                    try(SSLSocket tls=(SSLSocket)((SSLSocketFactory)SSLSocketFactory.getDefault()).createSocket(raw,host,443,true)) {
                        SSLParameters parameters=tls.getSSLParameters();parameters.setEndpointIdentificationAlgorithm("HTTPS");tls.setSSLParameters(parameters);tls.setSoTimeout(10000);tls.startHandshake();
                        tls.getOutputStream().write(("GET "+path+" HTTP/1.1\r\nHost: "+host+"\r\nUser-Agent: RegionLauncher-Connectivity/1.0\r\nAccept-Encoding: identity\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n").getBytes(StandardCharsets.US_ASCII));
                        body=HttpResponse.read(tls.getInputStream());
                    }
                }
            } else {
                HttpsURLConnection connection=(HttpsURLConnection)new URL("https://"+host+path).openConnection(Proxy.NO_PROXY);
                connection.setConnectTimeout(10000);connection.setReadTimeout(10000);connection.setInstanceFollowRedirects(false);connection.setRequestProperty("Cache-Control","no-cache");
                try {if(connection.getResponseCode()!=200)throw new IOException("检测站未返回 HTTP 200");try(InputStream stream=connection.getInputStream()){body=stream.readNBytes(65537);}if(body.length>65536)throw new IOException("响应过大");}
                finally {connection.disconnect();}
            }
            String value=new String(body,StandardCharsets.UTF_8);
            Matcher match=Pattern.compile(host.equals("www.cloudflare.com")?"(?m)^ip=(\\d{1,3}(?:\\.\\d{1,3}){3})\\s*$":"\\b(\\d{1,3}(?:\\.\\d{1,3}){3})\\b").matcher(value);
            if(!match.find())throw new IOException("响应中没有 IPv4 出口");String ip=match.group(1);
            for(String part:ip.split("\\."))if(Integer.parseInt(part)>255)throw new IOException("无效出口地址");
            return new Result(host,true,ip,(System.nanoTime()-start)/1000000,"");
        } catch(Exception ex) {return new Result(host,false,"",(System.nanoTime()-start)/1000000,ex instanceof SocketTimeoutException?"请求超时":ex.getClass().getSimpleName());}
    }
}
