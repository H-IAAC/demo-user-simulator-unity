using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

[DisallowMultipleComponent]
public class UltralightLocalServer : MonoBehaviour
{
    [Header("Binding")]
    [SerializeField] private Ultralight targetUltralight;

    [Header("Content")]
    [SerializeField] private string streamingAssetsSubfolder = "ui";
    [SerializeField] private string startPage = "groups_map.html";

    [Header("Server")]
    [SerializeField] private int port = 8787;
    [SerializeField] private bool autoStart = true;
    [SerializeField] private bool autoLoadInUltralight = true;
    [SerializeField] private bool logRequests = false;

    private HttpListener listener;
    private CancellationTokenSource cancellation;
    private string rootPath;
    private string startUrl;

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

        startUrl = $"http://127.0.0.1:{port}/{safeStartPage}";

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

        if (autoLoadInUltralight && targetUltralight != null)
        {
            targetUltralight.url = startUrl;
            targetUltralight.loadUrlNow = true;
        }

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
