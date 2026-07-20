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

using System.Reflection;
using HarmonyLib;
using MegaCrit.Sts2.Core.Map;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Models;
using MegaCrit.Sts2.Core.Multiplayer.Game;
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.TestSupport;
using Spirescry.Bridge;
using Spirescry.State;
using Spirescry.Threading;

namespace Spirescry.Host;

internal static class HeadlessBoot
{
    private const BindingFlags AllDeclared =
        BindingFlags.Public | BindingFlags.NonPublic
        | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static Harmony? _harmony;
    private static Timer? _signalTimer;

    // Every patch method here is a private static on this class.
    private static HarmonyMethod Local(string name) => new(
        typeof(HeadlessBoot).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static));

    public static void Start()
    {
        // Composition root: this is the only host decision about which
        // runtime adapter owns player decisions and model settlement.
        DecisionSurface.UseHeadless();

        // STS2_HOST_DEBUG=1: print first-chance exception stacks — the
        // engine's own logger swallows them down to one message line.
        // Known GodotSharp stub misses (type/method loads the stub doesn't
        // cover; SafeTypes() handles the partial results) collapse into
        // one summary so they can't bury real signal; =2 prints them all.
        var debug = Environment.GetEnvironmentVariable("STS2_HOST_DEBUG");
        if (debug is "1" or "2")
        {
            var stubMisses = 0;
            AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
            {
                var ex = e.Exception;
                var known = debug != "2" && FirstChanceFilter.IsKnownGodotStubMiss(ex);
                if (known)
                {
                    if (Interlocked.Increment(ref stubMisses) == 1)
                        Console.Error.WriteLine(
                            "[fce] suppressing known GodotSharp stub misses (STS2_HOST_DEBUG=2 shows them)");
                    return;
                }
                var frames = (ex.StackTrace ?? "").Split('\n');
                Console.Error.WriteLine(
                    $"[fce] {ex.GetType().Name}: {ex.Message}\n"
                    + string.Join('\n', frames.Take(6)));
            };
        }

        SynchronizationContext.SetSynchronizationContext(new InlineSynchronizationContext());
        InitModelDb();
        HeadlessLocalization.Init();
        ApplyHarmonyPatches();
        DecisionSurface.Current.InstallPersistentCardSelector();
        MainThreadPump.BootstrapHeadless();
        // Background continuations (unpatched Task.Delay in engine code)
        // mutate state outside handler calls; a slow pump tick folds those
        // into the signal stream so parked waiters still wake.
        _signalTimer = new Timer(
            _ => MainThreadPump.Instance?.Run(() => true), null, 250, 250);

        var port = HttpBridge.StartFromEnv();
        HostLog.Info($"bridge listening on http://127.0.0.1:{port}/");
    }

    private static void InitModelDb()
    {
        TestMode.IsOn = true;

        // ModelDb reaches ReflectionHelper.ModTypes, which is guarded by
        // ModManager initialization. No ModManager runs here — stamp the
        // engine's own "mod loading didn't run" state so the registry
        // resolves to no mod types instead of throwing.
        Reflect.SetStaticBackingField(typeof(ModManager), "State", ModManagerState.Skipped);

        var subtypes = AbstractModelSubtypes.All;
        int registered = 0, failed = 0;
        foreach (var t in subtypes)
        {
            try { ModelDb.Inject(t); registered++; }
            catch { failed++; }
        }
        HostLog.Info($"ModelDb: {registered} registered, {failed} failed (of {subtypes.Count})");

        // Adding cards/relics writes their saved properties; the cache is
        // normally primed during the game's boot (and its ContentSorter
        // wants AssemblyInfo first).
        try { AssemblyInfo.Init(); }
        catch (Exception ex) { HostLog.Error("AssemblyInfo", ex); }
        try { MegaCrit.Sts2.Core.Saves.Runs.SavedPropertiesTypeCache.Init(); }
        catch (Exception ex) { HostLog.Error("SavedPropertiesTypeCache", ex); }

        // After the models: progress data walks the character registry.
        try { SaveManager.Instance.InitProfileId(0); }
        catch (Exception ex) { HostLog.Error("InitProfileId", ex); }
        try { SaveManager.Instance.InitProgressData(); }
        catch (Exception ex) { HostLog.Error("InitProgressData", ex); }
        // Gameplay code dereferences prefs/settings (VFX timing reads
        // PrefsSave.FastMode on every X-cost card); the ForTest inits stamp
        // in-memory defaults without touching the player's real files.
        try { SaveManager.Instance.InitPrefsDataForTest(); }
        catch (Exception ex) { HostLog.Error("InitPrefsDataForTest", ex); }
        try { SaveManager.Instance.InitSettingsDataForTest(); }
        catch (Exception ex) { HostLog.Error("InitSettingsDataForTest", ex); }

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

            var bf = BindingFlags.NonPublic | BindingFlags.Static;
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
                Reflect.SetStaticBackingField(cacheT, prop, value);
            SetBacking("CategoryIdBitSize", Bits(catList.Count));
            SetBacking("EntryIdBitSize", Bits(entryList.Count));
            SetBacking("EpochIdBitSize", 1);
            SetBacking("Hash", (uint)((catList.Count * 397) ^ entryList.Count));
            HostLog.Info($"serialization cache: {catList.Count} categories, {entryList.Count} entries");
        }
        catch (Exception ex)
        {
            HostLog.Error("cache rebuild failed", ex);
        }
    }

    private static void ApplyHarmonyPatches()
    {
        _harmony = new Harmony("spirescry_host.patches");

        // Load-bearing — let failures abort boot: a half-patched process
        // that hangs on the first combat action is worse than not starting.
        PatchCmdWait();
        VerifyQueueWaitIlPatch();
        PatchCombatRewardParking();
        PatchTreasureChestGate();
        PatchAct4TreasureRooms();

        // Presentation-only failures are recorded with their exact method
        // and may continue. Their finalizers still swallow runtime display
        // faults exactly as before; this policy applies only while installing
        // the patches.
        PatchFinalizersByName("MegaCrit.Sts2.Core.Localization.LocString",
            "Exists", "GetIfExists", "GetRawText");
        PatchLocStringFormattedText();
        PatchAddDetailsTo();
        PatchFinalizersByName("MegaCrit.Sts2.Core.Events.EventOption", "ToString");
        PatchFinalizersByName("MegaCrit.Sts2.Core.Multiplayer.Game.EventSynchronizer",
            "SaveEventOptionToHistory", "AppendToMapPointHistory");
        PatchVfxPlay();
        PatchMonsterFlavorHooks();
        PatchReattachFadeOut();

        // These shims keep model-layer work alive or own a gameplay decision.
        // Missing targets and install failures are version skew, not a host
        // that can safely limp onward.
        PatchEpochLookups();
        // Broad audio/UI method finalizers are presentation-only and report
        // failures individually; the exact Trial decision reroutes installed
        // by this call are required and still abort on failure.
        ImmunizeAudioSingletons();
        PatchProgressTracking();
        RerouteBundleScreen();
        RerouteCrystalSphere();
        RerouteCustomRewards();
    }

    // Act 4 maps can contain treasure points, but the engine constructs a
    // TreasureRoom with CurrentActIndex and its constructor only accepts
    // 0..2. The argument is validation-only in the current game build:
    // treasure gold and relic rarity are generated later from run state/RNG,
    // so clamping does not select a different loot tier. Use the highest
    // accepted value for later acts while preserving normal room entry and
    // the real reward path.
    private static void PatchAct4TreasureRooms()
    {
        var createRoom = typeof(RunManager).GetMethod(
            "CreateRoom", AllDeclared, null,
            [typeof(RoomType), typeof(MapPointType), typeof(AbstractModel)], null);
        if (createRoom is null)
            throw new MissingMethodException(typeof(RunManager).FullName, "CreateRoom");
        PatchOne(
            "Act 4 treasure room construction",
            HostPatchFailurePolicy.Required,
            createRoom,
            prefix: Local(nameof(CreateRoomPrefix)));
        HostLog.Info("clamping post-Act-3 treasure rooms past the constructor guard");
    }

    private static bool CreateRoomPrefix(
        RunManager __instance,
        MapPointType mapPointType,
        ref AbstractRoom __result)
    {
        var actIndex = __instance.DebugOnlyGetState()?.CurrentActIndex ?? 0;
        if (mapPointType != MapPointType.Treasure || actIndex <= 2)
            return true;
        __result = new TreasureRoom(2);
        return false;
    }

    // ReattachPower's death fade calls Godot.Node.GetIndex(bool), an API
    // absent from the lightweight Godot stubs used by the headless host.
    // The fade is visual-only; skipping it preserves the kill and lets the
    // combat win/reward flow complete.
    private static void PatchReattachFadeOut()
    {
        var t = typeof(AbstractModelSubtypes).Assembly.GetType(
            "MegaCrit.Sts2.Core.Models.Powers.ReattachPower");
        var m = t?.GetMethod("DoFadeOutOnAllSegments",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (m is null)
        {
            new HostPatchBatchResult(0, 0, []).Enforce(
                "ReattachPower death fade",
                HostPatchFailurePolicy.PresentationOnly,
                HostLog.Error);
            return;
        }

        var prefix = m.ReturnType == typeof(Task)
            ? Local(nameof(SkipReattachFadeOutTask))
            : Local(nameof(SkipReattachFadeOutVoid));
        if (!PatchOne(
            "ReattachPower death fade",
            HostPatchFailurePolicy.PresentationOnly,
            m,
            prefix: prefix))
            return;
        HostLog.Info("skipping ReattachPower death fade in headless mode");
    }

    private static bool SkipReattachFadeOutTask(ref Task __result)
    {
        __result = Task.CompletedTask;
        return false;
    }

    private static bool SkipReattachFadeOutVoid() => false;

    private static void PatchTreasureChestGate()
    {
        var method = typeof(TreasureRoomRelicSynchronizer).GetMethod(
            nameof(TreasureRoomRelicSynchronizer.BeginRelicPicking),
            BindingFlags.Public | BindingFlags.Instance)
            ?? throw new InvalidOperationException(
                "TreasureRoomRelicSynchronizer.BeginRelicPicking not found");
        PatchOne(
            "treasure relic choice gate",
            HostPatchFailurePolicy.Required,
            method,
            prefix: Local(nameof(BeginRelicPickingPrefix)));
        HostLog.Info("gated treasure relic offers behind an explicit verb");
    }

    private static bool BeginRelicPickingPrefix() =>
        DecisionSurface.Current.CanBeginTreasureRelicPicking;

    // MarkPreFinished is the engine's transition from combat resolution to
    // the parked post-combat choice. Capture exactly there; observation is
    // now a pure read and cannot manufacture rewards.
    private static void PatchCombatRewardParking()
    {
        var method = typeof(CombatRoom).GetMethod(
            nameof(CombatRoom.MarkPreFinished),
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("CombatRoom.MarkPreFinished not found");
        PatchOne(
            "combat reward parking",
            HostPatchFailurePolicy.Required,
            method,
            postfix: Local(nameof(CombatRewardParkedPostfix)));
        HostLog.Info("parked post-combat rewards on the decision surface");
    }

    private static void CombatRewardParkedPostfix(CombatRoom __instance)
    {
        var parked = DecisionSurface.Current.ParkCombatRewards(__instance);
        if (!parked.Ok)
            throw new InvalidOperationException(
                parked.Message ?? "decision surface failed to park combat rewards");
    }

    // Custom reward offers (event trades like THE_FUTURE_OF_POTIONS) run
    // RewardsCmd.OfferCustom → RewardsSet.Offer, which shows the GUI
    // rewards screen and validates completion against it — headless that
    // throws and the offer evaporates while the trade's cost sticks.
    // Reroute the whole OfferCustom through the selected completion owner;
    // the host adapter parks it for the agent to claim from the rewards
    // phase. Scoped to OfferCustom so combat/treasure Offer() stay untouched.
    private static void RerouteCustomRewards()
    {
        Reroute(
            typeof(MegaCrit.Sts2.Core.Commands.RewardsCmd), "OfferCustom",
            BindingFlags.Public | BindingFlags.Static, nameof(OfferCustomPrefix),
            "rerouted custom reward offers to the headless stand-in");
    }

    private static bool OfferCustomPrefix(
        MegaCrit.Sts2.Core.Entities.Players.Player player,
        List<MegaCrit.Sts2.Core.Rewards.Reward> rewards,
        ref Task __result)
    {
        return !DecisionSurface.Current.TryOwnCustomRewardCompletion(
            player, rewards, out __result);
    }

    // Cmd.Wait gates card-play pacing on UI animation time; headless has no
    // frames, so it would park the action queue forever.
    private static void PatchCmdWait()
    {
        var cmdType = typeof(AbstractModelSubtypes).Assembly.GetType("MegaCrit.Sts2.Core.Commands.Cmd")
            ?? throw new InvalidOperationException("Cmd type not found — combat actions would hang");
        var prefix = Local(nameof(CmdWaitPrefix));
        var patched = 0;
        foreach (var m in cmdType.GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (m.Name != "Wait") continue;
            PatchOne(
                "Cmd.Wait overloads",
                HostPatchFailurePolicy.Required,
                m,
                prefix: prefix);
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
        var found = SafeTypes().Any(t => t.GetMethods(AllDeclared)
            .Any(m => m.Name == "WaitUntilQueueIsEmptyOrWaitingOnNonPlayerDrivenAction"));
        if (!found)
            throw new InvalidOperationException(
                "queue-wait method not found on sts2.headless.dll — IL patch missing (version skew)");
    }

    // GetTypes throws ReflectionTypeLoadException for the handful of types
    // the stub doesn't cover; the partial result is fine. Several patch
    // passes sweep the whole assembly — resolve it once.
    private static Type[]? _safeTypes;

    private static Type[] SafeTypes()
    {
        if (_safeTypes is not null) return _safeTypes;
        try { _safeTypes = typeof(AbstractModelSubtypes).Assembly.GetTypes(); }
        catch (ReflectionTypeLoadException ex)
        {
            _safeTypes = ex.Types.Where(t => t is not null).ToArray()!;
        }
        return _safeTypes;
    }

    private static readonly HarmonyMethod Swallow = Local(nameof(SwallowExceptionFinalizer));

    private static Exception? SwallowExceptionFinalizer(Exception __exception) => null;

    private static readonly HarmonyMethod SwallowTask = Local(nameof(SwallowExceptionTaskFinalizer));

    private static Exception? SwallowExceptionTaskFinalizer(Exception? __exception, ref Task __result)
    {
        if (__exception is not null || __result is null) __result = Task.CompletedTask;
        return null;
    }

    private static HostPatchBatchResult PatchMethodsAndSwallow(
        string patchSet,
        HostPatchFailurePolicy failurePolicy,
        IEnumerable<Type> types,
        BindingFlags flags,
        Func<MethodInfo, bool> matches,
        bool completeFaultedTasks = false)
    {
        var matched = 0;
        var patched = 0;
        var failures = new List<HostPatchFailure>();
        foreach (var t in types)
        {
            if (!t.IsClass) continue;
            foreach (var m in t.GetMethods(flags))
            {
                if (!matches(m) || m.IsAbstract || m.IsGenericMethodDefinition) continue;
                matched++;
                var finalizer = completeFaultedTasks
                    && typeof(Task).IsAssignableFrom(m.ReturnType)
                    ? SwallowTask : Swallow;
                // A failed patch usually means the method's body references
                // a Godot API the stubs don't cover, so Harmony can't JIT
                // it — the method then runs raw and its fault escapes the
                // swallow. Preserve the exact overload and exception so boot
                // policy can either stop or explicitly report and continue.
                try { _harmony!.Patch(m, finalizer: finalizer); patched++; }
                catch (Exception ex) { failures.Add(HostPatchFailure.From(m, ex)); }
            }
        }
        var result = new HostPatchBatchResult(matched, patched, failures);
        result.Enforce(patchSet, failurePolicy, HostLog.Error);
        return result;
    }

    private static bool PatchOne(
        string patchSet,
        HostPatchFailurePolicy failurePolicy,
        MethodInfo method,
        HarmonyMethod? prefix = null,
        HarmonyMethod? postfix = null,
        HarmonyMethod? finalizer = null)
    {
        try
        {
            _harmony!.Patch(
                method, prefix: prefix, postfix: postfix, finalizer: finalizer);
            return true;
        }
        catch (Exception ex)
        {
            var result = new HostPatchBatchResult(
                MatchedCount: 1,
                PatchedCount: 0,
                Failures: [HostPatchFailure.From(method, ex)]);
            result.Enforce(patchSet, failurePolicy, HostLog.Error);
            return false;
        }
    }

    private static void Reroute(
        Type type, string methodName, BindingFlags flags, string prefixName,
        string successLog)
    {
        var method = type.GetMethod(methodName, flags);
        if (method is null)
            throw new MissingMethodException(type.FullName, methodName);
        PatchOne(
            $"{type.FullName}.{methodName} reroute",
            HostPatchFailurePolicy.Required,
            method,
            prefix: Local(prefixName));
        HostLog.Info(successLog);
    }

    // Swallow-finalize every overload of the named methods on one type.
    private static void PatchFinalizersByName(string typeName, params string[] methods)
    {
        var t = typeof(AbstractModelSubtypes).Assembly.GetType(typeName);
        var bf = BindingFlags.Public | BindingFlags.NonPublic
                 | BindingFlags.Instance | BindingFlags.Static;
        PatchMethodsAndSwallow(
            $"presentation finalizers {typeName}.{string.Join('/', methods)}",
            HostPatchFailurePolicy.PresentationOnly,
            t is null ? [] : [t],
            bf,
            m => methods.Contains(m.Name));
    }

    // LocString.GetFormattedText faults bubble through Creature.LogName and
    // friends into game logic; fall back to the entry key so the agent
    // still sees a recognizable identifier.
    private static void PatchLocStringFormattedText()
    {
        var t = typeof(AbstractModelSubtypes).Assembly.GetType("MegaCrit.Sts2.Core.Localization.LocString");
        var m = t?.GetMethod("GetFormattedText", BindingFlags.Public | BindingFlags.Instance);
        if (m is null)
        {
            new HostPatchBatchResult(0, 0, []).Enforce(
                "LocString.GetFormattedText finalizer",
                HostPatchFailurePolicy.PresentationOnly,
                HostLog.Error);
            return;
        }
        PatchOne(
            "LocString.GetFormattedText finalizer",
            HostPatchFailurePolicy.PresentationOnly,
            m,
            finalizer: Local(nameof(LocStringFinalizer)));
    }

    private static Exception? LocStringFinalizer(
        Exception? __exception, MegaCrit.Sts2.Core.Localization.LocString __instance, ref string __result)
    {
        if (__exception is null) return null;
        // Raw table text (any unformatted {vars} included) beats the bare
        // key; the key stays the identifier of last resort. GetRawText is
        // itself swallow-finalized, so a missing entry comes back null.
        try
        {
            if (__instance.GetRawText() is { Length: > 0 } raw)
            {
                __result = raw;
                return null;
            }
        }
        catch { }
        try { __result = __instance.LocEntryKey ?? ""; }
        catch { __result = ""; }
        return null;
    }

    // Loc-var injection during event/option text construction throws on
    // missing tables; skipping it only degrades text.
    private static void PatchAddDetailsTo()
    {
        PatchMethodsAndSwallow(
            "localized detail injection finalizers",
            HostPatchFailurePolicy.PresentationOnly,
            SafeTypes(), AllDeclared,
            m => m.Name is "AddDetailsTo" or "AddLocVars");
    }

    // VFX entry points call into un-stubbed Godot APIs; purely visual.
    private static void PatchVfxPlay()
    {
        var names = new[] { "Play", "PlayAnim", "Animate", "Stop", "Show", "Pulse", "Trigger" };
        var types = SafeTypes().Where(t =>
            (t.Namespace ?? "").Contains(".Vfx") || t.Name.EndsWith("Vfx"));
        PatchMethodsAndSwallow(
            "VFX finalizers",
            HostPatchFailurePolicy.PresentationOnly,
            types,
            AllDeclared,
            m => names.Contains(m.Name));
    }

    // CardSelectCmd.FromChooseABundleScreen is UI-only (shows the bundle
    // screen, awaits its click) — ask the boot-selected decision surface
    // to take completion ownership in headless mode.
    private static void RerouteBundleScreen()
    {
        Reroute(
            typeof(MegaCrit.Sts2.Core.Commands.CardSelectCmd),
            "FromChooseABundleScreen", BindingFlags.Public | BindingFlags.Static,
            nameof(BundlePrefix),
            "rerouted bundle offers to the headless stand-in");
    }

    private static bool BundlePrefix(
        IReadOnlyList<IReadOnlyList<MegaCrit.Sts2.Core.Models.CardModel>> bundles,
        ref Task<IEnumerable<MegaCrit.Sts2.Core.Models.CardModel>> __result)
    {
        return !DecisionSurface.Current.TryOwnBundleCompletion(bundles, out __result);
    }

    // The crystal-sphere minigame is model-driven; ShowScreen is its only
    // UI coupling. Park the entity and skip the screen — the bridge's
    // crystal verbs drive the model, and the minigame's own completion
    // source resumes the event.
    private static void RerouteCrystalSphere()
    {
        Reroute(
            typeof(MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere.NCrystalSphereScreen),
            "ShowScreen", BindingFlags.Public | BindingFlags.Static,
            nameof(CrystalPrefix),
            "rerouted crystal sphere to the headless stand-in");
    }

    private static bool CrystalPrefix(
        MegaCrit.Sts2.Core.Events.Custom.CrystalSphereEvent.CrystalSphereMinigame grid,
        ref MegaCrit.Sts2.Core.Nodes.Events.Custom.CrystalSphere.NCrystalSphereScreen __result)
    {
        if (!DecisionSurface.Current.TryOwnCrystalScreen(grid))
            return true;
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
        var result = PatchMethodsAndSwallow(
            "progress tracking finalizers",
            HostPatchFailurePolicy.Required,
            t is null ? [] : [t],
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly,
            m => !m.IsSpecialName, completeFaultedTasks: true);
        HostLog.Info($"swallowed {result.PatchedCount} progress-tracking methods");
    }

    // Monster model flavor hooks (BeforeDeath, BeforeRemovedFromRoom) play
    // death SFX / arm animations; in host they NRE on missing audio and
    // scene nodes, and that aborts CreatureCmd.Kill — the boss never dies
    // and the win condition never fires. Soul Nexus puts the same
    // presentation-only work in its AfterDeath event handler, so target that
    // one exact hook too. Do not blanket-swallow AfterDeath: other monsters
    // can own gameplay there.
    private static void PatchMonsterFlavorHooks()
    {
        var bf = BindingFlags.Public | BindingFlags.NonPublic
                 | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        var types = SafeTypes().Where(t =>
            (t.Namespace ?? "").Contains("Models.Monsters"));
        // Task-returning hooks must not leave a null result behind —
        // the engine awaits it.
        var result = PatchMethodsAndSwallow(
            "monster flavor finalizers",
            HostPatchFailurePolicy.PresentationOnly,
            types, bf, m => m.Name is "BeforeDeath" or "BeforeRemovedFromRoom"
                || (m.Name == "AfterDeath"
                    && m.DeclaringType?.FullName
                        == "MegaCrit.Sts2.Core.Models.Monsters.SoulNexus"),
            completeFaultedTasks: true);
        HostLog.Info($"swallowed {result.PatchedCount} monster flavor hooks");
    }

    // Model-layer code calls display/audio singletons without null guards:
    // NAudioManager.Instance.PlayOneShot in monster death hooks
    // (Kaiser-crab bosses), NDebugAudioManager.Instance.Play and
    // NGame.Instance.ScreenShakeTrauma/ScreenRumble mid-event-effect
    // (Dense Vegetation's rest, the Amalgamator's combines). Headless has
    // none of these nodes, so the null deref aborts the effect halfway —
    // the Amalgamator removed two Defends and never granted the Ultimate
    // Defend. Give each singleton an uninitialized body and swallow every
    // method: they are presentation output, and every one of these calls
    // was already a guaranteed NRE in this host, so a no-op is strictly more
    // faithful to the GUI's behavior.
    private static void ImmunizeAudioSingletons()
    {
        var audio = ImmunizeSingleton(
            "MegaCrit.Sts2.Core.Nodes.Audio.NAudioManager",
            HostPatchFailurePolicy.PresentationOnly);
        var debugAudio = ImmunizeSingleton(
            "MegaCrit.Sts2.Core.Audio.Debug.NDebugAudioManager",
            HostPatchFailurePolicy.PresentationOnly);
        // NGame is the root visual node, so its dummy must be fully
        // inert: property accessors too (non-auto getters deref null
        // fields), and Task-returning members must complete rather than
        // return null — engine code awaits them. Adapter selection already
        // happened at this composition root before the dummy is installed.
        var game = ImmunizeSingleton("MegaCrit.Sts2.Core.Nodes.NGame",
            HostPatchFailurePolicy.PresentationOnly,
            includeSpecialNames: true, completeFaultedTasks: true)
            ?? throw new InvalidOperationException(
                "NGame presentation shim unavailable — Trial decisions cannot complete");

        // Sub-manager chains resolve through the dummy: _Ready would have
        // GetNode'd these auto-properties, and NDebugAudioManager.Instance
        // is computed as NGame.Instance?.DebugAudio — leave them null and
        // Dense Vegetation's rest still NREs one dereference later.
        if (debugAudio is not null)
            Reflect.SetPropertyOrBackingField(game, "DebugAudio", debugAudio);
        if (audio is not null)
            Reflect.SetPropertyOrBackingField(game, "AudioManager", audio);

        ImmunizeEventRoomUi();
    }

    // Trial reaches event-room UI without null guards or IsMe-independent
    // logic separation: NEventRoom.Instance.Layout.RemoveNodesOnPortrait,
    // .SetPortrait(Cache.GetTexture2D(...)), and (via DoubleDown)
    // NModalContainer.Instance.Add(NAbandonRunConfirmPopup.Create(null)).
    // Every other event reaches these singletons through null-safe `?.`
    // chains (decompile survey), so inert dummies change Trial's fate
    // only: the effect completes instead of aborting halfway.
    private static object? _eventLayoutDummy;

    private static void ImmunizeEventRoomUi()
    {
        _eventLayoutDummy = ImmunizeSingleton(
            "MegaCrit.Sts2.Core.Nodes.Events.NEventLayout",
            HostPatchFailurePolicy.PresentationOnly,
            includeSpecialNames: true, completeFaultedTasks: true)
            ?? throw new InvalidOperationException(
                "NEventLayout presentation shim unavailable — Trial decisions cannot complete");
        var room = ImmunizeSingleton(
            "MegaCrit.Sts2.Core.Nodes.Rooms.NEventRoom",
            HostPatchFailurePolicy.PresentationOnly,
            includeSpecialNames: true, completeFaultedTasks: true)
            ?? throw new InvalidOperationException(
                "NEventRoom presentation shim unavailable — Trial decisions cannot complete");
        // NModalContainer must NOT get a dummy: a non-null Instance opens
        // engine FTUE gates — CardPileCmd's first-shuffle popup checks
        // `NModalContainer.Instance != null`, then awaits a modal that can
        // never resolve headless, parking every draw that shuffles. The
        // one unguarded model-layer use (Trial's DoubleDown) is swallowed
        // below instead.

        // The whole chain is computed — NEventRoom.Instance =>
        // NRun.Instance?.EventRoom => NGame.Instance?.CurrentRunNode —
        // so installing a dummy in a backing field can never surface it.
        // Reroute the two getters Trial dereferences: Instance to the
        // inert room, Layout (_eventContainer.CurrentScene) to the inert
        // layout. Every other caller reads these through null-safe `?.`
        // chains whose sub-properties stay null, so behavior elsewhere is
        // unchanged.
        _eventRoomDummy = room;
        var getInstance = typeof(MegaCrit.Sts2.Core.Nodes.Rooms.NEventRoom)
            .GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)
            ?.GetGetMethod()
            ?? throw new MissingMethodException(
                typeof(MegaCrit.Sts2.Core.Nodes.Rooms.NEventRoom).FullName,
                "get_Instance");
        PatchOne(
            "Trial event room instance",
            HostPatchFailurePolicy.Required,
            getInstance,
            prefix: Local(nameof(EventRoomInstancePrefix)));

        var getLayout = typeof(MegaCrit.Sts2.Core.Nodes.Rooms.NEventRoom)
            .GetProperty("Layout")?.GetGetMethod()
            ?? throw new MissingMethodException(
                typeof(MegaCrit.Sts2.Core.Nodes.Rooms.NEventRoom).FullName,
                "get_Layout");
        PatchOne(
            "Trial event layout",
            HostPatchFailurePolicy.Required,
            getLayout,
            prefix: Local(nameof(EventLayoutPrefix)));

        // Two Trial helpers fault headless. AddVfxAnchoredToPortrait is
        // pure presentation (NREs on Cache.GetScene(path).Instantiate) —
        // swallowed. DoubleDown is gameplay: its body opens the
        // abandon-run confirm popup whose accepted action is
        // RunManager.Abandon(); the popup cannot exist here (see the
        // NModalContainer note above), so the prefix runs that accepted
        // action directly — the agent's option click is the confirmation
        // (obs already marks the option lethal).
        var trial = typeof(AbstractModelSubtypes).Assembly.GetType(
            "MegaCrit.Sts2.Core.Models.Events.Trial")
            ?? throw new TypeLoadException(
                "Trial event model not found — double-down completion shim unavailable");
        PatchMethodsAndSwallow(
            "Trial portrait VFX finalizer",
            HostPatchFailurePolicy.PresentationOnly,
            [trial],
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly,
            m => m.Name == "AddVfxAnchoredToPortrait");
        var doubleDown = trial.GetMethod(
            "DoubleDown", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new MissingMethodException(trial.FullName, "DoubleDown");
        PatchOne(
            "Trial double-down confirmation",
            HostPatchFailurePolicy.Required,
            doubleDown,
            prefix: Local(nameof(TrialDoubleDownPrefix)));
    }

    private static bool TrialDoubleDownPrefix(ref Task __result)
    {
        // The popup's accepted action is RunManager.Abandon(), but engine
        // AbandonInternal opens with screen closes that NRE headless and
        // log an error line — ask the selected adapter to accept the same
        // confirmation through its screen-free teardown.
        try
        {
            if (RunManager.Instance is { } rm)
            {
                var abandoned = DecisionSurface.Current
                    .AcceptAbandonConfirmation(rm);
                if (!abandoned.Ok)
                    HostLog.Info(abandoned.Message
                        ?? "trial double-down abandon was rejected");
            }
        }
        catch (Exception ex) { HostLog.Error("trial double-down abandon", ex); }
        __result = Task.CompletedTask;
        return false;
    }

    private static object? _eventRoomDummy;

    private static bool EventRoomInstancePrefix(
        ref MegaCrit.Sts2.Core.Nodes.Rooms.NEventRoom? __result)
    {
        __result = (MegaCrit.Sts2.Core.Nodes.Rooms.NEventRoom?)_eventRoomDummy;
        return false;
    }

    private static bool EventLayoutPrefix(
        ref MegaCrit.Sts2.Core.Nodes.Events.NEventLayout? __result)
    {
        __result = (MegaCrit.Sts2.Core.Nodes.Events.NEventLayout?)_eventLayoutDummy;
        return false;
    }


    private static object? ImmunizeSingleton(
        string name,
        HostPatchFailurePolicy failurePolicy,
        bool includeSpecialNames = false,
        bool completeFaultedTasks = false)
    {
        var t = typeof(AbstractModelSubtypes).Assembly.GetType(name);
        if (t is null)
        {
            new HostPatchBatchResult(0, 0, []).Enforce(
                $"{name} inert presentation methods",
                failurePolicy,
                HostLog.Error);
            return null;
        }
        try
        {
            var inst = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(t);
            if (!Reflect.SetStaticBackingField(t, "Instance", inst))
                t.GetField("_instance",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    ?.SetValue(null, inst);
            var result = PatchMethodsAndSwallow(
                $"{t.FullName} inert presentation methods",
                failurePolicy,
                [t], BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly,
                m => includeSpecialNames || !m.IsSpecialName,
                completeFaultedTasks);
            HostLog.Info($"immunized {t.Name}: {result.PatchedCount} methods");
            return inst;
        }
        catch (Exception ex)
        {
            HostLog.Error($"immunize {name}", ex);
            if (failurePolicy == HostPatchFailurePolicy.Required)
                throw;
            return null;
        }
    }

    // Epoch lookups throw on a cache miss and EnterAct's act-transition
    // broadcast hits them; return the NONE sentinel instead.
    private static void PatchEpochLookups()
    {
        var t = typeof(AbstractModelSubtypes).Assembly
            .GetType("MegaCrit.Sts2.Core.Multiplayer.Serialization.ModelIdSerializationCache");
        if (t is null)
            throw new TypeLoadException(
                "ModelIdSerializationCache not found — epoch transition shim unavailable");
        var bf = BindingFlags.Public | BindingFlags.Static;
        var getNet = t.GetMethod("GetNetIdForEpochId", bf)
            ?? throw new MissingMethodException(t.FullName, "GetNetIdForEpochId");
        var getId = t.GetMethod("GetEpochIdForNetId", bf)
            ?? throw new MissingMethodException(t.FullName, "GetEpochIdForNetId");
        PatchOne(
            "epoch id to network id lookup",
            HostPatchFailurePolicy.Required,
            getNet,
            prefix: Local(nameof(EpochNetIdPrefix)));
        PatchOne(
            "epoch network id to model id lookup",
            HostPatchFailurePolicy.Required,
            getId,
            prefix: Local(nameof(EpochIdPrefix)));
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
