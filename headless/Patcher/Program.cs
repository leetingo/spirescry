// Patcher — applies two surgical IL patches to sts2.dll for headless mode.
//
// Why not Harmony at runtime? sts2.dll runs in a process with no Godot main
// loop. The two methods we patch sit on hot paths that would deadlock or
// spin forever before Harmony's [HarmonyPatch] could even apply. So we
// rewrite the IL at setup time, write back to a `.headless` sibling, and
// load that variant from the host.
//
// Usage:
//   dotnet run --project headless/Patcher -- <input-sts2.dll> <output-sts2.headless.dll>
//
// Patch 1 — Task.Yield's awaiter IsCompleted always returns true.
//   sts2 calls `await Task.Yield()` extensively to break up long
//   synchronous chains and let other Godot frames render. With no Godot
//   main loop, those yields land on a SynchronizationContext that never
//   pumps, and the chain hangs. Returning IsCompleted=true short-circuits
//   the await: no continuation queued, control returns immediately.
//
//   Target: System.Runtime.CompilerServices.YieldAwaitable+YieldAwaiter
//           .get_IsCompleted()
//   But sts2 doesn't define that type — it lives in System.Runtime. Why
//   does sts2-cli's patcher walk sts2's types? Because sts2 emits
//   compiler-generated state machine display classes that *embed*
//   YieldAwaiter as a nested type via async lowering. Each `async` method
//   that uses `Task.Yield()` gets its own state machine struct. The
//   patcher walks those.

using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: patcher <in.dll> <out.dll>");
    return 2;
}
var inPath = Path.GetFullPath(args[0]);
var outPath = Path.GetFullPath(args[1]);
Console.WriteLine($"reading {inPath}");

var resolver = new DefaultAssemblyResolver();
resolver.AddSearchDirectory(Path.GetDirectoryName(inPath)!);

ModuleDefinition module;
try
{
    module = ModuleDefinition.ReadModule(inPath, new ReaderParameters
    {
        AssemblyResolver = resolver,
        ReadingMode = ReadingMode.Deferred,
    });
}
catch (Exception ex)
{
    Console.Error.WriteLine($"read failed: {ex.Message}");
    return 1;
}

int yieldPatches = 0;
int waitPatches = 0;

// ── Patch 1: YieldAwaiter.IsCompleted ────────────────────────────────
// Async methods that use `await Task.Yield()` get rewritten by the C#
// compiler into a state machine. The state machine's MoveNext checks
// the awaiter's IsCompleted to decide between sync continuation and
// scheduling. We can either patch each state machine's local awaiter
// usage (fragile) or patch every YieldAwaiter.get_IsCompleted method
// embedded across all of sts2's nested compiler-generated types
// (mechanical and complete).
//
// The signature we look for: a method named "get_IsCompleted" on a
// type whose name contains "YieldAwaiter". Walk arbitrarily deep
// because the compiler may nest state machines under display classes.
foreach (var type in module.GetTypes())
{
    if (!type.Name.Contains("YieldAwaiter")) continue;
    foreach (var method in type.Methods)
    {
        if (method.Name != "get_IsCompleted" || method.Body == null) continue;
        var il = method.Body.GetILProcessor();
        method.Body.Instructions.Clear();
        method.Body.ExceptionHandlers.Clear();
        il.Emit(OpCodes.Ldc_I4_1);
        il.Emit(OpCodes.Ret);
        yieldPatches++;
    }
}

// ── Patch 2: WaitUntilQueueIsEmptyOrWaitingOnNonPlayerDrivenAction ─
// This method (on ActionExecutor / similar) blocks an async chain until
// the action queue drains via the Godot main loop. In headless we drain
// the queue inline — there's no main loop, so this wait would never
// complete. Replace its body with `return Task.CompletedTask;` so any
// caller proceeds immediately.
//
// We don't know the declaring type up front (sts2 may rename across
// versions), so match on method name + signature: returns Task, takes no
// args. There may be overloads; patch all.
TypeReference taskType = module.ImportReference(typeof(System.Threading.Tasks.Task));
MethodReference completedTaskGetter = module.ImportReference(
    typeof(System.Threading.Tasks.Task).GetProperty("CompletedTask")!.GetGetMethod()!);

foreach (var type in module.GetTypes())
{
    foreach (var method in type.Methods)
    {
        if (method.Name != "WaitUntilQueueIsEmptyOrWaitingOnNonPlayerDrivenAction") continue;
        if (method.Body == null) continue;
        method.Body.Instructions.Clear();
        method.Body.ExceptionHandlers.Clear();
        method.Body.Variables.Clear();
        var il = method.Body.GetILProcessor();
        il.Emit(OpCodes.Call, completedTaskGetter);
        il.Emit(OpCodes.Ret);
        waitPatches++;
        Console.WriteLine($"  patched {type.FullName}.{method.Name}");
    }
}

Console.WriteLine($"yield-awaiter IsCompleted patches: {yieldPatches}");
Console.WriteLine($"queue-wait patches: {waitPatches}");
// 0 yield-awaiter hits is fine — current sts2 builds don't embed
// YieldAwaiter as a nested type, so the patch family is dormant; headless
// liveness rests on the queue-wait patch below plus the Cmd.Wait Harmony
// prefix. The yield patch stays in case a future sts2 version reintroduces
// nested awaiters.
//
// The queue-wait patch is load-bearing: without it, headless combat
// actions hang forever on a drain that never completes. Zero hits means
// version skew on sts2.dll renamed the target — fail the patch (nonzero
// exit) so build.sh aborts rather than shipping a hang-prone DLL.
if (waitPatches == 0)
{
    Console.Error.WriteLine("ERROR: queue-wait patch found no targets — sts2.dll version skew; agent would hang on combat actions");
    return 3;
}

Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
module.Write(outPath);
Console.WriteLine($"wrote {outPath}");
return 0;
