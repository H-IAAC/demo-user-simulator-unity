using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

[DisallowMultipleComponent]
public class UltralightLocalServer : MonoBehaviour
{
    [Serializable]
    private class AutoLoadView
    {
        public Ultralight target;
        public string page = "index.html";
    }

    [Header("Binding")]
    [SerializeField] private Ultralight targetUltralight;
    [SerializeField] private List<AutoLoadView> additionalViews = new List<AutoLoadView>();

    [Header("Content")]
    [SerializeField] private string streamingAssetsSubfolder = "ui";
    [SerializeField] private string startPage = "groups_map.html";

    [Header("Server")]
    [SerializeField] private int port = 8787;
    [SerializeField] private bool autoStart = true;
    [SerializeField] private bool autoLoadInUltralight = true;
    [SerializeField] private bool logRequests = false;

    [Header("Additional Views")]
    [SerializeField] private bool autoLayoutAdditionalViews = false;
    [SerializeField] private int additionalViewsSpacing = 20;

    [Header("HTML Optimization")]
    [SerializeField] private bool optimizeLargeHtmlFiles = true;
    [SerializeField] private int largeHtmlThresholdBytes = 25 * 1024 * 1024;
    [SerializeField] private bool downsampleTypedArraysForUltralight = true;
    [SerializeField] private int maxTypedArrayPoints = 10000;
    [SerializeField] private bool convertTypedArraysToPlainJson = true;
    [SerializeField] private bool injectUltralightClientDiagnostics = true;
    [SerializeField] private bool logOptimization = true;

    private HttpListener listener;
    private CancellationTokenSource cancellation;
    private string rootPath;
    private string startUrl;

    private static readonly string[] HeavyJsonFieldsForUltralight = { "customdata", "hovertext" };
    private const string OptimizationMarkerPrefix = "<!-- UL_OPTIMIZED_V5";

    private static readonly Dictionary<string, string> ContentTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        { ".html", "text/html; charset=utf-8" },
        { ".htm", "text/html; charset=utf-8" },
        { ".js", "application/javascript; charset=utf-8" },
        { ".css", "text/css; charset=utf-8" },
        { ".json", "application/json; charset=utf-8" },
        { ".svg", "image/svg+xml" },
        { ".png", "image/png" },
        { ".jpg", "image/jpeg" },
        { ".jpeg", "image/jpeg" },
        { ".gif", "image/gif" },
        { ".ico", "image/x-icon" },
        { ".woff", "font/woff" },
        { ".woff2", "font/woff2" },
        { ".ttf", "font/ttf" },
        { ".map", "application/json; charset=utf-8" }
    };

    public string StartUrl => startUrl;

    private void Awake()
    {
        if (targetUltralight == null)
            targetUltralight = GetComponent<Ultralight>();
    }

    private void Start()
    {
        if (autoStart)
            StartServer();
    }

    public void StartServer()
    {
#if UNITY_WEBGL
        Debug.LogWarning("[UltralightLocalServer] HttpListener is not available on WebGL.");
        return;
#else
        if (listener != null && listener.IsListening)
            return;

        if (port <= 0 || port > 65535)
        {
            Debug.LogError($"[UltralightLocalServer] Invalid port: {port}");
            return;
        }

        rootPath = ResolveRootPath();
        if (!Directory.Exists(rootPath))
        {
            Debug.LogError($"[UltralightLocalServer] Folder not found: {rootPath}");
            return;
        }

        string safeStartPage = SanitizeRelativePath(startPage);
        if (string.IsNullOrEmpty(safeStartPage))
            safeStartPage = "index.html";

        string startPageForLoad = ResolvePageForLoad(safeStartPage);
        startUrl = BuildUrl(startPageForLoad);

        listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");

        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[UltralightLocalServer] Could not start server on port {port}: {ex.Message}");
            listener.Close();
            listener = null;
            return;
        }

        cancellation = new CancellationTokenSource();
        _ = ListenLoop(cancellation.Token);

        if (autoLoadInUltralight)
            AutoLoadConfiguredViews(safeStartPage);

        Debug.Log($"[UltralightLocalServer] Serving '{rootPath}' at {startUrl}");
