using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Spirescry.State;

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
    // STS2_AGENT_HTTP_LOG=1: one log line per request — verb, status,
    // revision movement, wall time. The audit trail for reconstructing
    // what an agent fired when a run wedges; off by default because obs
    // long-polls would dominate the log.
    private static readonly bool LogRequests =
        Environment.GetEnvironmentVariable("STS2_AGENT_HTTP_LOG") == "1";

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
        var timer = LogRequests ? Stopwatch.StartNew() : null;
        var revBefore = LogRequests ? Signals.Revision : 0;
        var trace = "?";
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
            if (timer is not null)
            {
                trace = $"{req.HttpMethod} {req.Url?.PathAndQuery ?? path}";
                if (path == "/step") trace += $" {StepAction(body)}";
            }
            resp = (req.HttpMethod, path) switch
            {
                ("GET", "/health") => await Handlers.Health(),
                ("GET", "/models") => await Handlers.Models(req.QueryString["kind"]),
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
        if (timer is not null)
            SafeLog.Info(
                $"http {trace} → {resp.Status} rev {revBefore}→{Signals.Revision} {timer.ElapsedMilliseconds}ms");
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

    // The dispatcher re-parses the body anyway; a second parse here only
    // runs when request logging is on.
    private static string StepAction(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("action", out var a)
                ? a.GetString() ?? "?"
                : "?";
        }
        catch { return "?"; }
    }
}
