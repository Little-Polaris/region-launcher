package io.regionlauncher.android;

import org.junit.Test;
import java.net.*;
import java.io.*;
import java.util.concurrent.*;
import static org.junit.Assert.*;

public class ProbeCancellationTest {
    @Test(timeout=15000) public void cancellationClosesSocketDuringStalledTlsHandshake()throws Exception{
        ExecutorService worker=Executors.newSingleThreadExecutor();
        try(ServerSocket server=new ServerSocket(0,1,InetAddress.getLoopbackAddress());
            Health.Probe probe=new Health.Probe("example.invalid","/",server.getLocalPort())){
            server.setSoTimeout(5000);
            Future<Health.Result> request=worker.submit(probe);
            try(Socket peer=server.accept()){
                peer.setSoTimeout(5000);InputStream in=peer.getInputStream();OutputStream out=peer.getOutputStream();
                assertEquals(5,in.read());int methods=in.read();assertTrue(methods>0);assertEquals(methods,in.readNBytes(methods).length);
                out.write(new byte[]{5,0});out.flush();
                assertArrayEquals(new byte[]{5,1,0,3},in.readNBytes(4));int length=in.read();assertTrue(length>0);assertEquals(length+2,in.readNBytes(length+2).length);
                out.write(new byte[]{5,0,0,1,127,0,0,1,1,1});out.flush();
                assertEquals(22,in.read()); // TLS ClientHello reached a server that will never finish the handshake.
                probe.close();
                assertFalse(request.get(3,TimeUnit.SECONDS).ok());
            }
        }finally{worker.shutdownNow();}
    }
    @Test public void cancellingBeforeExecutionDoesNotConnect()throws Exception{
        try(ServerSocket server=new ServerSocket(0,1,InetAddress.getLoopbackAddress());
            Health.Probe probe=new Health.Probe("example.invalid","/",server.getLocalPort())){
            probe.close();assertFalse(probe.call().ok());server.setSoTimeout(100);
            assertThrows(SocketTimeoutException.class,server::accept);
        }
    }
}
