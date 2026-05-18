using RimWorld;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;
using Verse.AI;

namespace SignalInterceptor.AI.Psycaster
{
    public partial class PsycasterBrain
    {
        private bool TryRunRecoveryLogic(BattlefieldSnapshot snap)
        {
            if (caster == null || caster.Destroyed || caster.Dead || caster.Downed)
                return false;

            int now = Find.TickManager.TicksGame;

            if (!recoveryMode && ShouldEnterRecoveryMode(snap))
            {
                recoveryMode = true;
                recoveryStartedTick = now;
                nextRecoveryThinkTick = now;

                pendingMeleeTargetThingId = -1;
                pendingMeleeUntilTick = -1;
                pendingMeleeReason = null;

                ClearKillContract("enter recovery");

                currentStance = PsycasterStance.Survive;
                nextStanceReevalTick = now + Rand.RangeInclusive(180, 300);
                nextActionSelectTick = now;

                Log.Message("[Signal Interceptor] Psycaster entering recovery mode: "
                            + caster.LabelShort
                            + " | hp=" + caster.health.summaryHealth.SummaryHealthPercent.ToString("F2")
                            + " | reason=" + GetRecoveryReasonForLog(snap));

                return true;
            }

            if (!recoveryMode)
                return false;

            /*
             * Новый выход из recovery:
             * не держим режим, если HP уже достаточно безопасный,
             * кровотечения нет, и нет настоящего давления.
             */
            if (IsRecoveredEnough(snap))
            {
                recoveryMode = false;
                recoveryStartedTick = -1;
                nextRecoveryThinkTick = -1;

                currentStance = StanceSelector.Select(snap, currentStance);
                nextStanceReevalTick = now + Rand.RangeInclusive(120, 180);
                nextActionSelectTick = now + Rand.RangeInclusive(30, 60);

                Log.Message("[Signal Interceptor] Psycaster leaving recovery mode: "
                            + caster.LabelShort
                            + " | hp=" + caster.health.summaryHealth.SummaryHealthPercent.ToString("F2")
                            + " | bleeding=" + HasDangerousBleeding()
                            + " | pressure=" + HasRecoveryThreatPressure(snap));

                return false;
            }

            if (now < nextRecoveryThinkTick)
                return true;

            /*
             * Если реген может работать, давления нет, кровотечения нет —
             * не надо каждые 90-150 тиков заново давать retreat/Goto.
             * Держим позицию.
             */
            if (ShouldHoldRecoveryPosition(snap))
            {
                nextRecoveryThinkTick = now + Rand.RangeInclusive(180, 300);

                if (Prefs.DevMode)
                {
                    HediffComp_PsycasterRestoringMechanisms restore = GetRestoringMechanisms();

                    Log.Message("[Signal Interceptor] Psycaster recovery hold position: "
                                + caster.LabelShort
                                + " | hp=" + caster.health.summaryHealth.SummaryHealthPercent.ToString("F2")
                                + " | canRegen=" + (restore != null && restore.CanRegenerateNow)
                                + " | ticksSinceDamage=" + (restore != null ? restore.TicksSinceDamage : -1));
                }

                return true;
            }

            nextRecoveryThinkTick = now + Rand.RangeInclusive(90, 150);

            RunRecoveryMovement(snap);

            return true;
        }

