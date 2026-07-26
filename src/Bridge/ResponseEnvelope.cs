using System.Text.Json.Nodes;

namespace Spirescry.Bridge;

// The response envelope, stated over plain values: every bridge body is a
// JSON object carrying a boolean `ok` that agrees with its HTTP status —
// true on a result, false on a rejection. Consumers validate that strictly
// (the CLI treats a body without a boolean `ok`, or one contradicting its
// status, as malformed and exits 1), so the flag is stamped here rather
// than hand-written per route, where a new route could quietly forget it
// and ship a body no consumer accepts.
public static class ResponseEnvelope
{
    public const string OkField = "ok";

    // The same split the CLI makes on the status line.
    public static bool OkFor(int status) => status >= 200 && status < 300;

    // Stamps in place and hands the body back, so the caller reads as one
    // expression. Any `ok` a payload carried itself is overwritten: the
    // status is the authority, never the body.
    public static JsonObject Stamp(JsonObject body, int status)
    {
        body[OkField] = OkFor(status);
        return body;
    }
}