#endif
    }

    public void StopServer()
    {
        if (cancellation != null)
        {
            cancellation.Cancel();
            cancellation.Dispose();
            cancellation = null;
        }

        if (listener != null)
        {
            if (listener.IsListening)
                listener.Stop();
            listener.Close();
            listener = null;
        }
    }

    private void OnDestroy()
    {
        StopServer();
    }

    private void OnDisable()
    {
        StopServer();
    }

    public string GetUrlForPage(string page)
    {
        string safePage = SanitizeRelativePath(page);
        if (string.IsNullOrEmpty(safePage))
            return string.Empty;
        string resolvedPage = ResolvePageForLoad(safePage);
        return BuildUrl(resolvedPage);
    }

    public bool LoadPage(Ultralight ultralight, string page)
    {
        if (ultralight == null)
            return false;

        string pageUrl = GetUrlForPage(page);
        if (string.IsNullOrEmpty(pageUrl))
            return false;

        ultralight.url = pageUrl;
        ultralight.loadUrlNow = true;
        return true;
    }

    public void ReloadConfiguredViews()
    {
        string safeStartPage = SanitizeRelativePath(startPage);
        if (string.IsNullOrEmpty(safeStartPage))
            safeStartPage = "index.html";

        AutoLoadConfiguredViews(safeStartPage);
    }

    public void SetAdditionalViewsVisible(bool visible)
    {
        if (additionalViews == null)
            return;

        if (visible && (listener == null || !listener.IsListening))
        {
            StartServer();
            return;
        }

        HashSet<Ultralight> seenTargets = new HashSet<Ultralight>();
        if (targetUltralight != null)
            seenTargets.Add(targetUltralight);

        int layoutIndex = 0;
        for (int i = 0; i < additionalViews.Count; i++)
        {
            AutoLoadView view = additionalViews[i];
            if (view == null || view.target == null)
                continue;

            if (!seenTargets.Add(view.target))
                continue;

            if (visible)
            {
                ApplyAdditionalViewLayout(view.target, layoutIndex);
                layoutIndex++;
            }

            view.target.gameObject.SetActive(visible);
            if (visible)
                TryApplyAutoLoadView(view, i);
        }
    }

    private async Task ListenLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested && listener != null && listener.IsListening)
        {
            HttpListenerContext context = null;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception)
            {
                if (token.IsCancellationRequested || listener == null || !listener.IsListening)
                    break;
            }

            if (context == null)
                continue;

            _ = Task.Run(() => HandleRequest(context), token);
        }
    }

    private void HandleRequest(HttpListenerContext context)
    {
        try
        {
            if (IsClientDebugRequest(context.Request))
            {
                HandleClientDebugRequest(context);
                return;
            }

            string path = ResolvePath(context.Request);
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                WriteTextResponse(context, HttpStatusCode.NotFound, "Not found");
                return;
            }

            byte[] data = File.ReadAllBytes(path);
            context.Response.StatusCode = (int)HttpStatusCode.OK;
            context.Response.ContentType = GetContentType(path);
            context.Response.ContentLength64 = data.LongLength;
            context.Response.OutputStream.Write(data, 0, data.Length);
            context.Response.OutputStream.Flush();

            if (logRequests)
                Debug.Log($"[UltralightLocalServer] 200 {context.Request.RawUrl}");
        }
        catch (Exception ex)
        {
            if (logRequests)
                Debug.LogWarning($"[UltralightLocalServer] 500 {context.Request.RawUrl} -> {ex.Message}");

            try
            {
                WriteTextResponse(context, HttpStatusCode.InternalServerError, "Internal error");
            }
            catch
            {
                // Ignore response write errors from disconnected clients.
            }
        }
        finally
        {
            try { context.Response.Close(); } catch { }
        }
    }

    private string ResolveRootPath()
    {
        if (string.IsNullOrWhiteSpace(streamingAssetsSubfolder))
            return Path.GetFullPath(Application.streamingAssetsPath);
        return Path.GetFullPath(Path.Combine(Application.streamingAssetsPath, streamingAssetsSubfolder));
    }

    private string ResolvePath(HttpListenerRequest request)
    {
        string rawPath = request.Url.AbsolutePath;
        string relative = string.IsNullOrEmpty(rawPath) || rawPath == "/" ? startPage : rawPath.TrimStart('/');
        relative = SanitizeRelativePath(relative);

        if (string.IsNullOrEmpty(relative))
            return null;

        string candidate = Path.GetFullPath(Path.Combine(rootPath, relative));
        string normalizedRoot = rootPath.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            ? rootPath
            : rootPath + Path.DirectorySeparatorChar;

        bool isInsideRoot = candidate.StartsWith(normalizedRoot, StringComparison.Ordinal) || string.Equals(candidate, rootPath, StringComparison.Ordinal);
        if (!isInsideRoot)
            return null;

        if (Directory.Exists(candidate))
            candidate = Path.Combine(candidate, "index.html");

        return candidate;
    }

    private static string SanitizeRelativePath(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return string.Empty;

        string decoded = Uri.UnescapeDataString(relativePath).Replace('\\', '/').TrimStart('/');
        while (decoded.Contains("//"))
            decoded = decoded.Replace("//", "/");
        if (decoded.Contains(".."))
            return string.Empty;

        return decoded;
    }

    private string BuildUrl(string safeRelativePath)
    {
        return $"http://127.0.0.1:{port}/{safeRelativePath}";
    }

    private void ApplyAdditionalViewLayout(Ultralight view, int positionIndex)
    {
        if (!autoLayoutAdditionalViews || targetUltralight == null || view == null)
            return;

        int slotWidth = targetUltralight.width + additionalViewsSpacing;
        view.width = targetUltralight.width;
        view.height = targetUltralight.height;
        view.offsetX = targetUltralight.offsetX + (slotWidth * (positionIndex + 1));
        view.offsetY = targetUltralight.offsetY;
    }

    private bool TryApplyAutoLoadView(AutoLoadView view, int index)
    {
        if (view == null || view.target == null)
            return false;

        string safePage = SanitizeRelativePath(view.page);
        if (string.IsNullOrEmpty(safePage))
        {
            Debug.LogWarning($"[UltralightLocalServer] Invalid page in additionalViews[{index}].");
            return false;
        }

        string pageForLoad = ResolvePageForLoad(safePage);
        string resolvedPath = TryResolvePagePath(pageForLoad);
        if (!string.IsNullOrEmpty(resolvedPath) && !File.Exists(resolvedPath))
            Debug.LogWarning($"[UltralightLocalServer] Page not found for additionalViews[{index}]: {resolvedPath}");

        view.target.url = BuildUrl(pageForLoad);
        view.target.loadUrlNow = true;
        if (logRequests || logOptimization)
            Debug.Log($"[UltralightLocalServer] additionalViews[{index}] '{view.target.gameObject.name}' => {view.target.url}");
        return true;
    }

    private string TryResolvePagePath(string safePage)
    {
        if (string.IsNullOrEmpty(safePage))
            return string.Empty;

        string currentRoot = rootPath;
        if (string.IsNullOrEmpty(currentRoot))
        {
            try
            {
                currentRoot = ResolveRootPath();
            }
            catch
            {
                return string.Empty;
            }
        }

        return Path.GetFullPath(Path.Combine(currentRoot, safePage));
    }

    private void AutoLoadConfiguredViews(string safeStartPage)
    {
        string startPageForLoad = ResolvePageForLoad(safeStartPage);

        if (targetUltralight != null)
        {
            targetUltralight.url = BuildUrl(startPageForLoad);
            targetUltralight.loadUrlNow = true;
            if (logRequests || logOptimization)
                Debug.Log($"[UltralightLocalServer] primary '{targetUltralight.gameObject.name}' => {targetUltralight.url}");
        }

        if (additionalViews == null)
            return;

        HashSet<Ultralight> seenTargets = new HashSet<Ultralight>();
        if (targetUltralight != null)
            seenTargets.Add(targetUltralight);

        int layoutIndex = 0;
        for (int i = 0; i < additionalViews.Count; i++)
        {
            AutoLoadView view = additionalViews[i];
            if (view == null || view.target == null)
                continue;

            if (!seenTargets.Add(view.target))
                continue;

            ApplyAdditionalViewLayout(view.target, layoutIndex);
            layoutIndex++;
            TryApplyAutoLoadView(view, i);
        }
    }

    private string ResolvePageForLoad(string safePage)
    {
        if (string.IsNullOrEmpty(safePage))
            return safePage;

        if (safePage.EndsWith(".ultralight.html", StringComparison.OrdinalIgnoreCase))
        {
            string sourceCandidate = safePage.Substring(0, safePage.Length - ".ultralight.html".Length) + ".html";
            string sourceCandidatePath = TryResolvePagePath(sourceCandidate);
            if (!string.IsNullOrEmpty(sourceCandidatePath) && File.Exists(sourceCandidatePath))
                safePage = sourceCandidate;
        }
        else if (safePage.EndsWith(".ultralight.htm", StringComparison.OrdinalIgnoreCase))
        {
            string sourceCandidate = safePage.Substring(0, safePage.Length - ".ultralight.htm".Length) + ".htm";
            string sourceCandidatePath = TryResolvePagePath(sourceCandidate);
            if (!string.IsNullOrEmpty(sourceCandidatePath) && File.Exists(sourceCandidatePath))
                safePage = sourceCandidate;
        }

        if (!optimizeLargeHtmlFiles)
            return safePage;

        if (!safePage.EndsWith(".html", StringComparison.OrdinalIgnoreCase) &&
            !safePage.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
            return safePage;

        string sourcePath = TryResolvePagePath(safePage);
        if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
            return safePage;

        long sourceSize = 0;
        try
        {
            sourceSize = new FileInfo(sourcePath).Length;
        }
        catch
        {
            return safePage;
        }

        if (sourceSize < Math.Max(1, largeHtmlThresholdBytes))
            return safePage;

        string optimizedPath = Path.Combine(
            Path.GetDirectoryName(sourcePath) ?? rootPath,
            Path.GetFileNameWithoutExtension(sourcePath) + ".ultralight" + Path.GetExtension(sourcePath)
        );

        try
        {
            bool needsRebuild = !File.Exists(optimizedPath) ||
                                File.GetLastWriteTimeUtc(optimizedPath) < File.GetLastWriteTimeUtc(sourcePath);
            if (!needsRebuild)
                needsRebuild = !HasExpectedOptimizationMarker(optimizedPath, BuildOptimizationMarker());

            if (needsRebuild)
            {
                bool created = CreateOptimizedHtml(sourcePath, optimizedPath);
                if (!created)
                    return safePage;
            }
        }
        catch
        {
            return safePage;
        }

        string relativeOptimized = MakeRelativeToRoot(optimizedPath);
        if (string.IsNullOrEmpty(relativeOptimized))
            return safePage;

        return relativeOptimized;
    }

    private bool CreateOptimizedHtml(string sourcePath, string destinationPath)
    {
        string html;
        try
        {
            html = File.ReadAllText(sourcePath);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[UltralightLocalServer] Failed reading HTML for optimization: {sourcePath} ({ex.Message})");
            return false;
        }

        string optimized = html;
        int plotlyCallIndex = optimized.LastIndexOf("Plotly.newPlot(", StringComparison.Ordinal);
        if (plotlyCallIndex < 0)
            return false;

        string prefix = optimized.Substring(0, plotlyCallIndex);
        string payload = optimized.Substring(plotlyCallIndex);
        for (int i = 0; i < HeavyJsonFieldsForUltralight.Length; i++)
            payload = RemoveJsonField(payload, HeavyJsonFieldsForUltralight[i]);

        int reducedTypedArrays = 0;
        if (downsampleTypedArraysForUltralight)
            payload = DownsamplePlotlyTypedArrays(payload, Math.Max(2, maxTypedArrayPoints), out reducedTypedArrays);

        int convertedTypedArrays = 0;
        if (convertTypedArraysToPlainJson)
            payload = ConvertPlotlyTypedArraysToJsonArrays(payload, out convertedTypedArrays);

        payload = InjectClientDiagnosticsScript(payload);

        optimized = BuildOptimizationMarker() + "\n" + prefix + payload;

        if (optimized.Length >= html.Length)
            return false;

        try
        {
            File.WriteAllText(destinationPath, optimized);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[UltralightLocalServer] Failed writing optimized HTML: {destinationPath} ({ex.Message})");
            return false;
        }

        if (logOptimization)
        {
            long before = new FileInfo(sourcePath).Length;
            long after = new FileInfo(destinationPath).Length;
            Debug.Log($"[UltralightLocalServer] Optimized HTML '{Path.GetFileName(sourcePath)}' ({before} -> {after} bytes, typed arrays reduced: {reducedTypedArrays}, typed arrays converted: {convertedTypedArrays}).");
        }

        return true;
    }

    private string BuildOptimizationMarker()
    {
        return $"{OptimizationMarkerPrefix}|max_points={Math.Max(2, maxTypedArrayPoints)}|typed_arrays={(downsampleTypedArraysForUltralight ? "on" : "off")}|plain_arrays={(convertTypedArraysToPlainJson ? "on" : "off")}|diag={(injectUltralightClientDiagnostics ? "on" : "off")} -->";
    }

    private bool IsClientDebugRequest(HttpListenerRequest request)
    {
        if (request == null || request.Url == null)
            return false;
        return string.Equals(request.Url.AbsolutePath, "/__ul_error", StringComparison.Ordinal);
    }

    private void HandleClientDebugRequest(HttpListenerContext context)
    {
        if (context == null || context.Request == null)
            return;

        string msg = context.Request.QueryString["m"];
        string line = context.Request.QueryString["l"];
        string col = context.Request.QueryString["c"];
        string src = context.Request.QueryString["s"];

        if (string.IsNullOrWhiteSpace(msg))
            msg = "(empty)";
        if (string.IsNullOrWhiteSpace(line))
            line = "-";
        if (string.IsNullOrWhiteSpace(col))
            col = "-";
        if (string.IsNullOrWhiteSpace(src))
            src = "-";

        Debug.LogWarning($"[UltralightLocalServer][ClientJS] {msg} (line={line}, col={col}, src={src})");

        context.Response.StatusCode = (int)HttpStatusCode.NoContent;
        context.Response.ContentLength64 = 0;
        context.Response.OutputStream.Flush();
    }

    private string InjectClientDiagnosticsScript(string payload)
    {
        if (!injectUltralightClientDiagnostics || string.IsNullOrEmpty(payload))
            return payload;

        const string marker = "Plotly.newPlot(";
        int idx = payload.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0)
            return payload;

        string script = @"
window.__ulSendErr=function(m,l,c,s){try{var u='/__ul_error?m='+encodeURIComponent(String(m||''))+'&l='+encodeURIComponent(String(l||''))+'&c='+encodeURIComponent(String(c||''))+'&s='+encodeURIComponent(String(s||''));(new Image()).src=u;}catch(_){}}; 
window.onerror=function(m,s,l,c){window.__ulSendErr(m,l,c,s);};
window.onunhandledrejection=function(e){var r=(e&&e.reason!==undefined)?e.reason:'unhandledrejection';window.__ulSendErr(r,'','','promise');};
window.__ulDecodeArray=function(v){
  try{
    if(Array.isArray(v)) return v;
    if(!v||typeof v!=='object') return [];
    if(typeof v.length==='number'){
      var outLike=[];
      for(var i=0;i<v.length;i++){
        var n=+v[i];
        if(isFinite(n)) outLike.push(n);
      }
      if(outLike.length) return outLike;
    }
    var b=v.bdata,d=v.dtype;
    if(typeof b!=='string'||typeof d!=='string') return [];
    var bin=atob(b);
    var bytes=new Uint8Array(bin.length);
    for(var bi=0;bi<bin.length;bi++) bytes[bi]=bin.charCodeAt(bi);
    var view=new DataView(bytes.buffer);
    var little=true,step=1,read=null;
    if(d==='f8'){step=8;read=function(o){return view.getFloat64(o,little);};}
    else if(d==='f4'){step=4;read=function(o){return view.getFloat32(o,little);};}
    else if(d==='i4'){step=4;read=function(o){return view.getInt32(o,little);};}
    else if(d==='u4'){step=4;read=function(o){return view.getUint32(o,little);};}
    else if(d==='i2'){step=2;read=function(o){return view.getInt16(o,little);};}
    else if(d==='u2'){step=2;read=function(o){return view.getUint16(o,little);};}
    else if(d==='i1'||d==='b'){step=1;read=function(o){return view.getInt8(o);};}
    else if(d==='u1'||d==='B'){step=1;read=function(o){return view.getUint8(o);};}
    else return [];
    var out=[];
    for(var off=0;off+step<=view.byteLength;off+=step){
      var num=read(off);
      if(isFinite(num)) out.push(num);
    }
    return out;
  }catch(ex){
    window.__ulSendErr('decode_array_error:'+ex);
    return [];
  }
};
window.__ulAxisSuffix=function(ykey){
  var m=/^y(\d*)$/i.exec(String(ykey||'y'));
  if(!m) return '';
  return m[1]||'';
};
window.__ulYLayoutName=function(ykey){
  var s=window.__ulAxisSuffix(ykey);
  return s?('yaxis'+s):'yaxis';
};
window.__ulXRefFromY=function(ykey){
  var s=window.__ulAxisSuffix(ykey);
  return s?('x'+s):'x';
};
window.__ulStripHtml=function(txt){
  return String(txt||'').replace(/<[^>]*>/g,' ').replace(/\s+/g,' ').trim();
};
window.__ulFallbackRender=function(target,data,layout){
  try{
    layout=layout||{};
    var panels={};
    for(var i=0;i<(data||[]).length;i++){
      var tr=data[i]||{};
      var yid=tr.yaxis||'y';
      if(!panels[yid]) panels[yid]=[];
      panels[yid].push(tr);
    }
    var keys=[];
    for(var k in panels){if(panels.hasOwnProperty(k)) keys.push(k);}
    keys.sort();
    target.innerHTML='';
    target.style.overflowY='auto';
    target.style.background='#fff';
    if(!keys.length){
      target.innerHTML='<div style=""padding:12px;font:14px sans-serif;color:#8b0000;background:#fff5f5;border:1px solid #f2c8c8"">Sem dados para renderizar.</div>';
      return;
    }
    var panelMeta=[];
    for(var ki=0;ki<keys.length;ki++){
      var ykey0=keys[ki];
      var yAxisName=window.__ulYLayoutName(ykey0);
      var yAxis=(layout&&layout[yAxisName])?layout[yAxisName]:null;
      var domainTop=0;
      if(yAxis&&yAxis.domain&&yAxis.domain.length>=2){
        var dTop=+yAxis.domain[1];
        if(isFinite(dTop)) domainTop=dTop;
      }
      panelMeta.push({key:ykey0,xref:window.__ulXRefFromY(ykey0),domainTop:domainTop,title:''});
    }
    panelMeta.sort(function(a,b){return (+b.domainTop)-(+a.domainTop);});
    var annTitles=[];
    if(layout&&Array.isArray(layout.annotations)){
      for(var ai=0;ai<layout.annotations.length;ai++){
        var ann=layout.annotations[ai]||{};
        var txt=window.__ulStripHtml(ann.text);
        if(!txt) continue;
        var ay=+ann.y;
        if(!isFinite(ay)) ay=0;
        annTitles.push({text:txt,y:ay});
      }
    }
    annTitles.sort(function(a,b){return b.y-a.y;});
    for(var mi=0;mi<panelMeta.length;mi++){
      panelMeta[mi].title=(mi<annTitles.length)?annTitles[mi].text:('Painel '+(mi+1));
    }
    var allShapes=(layout&&Array.isArray(layout.shapes))?layout.shapes:[];
    for(var pi=0;pi<panelMeta.length;pi++){
      var meta=panelMeta[pi];
      var traces=panels[meta.key]||[];
      var wrapper=document.createElement('div');
      wrapper.style.padding='6px 8px';
      var title=document.createElement('div');
      title.textContent=meta.title;
      title.style.cssText='font:12px sans-serif;color:#444;margin:0 0 4px 2px;';
      wrapper.appendChild(title);
      var canvas=document.createElement('canvas');
      var cw=Math.max(320,target.clientWidth||target.offsetWidth||640)-20;
      var ch=180;
      canvas.width=cw;
      canvas.height=ch;
      canvas.style.width=cw+'px';
      canvas.style.height=ch+'px';
      canvas.style.border='1px solid #ddd';
      canvas.style.background='#fff';
      wrapper.appendChild(canvas);
      target.appendChild(wrapper);
      var ctx=canvas.getContext('2d');
      if(!ctx) continue;
      var panelShapes=[];
      for(var si=0;si<allShapes.length;si++){
        var shp=allShapes[si]||{};
        if(String(shp.type||'line')!=='line') continue;
        var sxref0=String(shp.xref||meta.xref);
        var syref0=String(shp.yref||meta.key);
        var xOk=(sxref0===meta.xref||sxref0===meta.xref+' domain');
        var yOk=(syref0===meta.key||syref0===meta.key+' domain');
        if(xOk&&yOk) panelShapes.push(shp);
      }
      var xmin=Infinity,xmax=-Infinity,ymin=Infinity,ymax=-Infinity;
      for(var ti=0;ti<traces.length;ti++){
        var t=traces[ti]||{};
        var xs=window.__ulDecodeArray(t.x), ys=window.__ulDecodeArray(t.y);
        var n=Math.min(xs.length||0,ys.length||0);
        for(var j=0;j<n;j++){
          var xv=+xs[j], yv=+ys[j];
          if(!isFinite(xv)||!isFinite(yv)) continue;
          if(xv<xmin) xmin=xv;
          if(xv>xmax) xmax=xv;
          if(yv<ymin) ymin=yv;
          if(yv>ymax) ymax=yv;
        }
      }
      for(var spi=0;spi<panelShapes.length;spi++){
        var ps=panelShapes[spi]||{};
        var sxref=String(ps.xref||meta.xref);
        var syref=String(ps.yref||meta.key);
        var x0=+ps.x0, x1=+ps.x1, y0=+ps.y0, y1=+ps.y1;
        if(sxref===meta.xref){
          if(isFinite(x0)){if(x0<xmin) xmin=x0;if(x0>xmax) xmax=x0;}
          if(isFinite(x1)){if(x1<xmin) xmin=x1;if(x1>xmax) xmax=x1;}
        }
        if(syref===meta.key){
          if(isFinite(y0)){if(y0<ymin) ymin=y0;if(y0>ymax) ymax=y0;}
          if(isFinite(y1)){if(y1<ymin) ymin=y1;if(y1>ymax) ymax=y1;}
        }
      }
      if(!isFinite(xmin)||!isFinite(xmax)||!isFinite(ymin)||!isFinite(ymax)) continue;
      if(xmin===xmax){xmin-=1;xmax+=1;}
      if(ymin===ymax){ymin-=1;ymax+=1;}
      var padX=(xmax-xmin)*0.03, padY=(ymax-ymin)*0.08;
      xmin-=padX; xmax+=padX; ymin-=padY; ymax+=padY;
      var L=38,T=10,R=8,B=20,W=cw-L-R,H=ch-T-B;
      var sx=function(v){return L+((v-xmin)/(xmax-xmin))*W;};
      var sy=function(v){return T+H-((v-ymin)/(ymax-ymin))*H;};
      ctx.strokeStyle='#d0d0d0';
      ctx.lineWidth=1;
      ctx.strokeRect(L,T,W,H);
      for(var sdi=0;sdi<panelShapes.length;sdi++){
        var sh=panelShapes[sdi]||{};
        var line=sh.line||{};
        var sxref2=String(sh.xref||meta.xref);
        var syref2=String(sh.yref||meta.key);
        var px0=null, px1=null, py0=null, py1=null;
        var sx0=+sh.x0, sx1=+sh.x1, sy0=+sh.y0, sy1=+sh.y1;
        if(sxref2===meta.xref+' domain'){
          if(isFinite(sx0)) px0=L+(sx0*W);
          if(isFinite(sx1)) px1=L+(sx1*W);
        }else{
          if(isFinite(sx0)) px0=sx(sx0);
          if(isFinite(sx1)) px1=sx(sx1);
        }
        if(syref2===meta.key+' domain'){
          if(isFinite(sy0)) py0=T+H-(sy0*H);
          if(isFinite(sy1)) py1=T+H-(sy1*H);
        }else{
          if(isFinite(sy0)) py0=sy(sy0);
          if(isFinite(sy1)) py1=sy(sy1);
        }
        if(px0===null||px1===null||py0===null||py1===null) continue;
        ctx.save();
        ctx.strokeStyle=(typeof line.color==='string')?line.color:'#888';
        var width=+line.width;
        ctx.lineWidth=isFinite(width)?Math.max(1,width):1;
        var dash=String(line.dash||'');
        if(dash==='dash') ctx.setLineDash([6,6]);
        else if(dash==='dot') ctx.setLineDash([2,4]);
        else if(dash==='dashdot') ctx.setLineDash([6,4,2,4]);
        else ctx.setLineDash([]);
        ctx.beginPath();
        ctx.moveTo(px0,py0);
        ctx.lineTo(px1,py1);
        ctx.stroke();
        ctx.restore();
      }
      for(var ti2=0;ti2<traces.length;ti2++){
        var tr2=traces[ti2]||{};
        var mode=String(tr2.mode||'lines');
        var color=(tr2.line&&typeof tr2.line.color==='string')?tr2.line.color:((tr2.marker&&typeof tr2.marker.color==='string')?tr2.marker.color:'#1f77b4');
        var xs2=window.__ulDecodeArray(tr2.x), ys2=window.__ulDecodeArray(tr2.y);
        var n2=Math.min(xs2.length||0,ys2.length||0);
        if(mode.indexOf('lines')>=0){
          ctx.beginPath();
          var started=false;
          for(var p=0;p<n2;p++){
            var x2=+xs2[p], y2=+ys2[p];
            if(!isFinite(x2)||!isFinite(y2)) continue;
            var px=sx(x2), py=sy(y2);
            if(!started){ctx.moveTo(px,py);started=true;} else {ctx.lineTo(px,py);}
          }
          ctx.strokeStyle=color;
          ctx.lineWidth=1;
          ctx.stroke();
        }
        if(mode.indexOf('markers')>=0){
          ctx.fillStyle=color;
          for(var p2=0;p2<n2;p2++){
            var x3=+xs2[p2], y3=+ys2[p2];
            if(!isFinite(x3)||!isFinite(y3)) continue;
            var mx=sx(x3), my=sy(y3);
            ctx.beginPath();
            ctx.arc(mx,my,2,0,Math.PI*2);
            ctx.fill();
          }
        }
        if(mode.indexOf('text')>=0){
          var txts=tr2.text;
          for(var p3=0;p3<n2;p3++){
            var x4=+xs2[p3], y4=+ys2[p3];
            if(!isFinite(x4)||!isFinite(y4)) continue;
            var lbl=Array.isArray(txts)?txts[p3]:txts;
            if(lbl===undefined||lbl===null||lbl==='') continue;
            var tx=sx(x4), ty=sy(y4);
            ctx.fillStyle='#202020';
            ctx.font='11px sans-serif';
            ctx.textAlign='center';
            ctx.textBaseline='bottom';
            ctx.fillText(String(lbl),tx,ty-4);
          }
          ctx.textAlign='left';
          ctx.textBaseline='alphabetic';
        }
      }
      ctx.fillStyle='#666';
      ctx.font='10px sans-serif';
      ctx.fillText('min '+ymin.toFixed(2),2,ch-4);
      ctx.fillText('max '+ymax.toFixed(2),2,10);
    }
  }catch(ex){
    window.__ulSendErr('fallback_render_error:'+ex);
  }
};
if(!window.Plotly||typeof window.Plotly.newPlot!=='function'){
  window.__ulSendErr('plotly_missing');
  window.Plotly={
    newPlot:function(divId,data,layout){
      window.__ulSendErr('plotly_stub_used');
      try{
        var target=(typeof divId==='string')?document.getElementById(divId):divId;
        if(!target){
          var all=document.getElementsByClassName('plotly-graph-div');
          if(all&&all.length) target=all[0];
        }
        if(target) window.__ulFallbackRender(target,data||[],layout||{});
      }catch(__e){window.__ulSendErr('plotly_stub_error:'+__e);}
      return {then:function(){return this;},catch:function(){return this;}};
    }
  };
}
if(window.Plotly&&typeof window.Plotly.newPlot==='function'&&!window.__ulWrappedPlotly){
  window.__ulWrappedPlotly=true;
  var __origNewPlot=window.Plotly.newPlot;
  window.Plotly.newPlot=function(){
    window.__ulSendErr('newPlot_call');
    try{
      var p=__origNewPlot.apply(this,arguments);
      if(p&&typeof p.then==='function'){
        p.then(function(){window.__ulSendErr('newPlot_ok');})
         .catch(function(err){window.__ulSendErr('newPlot_fail:'+err);});
      }
      return p;
    }catch(ex){
      window.__ulSendErr('newPlot_throw:'+ex);
      try{
        var target=(typeof arguments[0]==='string')?document.getElementById(arguments[0]):arguments[0];
        if(target) window.__ulFallbackRender(target,arguments[1]||[],arguments[2]||{});
      }catch(__e2){window.__ulSendErr('newPlot_fallback_error:'+__e2);}
      return {then:function(){return this;},catch:function(){return this;}};
    }
  };
}
window.__ulSendErr('boot');
";

        return payload.Insert(idx, script);
    }

    private static string ConvertPlotlyTypedArraysToJsonArrays(string input, out int convertedArrays)
    {
        convertedArrays = 0;
        if (string.IsNullOrEmpty(input))
            return input;

        const string bdataKey = "\"bdata\"";
        int cursor = 0;
        System.Text.StringBuilder output = new System.Text.StringBuilder(input.Length);
        bool changed = false;

        while (cursor < input.Length)
        {
            int bdataIndex = input.IndexOf(bdataKey, cursor, StringComparison.Ordinal);
            if (bdataIndex < 0)
            {
                output.Append(input, cursor, input.Length - cursor);
                break;
            }

            int objectStart = input.LastIndexOf('{', bdataIndex);
            if (objectStart < cursor)
            {
                output.Append(input, cursor, (bdataIndex + bdataKey.Length) - cursor);
                cursor = bdataIndex + bdataKey.Length;
                continue;
            }

            int objectEnd = ParseJsonContainerEnd(input, objectStart, '{', '}');
            if (objectEnd <= bdataIndex)
            {
                output.Append(input, cursor, (bdataIndex + bdataKey.Length) - cursor);
                cursor = bdataIndex + bdataKey.Length;
                continue;
            }

            string candidate = input.Substring(objectStart, objectEnd - objectStart);
            if (!TryParseTypedArrayObject(candidate, out byte[] bytes, out string dtype))
            {
                output.Append(input, cursor, (bdataIndex + bdataKey.Length) - cursor);
                cursor = bdataIndex + bdataKey.Length;
                continue;
            }

            string jsonArray = EncodeNumericArrayAsJson(bytes, dtype);
            if (string.IsNullOrEmpty(jsonArray))
            {
                output.Append(input, cursor, (bdataIndex + bdataKey.Length) - cursor);
                cursor = bdataIndex + bdataKey.Length;
                continue;
            }

            output.Append(input, cursor, objectStart - cursor);
            output.Append(jsonArray);
            cursor = objectEnd;
            convertedArrays++;
            changed = true;
        }

        return changed ? output.ToString() : input;
    }

    private static bool TryParseTypedArrayObject(string jsonObject, out byte[] bytes, out string dtype)
    {
        bytes = null;
        dtype = string.Empty;
        if (string.IsNullOrEmpty(jsonObject))
            return false;

        if (!TryReadJsonStringProperty(jsonObject, "dtype", out string dtypeValue))
            return false;
        if (!TryReadJsonStringProperty(jsonObject, "bdata", out string bdataValue))
            return false;

        int bytesPerElement = GetTypedArrayElementSize(dtypeValue);
        if (bytesPerElement <= 0)
            return false;

        if (!TryDecodeJsonEscapedBase64(bdataValue, out byte[] rawBytes))
            return false;

        if (rawBytes == null || rawBytes.Length < bytesPerElement)
            return false;

        dtype = dtypeValue;
        bytes = rawBytes;
        return true;
    }

    private static bool TryReadJsonStringProperty(string json, string propertyName, out string value)
    {
        value = string.Empty;
        if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(propertyName))
            return false;

        string key = "\"" + propertyName + "\"";
        int keyIndex = json.IndexOf(key, StringComparison.Ordinal);
        if (keyIndex < 0)
            return false;

        int afterKey = SkipWhitespace(json, keyIndex + key.Length);
        if (afterKey >= json.Length || json[afterKey] != ':')
            return false;

        int valueStart = SkipWhitespace(json, afterKey + 1);
        if (valueStart >= json.Length || json[valueStart] != '"')
            return false;

        int valueEnd = ParseJsonStringEnd(json, valueStart);
        if (valueEnd <= valueStart + 1)
            return false;

        string escapedValue = json.Substring(valueStart + 1, valueEnd - valueStart - 2);
        value = escapedValue;
        return true;
    }

    private static string EncodeNumericArrayAsJson(byte[] bytes, string dtype)
    {
        int elementSize = GetTypedArrayElementSize(dtype);
        if (elementSize <= 0 || bytes == null || bytes.Length < elementSize)
            return string.Empty;

        int count = bytes.Length / elementSize;
        if (count <= 0)
            return "[]";

        System.Text.StringBuilder sb = new System.Text.StringBuilder(Math.Min(bytes.Length * 3, 4_000_000));
        sb.Append('[');

        for (int i = 0; i < count; i++)
        {
            if (i > 0)
                sb.Append(',');

            int offset = i * elementSize;
            switch (dtype)
            {
                case "f8":
                    sb.Append(BitConverter.ToDouble(bytes, offset).ToString("R", CultureInfo.InvariantCulture));
                    break;
                case "f4":
                    sb.Append(BitConverter.ToSingle(bytes, offset).ToString("R", CultureInfo.InvariantCulture));
                    break;
                case "i8":
                    sb.Append(BitConverter.ToInt64(bytes, offset).ToString(CultureInfo.InvariantCulture));
                    break;
                case "u8":
                    sb.Append(BitConverter.ToUInt64(bytes, offset).ToString(CultureInfo.InvariantCulture));
                    break;
                case "i4":
                    sb.Append(BitConverter.ToInt32(bytes, offset).ToString(CultureInfo.InvariantCulture));
                    break;
                case "u4":
                    sb.Append(BitConverter.ToUInt32(bytes, offset).ToString(CultureInfo.InvariantCulture));
                    break;
                case "i2":
                    sb.Append(BitConverter.ToInt16(bytes, offset).ToString(CultureInfo.InvariantCulture));
                    break;
                case "u2":
                    sb.Append(BitConverter.ToUInt16(bytes, offset).ToString(CultureInfo.InvariantCulture));
                    break;
                case "i1":
                    sb.Append(((sbyte)bytes[offset]).ToString(CultureInfo.InvariantCulture));
                    break;
                case "u1":
                case "b1":
                    sb.Append(bytes[offset].ToString(CultureInfo.InvariantCulture));
                    break;
                default:
                    return string.Empty;
            }
        }

        sb.Append(']');
        return sb.ToString();
    }

    private static bool HasExpectedOptimizationMarker(string filePath, string expectedMarker)
    {
        if (string.IsNullOrEmpty(filePath) || string.IsNullOrEmpty(expectedMarker) || !File.Exists(filePath))
            return false;

        try
        {
            using (FileStream stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (StreamReader reader = new StreamReader(stream))
            {
                char[] buffer = new char[Math.Min(Math.Max(64, expectedMarker.Length + 16), 512)];
                int read = reader.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                    return false;

                string head = new string(buffer, 0, read);
                return head.StartsWith(expectedMarker, StringComparison.Ordinal);
            }
        }
        catch
        {
            return false;
        }
    }

    private static string DownsamplePlotlyTypedArrays(string input, int maxPoints, out int reducedArrays)
    {
        reducedArrays = 0;
        if (string.IsNullOrEmpty(input) || maxPoints < 2)
            return input;

        const string bdataKey = "\"bdata\"";
        int cursor = 0;
        bool changed = false;
        System.Text.StringBuilder output = new System.Text.StringBuilder(input.Length);

        while (cursor < input.Length)
        {
            int keyIndex = input.IndexOf(bdataKey, cursor, StringComparison.Ordinal);
            if (keyIndex < 0)
            {
                output.Append(input, cursor, input.Length - cursor);
                break;
            }

            output.Append(input, cursor, keyIndex - cursor);

            int afterKey = SkipWhitespace(input, keyIndex + bdataKey.Length);
            if (afterKey >= input.Length || input[afterKey] != ':')
            {
                output.Append(bdataKey);
                cursor = keyIndex + bdataKey.Length;
                continue;
            }

            int valueStart = SkipWhitespace(input, afterKey + 1);
            if (valueStart >= input.Length || input[valueStart] != '"')
            {
                output.Append(input, keyIndex, valueStart - keyIndex);
                cursor = valueStart;
                continue;
            }

            int valueEnd = ParseJsonStringEnd(input, valueStart);
            if (valueEnd <= valueStart + 1)
            {
                output.Append(input, keyIndex, Math.Max(0, valueEnd - keyIndex));
                cursor = Math.Max(valueEnd, valueStart + 1);
                continue;
            }

            int encodedLength = valueEnd - valueStart - 2;
            string encodedJson = encodedLength > 0
                ? input.Substring(valueStart + 1, encodedLength)
                : string.Empty;

            string replacementEncoded = encodedJson;

            string dtype = TryGetTypedArrayDtypeNear(input, keyIndex);
            int bytesPerElement = GetTypedArrayElementSize(dtype);
            if (bytesPerElement > 0 && TryDecodeJsonEscapedBase64(encodedJson, out byte[] rawBytes))
            {
                int elementCount = rawBytes.Length / bytesPerElement;
                if (elementCount > maxPoints)
                {
                    byte[] sampledBytes = DownsampleFixedWidthArray(rawBytes, bytesPerElement, maxPoints);
                    replacementEncoded = EscapeJsonString(Convert.ToBase64String(sampledBytes));
                    reducedArrays++;
                    changed = true;
                }
            }

            output.Append(bdataKey);
            int betweenStart = keyIndex + bdataKey.Length;
            int betweenLength = (valueStart + 1) - betweenStart;
            if (betweenLength > 0)
                output.Append(input, betweenStart, betweenLength);
            output.Append(replacementEncoded);
            output.Append('"');

            cursor = valueEnd;
        }

        return changed ? output.ToString() : input;
    }

    private static string TryGetTypedArrayDtypeNear(string input, int bdataKeyIndex)
    {
        const string dtypeKey = "\"dtype\"";
        int objectStart = input.LastIndexOf('{', Math.Max(0, bdataKeyIndex));
        int searchStart = objectStart >= 0 ? objectStart : Math.Max(0, bdataKeyIndex - 256);
        int searchEndExclusive = Math.Min(input.Length, bdataKeyIndex + 256);

        int dtypeKeyIndex = input.LastIndexOf(
            dtypeKey,
            Math.Max(searchStart, bdataKeyIndex),
            Math.Max(0, Math.Max(searchStart, bdataKeyIndex) - searchStart + 1),
            StringComparison.Ordinal
        );
        if (dtypeKeyIndex < searchStart)
        {
            int forwardLength = searchEndExclusive - Math.Min(input.Length, bdataKeyIndex);
            if (forwardLength <= 0)
                return string.Empty;
            dtypeKeyIndex = input.IndexOf(dtypeKey, bdataKeyIndex, forwardLength, StringComparison.Ordinal);
            if (dtypeKeyIndex < 0)
                return string.Empty;
        }

        int afterKey = SkipWhitespace(input, dtypeKeyIndex + dtypeKey.Length);
        if (afterKey >= input.Length || input[afterKey] != ':')
            return string.Empty;

        int valueStart = SkipWhitespace(input, afterKey + 1);
        if (valueStart >= input.Length || input[valueStart] != '"')
            return string.Empty;

        int valueEnd = ParseJsonStringEnd(input, valueStart);
        if (valueEnd <= valueStart + 1)
            return string.Empty;

        int encodedLength = valueEnd - valueStart - 2;
        string encodedJson = encodedLength > 0
            ? input.Substring(valueStart + 1, encodedLength)
            : string.Empty;

        return UnescapeJsonString(encodedJson);
    }

    private static int GetTypedArrayElementSize(string dtype)
    {
        if (string.IsNullOrEmpty(dtype))
            return 0;

        switch (dtype)
        {
            case "f8":
            case "i8":
            case "u8":
                return 8;
            case "f4":
            case "i4":
            case "u4":
                return 4;
            case "f2":
            case "i2":
            case "u2":
                return 2;
            case "f1":
            case "i1":
            case "u1":
            case "b1":
                return 1;
            default:
                return 0;
        }
    }

    private static byte[] DownsampleFixedWidthArray(byte[] source, int elementSize, int maxPoints)
    {
        if (source == null || source.Length == 0 || elementSize <= 0)
            return source ?? Array.Empty<byte>();

        int totalPoints = source.Length / elementSize;
        if (totalPoints <= maxPoints)
            return source;

        int sampledPoints = Math.Max(2, maxPoints);
        byte[] sampled = new byte[sampledPoints * elementSize];

        for (int i = 0; i < sampledPoints; i++)
        {
            double t = sampledPoints == 1 ? 0d : (double)i / (sampledPoints - 1);
            int srcIndex = (int)Math.Round(t * (totalPoints - 1));
            srcIndex = Math.Max(0, Math.Min(totalPoints - 1, srcIndex));
            Buffer.BlockCopy(source, srcIndex * elementSize, sampled, i * elementSize, elementSize);
        }

        return sampled;
    }

    private static bool TryDecodeJsonEscapedBase64(string jsonEncodedBase64, out byte[] data)
    {
        data = null;
        if (string.IsNullOrEmpty(jsonEncodedBase64))
            return false;

        try
        {
            string rawBase64 = UnescapeJsonString(jsonEncodedBase64);
            data = Convert.FromBase64String(rawBase64);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string UnescapeJsonString(string value)
    {
        if (string.IsNullOrEmpty(value) || value.IndexOf('\\') < 0)
            return value;

        System.Text.StringBuilder sb = new System.Text.StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c != '\\' || i + 1 >= value.Length)
            {
                sb.Append(c);
                continue;
            }

            char next = value[++i];
            switch (next)
            {
                case '"': sb.Append('"'); break;
                case '\\': sb.Append('\\'); break;
                case '/': sb.Append('/'); break;
                case 'b': sb.Append('\b'); break;
                case 'f': sb.Append('\f'); break;
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append('\r'); break;
                case 't': sb.Append('\t'); break;
                case 'u':
                    if (i + 4 < value.Length && TryReadHex(value, i + 1, 4, out int codePoint))
                    {
                        sb.Append((char)codePoint);
                        i += 4;
                    }
                    else
                    {
                        sb.Append(next);
                    }
                    break;
                default:
                    sb.Append(next);
                    break;
            }
        }

        return sb.ToString();
    }

    private static string EscapeJsonString(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;
        return value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private static bool TryReadHex(string text, int start, int length, out int value)
    {
        value = 0;
        if (string.IsNullOrEmpty(text) || start < 0 || length <= 0 || start + length > text.Length)
            return false;

        for (int i = 0; i < length; i++)
        {
            char c = text[start + i];
            int v;
            if (c >= '0' && c <= '9')
                v = c - '0';
            else if (c >= 'a' && c <= 'f')
                v = 10 + (c - 'a');
            else if (c >= 'A' && c <= 'F')
                v = 10 + (c - 'A');
            else
                return false;

            value = (value << 4) | v;
        }

        return true;
    }

    private string MakeRelativeToRoot(string fullPath)
    {
        string currentRoot = rootPath;
        if (string.IsNullOrEmpty(currentRoot))
            currentRoot = ResolveRootPath();

        Uri rootUri = new Uri(EnsureTrailingSeparator(currentRoot));
        Uri fileUri = new Uri(fullPath);
        string relative = Uri.UnescapeDataString(rootUri.MakeRelativeUri(fileUri).ToString()).Replace('\\', '/');
        if (relative.StartsWith("../", StringComparison.Ordinal))
            return string.Empty;
        return relative;
    }

    private static string EnsureTrailingSeparator(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;
        if (path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ||
            path.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            return path;
        return path + Path.DirectorySeparatorChar;
    }

    private static string RemoveJsonField(string input, string fieldName)
    {
        if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(fieldName))
            return input;

        string key = "\"" + fieldName + "\"";
        int i = 0;
        int n = input.Length;
        System.Text.StringBuilder output = new System.Text.StringBuilder(input.Length);

        while (i < n)
        {
            int keyIndex = input.IndexOf(key, i, StringComparison.Ordinal);
            if (keyIndex < 0)
            {
                output.Append(input, i, n - i);
                break;
            }

            output.Append(input, i, keyIndex - i);
            int afterKey = SkipWhitespace(input, keyIndex + key.Length);

            if (afterKey >= n || input[afterKey] != ':')
            {
                output.Append(key);
                i = keyIndex + key.Length;
                continue;
            }

            int valueStart = afterKey + 1;
            int valueEnd = ParseJsonValueEnd(input, valueStart);
            int tail = SkipWhitespace(input, valueEnd);

            if (tail < n && input[tail] == ',')
            {
                i = tail + 1;
                continue;
            }

            TrimTrailingComma(output);
            i = valueEnd;
        }

        return output.ToString();
    }

    private static int SkipWhitespace(string text, int index)
    {
        int i = index;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
            i++;
        return i;
    }

    private static int ParseJsonValueEnd(string text, int startIndex)
    {
        int i = SkipWhitespace(text, startIndex);
        if (i >= text.Length)
            return i;

        char ch = text[i];
        if (ch == '"')
            return ParseJsonStringEnd(text, i);
        if (ch == '[')
            return ParseJsonContainerEnd(text, i, '[', ']');
        if (ch == '{')
            return ParseJsonContainerEnd(text, i, '{', '}');

        while (i < text.Length)
        {
            char c = text[i];
            if (c == ',' || c == '}' || c == ']')
                break;
            i++;
        }
        return i;
    }

    private static int ParseJsonStringEnd(string text, int quoteIndex)
    {
        int i = quoteIndex + 1;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\\')
            {
                i += 2;
                continue;
            }
            if (c == '"')
                return i + 1;
            i++;
        }
        return text.Length;
    }

    private static int ParseJsonContainerEnd(string text, int startIndex, char openChar, char closeChar)
    {
        int depth = 1;
        int i = startIndex + 1;
        bool inString = false;

        while (i < text.Length)
        {
            char c = text[i];
            if (inString)
            {
                if (c == '\\')
                {
                    i += 2;
                    continue;
                }
                if (c == '"')
                    inString = false;
                i++;
                continue;
            }

            if (c == '"')
            {
                inString = true;
                i++;
                continue;
            }

            if (c == openChar)
                depth++;
            else if (c == closeChar)
            {
                depth--;
                if (depth == 0)
                    return i + 1;
            }

            i++;
        }

        return text.Length;
    }

    private static void TrimTrailingComma(System.Text.StringBuilder buffer)
    {
        int i = buffer.Length - 1;
        while (i >= 0 && char.IsWhiteSpace(buffer[i]))
            i--;
        if (i >= 0 && buffer[i] == ',')
            buffer.Remove(i, 1);
    }

    private static string GetContentType(string path)
    {
        string ext = Path.GetExtension(path);
        string contentType;
        if (ext != null && ContentTypes.TryGetValue(ext, out contentType))
            return contentType;
        return "application/octet-stream";
    }

    private static void WriteTextResponse(HttpListenerContext context, HttpStatusCode statusCode, string text)
    {
        byte[] body = System.Text.Encoding.UTF8.GetBytes(text);
        context.Response.StatusCode = (int)statusCode;
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.ContentLength64 = body.LongLength;
        context.Response.OutputStream.Write(body, 0, body.Length);
        context.Response.OutputStream.Flush();
    }
}