        private void RunRecoveryMovement(BattlefieldSnapshot snap)
        {
            if (!IsCasterFreeToAct())
                return;

            /*
             * Если можно безопасно регениться на месте — не стартуем retreat/Goto.
             */
            if (ShouldHoldRecoveryPosition(snap))
                return;

            /*
             * 1) Сначала recovery-kite Skip.
             */
            if (snap != null && snap.HasEnemies && TryRecoveryKiteSkip(snap))
                return;

            /*
             * 2) Потом emergency retreat.
             */
            if (snap != null && snap.HasEnemies && TryEmergencyRetreat(snap))
                return;

            /*
             * 3) Defensive psycast только при реальном ranged pressure.
             */
            if (snap != null && snap.HasEnemies && TryRecoveryDefensivePsycast(snap))
                return;

            /*
             * 4) Если уже идёт безопасный Goto — не трогаем.
             */
            if (caster.CurJobDef == JobDefOf.Goto && IsRecoveryPositionStillSafe(snap))
                return;

            /*
             * 5) Fallback retreat.
             */
            IntVec3 retreatCell;

            if (snap != null && snap.HasEnemies && TryFindEmergencyRetreatCell(snap, out retreatCell))
            {
                Job job = JobMaker.MakeJob(JobDefOf.Goto, retreatCell);
                job.locomotionUrgency = LocomotionUrgency.Sprint;
                job.expiryInterval = Rand.RangeInclusive(75, 120);

                caster.jobs.StartJob(job, JobCondition.InterruptForced);

                HediffComp_PsycasterRestoringMechanisms restore = GetRestoringMechanisms();

                Log.Message("[Signal Interceptor] Psycaster recovery retreat: "
                            + caster.LabelShort
                            + " -> " + retreatCell
                            + " | hp=" + caster.health.summaryHealth.SummaryHealthPercent.ToString("F2")
                            + " | canRegen=" + (restore != null && restore.CanRegenerateNow)
                            + " | ticksSinceDamage=" + (restore != null ? restore.TicksSinceDamage : -1));

                return;
            }

            HediffComp_PsycasterRestoringMechanisms comp = GetRestoringMechanisms();

            if (Prefs.DevMode)
            {
                Log.Message("[Signal Interceptor] Psycaster recovery holding: "
                            + caster.LabelShort
                            + " | hp=" + caster.health.summaryHealth.SummaryHealthPercent.ToString("F2")
                            + " | canRegen=" + (comp != null && comp.CanRegenerateNow)
                            + " | ticksSinceDamage=" + (comp != null ? comp.TicksSinceDamage : -1));
            }
        }

