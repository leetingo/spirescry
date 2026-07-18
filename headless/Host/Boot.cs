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
using MegaCrit.Sts2.Core.Rooms;
using MegaCrit.Sts2.Core.Runs;
using MegaCrit.Sts2.Core.Saves;
using MegaCrit.Sts2.Core.TestSupport;
using Spirescry.Bridge;
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
        PatchAct4TreasureRooms();

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
            PatchReattachFadeOut();
            RerouteBundleScreen();
            RerouteCrystalSphere();
            RerouteCustomRewards();
        }
        catch (Exception ex)
        {
            HostLog.Error("cosmetic patch failed", ex);
        }
    }

    // Act 4 maps can contain treasure points, but the engine constructs a
    // TreasureRoom with CurrentActIndex and its constructor only accepts the
    // three normal-act loot tiers (0..2). The GUI build avoids that invalid
    // combination elsewhere; the headless map action reaches it directly
    // and faults without ever entering the room. Use the last real tier for
    // later acts while preserving the engine's normal room-entry action.
    private static void PatchAct4TreasureRooms()
    {
        var createRoom = typeof(RunManager).GetMethod(
            "CreateRoom", AllDeclared, null,
            [typeof(RoomType), typeof(MapPointType), typeof(AbstractModel)], null);
        if (createRoom is null)
            throw new MissingMethodException(typeof(RunManager).FullName, "CreateRoom");
        _harmony!.Patch(createRoom, prefix: Local(nameof(CreateRoomPrefix)));
        HostLog.Info("clamping post-Act-3 treasure rooms to the final loot tier");
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
            HostLog.Info("ReattachPower.DoFadeOutOnAllSegments not found");
            return;
        }

        var prefix = m.ReturnType == typeof(Task)
            ? Local(nameof(SkipReattachFadeOutTask))
            : Local(nameof(SkipReattachFadeOutVoid));
        _harmony!.Patch(m, prefix: prefix);
        HostLog.Info("skipping ReattachPower death fade in headless mode");
    }

    private static bool SkipReattachFadeOutTask(ref Task __result)
    {
        __result = Task.CompletedTask;
        return false;
    }

    private static bool SkipReattachFadeOutVoid() => false;

    // Custom reward offers (event trades like THE_FUTURE_OF_POTIONS) run
    // RewardsCmd.OfferCustom → RewardsSet.Offer, which shows the GUI
    // rewards screen and validates completion against it — headless that
    // throws and the offer evaporates while the trade's cost sticks.
    // Reroute the whole OfferCustom: generate the set, park it in
    // HeadlessRewards, and return; the agent claims from the rewards
    // phase like any post-combat offer. Scoped to OfferCustom so the
    // combat/treasure Offer() paths stay untouched.
    private static void RerouteCustomRewards()
    {
        var m = typeof(MegaCrit.Sts2.Core.Commands.RewardsCmd)
            .GetMethod("OfferCustom", BindingFlags.Public | BindingFlags.Static);
        if (m is null)
        {
            HostLog.Info("RewardsCmd.OfferCustom not found — custom reward offers unsupported");
            return;
        }
        _harmony!.Patch(m, prefix: Local(nameof(OfferCustomPrefix)));
        HostLog.Info("rerouted custom reward offers to the headless stand-in");
    }

    private static bool OfferCustomPrefix(
        MegaCrit.Sts2.Core.Entities.Players.Player player,
        List<MegaCrit.Sts2.Core.Rewards.Reward> rewards,
        ref Task __result)
    {
        __result = CaptureCustomOffer(player, rewards);
        return false;
    }

    private static async Task CaptureCustomOffer(
        MegaCrit.Sts2.Core.Entities.Players.Player player,
        List<MegaCrit.Sts2.Core.Rewards.Reward> rewards)
    {
        var set = new MegaCrit.Sts2.Core.Rewards.RewardsSet(player)
            .WithCustomRewards(rewards);
        await set.GenerateWithoutOffering();
        Spirescry.State.HeadlessRewards.CaptureFromSet(set);
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

    // Swallow-finalize every overload of the named methods on one type.
    private static void PatchFinalizersByName(string typeName, params string[] methods)
    {
        var t = typeof(AbstractModelSubtypes).Assembly.GetType(typeName);
        if (t is null) return;
        var bf = BindingFlags.Public | BindingFlags.NonPublic
                 | BindingFlags.Instance | BindingFlags.Static;
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
        var m = t?.GetMethod("GetFormattedText", BindingFlags.Public | BindingFlags.Instance);
        if (m is null) return;
        _harmony!.Patch(m, finalizer: Local(nameof(LocStringFinalizer)));
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
        foreach (var t in SafeTypes())
        {
            if (!t.IsClass) continue;
            foreach (var m in t.GetMethods(AllDeclared))
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
        foreach (var t in SafeTypes())
        {
            if (!t.IsClass) continue;
            var ns = t.Namespace ?? "";
            if (!ns.Contains(".Vfx") && !t.Name.EndsWith("Vfx")) continue;
            foreach (var m in t.GetMethods(AllDeclared))
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
            "FromChooseABundleScreen", BindingFlags.Public | BindingFlags.Static);
        if (m is null)
        {
            HostLog.Info("FromChooseABundleScreen not found — bundle offers unsupported");
            return;
        }
        _harmony!.Patch(m, prefix: Local(nameof(BundlePrefix)));
        HostLog.Info("rerouted bundle offers to the headless stand-in");
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
            .GetMethod("ShowScreen", BindingFlags.Public | BindingFlags.Static);
        if (m is null)
        {
            HostLog.Info("NCrystalSphereScreen.ShowScreen not found — crystal sphere unsupported");
            return;
        }
        _harmony!.Patch(m, prefix: Local(nameof(CrystalPrefix)));
        HostLog.Info("rerouted crystal sphere to the headless stand-in");
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
        foreach (var m in t.GetMethods(
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            if (m.IsAbstract || m.IsGenericMethodDefinition || m.IsSpecialName) continue;
            var fin = typeof(Task).IsAssignableFrom(m.ReturnType) ? SwallowTask : Swallow;
            try { _harmony!.Patch(m, finalizer: fin); patched++; }
            catch { }
        }
        HostLog.Info($"swallowed {patched} progress-tracking methods");
    }

    // Monster model flavor hooks (BeforeDeath, BeforeRemovedFromRoom) play
    // death SFX / arm animations; in host they NRE on missing audio and
    // scene nodes, and that aborts CreatureCmd.Kill — the boss never dies
    // and the win condition never fires. Swallow: flavor only.
    private static void PatchMonsterFlavorHooks()
    {
        var bf = BindingFlags.Public | BindingFlags.NonPublic
                 | BindingFlags.Instance | BindingFlags.DeclaredOnly;
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
        HostLog.Info($"swallowed {patched} monster flavor hooks");
    }

    // Some monster death hooks call NAudioManager.Instance.PlayOneShot
    // without a null guard (Kaiser-crab bosses). Give the singleton an
    // uninitialized body and swallow every method's exceptions — audio is
    // pure output, no game state.
    private static void ImmunizeAudioSingletons()
    {
        const string name = "MegaCrit.Sts2.Core.Nodes.Audio.NAudioManager";
        var t = typeof(AbstractModelSubtypes).Assembly.GetType(name);
        if (t is null) return;
        try
        {
            var inst = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(t);
            if (!Reflect.SetStaticBackingField(t, "Instance", inst))
                t.GetField("_instance",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    ?.SetValue(null, inst);
            var patched = 0;
            foreach (var m in t.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (m.IsAbstract || m.IsGenericMethodDefinition || m.IsSpecialName) continue;
                try { _harmony!.Patch(m, finalizer: Swallow); patched++; }
                catch { }
            }
            HostLog.Info($"immunized {t.Name}: {patched} methods");
        }
        catch (Exception ex)
        {
            HostLog.Error($"immunize {name}", ex);
        }
    }

    // Epoch lookups throw on a cache miss and EnterAct's act-transition
    // broadcast hits them; return the NONE sentinel instead.
    private static void PatchEpochLookups()
    {
        var t = typeof(AbstractModelSubtypes).Assembly
            .GetType("MegaCrit.Sts2.Core.Multiplayer.Serialization.ModelIdSerializationCache");
        if (t is null) return;
        var bf = BindingFlags.Public | BindingFlags.Static;
        if (t.GetMethod("GetNetIdForEpochId", bf) is { } getNet)
            _harmony!.Patch(getNet, prefix: Local(nameof(EpochNetIdPrefix)));
        if (t.GetMethod("GetEpochIdForNetId", bf) is { } getId)
            _harmony!.Patch(getId, prefix: Local(nameof(EpochIdPrefix)));
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
