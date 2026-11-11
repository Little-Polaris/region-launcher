package io.regionlauncher.android;

import java.io.IOException;
import java.util.function.LongSupplier;

/** Runs only when called by the manual check or application launch action. */
final class Recovery {
    @FunctionalInterface interface Step {void run()throws Exception;}
    private final LongSupplier milliseconds;
    private final Step retryDelay;
    private Long lastRepair;

    Recovery(){this(()->System.nanoTime()/1_000_000,()->Thread.sleep(2000));}
    Recovery(LongSupplier milliseconds,Step retryDelay){this.milliseconds=milliseconds;this.retryDelay=retryDelay;}

    void check(Step verify,Step repair)throws Exception{
        try{verify.run();return;}catch(IOException first){/* One failed probe is not enough to restart the core. */}
        retryDelay.run();
        try{verify.run();return;}catch(IOException second){
            long now=milliseconds.getAsLong();
            if(lastRepair!=null&&now-lastRepair<60_000)throw new IOException("实际联网仍失败；距上次重连不足一分钟，请稍后重试",second);
            lastRepair=now;
        }
        repair.run();
        verify.run();
    }
}