        private bool TryRecoveryDefensivePsycast(BattlefieldSnapshot snap)
        {
            if (snap == null || caster == null || caster.Destroyed || caster.Dead || caster.Downed || !caster.Spawned)
                return false;

            if (caster.Map == null || !snap.HasEnemies)
                return false;

            float hp = caster.health != null && caster.health.summaryHealth != null
                ? caster.health.summaryHealth.SummaryHealthPercent
                : 1f;

            float meaningfulIncomingDps;
            float nearestShooterDist;

            int rangedPressure = CountMeaningfulRangedPressure(
                snap,
                out meaningfulIncomingDps,
                out nearestShooterDist);

            int enemyCount = snap.enemies != null ? snap.enemies.Count : 0;
            bool singleEnemy = enemyCount <= 1;

            bool invisible = IsCasterInvisibleNow();

            bool underRangedPressure =
                rangedPressure >= 1 ||
                meaningfulIncomingDps >= 10f;

            bool severeRangedPressure =
                rangedPressure >= 2 ||
                meaningfulIncomingDps >= 35f;

            bool panicRangedPressure =
                rangedPressure >= 3 ||
                meaningfulIncomingDps >= 55f;

            bool critical = hp <= 0.45f;
            bool dangerous = hp <= 0.65f;
            bool damaged = hp <= 0.78f;

            /*
             * Жёсткий guard:
             * defensive psycast в recovery не нужен, если нет настоящего ranged pressure.
             */
            if (!underRangedPressure)
                return false;

            /*
             * Почти полный HP — не тратить Invisibility/Smoke,
             * кроме настоящей паники под фокусом стрелков.
             */
            if (hp >= 0.88f && !panicRangedPressure)
                return false;

            /*
             * Уже invisible — второй defensive layer только при критической панике.
             */
            if (invisible && !(critical && panicRangedPressure))
                return false;

            /*
             * Один стрелок против нормального HP не должен вызывать защитные касты.
             */
            if (singleEnemy && hp >= 0.80f && !panicRangedPressure)
                return false;

            /*
             * 1) Invisibility — только если реально опасно.
             */
            bool shouldCastInvisibility =
                !invisible &&
                (
                    critical ||
                    dangerous ||
                    severeRangedPressure ||
                    (damaged && panicRangedPressure)
                );

            if (shouldCastInvisibility)
            {
                if (TryCastRecoverySelfAbility("Invisibility", PsycasterTuning.InvisibilitySoftCooldownMin, PsycasterTuning.InvisibilitySoftCooldownMax))
                {
                    nextRecoveryThinkTick = Find.TickManager.TicksGame + Rand.RangeInclusive(130, 190);
                    nextActionSelectTick = Find.TickManager.TicksGame + PsycasterTuning.CastWarmupMedium + 120;

                    Log.Message("[Signal Interceptor] Psycaster recovery defensive Invisibility"
                                + " | HP=" + hp.ToString("F2")
                                + " | rangedPressure=" + rangedPressure
                                + " | meaningfulDps=" + meaningfulIncomingDps.ToString("F1")
                                + " | nearestShooter=" + nearestShooterDist.ToString("F1")
                                + " | enemies=" + enemyCount);

                    return true;
                }
            }

            /*
             * 2) Smokepop — только под реальным стрелковым давлением.
             */
            bool shouldCastSmokepop =
                rangedPressure >= 1 &&
                (
                    critical ||
                    dangerous ||
                    panicRangedPressure ||
                    (damaged && severeRangedPressure)
                );

            if (shouldCastSmokepop)
            {
                if (TryCastRecoverySelfAbility("Smokepop", PsycasterTuning.SmokepopSoftCooldownMin, PsycasterTuning.SmokepopSoftCooldownMax))
                {
                    nextRecoveryThinkTick = Find.TickManager.TicksGame + Rand.RangeInclusive(130, 190);
                    nextActionSelectTick = Find.TickManager.TicksGame + PsycasterTuning.CastWarmupMedium + 120;

                    Log.Message("[Signal Interceptor] Psycaster recovery defensive Smokepop"
                                + " | HP=" + hp.ToString("F2")
                                + " | rangedPressure=" + rangedPressure
                                + " | meaningfulDps=" + meaningfulIncomingDps.ToString("F1")
                                + " | nearestShooter=" + nearestShooterDist.ToString("F1")
                                + " | invisible=" + invisible
                                + " | enemies=" + enemyCount);

                    return true;
                }
            }

            return false;
        }

        private bool TryCastRecoverySelfAbility(string abilityDefName, int softCooldownMin, int softCooldownMax)
        {
            if (string.IsNullOrEmpty(abilityDefName))
                return false;

            if (IsOnSoftCooldown(abilityDefName))
                return false;

            if (!HasEnoughPsyfocusForAbility(abilityDefName))
            {
                if (Prefs.DevMode)
                {
                    Log.Message("[Signal Interceptor] Psycaster recovery cast blocked by psyfocus: "
                                + abilityDefName
                                + " | psyfocus=" + GetCurrentPsyfocusFraction().ToString("F2")
                                + " | required=" + GetMinimumPsyfocusForAbility(abilityDefName).ToString("F2"));
                }

                return false;
            }

            AbilityDef def = GetAbilityDef(abilityDefName);

            if (def == null)
                return false;

            object ability = GetPawnAbilityObject(def);

            if (ability == null)
                return false;

            if (IsAbilityOnCooldown(ability))
                return false;

            bool casted = gc.TryCastSelfPsyAbility_Public(caster, abilityDefName);

            if (!casted)
                return false;

            softCooldowns[abilityDefName] = Find.TickManager.TicksGame + Rand.RangeInclusive(softCooldownMin, softCooldownMax);

            pendingMeleeTargetThingId = -1;
            pendingMeleeUntilTick = -1;
            pendingMeleeReason = null;

            ClearKillContract("recovery defensive " + abilityDefName);

            return true;
        }

