using System.Reflection;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace BellowsFix;

internal static class ImprovedMetallurgyCompat
{
    private const string FurnaceTypeName = "ImprovedMetallurgy.BlockEntityBlastFurnace";
    private static Type? furnaceType;
    private static MethodInfo? blowAir;
    private static bool resolved;
    private static bool disabled;

    internal static bool TryBlow(ICoreAPI api, BlockPos bellowsPos, BlockFacing direction, float amount)
    {
        if (api.Side != EnumAppSide.Server || direction.IsVertical || !float.IsFinite(amount) || amount <= 0f)
            return false;

        if (disabled || !Resolve(api)) return false;

        var accessor = api.World.BlockAccessor;
        var intake = accessor.GetBlock(bellowsPos.AddCopy(direction));
        var intakeCode = intake?.Code;
        if (intakeCode == null ||
            (!intakeCode.Path.StartsWith("refractorybrick", StringComparison.Ordinal) &&
             intakeCode.Domain != "improved_metallurgy"))
            return false;

        // Use the bounded intake area supported by Improved Metallurgy's manual bellows.
        int lateralX = Math.Abs(direction.Normali.Z);
        int lateralZ = Math.Abs(direction.Normali.X);
        for (int forward = 1; forward <= 3; forward++)
        {
            for (int lateral = -1; lateral <= 1; lateral++)
            {
                for (int height = 0; height <= 2; height++)
                {
                    var position = bellowsPos.AddCopy(
                        direction.Normali.X * forward + lateralX * lateral,
                        height,
                        direction.Normali.Z * forward + lateralZ * lateral);
                    var furnace = accessor.GetBlockEntity(position);
                    if (furnace == null || !furnaceType!.IsInstanceOfType(furnace)) continue;

                    try
                    {
                        // The furnace owns its burning-state check and oxygen limit.
                        blowAir!.Invoke(furnace, [amount, direction]);
                        return true;
                    }
                    catch (Exception error)
                    {
                        Disable(api, error.GetBaseException().Message);
                        return false;
                    }
                }
            }
        }

        return false;
    }

    internal static void Reset()
    {
        furnaceType = null;
        blowAir = null;
        resolved = false;
        disabled = false;
    }

    private static bool Resolve(ICoreAPI api)
    {
        if (resolved) return furnaceType != null && blowAir != null;
        resolved = true;

        try
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var candidate = assembly.GetType(FurnaceTypeName, false);
                if (candidate == null) continue;

                furnaceType = candidate;
                blowAir = candidate.GetMethod("BlowAirInto", BindingFlags.Public | BindingFlags.Instance,
                    null, [typeof(float), typeof(BlockFacing)], null);
                if (typeof(BlockEntity).IsAssignableFrom(candidate) && blowAir?.ReturnType == typeof(void))
                    return true;

                Disable(api, "The loaded furnace does not expose the supported air-input method.");
                return false;
            }
        }
        catch (Exception error)
        {
            Disable(api, error.GetBaseException().Message);
            return false;
        }

        return false;
    }

    private static void Disable(ICoreAPI api, string reason)
    {
        disabled = true;
        api.Logger?.Warning("[Big Bellows Revival] Optional Improved Metallurgy support disabled for this session. {0}", reason);
    }
}
