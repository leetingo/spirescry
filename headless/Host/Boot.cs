// Headless boot — the stand-in for the game's mod loader + scene boot.
//
// Order matters:
//   1. InlineSynchronizationContext so async chains drain deterministically.
//   2. ModelDb init — without it RunManager has no idea what IRONCLAD is.
//   3. Localization from the setup-extracted JSON tables.
//   4. Harmony patches: Cmd.Wait (load-bearing — the action queue would
//      otherwise wait on animation frames that never come) plus finalizers
//      that swallow display-only exceptions (loc vars, VFX).
//   5. Headless pump + the same HTTP bridge GUI mode uses.

using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.TestSupport;
using Spirescry.Bridge;
using Spirescry.Threading;

namespace Spirescry.Host;

internal static class HeadlessBoot
{
    private static Harmony? _harmony;
    private static Timer? _signalTimer;

    public static void Start()
    {
        // STS2_HOST_DEBUG=1: print first-chance exception stacks — the
        // engine's own logger swallows them down to one message line.
        if (Environment.GetEnvironmentVariable("STS2_HOST_DEBUG") == "1")
            AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
            {
                var frames = (e.Exception.StackTrace ?? "").Split('\n');
                Console.Error.WriteLine(
                    $"[fce] {e.Exception.GetType().Name}: {e.Exception.Message}\n"
                    + string.Join('\n', frames.Take(6)));
            };

        SynchronizationContext.SetSynchronizationContext(new InlineSynchronizationContext());
        InitModelDb();
        HeadlessLocalization.Init();
        ApplyHarmonyPatches();
        MainThreadPump.BootstrapHeadless();
        // Background continuations (unpatched Task.Delay in engine code)
        // mutate state outside handler calls; a slow pump tick folds those
        // into the signal stream so parked waiters still wake.
        _signalTimer = new Timer(
            _ => MainThreadPump.Instance?.Run(() => true), null, 250, 250);

        var portVar = Environment.GetEnvironmentVariable("STS2_AGENT_PORT");
        var port = int.TryParse(portVar, out var p) ? p : 7777;
        new HttpBridge().Start("127.0.0.1", port);
        Console.Error.WriteLine($"[spirescry_host] bridge listening on http://127.0.0.1:{port}/");
    }