        private bool TryRecoveryKiteSkip(BattlefieldSnapshot snap)
        {
            if (snap == null || caster == null || caster.Destroyed || caster.Dead || caster.Downed || !caster.Spawned)
                return false;

            if (caster.Map == null || !snap.HasEnemies)
                return false;

            int now = Find.TickManager.TicksGame;

            /*
             * ВАЖНО:
             * Не используем слишком длинный nextEmergencyRetreatTick как жёсткий запрет.
             * В recovery быстрые животные могут догнать кастера раньше,
             * чем старый emergency cooldown истечёт.
             *
             * Но полностью спамить Skip тоже нельзя.
             * Поэтому используем мягкое окно 150 тиков после последнего emergency Skip.
             */
            if (now < nextEmergencyRetreatTick - 210)
                return false;

            float hp = caster.health != null && caster.health.summaryHealth != null
                ? caster.health.summaryHealth.SummaryHealthPercent
                : 1f;

            int closeMeleeOrAnimals = 0;
            int veryCloseMeleeOrAnimals = 0;
            float nearestMeleeOrAnimalDist = 999f;
            Pawn nearestMeleeOrAnimal = null;

            if (snap.enemies != null)
            {
                for (int i = 0; i < snap.enemies.Count; i++)
                {
                    EnemyAssessment e = snap.enemies[i];

                    if (e == null || e.pawn == null)
                        continue;

                    if (e.pawn.Destroyed || e.pawn.Dead || e.pawn.Downed || !e.pawn.Spawned || e.pawn.Map != caster.Map)
                        continue;

                    bool meleeLike = e.IsAnimal || e.IsMelee || e.role == EnemyRole.Wimp;

                    if (!meleeLike)
                        continue;

                    if (e.distanceToCaster < nearestMeleeOrAnimalDist)
                    {
                        nearestMeleeOrAnimalDist = e.distanceToCaster;
                        nearestMeleeOrAnimal = e.pawn;
                    }

                    if (e.distanceToCaster <= 10f)
                        closeMeleeOrAnimals++;

                    if (e.distanceToCaster <= 4f)
                        veryCloseMeleeOrAnimals++;
                }
            }

            /*
             * Условия recovery-skip.
             *
             * Главная цель:
             * не ждать, пока животное ударит.
             * Если оно уже в 8-10 клетках и кастер лечится/кровоточит —
             * лучше потратить Skip и сохранить реген.
             */
            bool shouldSkip =
                (hp <= 0.82f && nearestMeleeOrAnimalDist <= 12f) ||
                (hp <= 0.90f && nearestMeleeOrAnimalDist <= 8f) ||
                (veryCloseMeleeOrAnimals >= 1) ||
                (closeMeleeOrAnimals >= 2);

            if (!shouldSkip)
                return false;

            AbilityDef skipDef = GetAbilityDef("Skip");

            if (skipDef == null)
                return false;

            if (!HasEnoughPsyfocusForAbility("Skip"))
            {
                if (Prefs.DevMode)
                {
                    Log.Message("[Signal Interceptor] Psycaster recovery-kite Skip blocked by psyfocus: "
                                + caster.LabelShort
                                + " | psyfocus=" + GetCurrentPsyfocusFraction().ToString("F2")
                                + " | required=" + GetMinimumPsyfocusForAbility("Skip").ToString("F2"));
                }

                return false;
            }

            object ability = GetPawnAbilityObject(skipDef);

            if (ability == null)
                return false;

            if (IsAbilityOnCooldown(ability))
                return false;

            IntVec3 skipCell;

            if (!TryFindRecoverySkipCell(snap, out skipCell))
                return false;

            bool casted = gc.TryCastPsyAbilityToDestination_Public(
                caster,
                "Skip",
                caster,
                skipCell);

            if (!casted)
                return false;

            pendingMeleeTargetThingId = -1;
            pendingMeleeUntilTick = -1;
            pendingMeleeReason = null;

            ClearKillContract("recovery kite skip");

            ApplySoftCooldown("Skip");

            /*
             * Короткое окно: это именно recovery-kite, а не обычный боевой escape.
             * Если животное снова догнало — через несколько секунд можно повторить.
             */
            nextEmergencyRetreatTick = Find.TickManager.TicksGame + Rand.RangeInclusive(150, 240);
            nextActionSelectTick = Find.TickManager.TicksGame + PsycasterTuning.CastWarmupShort + 75;
            nextRecoveryThinkTick = Find.TickManager.TicksGame + Rand.RangeInclusive(75, 105);

            Log.Message("[Signal Interceptor] Psycaster recovery-kite Skip self to "
                        + skipCell
                        + " | HP=" + hp.ToString("F2")
                        + " | nearestMelee="
                        + (nearestMeleeOrAnimal != null ? nearestMeleeOrAnimal.LabelShort : "null")
                        + " | nearestD=" + nearestMeleeOrAnimalDist.ToString("F1")
                        + " | closeMelee=" + closeMeleeOrAnimals
                        + " | veryCloseMelee=" + veryCloseMeleeOrAnimals);

            return true;
        }

