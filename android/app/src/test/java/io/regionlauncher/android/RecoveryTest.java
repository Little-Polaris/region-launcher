package io.regionlauncher.android;

import org.junit.Test;
import java.io.IOException;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicLong;
import static org.junit.Assert.*;

public class RecoveryTest {
    @Test public void healthyConnectionNeverRestarts()throws Exception{
        AtomicInteger checks=new AtomicInteger();
        new Recovery(()->0,()->fail("Unexpected delay")).check(checks::incrementAndGet,()->fail("Healthy core restarted"));
        assertEquals(1,checks.get());
    }
    @Test public void transientFailureDoesNotRestart()throws Exception{
        AtomicInteger checks=new AtomicInteger(),delays=new AtomicInteger();
        new Recovery(()->0,delays::incrementAndGet).check(()->{if(checks.incrementAndGet()==1)throw new IOException("transient");},()->fail("Transient failure restarted core"));
        assertEquals(2,checks.get());assertEquals(1,delays.get());
    }
    @Test public void workingProxyButBrokenVpnPathIsRepaired()throws Exception{
        AtomicInteger checks=new AtomicInteger(),repairs=new AtomicInteger();
        var proxy=java.util.List.of(new Health.Result("probe",true,"203.0.113.4",1,""));
        new Recovery(()->0,()->{}).check(()->{
            checks.incrementAndGet();
            var inside=repairs.get()==0?java.util.List.of(new Health.Result("probe",false,"",1,"timeout")):proxy;
            Health.match(proxy,inside);
        },repairs::incrementAndGet);
        assertEquals(3,checks.get());assertEquals(1,repairs.get());
    }
    @Test public void failedRepairIsVerifiedAndRateLimited()throws Exception{
        AtomicLong now=new AtomicLong();AtomicInteger checks=new AtomicInteger(),repairs=new AtomicInteger();
        Recovery recovery=new Recovery(now::get,()->{});
        Recovery.Step offline=()->{checks.incrementAndGet();throw new IOException("offline");};
        assertThrows(IOException.class,()->recovery.check(offline,repairs::incrementAndGet));
        assertEquals(3,checks.get());assertEquals(1,repairs.get());
        assertThrows(IOException.class,()->recovery.check(offline,repairs::incrementAndGet));
        assertEquals(5,checks.get());assertEquals(1,repairs.get());
        now.set(60_001);
        assertThrows(IOException.class,()->recovery.check(offline,repairs::incrementAndGet));
        assertEquals(8,checks.get());assertEquals(2,repairs.get());
    }
    @Test public void cancellationDoesNotRestartOrRetry(){
        assertThrows(InterruptedException.class,()->new Recovery(()->0,()->fail("Unexpected retry")).check(()->{throw new InterruptedException();},()->fail("Restart after cancellation")));
    }
}
