using RimWorld;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace SignalInterceptor
{
    public static class SignalInterceptorUtility
    {
        public static Thing TryGenerateUniqueWeapon()
        {
            try
            {
                List<ThingDef> uniqueWeaponDefs = DefDatabase<ThingDef>.AllDefs
                    .Where(d => d.comps != null &&
                           d.comps.Any(c => c.compClass == typeof(CompUniqueWeapon)))
                    .ToList();

                if (!uniqueWeaponDefs.Any())
                    return null;

                bool oldDevMode = Prefs.DevMode;
                Prefs.DevMode = false;
                Thing weapon = null;
                try
                {
                    weapon = ThingMaker.MakeThing(uniqueWeaponDefs.RandomElement());
                }
                finally
                {
                    Prefs.DevMode = oldDevMode;
                }

                return weapon;
            }
            catch (System.Exception ex)
            {
                Log.Warning("[Signal Interceptor] Failed to generate unique weapon: " + ex.Message);
                return null;
            }
        }
    }
}