        private bool IsRecoveryPositionStillSafe(BattlefieldSnapshot snap)
        {
            if (caster == null || caster.Destroyed || caster.Dead || caster.Downed || !caster.Spawned)
                return false;

            Map map = caster.Map;

            if (map == null)
                return false;

            /*
             * Если текущий recovery-Goto ведёт к краю карты — это больше не safe.
             */
            Job curJob = caster.CurJob;

            if (curJob != null && curJob.def == JobDefOf.Goto && curJob.targetA.IsValid)
            {
                IntVec3 targetCell = curJob.targetA.Cell;

                if (targetCell.IsValid)
                {
                    if (IsNearMapEdge(targetCell, map, MapEdgeDangerDistance))
                        return false;

                    /*
                     * Если target слишком далеко от HomeAnchor, не считаем этот Goto безопасным.
                     * Это не жёсткая привязка — просто текущий retreat надо пересчитать.
                     */
                    if (HomeAnchor.IsValid)
                    {
                        float targetHomeDist = targetCell.DistanceTo(HomeAnchor);

                        if (targetHomeDist > SoftHomeHardRadius)
                            return false;
                    }
                }
            }

            /*
             * Если он сам уже слишком близко к краю, текущая позиция небезопасна.
             */
            if (IsNearMapEdge(caster.Position, map, MapEdgeDangerDistance))
                return false;

            if (snap == null || snap.enemies == null || snap.enemies.Count == 0)
                return true;

            for (int i = 0; i < snap.enemies.Count; i++)
            {
                EnemyAssessment e = snap.enemies[i];

                if (e == null || e.pawn == null)
                    continue;

                if (e.pawn.Destroyed || e.pawn.Dead || e.pawn.Downed || !e.pawn.Spawned || e.pawn.Map != map)
                    continue;

                /*
                 * Животные и melee-враги опасны даже без "ranged fire".
                 * Если они снова подошли близко — текущий Goto больше не безопасен,
                 * надо пересчитать отступление.
                 */
                if ((e.IsAnimal || e.IsMelee || e.role == EnemyRole.Wimp) && e.distanceToCaster < 14f)
                    return false;

                /*
                 * Дальник с LOS тоже ломает recovery:
                 * реген не успеет начаться, если кастера продолжают простреливать.
                 */
                if (e.IsRanged && e.hasLineOfSight)
                {
                    float safeRange = e.weaponRange > 0f ? e.weaponRange + 2f : 24f;

                    if (e.distanceToCaster <= safeRange)
                        return false;
                }
            }

            return true;
        }

        private bool ShouldHoldRecoveryPosition(BattlefieldSnapshot snap)
        {
            if (caster == null || caster.health == null)
                return false;

            if (HasDangerousBleeding())
                return false;

            if (HasRecoveryThreatPressure(snap))
                return false;

            float hp = caster.health.summaryHealth.SummaryHealthPercent;

            HediffComp_PsycasterRestoringMechanisms restore = GetRestoringMechanisms();

            if (restore == null)
                return false;

            if (!restore.CanRegenerateNow)
                return hp >= 0.70f && restore.TicksSinceDamage >= 0;

            /*
             * Было 0.82.
             * Теперь держим recovery дольше, иначе он выходит/двигается слишком рано,
             * хотя реген уже может спокойно доделать работу.
             */
            if (hp >= 0.88f)
                return false;

            return hp >= 0.45f;
        }

