package io.regionlauncher.android;

import android.content.Context;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.time.OffsetDateTime;

final class Journal {
    private static final Object LOCK=new Object();
    private final File file;
    Journal(Context context) {file=new File(context.getFilesDir(),"network.log");}
    void write(String text) { synchronized(LOCK) {
        text=text.replaceAll("(?i)(password|token|secret|uuid|authorization)([\\s\"=:]+)[^\\s,;\"]+","$1=[已隐藏]")
            .replaceAll("https?://[^\\s]+[?][^\\s]+","[已隐藏带参数链接]");
        try {
            if(file.length()>2*1024*1024) {File previous=new File(file.getParentFile(),"network.previous.log"); if(previous.exists()&&!previous.delete())throw new IOException(); if(!file.renameTo(previous))throw new IOException();}
            try(FileOutputStream stream=new FileOutputStream(file,true)){stream.write((OffsetDateTime.now()+" "+text+"\n").getBytes(StandardCharsets.UTF_8));}
        } catch(IOException ignored) {android.util.Log.e("RegionLauncher","无法写入本地诊断日志");}
    }}
    String read() throws IOException {synchronized(LOCK){if(!file.exists())return "尚无日志";try(InputStream in=new FileInputStream(file)){return new String(in.readNBytes(2*1024*1024),StandardCharsets.UTF_8);}}}
}
