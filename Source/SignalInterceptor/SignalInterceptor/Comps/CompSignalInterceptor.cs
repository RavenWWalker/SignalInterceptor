using RimWorld;
using RimWorld.QuestGen;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;
using Verse.AI;

namespace SignalInterceptor
{
    [StaticConstructorOnStartup]
    public class CompSignalInterceptor : ThingComp
    {
        private static readonly Texture2D ScanIcon = ContentFinder<Texture2D>.Get("UI/Commands/Hack");
        private static readonly Texture2D IconSecretStash = ContentFinder<Texture2D>.Get("UI/Commands/SI_SecretStash");
        private static readonly Texture2D IconKidnapVIP = ContentFinder<Texture2D>.Get("UI/Commands/SI_KidnapVIP");

        private InterceptedSignalType selectedSignalType = InterceptedSignalType.SecretStash;
        private float daysWorkedSinceLastFind = 0f;
        private bool scanningEnabled = true;

        public bool ScanningEnabled => scanningEnabled;

        public CompProperties_SignalInterceptor Props
            => (CompProperties_SignalInterceptor)props;

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref selectedSignalType, "selectedSignalType", InterceptedSignalType.SecretStash);
            Scribe_Values.Look(ref daysWorkedSinceLastFind, "daysWorkedSinceLastFind", 0f);
            Scribe_Values.Look(ref scanningEnabled, "scanningEnabled", true);
        }

        // === Синхронизируемые методы ===

        public void ToggleScanning()
        {
            scanningEnabled = !scanningEnabled;
        }

        public void DevForceFind()
        {
            Pawn anyone = parent.Map.mapPawns.FreeColonistsSpawned.RandomElementWithFallback(null);
            if (anyone != null)
            {
                DoFind(anyone);
                daysWorkedSinceLastFind = 0f;
            }
        }

        public void CycleSignalType()
        {
            var values = GetAvailableSignalTypes();
            int currentIndex = values.IndexOf(selectedSignalType);
            int nextIndex = (currentIndex + 1) % values.Count;
            selectedSignalType = values[nextIndex];
        }

        // === Доступные типы сигналов ===

        private List<InterceptedSignalType> GetAvailableSignalTypes()
        {
            var types = new List<InterceptedSignalType>();
            types.Add(InterceptedSignalType.SecretStash);
            types.Add(InterceptedSignalType.KidnapVIP);
            return types;
        }

        // === Сканирование ===

        public bool OnScanTick(Pawn worker)
        {
            // Обновляем только раз в 60 тиков (~1 сек)
            if (Find.TickManager.TicksGame % 60 != 0)
                return false;

            float intellectLevel =
                worker.skills.GetSkill(SkillDefOf.Intellectual).Level;

            float speedMultiplier = intellectLevel / 10f;

            if (speedMultiplier < 0.1f)
                speedMultiplier = 0.1f;

            // Добавляем прогресс за 60 тиков
            daysWorkedSinceLastFind +=
                speedMultiplier * 60f / 60000f;

            // MTB-проверка тоже раз в 60 тиков
            if (Rand.MTBEventOccurs(
                Props.scanFindMtbDays / speedMultiplier,
                60000f,
                60f))
            {
                DoFind(worker);
                daysWorkedSinceLastFind = 0f;
                return true;
            }

            // Гарантированная находка
            if (daysWorkedSinceLastFind >=
                Props.scanFindGuaranteedDays)
            {
                DoFind(worker);
                daysWorkedSinceLastFind = 0f;
                return true;
            }

            return false;
        }
        public float ScanProgress => daysWorkedSinceLastFind / Props.scanFindGuaranteedDays;

        private void DoFind(Pawn worker)
        {
            switch (selectedSignalType)
            {
                case InterceptedSignalType.SecretStash:
                    GenerateStashQuest(worker);
                    break;
                case InterceptedSignalType.KidnapVIP:
                    GenerateVIPQuest(worker);
                    break;
            }

            Log.Message("[Signal Interceptor] DoFind triggered! Type: " + selectedSignalType +
                        " Worker: " + worker.LabelShort);
        }

        private void GenerateStashQuest(Pawn worker)
        {
            QuestScriptDef questScript = DefDatabase<QuestScriptDef>.GetNamedSilentFail("Quest_InterceptedStash");
            if (questScript == null)
            {
                Log.Error("[Signal Interceptor] Quest_InterceptedStash QuestScriptDef not found!");
                return;
            }

            bool hasFaction = Find.FactionManager.AllFactions
                .Any(f => !f.IsPlayer && !f.defeated && !f.Hidden && f.def.humanlikeFaction);

            if (!hasFaction)
            {
                Messages.Message(
                    "Failed to generate stash quest — no valid factions available.",
                    parent,
                    MessageTypeDefOf.RejectInput,
                    historical: false);
                return;
            }

            Slate slate = new Slate();
            slate.Set("map", parent.Map);
            slate.Set("worker", worker);

            Quest quest = QuestUtility.GenerateQuestAndMakeAvailable(questScript, slate);
            quest.Accept(null);

            Find.LetterStack.ReceiveLetter(
                quest.name,
                quest.description,
                LetterDefOf.PositiveEvent,
                null, null, quest);
        }

        private void GenerateVIPQuest(Pawn worker)
        {
            QuestScriptDef questScript = DefDatabase<QuestScriptDef>.GetNamedSilentFail("Quest_InterceptedVIP");
            if (questScript == null)
            {
                Log.Error("[Signal Interceptor] Quest_InterceptedVIP QuestScriptDef not found!");
                return;
            }

            bool hasFaction = Find.FactionManager.AllFactions
                .Any(f => !f.IsPlayer && !f.defeated && !f.Hidden && f.def.humanlikeFaction);

            if (!hasFaction)
            {
                Messages.Message(
                    "Failed to generate VIP quest — no valid factions available.",
                    parent,
                    MessageTypeDefOf.RejectInput,
                    historical: false);
                return;
            }

            Slate slate = new Slate();
            slate.Set("map", parent.Map);
            slate.Set("worker", worker);

            Quest quest = QuestUtility.GenerateQuestAndMakeAvailable(questScript, slate);
            quest.Accept(null);

            Find.LetterStack.ReceiveLetter(
                quest.name,
                quest.description,
                LetterDefOf.PositiveEvent,
                null, null, quest);
        }

        // === Gizmos ===

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (Gizmo gizmo in base.CompGetGizmosExtra())
            {
                yield return gizmo;
            }

            if (parent.Faction != Faction.OfPlayer)
                yield break;

            // Кнопка вкл/выкл
            Command_Toggle toggleScan = new Command_Toggle();
            toggleScan.defaultLabel = scanningEnabled
                ? "SI_ScanningOn".Translate()
                : "SI_ScanningOff".Translate();
            toggleScan.defaultDesc = scanningEnabled
                ? "SI_ScanningOnDesc".Translate()
                : "SI_ScanningOffDesc".Translate();
            toggleScan.isActive = () => scanningEnabled;
            toggleScan.toggleAction = delegate { ToggleScanning(); };
            toggleScan.icon = ScanIcon;
            yield return toggleScan;

            // Кнопка выбора типа сигнала
            Command_Action cycleType = new Command_Action();
            cycleType.defaultLabel = "SI_SignalType".Translate(GetSignalTypeLabel(selectedSignalType));
            cycleType.defaultDesc = "SI_SignalTypeDesc".Translate();
            cycleType.icon = GetSignalTypeIcon(selectedSignalType);
            cycleType.action = delegate { CycleSignalType(); };
            yield return cycleType;

            // Дебаг
            if (Prefs.DevMode)
            {
                Command_Action devFind = new Command_Action();
                devFind.defaultLabel = "SI_DevForceFind".Translate();
                devFind.action = delegate { DevForceFind(); };
                yield return devFind;
            }
        }

        public override string CompInspectStringExtra()
        {
            if (!scanningEnabled)
                return "SI_InspectDisabled".Translate();

            string text = "SI_InspectScanning".Translate(scanningEnabled ? "SI_ScanningOn".Translate() : "SI_ScanningOff".Translate());
            text += "\n" + "SI_InspectTarget".Translate(GetSignalTypeLabel(selectedSignalType));
            text += "\n" + "SI_InspectProgress".Translate(ScanProgress.ToStringPercent());
            return text;
        }

        private Texture2D GetSignalTypeIcon(InterceptedSignalType type)
        {
            switch (type)
            {
                case InterceptedSignalType.SecretStash:
                    return IconSecretStash;
                case InterceptedSignalType.KidnapVIP:
                    return IconKidnapVIP;
                default:
                    return ScanIcon;
            }
        }

        private string GetSignalTypeLabel(InterceptedSignalType type)
        {
            switch (type)
            {
                case InterceptedSignalType.SecretStash:
                    return "SI_TypeSecretStash".Translate();
                case InterceptedSignalType.KidnapVIP:
                    return "SI_TypeKidnapVIP".Translate();
                default:
                    return "Unknown";
            }
        }
    }
}