        private bool ShouldEnterRecoveryMode(BattlefieldSnapshot snap)
        {
            if (caster == null || caster.health == null)
                return false;

            float hp = caster.health.summaryHealth.SummaryHealthPercent;


            int psylink = GetCasterPsylinkLevelSafe();

            float recentDamageRecoveryHp =
                psylink <= 4 ? 0.68f :
                psylink == 5 ? 0.58f :
                0.50f;

            if (WasRecentlyDamaged(240) && hp <= recentDamageRecoveryHp)
                return true;

            /*
             * Жёсткий критический порог.
             * Сюда входим всегда.
             */
            if (hp <= 0.45f)
                return true;

            /*
             * Старое правило: если HP просел и есть опасное кровотечение —
             * пора отходить.
             */
            if (hp <= 0.58f && HasDangerousBleeding())
                return true;

            if (snap == null || snap.enemies == null || snap.enemies.Count == 0)
                return false;

            int closeMeleeOrAnimals = 0;
            int veryCloseMeleeOrAnimals = 0;
            bool dangerousCloseAnimal = false;
            bool dangerousCloseHumanMelee = false;

            for (int i = 0; i < snap.enemies.Count; i++)
            {
                EnemyAssessment e = snap.enemies[i];

                if (e == null || e.pawn == null)
                    continue;

                if (e.pawn.Destroyed || e.pawn.Dead || e.pawn.Downed || !e.pawn.Spawned)
                    continue;

                bool meleeLike = e.IsAnimal || e.IsMelee || e.role == EnemyRole.Wimp;

                if (!meleeLike)
                    continue;

                if (e.distanceToCaster <= 6f)
                    closeMeleeOrAnimals++;

                if (e.distanceToCaster <= 2.5f)
                    veryCloseMeleeOrAnimals++;

                /*
                 * Животное рядом — это реальная угроза, даже если у него threatScore не огромный.
                 * Медведи/пумы/ленивцы регулярно сбивают реген.
                 */
                if (e.IsAnimal && e.distanceToCaster <= 5f && e.threatScore >= 2f)
                    dangerousCloseAnimal = true;

                /*
                 * Wimp/Melee-гуманоид тоже может зацарапать кастера,
                 * особенно если бой затянулся и реген постоянно сбивается.
                 */
                if ((e.IsMelee || e.role == EnemyRole.Wimp) && e.distanceToCaster <= 2.5f)
                    dangerousCloseHumanMelee = true;
            }

            /*
             * Новый важный блок:
             * если кастер уже не фулловый и рядом есть контактная угроза —
             * не ждём 45% HP. Уходим раньше.
             */
            if (hp <= 0.82f && veryCloseMeleeOrAnimals >= 1)
                return true;

            if (hp <= 0.75f && closeMeleeOrAnimals >= 1)
                return true;

            if (hp <= 0.88f && dangerousCloseAnimal)
                return true;

            if (hp <= 0.78f && dangerousCloseHumanMelee)
                return true;

            /*
             * Если рядом сразу несколько ближников/животных —
             * это уже окружение, даже при неплохом HP.
             */
            if (hp <= 0.90f && closeMeleeOrAnimals >= 2)
                return true;

            return false;
        }

        private bool IsRecoveredEnough(BattlefieldSnapshot snap)
        {
            if (caster == null || caster.health == null)
                return false;

            float hp = caster.health.summaryHealth.SummaryHealthPercent;

            bool bleeding = HasDangerousBleeding();
            bool pressure = HasRecoveryThreatPressure(snap);

            /*
             * Не выходим из recovery под давлением.
             */
            if (pressure)
                return false;

            /*
             * Не выходим, пока есть опасное кровотечение.
             */
            if (bleeding)
                return false;

            /*
             * Нормальный чистый выход.
             * Было 0.92 без проверки pressure в первом условии.
             */
            if (hp >= 0.95f)
                return true;

            /*
             * Осторожный выход.
             * Было 0.82 — слишком рано.
             */
            if (hp >= 0.88f)
                return true;

            /*
             * Если остались только melee/animals и они реально далеко,
             * можно выйти чуть раньше, но не на 0.78.
             */
            if (hp >= 0.86f && HasOnlyMeleeOrAnimalEnemies(snap))
                return true;

            /*
             * Plateau после ампутации/перманентного cap-а.
             * Только без крови и давления.
             */
            if (IsRecoveryPlateauLikely())
                return true;

            return false;
        }

