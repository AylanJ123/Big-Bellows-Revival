using HarmonyLib;
using System.Reflection;
using System.Runtime.CompilerServices;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using Vintagestory.GameContent.Mechanics;

namespace BellowsFix;

public sealed class BellowsFixModSystem : ModSystem
{
    private const string HarmonyOwner = "bellowsfix";
    private static readonly object PatchGate = new();
    private static Harmony? sharedPatcher;
    private static int activeInstances;
    private bool hasLease;

    public override void Start(ICoreAPI api)
    {
        base.Start(api);
        lock (PatchGate)
        {
            if (hasLease) return;
            if (sharedPatcher == null)
            {
                BellowsRuntime.Reset();
                ImprovedMetallurgyCompat.Reset();
                Harmony candidate = new(HarmonyOwner);
                try
                {
                    candidate.PatchAll(typeof(BellowsFixModSystem).Assembly);
                    sharedPatcher = candidate;
                }
                catch
                {
                    try { candidate.UnpatchAll(HarmonyOwner); }
                    finally
                    {
                        BellowsRuntime.Reset();
                        ImprovedMetallurgyCompat.Reset();
                    }
                    throw;
                }
            }
            activeInstances++;
            hasLease = true;
        }
    }

    public override void Dispose()
    {
        lock (PatchGate)
        {
            if (hasLease)
            {
                hasLease = false;
                if (--activeInstances == 0)
                {
                    try { sharedPatcher?.UnpatchAll(HarmonyOwner); }
                    finally
                    {
                        sharedPatcher = null;
                        BellowsRuntime.Reset();
                        ImprovedMetallurgyCompat.Reset();
                    }
                }
            }
        }
        base.Dispose();
    }
}

internal static class BellowsMath
{
    internal const float TwoPi = MathF.PI * 2f;
    internal const int VanillaFrames = 60;
    internal const int MaxStrokesPerTick = 8;

    internal static float FrameFromAngle(float angle, int frames, bool invert)
    {
        if (!float.IsFinite(angle) || frames <= 0) return 0f;
        float wrapped = angle % TwoPi;
        if (wrapped < 0f) wrapped += TwoPi;
        if (invert) wrapped = (TwoPi - wrapped) % TwoPi;
        float frame = wrapped / TwoPi * frames;
        return frame >= frames ? 0f : Math.Max(0f, frame);
    }

    internal static bool ShouldInvertRod(float rotation)
    {
        if (!float.IsFinite(rotation)) return false;
        int quarter = ((int)MathF.Round(rotation / 90f) % 4 + 4) % 4;
        return quarter is 0 or 1;
    }

    internal static int CountCrossedStrokes(double previous, double current, float trueSpeed, float dt)
    {
        if (!double.IsFinite(previous) || !double.IsFinite(current) ||
            !float.IsFinite(trueSpeed) || !float.IsFinite(dt) || dt <= 0f || trueSpeed <= 0.00001f)
            return 0;

        // A network replacement or packet correction must not produce a burst of air.
        double travelled = Math.Abs(current - previous);
        double expected = trueSpeed * 5d * dt;
        if (travelled > expected * 1.5d + 0.05d) return 0;
        double strokes = Math.Abs(Math.Floor(current / Math.PI) - Math.Floor(previous / Math.PI));
        return (int)Math.Min(MaxStrokesPerTick, strokes);
    }
}

internal sealed class BellowsState
{
    internal MechanicalNetwork? Network;
    internal float GearedRatio;
    internal double PreviousDriveAngle = double.NaN;
    internal bool WaitingForMesh;
    internal bool InitializationWarning;
    internal bool InitializationQueued;
    internal bool Disposed;

    internal int Advance(BEBehaviorMPConsumer consumer, float dt)
    {
        MechanicalNetwork? network = consumer.Network;
        float ratio = consumer.GearedRatio;
        if (network == null || !float.IsFinite(ratio))
        {
            ClearDrive();
            return 0;
        }

        // The network angle is continuous. AngleRad on the consumer is wrapped and
        // cannot distinguish several turns between the vanilla 500 ms ticks.
        double angle = (double)network.AngleRad * ratio;
        if (!ReferenceEquals(Network, network) || ratio != GearedRatio)
        {
            Network = network;
            GearedRatio = ratio;
            PreviousDriveAngle = angle;
            return 0;
        }

        int strokes = BellowsMath.CountCrossedStrokes(PreviousDriveAngle, angle, consumer.TrueSpeed, dt);
        PreviousDriveAngle = angle;
        return strokes;
    }

