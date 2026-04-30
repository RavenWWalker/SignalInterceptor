using System;
using System.Linq;
using Verse;

namespace SignalInterceptor
{
    [StaticConstructorOnStartup]
    public static class MultiplayerCompat
    {
        private static bool mpActive = false;

        public static bool IsInMultiplayer => mpActive;

        static MultiplayerCompat()
        {
            try
            {
                if (LoadedModManager.RunningModsListForReading
                    .Any(m => m.PackageId?.ToLower() == "rwmt.multiplayer"
                           || m.PackageId?.ToLower() == "zetrith.multiplayer"))
                {
                    InitMultiplayer();
                }
                else
                {
                    Log.Message("[Signal Interceptor] Multiplayer mod not detected, skipping MP compat.");
                }
            }
            catch (Exception e)
            {
                Log.Error("[Signal Interceptor] Error during MP compat init: " + e);
            }
        }

        private static void InitMultiplayer()
        {
            try
            {
                if (!Multiplayer.API.MP.enabled)
                    return;

                Multiplayer.API.MP.RegisterSyncMethod(
                    typeof(CompSignalInterceptor),
                    nameof(CompSignalInterceptor.ToggleScanning));

                Multiplayer.API.MP.RegisterSyncMethod(
                    typeof(CompSignalInterceptor),
                    nameof(CompSignalInterceptor.DevForceFind));

                Multiplayer.API.MP.RegisterSyncMethod(
                    typeof(CompSignalInterceptor),
                    nameof(CompSignalInterceptor.CycleSignalType));

                Multiplayer.API.MP.RegisterSyncMethod(
                    typeof(Patch_FactionDialog),
                    nameof(Patch_FactionDialog.RewardPlayer));

                mpActive = true;
                Log.Message("[Signal Interceptor] Multiplayer compatibility initialized.");
            }
            catch (Exception e)
            {
                Log.Error("[Signal Interceptor] Failed to initialize MP compat: " + e);
            }
        }
    }
}