        private string GetRecoveryReasonForLog(BattlefieldSnapshot snap)
        {
            if (caster == null || caster.health == null)
                return "unknown";

            float hp = caster.health.summaryHealth.SummaryHealthPercent;

            if (hp <= 0.45f)
                return "criticalHp";

            if (hp <= 0.58f && HasDangerousBleeding())
                return "bleedingLowHp";

            if (snap == null || snap.enemies == null)
                return "unknown";

            int closeMeleeOrAnimals = 0;
            int veryCloseMeleeOrAnimals = 0;

            for (int i = 0; i < snap.enemies.Count; i++)
            {
                EnemyAssessment e = snap.enemies[i];

                if (e == null || e.pawn == null)
                    continue;

                bool meleeLike = e.IsAnimal || e.IsMelee || e.role == EnemyRole.Wimp;

                if (!meleeLike)
                    continue;

                if (e.distanceToCaster <= 6f)
                    closeMeleeOrAnimals++;

                if (e.distanceToCaster <= 2.5f)
                    veryCloseMeleeOrAnimals++;
            }

            if (hp <= 0.82f && veryCloseMeleeOrAnimals >= 1)
                return "veryCloseMeleePressure";

            if (hp <= 0.75f && closeMeleeOrAnimals >= 1)
                return "closeMeleePressure";

            if (hp <= 0.90f && closeMeleeOrAnimals >= 2)
                return "multipleMeleePressure";

            return "unknown";
        }

        private bool IsRecoveryPlateauLikely()
        {
            if (caster == null || caster.health == null)
                return false;

            float hp = caster.health.summaryHealth.SummaryHealthPercent;

            if (hp < 0.60f)
                return false;

            HediffComp_PsycasterRestoringMechanisms restore = GetRestoringMechanisms();

            int ticksSinceDamage = restore != null ? restore.TicksSinceDamage : -1;

            /*
             * Было 1800 и hp >= 0.55.
             * Это могло выпускать его слишком рано.
             */
            if (restore != null &&
                restore.CanRegenerateNow &&
                ticksSinceDamage >= 3600 &&
                hp >= 0.60f)
            {
                return true;
            }

            /*
             * Долгий recovery fallback.
             * Было 2400 / hp 0.55 — тоже рановато.
             */
            if (recoveryStartedTick > 0)
            {
                int recoveryDuration = Find.TickManager.TicksGame - recoveryStartedTick;

                if (recoveryDuration >= 4200 && hp >= 0.60f)
                    return true;
            }

            return false;
        }

        private bool HasRecoveryThreatPressure(BattlefieldSnapshot snap)
        {
            if (snap == null || snap.enemies == null || snap.enemies.Count == 0)
                return false;

            int closeMeleeOrAnimals = 0;
            int rangedLos = 0;

            for (int i = 0; i < snap.enemies.Count; i++)
            {
                EnemyAssessment e = snap.enemies[i];

                if (e == null || e.pawn == null)
                    continue;

                if (e.pawn.Destroyed || e.pawn.Dead || e.pawn.Downed || !e.pawn.Spawned)
                    continue;

                bool meleeLike = e.IsAnimal || e.IsMelee || e.role == EnemyRole.Wimp;

                if (meleeLike && e.distanceToCaster <= 10f)
                    closeMeleeOrAnimals++;

                if (e.IsRanged && e.hasLineOfSight && e.canShootNow)
                    rangedLos++;
            }

            if (closeMeleeOrAnimals > 0)
                return true;

            if (rangedLos > 0)
                return true;

            if (snap.totalIncomingDps >= 10f)
                return true;

            return false;
        }