    internal void ClearDrive()
    {
        Network = null;
        PreviousDriveAngle = double.NaN;
    }
}

internal static class BellowsRuntime
{
    internal const string AnimationCode = "bellowing";
    internal const float AirPerStroke = 0.2f;
    private static ConditionalWeakTable<BlockEntityMechPoweredBellows, BellowsState> states = new();
    private static int generation;

    internal static BellowsState State(BlockEntityMechPoweredBellows be) => states.GetOrCreateValue(be);
    internal static int Generation => Volatile.Read(ref generation);

    internal static void Reset()
    {
        Interlocked.Increment(ref generation);
        states = new();
    }

    internal static void Forget(BlockEntityMechPoweredBellows be)
    {
        if (states.TryGetValue(be, out BellowsState? state))
        {
            lock (state) state.Disposed = true;
        }
        states.Remove(be);
    }

    internal static void QueueInitialization(BlockEntityMechPoweredBellows be, BlockEntityAnimationUtil util,
        BEBehaviorMPConsumer? consumer, AnimationMetaData? animation)
    {
        if (be.Api is not ICoreClientAPI client) return;
        BellowsState state = State(be);
        lock (state)
        {
            if (state.Disposed || state.InitializationQueued) return;
            state.InitializationQueued = true;
        }
        int version = Generation;
        client.Event.EnqueueMainThreadTask(() => InitializeIfCurrent(be, util, consumer, animation, state, version),
            "big-bellows-init");
    }

    internal static bool InitializeIfCurrent(BlockEntityMechPoweredBellows be, BlockEntityAnimationUtil util,
        BEBehaviorMPConsumer? consumer, AnimationMetaData? animation, BellowsState state, int version)
    {
        lock (state)
        {
            state.InitializationQueued = false;
            if (state.Disposed || version != Generation ||
                !states.TryGetValue(be, out BellowsState? current) || !ReferenceEquals(current, state)) return false;
        }
        if (be.Api is not ICoreClientAPI client) return false;

        try
        {
            if (!ReferenceEquals(client.World.BlockAccessor.GetBlockEntity(be.Pos), be)) return false;
            if (util.animator != null) return MeshReady(util);
            if (be.Block?.Shape?.Base == null)
            {
                WarnInitialization(be, state, "The block shape is unavailable.");
                return false;
            }
            string key = "bellowsfix-large-" + be.Block.Shape.Base;
            // Do not let the game enqueue an uncancellable renderer upload from
            // the chunk worker. This callback owns the entire main-thread setup.
            util.InitializeAnimator(key, null, null, new Vec3f(0f, Rotation(be), 0f));
            if (!MeshReady(util))
            {
                DiscardRenderer(util);
                WarnInitialization(be, state, "The animated mesh is unavailable.");
                return false;
            }
            PinFrame(be, consumer, util, animation);
            state.WaitingForMesh = false;
            client.World.BlockAccessor.MarkBlockDirty(be.Pos);
            return true;
        }
        catch (Exception error)
        {
            DiscardRenderer(util);
            WarnInitialization(be, state, error.Message);
            return false;
        }
    }

    private static void DiscardRenderer(BlockEntityAnimationUtil util)
    {
        try { util.renderer?.Dispose(); }
        catch { /* The static chunk mesh remains the fallback during shutdown. */ }
        util.renderer = null;
        util.animator = null;
    }

    private static void WarnInitialization(BlockEntityMechPoweredBellows be, BellowsState state, string reason)
    {
        if (state.InitializationWarning) return;
        state.InitializationWarning = true;
        be.Api.Logger.Warning("Big Bellows Revival could not initialize an animated mesh. Keeping the static bellows visible. {0}", reason);
    }

    internal static float Rotation(BlockEntityMechPoweredBellows be)
    {
        if (be.Block?.Shape is { } shape && float.IsFinite(shape.rotateY)) return shape.rotateY;
        BlockFacing? facing = Facing(be);
        return facing == null ? 0f : facing.HorizontalAngleIndex * 90f + 180f;
    }

    internal static BlockFacing? Facing(BlockEntityMechPoweredBellows be) =>
        be.Block?.Variant.TryGetValue("side", out string? side) == true ? BlockFacing.FromCode(side) : null;

