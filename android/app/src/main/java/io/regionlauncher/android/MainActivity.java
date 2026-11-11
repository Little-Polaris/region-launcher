package io.regionlauncher.android;

import android.Manifest;
import android.app.*;
import android.app.admin.DevicePolicyManager;
import android.content.*;
import android.content.pm.*;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.net.*;
import android.os.Bundle;
import android.provider.Settings;
import android.view.*;
import android.widget.*;
import org.json.*;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.util.*;
import java.util.concurrent.*;

public final class MainActivity extends Activity {
    private Store store;
    private JSONObject settings;
    private LinearLayout content;
    private TextView status, apps;
    private Spinner nodes, launch;
    private Button importButton, updateButton;
    private boolean loading, visible, busy;
    private final List<String> launchPackages=new ArrayList<>();
    private final ExecutorService worker=Executors.newSingleThreadExecutor();
    private String pendingTarget;
    private final BroadcastReceiver updates=new BroadcastReceiver(){@Override public void onReceive(Context c,Intent i){
        refreshStatus();
        String pkg=i.getStringExtra("target");
        if(visible&&i.getBooleanExtra("ready",false)&&pkg!=null) {
            Intent open=getPackageManager().getLaunchIntentForPackage(pkg);
            if(open!=null) {pendingTarget=null;startActivity(open);} else problem("应用无法打开，请确认它仍已安装");
        }
    }};
    @Override public void onCreate(Bundle state) {
        super.onCreate(state);store=new Store(this);
        try {settings=store.read();}catch(Exception ex){problem("配置文件读取失败，请查看应用数据");settings=new JSONObject();}
        ScrollView scroll=new ScrollView(this);content=new LinearLayout(this);content.setOrientation(LinearLayout.VERTICAL);content.setPadding(dp(20),dp(20),dp(20),dp(24));scroll.addView(content);setContentView(scroll);
        scroll.setOnApplyWindowInsetsListener((v,insets)->{android.graphics.Insets bars=insets.getInsets(WindowInsets.Type.systemBars());v.setPadding(bars.left,bars.top,bars.right,bars.bottom);return insets;});
        TextView heading=label("Region Launcher",27);heading.setTypeface(null,Typeface.BOLD);
        label("按应用联网 · Android 开发版",14).setTextColor(Color.DKGRAY);
        status=label("未连接",18);
        label("独立代理",18).setTypeface(null,Typeface.BOLD);
        nodes=new Spinner(this);content.addView(nodes);
        nodes.setOnItemSelectedListener(new AdapterView.OnItemSelectedListener(){public void onNothingSelected(AdapterView<?> p){} public void onItemSelected(AdapterView<?> p,View v,int pos,long id){if(!loading)try{settings.put("node",pos);store.save(settings);}catch(Exception ex){problem("无法保存节点选择");}}});
        importButton=button("导入订阅或节点",this::importNodes);
        updateButton=button("更新当前订阅",()->{String source=settings.optString("subscription","");if(source.isEmpty()){problem("当前节点由手动导入，未保存订阅地址");return;}loadNodes(source,true);});
        label("运行应用",18).setTypeface(null,Typeface.BOLD);
        apps=label("尚未选择应用",14);button("添加或移除应用",this::chooseApps);
        launch=new Spinner(this);content.addView(launch);
        button("启动所选应用",()->{if(launchPackages.isEmpty()){problem("请先添加应用");return;}request(launchPackages.get(Math.max(0,launch.getSelectedItemPosition())));});
        button("检测并修复",()->request(null));
        button("停止连接",()->new AlertDialog.Builder(this).setTitle("停止此环境的连接？").setMessage("所选应用将恢复手机原有网络。Android 普通应用不能强制结束其他应用，请先退出需要继续保护的应用。").setNegativeButton("取消",null).setPositiveButton("停止连接",(d,w)->startService(new Intent(this,RegionVpnService.class).setAction(RegionVpnService.STOP))).show());
        label("运行资料",18).setTypeface(null,Typeface.BOLD);
        boolean managed=getSystemService(android.os.UserManager.class).isManagedProfile();
        label(managed?"当前位于独立工作资料。个人资料中的应用与 VPN 分开运行。":"当前位于个人资料。所选应用从桌面打开时也会使用此连接；需要独立副本时，请创建工作资料。",14);
        if(!managed)button("创建独立工作资料",this::provision);
        label(managed?"请在工作资料中安装应用后再选择。连接时会为本应用管理的工作资料开启断网保护：未选中的工作应用也将断网，个人资料不变。停止连接会解除此保护。":"个人资料模式不提供进程意外退出后的断网保护。需要避免恢复直连时，请使用本应用创建的工作资料。",14).setTextColor(Color.DKGRAY);
        button("所选应用的系统语言设置",()->{if(launchPackages.isEmpty()){problem("请先选择应用");return;}try{startActivity(new Intent(Settings.ACTION_APP_LOCALE_SETTINGS,Uri.parse("package:"+launchPackages.get(launch.getSelectedItemPosition()))));}catch(ActivityNotFoundException ex){problem("当前系统未提供此应用的独立语言设置");}});
        label("未 Root：无法强制更改其他应用读到的地区和时区。支持的应用可使用系统语言设置；这里不会修改手机全局地区、时区或语言。",14).setTextColor(Color.DKGRAY);
        button("查看日志",()->{try{new AlertDialog.Builder(this).setTitle("网络日志").setMessage(new Journal(this).read()).setPositiveButton("关闭",null).setNeutralButton("导出",(d,w)->startActivityForResult(new Intent(Intent.ACTION_CREATE_DOCUMENT).setType("text/plain").addCategory(Intent.CATEGORY_OPENABLE).putExtra(Intent.EXTRA_TITLE,"region-launcher-network.log"),3)).show();}catch(IOException ex){problem("无法读取日志");}});
        registerReceiver(updates,new IntentFilter(RegionVpnService.EVENT),Context.RECEIVER_NOT_EXPORTED);
        refreshLists();refreshStatus();
        if(checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS)!=PackageManager.PERMISSION_GRANTED)requestPermissions(new String[]{Manifest.permission.POST_NOTIFICATIONS},1);
    }
    private int dp(int value){return Math.round(value*getResources().getDisplayMetrics().density);}
    private TextView label(String text,int size){TextView view=new TextView(this);view.setText(text);view.setTextSize(size);view.setPadding(0,dp(10),0,dp(8));content.addView(view);return view;}
    private Button button(String title,Runnable click){Button view=new Button(this);view.setText(title);view.setAllCaps(false);view.setOnClickListener(v->click.run());content.addView(view,new LinearLayout.LayoutParams(-1,dp(52)));return view;}
    private void problem(String message){if(!isFinishing())new AlertDialog.Builder(this).setTitle("提示").setMessage(message).setPositiveButton("知道了",null).show();}
    private String name(String pkg){try{return getPackageManager().getApplicationLabel(getPackageManager().getApplicationInfo(pkg,0)).toString();}catch(PackageManager.NameNotFoundException e){return pkg;}}
    private void refreshLists(){
        loading=true;List<String> names=new ArrayList<>();JSONArray values=settings.optJSONArray("nodes");
        if(values!=null)for(int i=0;i<values.length();i++)names.add(values.optJSONObject(i).optString("name","节点 "+(i+1)));
        if(names.isEmpty())names.add("请先导入节点");
        nodes.setAdapter(new ArrayAdapter<>(this,android.R.layout.simple_spinner_dropdown_item,names));nodes.setSelection(Math.min(settings.optInt("node",0),names.size()-1));
        launchPackages.clear();launchPackages.addAll(Store.apps(settings));List<String> labels=new ArrayList<>();for(String pkg:launchPackages)labels.add(name(pkg));
        apps.setText(getString(R.string.selected_apps,labels.size()));
        if(labels.isEmpty())labels.add("尚未选择应用");launch.setAdapter(new ArrayAdapter<>(this,android.R.layout.simple_spinner_dropdown_item,labels));
        nodes.post(()->loading=false);
    }
    private void refreshStatus(){SharedPreferences p=getSharedPreferences("status",MODE_PRIVATE);status.setText(RegionVpnService.alive?p.getString("message","未连接"):"未连接");boolean active=RegionVpnService.alive&&p.getBoolean("active",false);nodes.setEnabled(!active&&!busy);importButton.setEnabled(!active&&!busy);updateButton.setEnabled(!active&&!busy&&!settings.optString("subscription","").isEmpty());}
    private void importNodes(){
        EditText field=new EditText(this);field.setMinLines(3);field.setMaxLines(8);field.setHint("HTTPS 订阅链接、分享链接，或 Clash YAML / JSON");field.setInputType(android.text.InputType.TYPE_CLASS_TEXT|android.text.InputType.TYPE_TEXT_FLAG_MULTI_LINE|android.text.InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS);
        new AlertDialog.Builder(this).setTitle("导入独立代理").setView(field).setNegativeButton("取消",null).setPositiveButton("导入",(dialog,which)->{
            loadNodes(field.getText().toString().trim(),false);
        }).show();
    }
    private void loadNodes(String input,boolean update){
        busy=true;refreshStatus();status.setText("正在读取节点…");
        String previous="";try{previous=Store.node(settings).optString("name","");}catch(Exception ignored){}
        final String selectedName=previous;
        worker.execute(()->{try{
            boolean subscription=input.startsWith("https://");
            JSONArray parsed=Subscriptions.parse(subscription?Subscriptions.fetch(input):input);
            runOnUiThread(()->{try{
                int selected=0;if(update)for(int i=0;i<parsed.length();i++)if(parsed.getJSONObject(i).optString("name").equals(selectedName)){selected=i;break;}
                settings.put("nodes",parsed).put("node",selected).put("subscription",subscription?input:"");store.save(settings);refreshLists();new Journal(this).write("已导入节点，数量="+parsed.length());
            }catch(Exception ex){problem("无法保存节点");}finally{busy=false;refreshStatus();}});
        }catch(Exception ex){runOnUiThread(()->{busy=false;refreshStatus();problem("导入失败：请检查订阅或节点格式。"+(ex instanceof IOException?"\n"+ex.getMessage():""));});}});
    }
    private void chooseApps(){
        List<ResolveInfo> resolved=getPackageManager().queryIntentActivities(new Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER),0);
        Map<String,String> available=new TreeMap<>();for(ResolveInfo info:resolved)if(!info.activityInfo.packageName.equals(getPackageName()))available.put(info.activityInfo.packageName,info.loadLabel(getPackageManager()).toString());
        List<String> packages=new ArrayList<>(available.keySet());packages.sort(Comparator.comparing(available::get));Set<String> selection=Store.apps(settings);
        LinearLayout layout=new LinearLayout(this);layout.setOrientation(LinearLayout.VERTICAL);EditText search=new EditText(this);search.setSingleLine(true);search.setHint("搜索应用名称或包名");layout.addView(search);
        ListView list=new ListView(this);list.setChoiceMode(ListView.CHOICE_MODE_MULTIPLE);layout.addView(list,new LinearLayout.LayoutParams(-1,dp(360)));List<String> filtered=new ArrayList<>();
        Runnable filter=()->{String q=search.getText().toString().toLowerCase(Locale.ROOT);filtered.clear();List<String> labels=new ArrayList<>();for(String pkg:packages)if((available.get(pkg)+pkg).toLowerCase(Locale.ROOT).contains(q)){filtered.add(pkg);labels.add(available.get(pkg)+"\n"+pkg);}list.setAdapter(new ArrayAdapter<>(this,android.R.layout.simple_list_item_multiple_choice,labels));for(int i=0;i<filtered.size();i++)list.setItemChecked(i,selection.contains(filtered.get(i)));};
        search.addTextChangedListener(new android.text.TextWatcher(){public void beforeTextChanged(CharSequence s,int a,int c,int f){}public void onTextChanged(CharSequence s,int a,int b,int c){filter.run();}public void afterTextChanged(android.text.Editable s){}});
        list.setOnItemClickListener((parent,view,position,id)->{String pkg=filtered.get(position);if(list.isItemChecked(position))selection.add(pkg);else selection.remove(pkg);});filter.run();
        new AlertDialog.Builder(this).setTitle("环境内应用").setView(layout).setNegativeButton("取消",null).setPositiveButton("保存",(d,w)->{try{settings.put("applications",new JSONArray(selection));store.save(settings);refreshLists();}catch(Exception ex){problem("无法保存应用列表");}}).show();
    }
    private void request(String target){
        if(busy||RegionVpnService.isChecking()){problem("检测正在进行，请稍候");return;}
        ConnectivityManager manager=getSystemService(ConnectivityManager.class);
        if(!RegionVpnService.alive)for(Network network:manager.getAllNetworks()){NetworkCapabilities caps=manager.getNetworkCapabilities(network);if(caps!=null&&caps.hasTransport(NetworkCapabilities.TRANSPORT_VPN)){problem("此资料已有其他 VPN，已停止操作，避免影响原来的连接。请在独立工作资料中使用 Region Launcher。");return;}}
        try{Store.node(settings);if(Store.apps(settings).isEmpty())throw new IOException("请先选择要使用的应用");}catch(Exception ex){problem(ex.getMessage());return;}
        pendingTarget=target;Intent permission=VpnService.prepare(this);
        if(permission!=null)startActivityForResult(permission,2);else startCheck();
    }
    private void startCheck(){startForegroundService(new Intent(this,RegionVpnService.class).setAction(RegionVpnService.CHECK).putExtra("target",pendingTarget));}
    private void provision(){
        DevicePolicyManager policy=getSystemService(DevicePolicyManager.class);
        if(!policy.isProvisioningAllowed(DevicePolicyManager.ACTION_PROVISION_MANAGED_PROFILE)){problem("当前系统不允许创建新的工作资料，可能已存在工作资料或厂商限制");return;}
        Intent create=new Intent(DevicePolicyManager.ACTION_PROVISION_MANAGED_PROFILE).putExtra(DevicePolicyManager.EXTRA_PROVISIONING_DEVICE_ADMIN_COMPONENT_NAME,new ComponentName(this,ProfileAdmin.class));
        try{startActivityForResult(create,4);}catch(ActivityNotFoundException ex){problem("此系统没有工作资料创建入口");}
    }
    @Override protected void onActivityResult(int request,int result,Intent data){super.onActivityResult(request,result,data);if(request==2){if(result==RESULT_OK)startCheck();else problem("未获得 VPN 授权，应用没有启动");}if(request==3&&result==RESULT_OK&&data!=null)try(OutputStream out=getContentResolver().openOutputStream(data.getData())){out.write(new Journal(this).read().getBytes(StandardCharsets.UTF_8));}catch(IOException ex){problem("日志导出失败");}}
    @Override protected void onResume(){super.onResume();visible=true;if(status!=null)refreshStatus();}
    @Override protected void onPause(){visible=false;super.onPause();}
    @Override protected void onDestroy(){unregisterReceiver(updates);worker.shutdownNow();super.onDestroy();}
}
