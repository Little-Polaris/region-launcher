package io.regionlauncher.android;

import java.io.*;
import java.nio.charset.StandardCharsets;
import java.util.*;

/** Bounded HTTP/1 response reader for the explicit SOCKS connectivity probe. */
final class HttpResponse {
    static byte[] read(InputStream in) throws IOException {
        String status=line(in,4096);
        if(!status.matches("HTTP/1\\.[01] 200(?: .*)?"))throw new IOException("检测站未返回 HTTP 200");
        Map<String,String> headers=new HashMap<>();int size=status.length();
        for(;;){String s=line(in,8192);size+=s.length()+2;if(size>16384)throw new IOException("响应头过大");if(s.isEmpty())break;
            int colon=s.indexOf(':');if(colon<1)throw new IOException("响应头无效");String key=s.substring(0,colon).toLowerCase(Locale.ROOT);
            if(headers.put(key,s.substring(colon+1).trim())!=null&&(key.equals("content-length")||key.equals("transfer-encoding")))throw new IOException("响应长度有歧义");}
        String encoding=headers.get("transfer-encoding");
        if(encoding!=null){
            if(!encoding.equalsIgnoreCase("chunked")||headers.containsKey("content-length"))throw new IOException("不支持的响应编码");
            ByteArrayOutputStream out=new ByteArrayOutputStream();
            for(;;){String length=line(in,128).split(";",2)[0];long n;
                try{n=Long.parseLong(length.trim(),16);}catch(NumberFormatException ex){throw new IOException("分块长度无效");}
                if(n<0||n>65536-out.size())throw new IOException("响应过大");
                if(n==0)return out.toByteArray();
                byte[] part=in.readNBytes((int)n);if(part.length!=n)throw new EOFException("响应不完整");out.write(part);
                if(!line(in,2).isEmpty())throw new IOException("分块分隔符无效");}
        }
        String length=headers.get("content-length");
        if(length!=null){long n;try{n=Long.parseLong(length);}catch(NumberFormatException ex){throw new IOException("响应长度无效");}
            if(n<0||n>65536)throw new IOException("响应过大");byte[] body=in.readNBytes((int)n);if(body.length!=n)throw new EOFException("响应不完整");return body;}
        byte[] body=in.readNBytes(65537);if(body.length>65536)throw new IOException("响应过大");return body;
    }
    private static String line(InputStream in,int limit)throws IOException{
        ByteArrayOutputStream out=new ByteArrayOutputStream();int last=-1;
        for(int i=0;i<=limit+1;i++){int b=in.read();if(b<0)throw new EOFException("响应不完整");if(last=='\r'&&b=='\n'){byte[] v=out.toByteArray();return new String(v,0,v.length-1,StandardCharsets.US_ASCII);}out.write(b);last=b;}
        throw new IOException("响应行过长");
    }
}