        private bool TryFindRecoverySkipCell(BattlefieldSnapshot snap, out IntVec3 result)
        {
            result = IntVec3.Invalid;

            if (snap == null || caster == null || caster.Map == null)
                return false;

            Map map = caster.Map;

            IntVec3 best = IntVec3.Invalid;
            float bestScore = float.MinValue;

            int radius = Mathf.RoundToInt(PsycasterTuning.SkipRangeMax);

            for (int i = 0; i < 220; i++)
            {
                IntVec3 cell;

                bool found = CellFinder.TryFindRandomCellNear(
                    caster.Position,
                    map,
                    radius,
                    c => c.InBounds(map)
                         && c.Standable(map)
                         && c.GetFirstPawn(map) == null
                         && c.DistanceToEdge(map) >= MapEdgeDangerDistance
                         && caster.CanReach(c, PathEndMode.OnCell, Danger.Deadly),
                    out cell);

                if (!found)
                    continue;

                float distFromCaster = cell.DistanceTo(caster.Position);

                /*
                 * Skip ради recovery должен реально отрывать дистанцию.
                 */
                if (distFromCaster < 10f)
                    continue;

                if (IsNearMapEdge(cell, map, MapEdgeDangerDistance))
                    continue;

                float nearestMeleeOrAnimal = 999f;
                float nearestAnyEnemy = 999f;
                float nearestRangedLos = 999f;
                int visibleShooters = 0;

                if (snap.enemies != null)
                {
                    for (int eIndex = 0; eIndex < snap.enemies.Count; eIndex++)
                    {
                        EnemyAssessment e = snap.enemies[eIndex];

                        if (e == null || e.pawn == null)
                            continue;

                        if (e.pawn.Destroyed || e.pawn.Dead || e.pawn.Downed || !e.pawn.Spawned || e.pawn.Map != map)
                            continue;

                        float ed = cell.DistanceTo(e.pawn.Position);

                        if (ed < nearestAnyEnemy)
                            nearestAnyEnemy = ed;

                        if (e.IsAnimal || e.IsMelee || e.role == EnemyRole.Wimp)
                        {
                            if (ed < nearestMeleeOrAnimal)
                                nearestMeleeOrAnimal = ed;
                        }

                        if (e.IsRanged && GenSight.LineOfSight(e.pawn.Position, cell, map))
                        {
                            visibleShooters++;

                            if (ed < nearestRangedLos)
                                nearestRangedLos = ed;
                        }
                    }
                }

                /*
                 * Recovery Skip должен создать пространство для регена.
                 */
                if (nearestMeleeOrAnimal < 18f)
                    continue;

                if (nearestAnyEnemy < 14f)
                    continue;

                float score = 0f;

                if (nearestMeleeOrAnimal < 999f)
                    score += nearestMeleeOrAnimal * 3.4f;

                if (nearestAnyEnemy < 999f)
                    score += nearestAnyEnemy * 1.5f;

                score += distFromCaster * 0.35f;

                /*
                 * Не прыгать под стрелков.
                 */
                score -= visibleShooters * 28f;

                if (nearestRangedLos < 26f)
                    score -= (26f - nearestRangedLos) * 4.5f;

                /*
                 * Главное отличие от старой логики:
                 * Skip не должен уводить VIP к краю карты.
                 */
                score -= GetMapEdgePenalty(cell, map);

                /*
                 * Мягкий leash: в emergency/recovery Skip можно уйти дальше,
                 * но не бесконечно наружу.
                 */
                score -= GetSoftHomePenalty(cell, true);

                /*
                 * Если клетка хотя бы немного возвращает к HomeAnchor — это плюс.
                 */
                if (HomeAnchor.IsValid)
                {
                    float currentHomeDist = caster.Position.DistanceTo(HomeAnchor);
                    float newHomeDist = cell.DistanceTo(HomeAnchor);

                    if (newHomeDist < currentHomeDist)
                        score += 14f;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    best = cell;
                }
            }

            if (!best.IsValid)
                return false;

            result = best;
            return true;
        }

    }
}
