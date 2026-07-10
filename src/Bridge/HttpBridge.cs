using System.Net;
using System.Text;
using System.Text.Json;

namespace Spirescry.Bridge;

public sealed class Response
{
    public int Status { get; init; } = 200;
    public string Body { get; init; } = "";

    public static Response Json(object payload, int status = 200) =>
        new() { Status = status, Body = JsonSerializer.Serialize(payload) };

    public static Response Error(string code, string msg, int status = 400) =>
        Json(new { ok = false, err = code, msg }, status);
}

// Loopback-only HTTP server. No auth: the bridge binds 127.0.0.1
// exclusively.
public sealed class HttpBridge
{
    private HttpListener? _listener;

    // The shared startup for both boots: STS2_AGENT_PORT (default 7777),
    // loopback only. Returns the bound port.
    public static int StartFromEnv()
    {
        var portVar = Environment.GetEnvironmentVariable("STS2_AGENT_PORT");
        var port = int.TryParse(portVar, out var p) ? p : 7777;
        new HttpBridge().Start("127.0.0.1", port);
        return port;
    }

    public void Start(string host, int port)
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://{host}:{port}/");
        _listener.Start();
        _ = Task.Run(AcceptLoop);
        SafeLog.Info($"bridge listening on http://{host}:{port}/");
    }

    private async Task AcceptLoop()
    {
        while (true)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener!.GetContextAsync(); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                SafeLog.Error("accept loop stopped", ex);
                break;
            }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        Response resp;
        try
        {
            var req = ctx.Request;
            var body = "";
            if (req.HttpMethod == "POST")
            {
                using var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
                body = await reader.ReadToEndAsync();
            }
            var path = req.Url?.AbsolutePath ?? "/";
            resp = (req.HttpMethod, path) switch
            {
                ("GET", "/health") => await Handlers.Health(),
                ("GET", "/obs") => await Handlers.Obs(
                    req.QueryString["since"], req.QueryString["wait"], req.QueryString["compact"]),
                ("POST", "/step") => await Handlers.Step(body),
                _ => Response.Error("not_found", $"no route {req.HttpMethod} {path}", 404),
            };
        }
        catch (Exception ex)
        {
            SafeLog.Error("http handler error", ex);
            resp = Response.Error("internal", ex.Message, 500);
        }
        try
        {
            ctx.Response.StatusCode = resp.Status;
            ctx.Response.ContentType = "application/json; charset=utf-8";
            var bytes = Encoding.UTF8.GetBytes(resp.Body);
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
        catch (Exception ex)
        {
            SafeLog.Error("http write error", ex);
        }
    }
}