    internal static bool MeshReady(BlockEntityAnimationUtil? util)
    {
        AnimatableRenderer? renderer = util?.renderer;
        return util?.animator != null && renderer?.mtmeshrefOpaque is { Initialized: true, Disposed: false };
    }

    internal static void PinFrame(BlockEntityMechPoweredBellows be, BEBehaviorMPConsumer? consumer,
        BlockEntityAnimationUtil? util, AnimationMetaData? animation)
    {
        if (animation == null) return;
        RunningAnimation? running = util?.animator?.GetAnimationState(AnimationCode);
        int frames = running?.Animation.QuantityFrames ?? BellowsMath.VanillaFrames;
        float frame = BellowsMath.FrameFromAngle(consumer?.AngleRad ?? 0f, frames,
            BellowsMath.ShouldInvertRod(Rotation(be)));
        animation.AnimationSpeed = 0f;
        animation.StartFrameOnce = frame;
        if (running == null) return;
        running.CurrentFrame = frame;
        // A manually pinned animation does not advance its easing or weight.
        running.EasingFactor = 1f;
        running.BlendedWeight = 1f;
        running.NowPlaying = false;
    }

    internal static void Stop(BlockEntityAnimationUtil? util, AnimationMetaData? animation)
    {
        if (animation != null) animation.AnimationSpeed = 0f;
        util?.StopAnimation(AnimationCode);
        // Speed zero prevents the ordinary ease-out from ever finishing.
        util?.animator?.GetAnimationState(AnimationCode)?.Stop();
    }

    internal static void Pump(BlockEntityMechPoweredBellows be, BEBehaviorMPConsumer consumer, float dt)
    {
        BellowsState state = State(be);
        int strokes = state.Advance(consumer, dt);
        if (strokes == 0) return;
        BlockFacing? facing = Facing(be);
        if (facing == null) return;
        BlockPos target = be.Pos.AddCopy(facing);
        IWorldAccessor world = be.Api.World;
        IBellowsAirReceiver? receiver = world.BlockAccessor.GetBlock(target)
            .GetInterface<IBellowsAirReceiver>(world, target);
        bool server = be.Api.Side == EnumAppSide.Server;
        for (int i = 0; i < strokes; i++)
        {
            if (server)
            {
                if (receiver != null) receiver.BlowAirInto(world, target, AirPerStroke, facing);
                else ImprovedMetallurgyCompat.TryBlow(be.Api, be.Pos, facing, AirPerStroke);
            }
            else if (receiver is BlockEntityForge)
            {
                // Vanilla forge particles are local and are not synchronized.
                // Unknown modded receivers receive authoritative server calls only.
                receiver.BlowAirInto(world, target, AirPerStroke, facing);
            }
        }
        if (!server) return;
        bool intake = Math.Abs(Math.Floor(state.PreviousDriveAngle / Math.PI) % 2d) >= 1d;
        int variant = world.Rand.Next(1, 4);
        world.PlaySoundAt(new AssetLocation($"game:sounds/block/bellowslarge/bellowlarge-{(intake ? "in" : "out")}{variant}"),
            be.Pos, 1d, null, true, 15f);
    }
}

[HarmonyPatch(typeof(BlockEntityMechPoweredBellows), nameof(BlockEntityMechPoweredBellows.Initialize))]
internal static class LargeBellowsInitializePatch
{
    internal static void Postfix(BlockEntityMechPoweredBellows __instance, ref AnimationMetaData? ___animation)
    {
        BellowsRuntime.Forget(__instance);
        if (___animation == null) return;
        ___animation = ___animation.Clone();
        ___animation.AnimationSpeed = 0f;
        ___animation.StartFrameOnce = 0f;
        // Stroke sounds are broadcast once by the server. Do not also play the
        // client animation sounds at every render frame.
        ___animation.AnimationSounds = Array.Empty<AnimationSound>();
    }
}

