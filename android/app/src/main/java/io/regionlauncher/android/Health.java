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
        List<Probe> probes=new ArrayList<>();
        List<Future<Result>> tasks=new ArrayList<>();
        long started=System.nanoTime(),deadline=started+TimeUnit.SECONDS.toNanos(25);
        try {
            for(String[] site:SITES){Probe probe=new Probe(site[0],site[1],socksPort);probes.add(probe);tasks.add(workers.submit(probe));}
            List<Result> results=new ArrayList<>();
            for(int i=0;i<tasks.size();i++) {
                Future<Result> task=tasks.get(i);Result r;
                try{r=task.get(Math.max(1,deadline-System.nanoTime()),TimeUnit.NANOSECONDS);}
                catch(TimeoutException ex){task.cancel(true);probes.get(i).close();r=new Result(SITES[i][0],false,"",(System.nanoTime()-started)/1_000_000,"请求超时");}
                catch(ExecutionException ex){r=new Result(SITES[i][0],false,"",(System.nanoTime()-started)/1_000_000,"检测异常");}
                results.add(r);
                log.write((socksPort==null?"[环境内] ":"[指定代理] ")+r.host()+" "+(r.ok()?"HTTPS 200 出口="+r.ip():"失败 "+r.error())+" 耗时="+r.millis()+"ms");
            }
            return results;
        } finally {for(Future<Result> task:tasks)task.cancel(true);for(Probe probe:probes)probe.close();workers.shutdownNow();}
    }
    static boolean usable(List<Result> values) {return values.stream().anyMatch(Result::ok);}
    static String extractIp(String host,String value)throws IOException{
        String address="(\\d{1,3}(?:\\.\\d{1,3}){3})";
        String pattern;
        if(host.equals("www.cloudflare.com"))pattern="(?m)^ip="+address+"\\s*$";
        else if(host.equals("my.ip.cn")){value=value.replaceAll("<[^>]*>"," ");pattern="(?i)\\bip\\s*[：:]\\s*"+address+"(?![\\d.])";}
        else throw new IOException("未知的检测站响应格式");
        Matcher match=Pattern.compile(pattern).matcher(value);
        if(!match.find())throw new IOException("响应中没有明确的 IPv4 出口字段");String ip=match.group(1);
        if(match.find())throw new IOException("响应中包含多个出口字段");
        for(String part:ip.split("\\."))if(Integer.parseInt(part)>255)throw new IOException("无效出口地址");
        return ip;
    }
    static void match(List<Result> proxy,List<Result> inside) throws IOException {
        boolean matched=false;
        for(Result a:proxy)for(Result b:inside) if(a.host().equals(b.host())&&a.ok()&&b.ok()) {
            if(!a.ip().equals(b.ip()))throw new IOException("代理与环境内出口不一致，未启动应用");matched=true;
        }
        if(!matched)throw new IOException("环境内联网未验证通过，未启动应用");
    }
    static final class Probe implements Callable<Result>, AutoCloseable {
        private final String host,path;
        private final Integer port;
        private AutoCloseable active;
        private boolean cancelled;
        Probe(String host,String path,Integer port){this.host=host;this.path=path;this.port=port;}
        private synchronized void hold(AutoCloseable resource)throws Exception{
            if(cancelled){resource.close();throw new InterruptedIOException("检测已取消");}
            active=resource;
        }
        @Override public void close(){
            AutoCloseable resource;
            synchronized(this){cancelled=true;resource=active;active=null;}
            if(resource!=null)try{resource.close();}catch(Exception ignored){}
        }
        @Override public Result call() {
        long start=System.nanoTime();
        try {
            byte[] body;
            if(port!=null) {
                try(Socket raw=new Socket(new Proxy(Proxy.Type.SOCKS,new InetSocketAddress("127.0.0.1",port)))) {
                    // Keep the raw socket so cancellation can interrupt a TLS handshake immediately.
                    hold(raw);
                    raw.setSoTimeout(10000);raw.connect(InetSocketAddress.createUnresolved(host,443),10000);
                    try(SSLSocket tls=(SSLSocket)((SSLSocketFactory)SSLSocketFactory.getDefault()).createSocket(raw,host,443,true)) {
                        SSLParameters parameters=tls.getSSLParameters();parameters.setEndpointIdentificationAlgorithm("HTTPS");tls.setSSLParameters(parameters);tls.setSoTimeout(10000);tls.startHandshake();
                        tls.getOutputStream().write(("GET "+path+" HTTP/1.1\r\nHost: "+host+"\r\nUser-Agent: RegionLauncher-Connectivity/1.0\r\nAccept-Encoding: identity\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n").getBytes(StandardCharsets.US_ASCII));
                        body=HttpResponse.read(tls.getInputStream());
                    }
                }
            } else {
                HttpsURLConnection connection=(HttpsURLConnection)new URL("https://"+host+path).openConnection(Proxy.NO_PROXY);
                hold(connection::disconnect);
                connection.setConnectTimeout(10000);connection.setReadTimeout(10000);connection.setInstanceFollowRedirects(false);connection.setRequestProperty("Cache-Control","no-cache");
                try {if(connection.getResponseCode()!=200)throw new IOException("检测站未返回 HTTP 200");try(InputStream stream=connection.getInputStream()){body=stream.readNBytes(65537);}if(body.length>65536)throw new IOException("响应过大");}
                finally {connection.disconnect();}
            }
            String ip=extractIp(host,new String(body,StandardCharsets.UTF_8));
            return new Result(host,true,ip,(System.nanoTime()-start)/1000000,"");
        } catch(Exception ex) {return new Result(host,false,"",(System.nanoTime()-start)/1000000,ex instanceof SocketTimeoutException?"请求超时":ex.getClass().getSimpleName());}
        finally {close();}
        }
    }
}
