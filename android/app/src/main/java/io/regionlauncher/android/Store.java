package io.regionlauncher.android;

import android.content.Context;
import android.util.AtomicFile;
import org.json.*;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.util.*;

final class Store {
    private static final Object LOCK=new Object();
    private final AtomicFile file;
    Store(Context c) { file = new AtomicFile(new File(c.getFilesDir(), "profiles.json")); }
    JSONObject read() throws Exception {synchronized(LOCK){
        if (!file.getBaseFile().exists()) return new JSONObject().put("nodes", new JSONArray()).put("applications", new JSONArray()).put("node", 0);
        return new JSONObject(new String(file.readFully(), StandardCharsets.UTF_8));
    }}
    void save(JSONObject value) throws Exception {synchronized(LOCK){
        FileOutputStream out = file.startWrite();
        try { out.write(value.toString(2).getBytes(StandardCharsets.UTF_8)); file.finishWrite(out); }
        catch (Exception ex) { file.failWrite(out); throw ex; }
    }}
    static LinkedHashSet<String> apps(JSONObject value) {
        LinkedHashSet<String> result = new LinkedHashSet<>();
        JSONArray array = value.optJSONArray("applications");
        if (array != null) for (int i=0;i<array.length();i++) result.add(array.optString(i));
        return result;
    }
    static JSONObject node(JSONObject value) throws Exception {
        JSONArray nodes = value.getJSONArray("nodes");
        if (nodes.length()==0) throw new IOException("请先导入订阅或节点");
        return nodes.getJSONObject(value.optInt("node", 0));
    }
}