[HarmonyPatch(typeof(BlockEntityMechPoweredBellows), "onTick")]
internal static class LargeBellowsTickPatch
{
    internal static bool Prefix(BlockEntityMechPoweredBellows __instance, float dt,
        ref BEBehaviorMPConsumer? ___mpc, ref bool ___connected,
        BlockEntityAnimationUtil? ___animUtil, AnimationMetaData? ___animation)
    {
        BlockFacing? facing = BellowsRuntime.Facing(__instance);
        BEBehaviorMPConsumer? consumer = null;
        if (facing != null && __instance.Api?.World is { } world)
        {
            BlockPos armPos = __instance.Pos.DownCopy().AddCopy(facing.Opposite);
            Block armBlock = world.BlockAccessor.GetBlock(armPos);
            if (armBlock is BlockMPConsumer) consumer = armBlock.GetBEBehavior<BEBehaviorMPConsumer>(armPos);
        }

        if (!ReferenceEquals(___mpc, consumer))
        {
            if (___connected && __instance.Api is ICoreClientAPI oldClient)
                oldClient.Event.UnregisterRenderer(__instance, EnumRenderStage.Before);
            BellowsRuntime.Stop(___animUtil, ___animation);
            BellowsRuntime.State(__instance).ClearDrive();
            ___mpc = consumer;
            ___connected = false;
        }

        if (consumer == null) return false;
        BellowsRuntime.PinFrame(__instance, consumer, ___animUtil, ___animation);
        if (!___connected)
        {
            ___connected = true;
            if (__instance.Api is ICoreClientAPI client)
                client.Event.RegisterRenderer(__instance, EnumRenderStage.Before, "mechpoweredbellows");
        }
        if (___animation != null) ___animUtil?.StartAnimation(___animation);
        BellowsRuntime.Pump(__instance, consumer, dt);
        return false;
    }
}

[HarmonyPatch(typeof(BlockEntityMechPoweredBellows), nameof(BlockEntityMechPoweredBellows.OnRenderFrame))]
internal static class LargeBellowsRenderPatch
{
    internal static bool Prefix(BlockEntityMechPoweredBellows __instance, BEBehaviorMPConsumer? ___mpc,
        bool ___connected, BlockEntityAnimationUtil? ___animUtil, AnimationMetaData? ___animation)
    {
        if (___connected) BellowsRuntime.PinFrame(__instance, ___mpc, ___animUtil, ___animation);
        BellowsState state = BellowsRuntime.State(__instance);
        if (state.WaitingForMesh && BellowsRuntime.MeshReady(___animUtil))
        {
            state.WaitingForMesh = false;
            __instance.Api.World.BlockAccessor.MarkBlockDirty(__instance.Pos);
        }
        return false;
    }
}

[HarmonyPatch(typeof(BlockEntityMechPoweredBellows), nameof(BlockEntityMechPoweredBellows.OnTesselation))]
internal static class LargeBellowsRotationPatch
{
    internal static bool Prefix(BlockEntityMechPoweredBellows __instance, BlockEntityAnimationUtil? ___animUtil,
        BEBehaviorMPConsumer? ___mpc, AnimationMetaData? ___animation, ref bool __result)
    {
        __result = false;
        if (___animUtil == null || __instance.Api?.Side != EnumAppSide.Client) return false;
        if (___animUtil.animator == null)
        {
            BellowsRuntime.QueueInitialization(__instance, ___animUtil, ___mpc, ___animation);
            return false;
        }

        bool requested = ___animUtil.activeAnimationsByAnimCode.Count > 0 ||
            ___animUtil.animator?.ActiveAnimationCount > 0;
        bool ready = BellowsRuntime.MeshReady(___animUtil);
        BellowsRuntime.State(__instance).WaitingForMesh = requested && !ready;
        // Never remove the chunk mesh before the animated replacement is ready.
        // StartAnimation's dirty callback enables the renderer after this chunk
        // mesh has been replaced. Its old visibility flag must not keep a duplicate.
        __result = requested && ready;
        return false;
    }
}

[HarmonyPatch]
internal static class LargeBellowsCleanupPatch
{
    internal static IEnumerable<MethodBase> TargetMethods()
    {
        foreach (string name in new[] { "OnBlockUnloaded", "OnBlockBroken", "OnBlockRemoved" })
            yield return AccessTools.DeclaredMethod(typeof(BlockEntityMechPoweredBellows), name);
    }

    internal static void Prefix(BlockEntityMechPoweredBellows __instance, ref bool ___connected,
        BlockEntityAnimationUtil? ___animUtil, AnimationMetaData? ___animation)
    {
        if (__instance.Api is ICoreClientAPI client)
            client.Event.UnregisterRenderer(__instance, EnumRenderStage.Before);
        ___connected = false;
        BellowsRuntime.Stop(___animUtil, ___animation);
        BellowsRuntime.Forget(__instance);
    }
}