    private static void InitModelDb()
    {
        TestMode.IsOn = true;

        // ModelDb reaches ReflectionHelper.ModTypes, which is guarded by
        // ModManager initialization. No ModManager runs here — stamp the
        // engine's own "mod loading didn't run" state so the registry
        // resolves to no mod types instead of throwing.
        typeof(ModManager)
            .GetField("<State>k__BackingField",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            ?.SetValue(null, ModManagerState.Skipped);

        var subtypes = AbstractModelSubtypes.All;
        int registered = 0, failed = 0;
        foreach (var t in subtypes)
        {
            try { ModelDb.Inject(t); registered++; }
            catch { failed++; }
        }
        Console.Error.WriteLine($"[spirescry_host] ModelDb: {registered} registered, {failed} failed (of {subtypes.Count})");

        // Adding cards/relics writes their saved properties; the cache is
        // normally primed during the game's boot (and its ContentSorter
        // wants AssemblyInfo first).
        try { AssemblyInfo.Init(); }
        catch (Exception ex) { Console.Error.WriteLine($"[spirescry_host] AssemblyInfo: {ex.GetType().Name}: {ex.Message}"); }
        try { MegaCrit.Sts2.Core.Saves.Runs.SavedPropertiesTypeCache.Init(); }
        catch (Exception ex) { Console.Error.WriteLine($"[spirescry_host] SavedPropertiesTypeCache: {ex.GetType().Name}: {ex.Message}"); }

        // After the models: progress data walks the character registry.
        try { SaveManager.Instance.InitProfileId(0); }
        catch (Exception ex) { Console.Error.WriteLine($"[spirescry_host] InitProfileId: {ex.Message}"); }
        try { SaveManager.Instance.InitProgressData(); }
        catch (Exception ex) { Console.Error.WriteLine($"[spirescry_host] InitProgressData: {ex.Message}"); }

        RebuildModelIdSerializationCache();
    }

    // ModelIdSerializationCache.Init() walks Assembly.GetTypes(), which
    // throws under the stub Godot (a few engine types stay unresolved) and
    // leaves the cache empty — and EnterAct's transition broadcast needs
    // epoch lookups from it. Rebuild it from the ModelDb we just populated.
    private static void RebuildModelIdSerializationCache()
    {
        try
        {
            var asm = typeof(AbstractModelSubtypes).Assembly;
            var cacheT = asm.GetType("MegaCrit.Sts2.Core.Multiplayer.Serialization.ModelIdSerializationCache");
            if (cacheT is null) return;

            var bf = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            var catMap = cacheT.GetField("_categoryNameToNetIdMap", bf)?.GetValue(null) as Dictionary<string, int>;
            var entryMap = cacheT.GetField("_entryNameToNetIdMap", bf)?.GetValue(null) as Dictionary<string, int>;
            var catList = cacheT.GetField("_netIdToCategoryNameMap", bf)?.GetValue(null) as List<string>;
            var entryList = cacheT.GetField("_netIdToEntryNameMap", bf)?.GetValue(null) as List<string>;
            if (catMap is null || entryMap is null || catList is null || entryList is null) return;
            catMap.Clear(); entryMap.Clear(); catList.Clear(); entryList.Clear();

            void AddCat(string name)
            {
                if (string.IsNullOrEmpty(name) || catMap.ContainsKey(name)) return;
                catMap[name] = catList.Count;
                catList.Add(name);
            }
            void AddEntry(string name)
            {
                if (string.IsNullOrEmpty(name) || entryMap.ContainsKey(name)) return;
                entryMap[name] = entryList.Count;
                entryList.Add(name);
            }

            // netId 0 must stay NONE — the engine's miss sentinel.
            AddCat("NONE");
            AddEntry("NONE");
            void Walk(System.Collections.IEnumerable? models)
            {
                if (models is null) return;
                foreach (var m in models)
                {
                    if (m is not AbstractModel model) continue;
                    AddCat(model.Id.Category);
                    AddEntry(model.Id.Entry);
                }
            }
            Walk(ModelDb.AllCards);
            Walk(ModelDb.AllRelics);
            Walk(ModelDb.AllPotions);
            Walk(ModelDb.AllCharacters);
            Walk(ModelDb.AllPowers);
            Walk(ModelDb.AllEvents);
            Walk(ModelDb.AllEncounters);
            Walk(ModelDb.AllAncients);
            Walk(ModelDb.Acts);
            Walk(ModelDb.Monsters);
            Walk(ModelDb.Orbs);

            int Bits(int n) => Math.Max(1, (int)Math.Ceiling(Math.Log2(Math.Max(2, n))));
            void SetBacking(string prop, object value) =>
                cacheT.GetField($"<{prop}>k__BackingField", bf)?.SetValue(null, value);
            SetBacking("CategoryIdBitSize", Bits(catList.Count));
            SetBacking("EntryIdBitSize", Bits(entryList.Count));
            SetBacking("EpochIdBitSize", 1);
            SetBacking("Hash", (uint)((catList.Count * 397) ^ entryList.Count));
            Console.Error.WriteLine($"[spirescry_host] serialization cache: {catList.Count} categories, {entryList.Count} entries");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[spirescry_host] cache rebuild failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void ApplyHarmonyPatches()
    {
        _harmony = new Harmony("spirescry_host.patches");

        // Load-bearing — let failures abort boot: a half-patched process
        // that hangs on the first combat action is worse than not starting.
        PatchCmdWait();
        VerifyQueueWaitIlPatch();

        // Cosmetic — a missing one loses a label or a particle, not liveness.
        try
        {
            PatchFinalizersByName("MegaCrit.Sts2.Core.Localization.LocString",
                "Exists", "GetIfExists", "GetRawText");
            PatchLocStringFormattedText();
            PatchAddDetailsTo();
            PatchFinalizersByName("MegaCrit.Sts2.Core.Events.EventOption", "ToString");
            PatchFinalizersByName("MegaCrit.Sts2.Core.Multiplayer.Game.EventSynchronizer",
                "SaveEventOptionToHistory", "AppendToMapPointHistory");
            PatchVfxPlay();
            PatchEpochLookups();
            ImmunizeAudioSingletons();
            PatchMonsterFlavorHooks();
            PatchProgressTracking();
            RerouteBundleScreen();
            RerouteCrystalSphere();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[spirescry_host] cosmetic patch failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // Cmd.Wait gates card-play pacing on UI animation time; headless has no
    // frames, so it would park the action queue forever.
    private static void PatchCmdWait()
    {
        var cmdType = typeof(AbstractModelSubtypes).Assembly.GetType("MegaCrit.Sts2.Core.Commands.Cmd")
            ?? throw new InvalidOperationException("Cmd type not found — combat actions would hang");
        var prefix = new HarmonyMethod(typeof(HeadlessBoot).GetMethod(nameof(CmdWaitPrefix),
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static));
        var patched = 0;
        foreach (var m in cmdType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
        {
            if (m.Name != "Wait") continue;
            _harmony!.Patch(m, prefix: prefix);
            patched++;
        }
        if (patched == 0)
            throw new InvalidOperationException("no Cmd.Wait overloads found — combat actions would hang");
    }

    private static bool CmdWaitPrefix(ref Task __result)
    {
        __result = Task.CompletedTask;
        return false;
    }

    // The setup-time Patcher rewrites this queue-drain wait to return
    // CompletedTask; if the method vanished (version skew) the rewrite
    // silently didn't happen and combat would hang — abort instead.
    private static void VerifyQueueWaitIlPatch()
    {
        var found = SafeTypes().Any(t => t.GetMethods(
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static
                | System.Reflection.BindingFlags.DeclaredOnly)
            .Any(m => m.Name == "WaitUntilQueueIsEmptyOrWaitingOnNonPlayerDrivenAction"));
        if (!found)
            throw new InvalidOperationException(
                "queue-wait method not found on sts2.headless.dll — IL patch missing (version skew)");
    }

    // GetTypes throws ReflectionTypeLoadException for the handful of types
    // the stub doesn't cover; the partial result is fine.
    private static Type[] SafeTypes()
    {
        try { return typeof(AbstractModelSubtypes).Assembly.GetTypes(); }
        catch (System.Reflection.ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null).ToArray()!;
        }
    }

    private static readonly HarmonyMethod Swallow = new(
        typeof(HeadlessBoot).GetMethod(nameof(SwallowExceptionFinalizer),
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static));

    private static Exception? SwallowExceptionFinalizer(Exception __exception) => null;

    private static readonly HarmonyMethod SwallowTask = new(
        typeof(HeadlessBoot).GetMethod(nameof(SwallowExceptionTaskFinalizer),
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static));

    private static Exception? SwallowExceptionTaskFinalizer(Exception? __exception, ref Task __result)
    {
        if (__exception is not null || __result is null) __result = Task.CompletedTask;
        return null;
    }

    // Swallow-finalize every overload of the named methods on one type.
    private static void PatchFinalizersByName(string typeName, params string[] methods)
    {
        var t = typeof(AbstractModelSubtypes).Assembly.GetType(typeName);
        if (t is null) return;
        var bf = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                 | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static;
        foreach (var m in t.GetMethods(bf))
        {
            if (!methods.Contains(m.Name) || m.IsAbstract || m.IsGenericMethodDefinition) continue;
            try { _harmony!.Patch(m, finalizer: Swallow); }
            catch { }
        }
    }

    // LocString.GetFormattedText faults bubble through Creature.LogName and
    // friends into game logic; fall back to the entry key so the agent
    // still sees a recognizable identifier.
    private static void PatchLocStringFormattedText()
    {
        var t = typeof(AbstractModelSubtypes).Assembly.GetType("MegaCrit.Sts2.Core.Localization.LocString");
        var m = t?.GetMethod("GetFormattedText",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        if (m is null) return;
        _harmony!.Patch(m, finalizer: new HarmonyMethod(typeof(HeadlessBoot).GetMethod(
            nameof(LocStringFinalizer),
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)));
    }

    private static Exception? LocStringFinalizer(
        Exception? __exception, MegaCrit.Sts2.Core.Localization.LocString __instance, ref string __result)
    {
        if (__exception is null) return null;
        try { __result = __instance.LocEntryKey ?? ""; }
        catch { __result = ""; }
        return null;
    }

    // Loc-var injection during event/option text construction throws on
    // missing tables; skipping it only degrades text.
    private static void PatchAddDetailsTo()
    {
        var bf = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                 | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static
                 | System.Reflection.BindingFlags.DeclaredOnly;
        foreach (var t in SafeTypes())
        {
            if (!t.IsClass) continue;
            foreach (var m in t.GetMethods(bf))
            {
                if (m.Name != "AddDetailsTo" && m.Name != "AddLocVars") continue;
                if (m.IsAbstract || m.IsGenericMethodDefinition) continue;
                try { _harmony!.Patch(m, finalizer: Swallow); }
                catch { }
            }
        }
    }

    // VFX entry points call into un-stubbed Godot APIs; purely visual.
    private static void PatchVfxPlay()
    {
        var names = new[] { "Play", "PlayAnim", "Animate", "Stop", "Show", "Pulse", "Trigger" };
        var bf = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                 | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static
                 | System.Reflection.BindingFlags.DeclaredOnly;
        foreach (var t in SafeTypes())
        {
            if (!t.IsClass) continue;
            var ns = t.Namespace ?? "";
            if (!ns.Contains(".Vfx") && !t.Name.EndsWith("Vfx")) continue;
            foreach (var m in t.GetMethods(bf))
            {
                if (!names.Contains(m.Name) || m.IsAbstract || m.IsGenericMethodDefinition) continue;
                try { _harmony!.Patch(m, finalizer: Swallow); }
                catch { }
            }
        }
    }

    // CardSelectCmd.FromChooseABundleScreen is UI-only (shows the bundle
    // screen, awaits its click) — reroute it onto the HeadlessBundle
    // stand-in so Neow's card packs work without a scene tree.
    private static void RerouteBundleScreen()
    {
        var m = typeof(MegaCrit.Sts2.Core.Commands.CardSelectCmd).GetMethod(
            "FromChooseABundleScreen",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        if (m is null)
        {
            Console.Error.WriteLine("[spirescry_host] FromChooseABundleScreen not found — bundle offers unsupported");
            return;
        }
        _harmony!.Patch(m, prefix: new HarmonyMethod(typeof(HeadlessBoot).GetMethod(
            nameof(BundlePrefix),
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)));
        Console.Error.WriteLine("[spirescry_host] rerouted bundle offers to the headless stand-in");
    }

    private static bool BundlePrefix(
        IReadOnlyList<IReadOnlyList<MegaCrit.Sts2.Core.Models.CardModel>> bundles,
        ref Task<IEnumerable<MegaCrit.Sts2.Core.Models.CardModel>> __result)
    {
        __result = Spirescry.State.HeadlessBundle.Park(bundles);
        return false;
    }

    // The crystal-sphere minigame is model-driven; ShowScreen is its only
    // UI coupling. Park the entity and skip the screen — the bridge's
    // crystal verbs drive the model, and the minigame's own completion
    // source resumes the event.
    private static void RerouteCrystalSphere()
    {
        var m = typeof(MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere.NCrystalSphereScreen)
            .GetMethod("ShowScreen",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        if (m is null)
        {
            Console.Error.WriteLine("[spirescry_host] NCrystalSphereScreen.ShowScreen not found — crystal sphere unsupported");
            return;
        }
        _harmony!.Patch(m, prefix: new HarmonyMethod(typeof(HeadlessBoot).GetMethod(
            nameof(CrystalPrefix),
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)));
        Console.Error.WriteLine("[spirescry_host] rerouted crystal sphere to the headless stand-in");
    }

    private static bool CrystalPrefix(
        MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent.CrystalSphereMinigame grid,
        ref MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere.NCrystalSphereScreen __result)
    {
        Spirescry.State.HeadlessCrystal.Park(grid);
        __result = null!;
        return false;
    }

    // Progress bookkeeping (epochs, unlock stats) throws on entries the
    // headless profile never earned — and UpdateAfterCombatWon sits inside
    // EndCombatInternal, so an "EpochModel was not found" aborts the whole
    // end-of-combat chain (second boss dies, combat never ends). Saves
    // don't persist in host anyway; swallow the lot.
    private static void PatchProgressTracking()
    {
        var t = typeof(AbstractModelSubtypes).Assembly
            .GetType("MegaCrit.Sts2.Core.Saves.Managers.ProgressSaveManager");
        if (t is null) return;
        var patched = 0;
        foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly))
        {
            if (m.IsAbstract || m.IsGenericMethodDefinition || m.IsSpecialName) continue;
            var fin = typeof(Task).IsAssignableFrom(m.ReturnType) ? SwallowTask : Swallow;
            try { _harmony!.Patch(m, finalizer: fin); patched++; }
            catch { }
        }
        Console.Error.WriteLine($"[spirescry_host] swallowed {patched} progress-tracking methods");
    }

    // Monster model flavor hooks (BeforeDeath, BeforeRemovedFromRoom) play
    // death SFX / arm animations; in host they NRE on missing audio and
    // scene nodes, and that aborts CreatureCmd.Kill — the boss never dies
    // and the win condition never fires. Swallow: flavor only.
    private static void PatchMonsterFlavorHooks()
    {
        var bf = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                 | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly;
        var patched = 0;
        foreach (var t in SafeTypes())
        {
            if (!t.IsClass || (t.Namespace ?? "").Contains("Models.Monsters") == false) continue;
            foreach (var m in t.GetMethods(bf))
            {
                if (m.Name != "BeforeDeath" && m.Name != "BeforeRemovedFromRoom") continue;
                if (m.IsAbstract || m.IsGenericMethodDefinition) continue;
                // Task-returning hooks must not leave a null result behind —
                // the engine awaits it.
                var fin = typeof(Task).IsAssignableFrom(m.ReturnType) ? SwallowTask : Swallow;
                try { _harmony!.Patch(m, finalizer: fin); patched++; }
                catch { }
            }
        }
        Console.Error.WriteLine($"[spirescry_host] swallowed {patched} monster flavor hooks");
    }

    // Some monster death hooks call NAudioManager.Instance.PlayOneShot
    // without a null guard (Kaiser-crab bosses). Give the singleton an
    // uninitialized body and swallow every method's exceptions — audio is
    // pure output, no game state.
    private static void ImmunizeAudioSingletons()
    {
        foreach (var name in new[] { "MegaCrit.Sts2.Core.Nodes.Audio.NAudioManager" })
        {
            var t = typeof(AbstractModelSubtypes).Assembly.GetType(name);
            if (t is null) continue;
            try
            {
                var inst = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(t);
                var bf = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic
                         | System.Reflection.BindingFlags.Static;
                (t.GetField("<Instance>k__BackingField", bf)
                    ?? t.GetField("_instance", bf))?.SetValue(null, inst);
                var patched = 0;
                foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly))
                {
                    if (m.IsAbstract || m.IsGenericMethodDefinition || m.IsSpecialName) continue;
                    try { _harmony!.Patch(m, finalizer: Swallow); patched++; }
                    catch { }
                }
                Console.Error.WriteLine($"[spirescry_host] immunized {t.Name}: {patched} methods");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[spirescry_host] immunize {name}: {ex.Message}");
            }
        }
    }

    // Epoch lookups throw on a cache miss and EnterAct's act-transition
    // broadcast hits them; return the NONE sentinel instead.
    private static void PatchEpochLookups()
    {
        var t = typeof(AbstractModelSubtypes).Assembly
            .GetType("MegaCrit.Sts2.Core.Multiplayer.Serialization.ModelIdSerializationCache");
        if (t is null) return;
        var bf = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static;
        if (t.GetMethod("GetNetIdForEpochId", bf) is { } getNet)
            _harmony!.Patch(getNet, prefix: new HarmonyMethod(typeof(HeadlessBoot).GetMethod(
                nameof(EpochNetIdPrefix), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)));
        if (t.GetMethod("GetEpochIdForNetId", bf) is { } getId)
            _harmony!.Patch(getId, prefix: new HarmonyMethod(typeof(HeadlessBoot).GetMethod(
                nameof(EpochIdPrefix), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)));
    }

    private static bool EpochNetIdPrefix(ref int __result)
    {
        __result = 0;
        return false;
    }

    private static bool EpochIdPrefix(ref string __result)
    {
        __result = "";
        return false;
    }
}
