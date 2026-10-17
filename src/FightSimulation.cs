using System;

namespace AnglersArtOfFishing
{
    public enum FishBehaviour
    {
        Swim,     // calm, light pull - the moment to reel
        Windup,   // telegraph: fish is about to bolt in FishDir
        Dash,     // heavy pull in FishDir - release / counter-steer
        Recover,  // fish catching its breath after a dash
        Tired     // stamina depleted - fish barely resists, reel fast
    }

    public enum FightOutcome
    {
        None,
        Landed,
        LineSnapped,
        FishEscaped
    }

    /// <summary>
    /// Everything the fight needs to know about the fish and the rod. Filled in by the server.
    /// </summary>
    public class FightParams
    {
        /// <summary>0..1 how hard the fish pulls.</summary>
        public float Power = 0.5f;
        /// <summary>Seconds spent in the sweet zone needed to completely tire the fish.</summary>
        public float StaminaSeconds = 14f;
        /// <summary>0..1 how often the fish bolts.</summary>
        public float Aggression = 0.5f;
        /// <summary>How many rod-strength tiers above the species requirement (0 = exactly enough).</summary>
        public int RodMargin = 0;
        /// <summary>Reel speed multiplier of the rod.</summary>
        public float ReelSpeed = 1f;
        /// <summary>Seconds of line-overload the rod tolerates before the line snaps.</summary>
        public float Forgiveness = 0.45f;
        /// <summary>Initial distance between angler and fish (0..1).</summary>
        public float StartDistance = 0.85f;
    }

    /// <summary>
    /// Pure, API-independent simulation of the fight. Driven once per frame by the client.
    ///
    /// Core loop (modelled on Monster Hunter Wilds' fishing):
    ///  - Holding "reel" adds tension and brings the fish closer, but the fish pulls back.
    ///  - Tension in the SWEET zone drains the fish's stamina. Too high = line snaps, too low = fish slips off.
    ///  - The fish telegraphs a bolt (Windup), then Dashes left or right. You survive by letting the line run
    ///    (release reel) and/or steering the rod AGAINST the fish.
    ///  - When stamina hits zero the fish is Tired: reel it in fast, but it will eventually get a second wind.
    /// </summary>
    public class FightSimulation
    {
        // ---- Tunables (feel) ----
        public const float SweetMin = 0.40f;
        public const float SweetMax = 0.72f;
        public const float DangerStart = 0.88f;

        const float BaseTension = 0.10f;
        const float ReelTension = 0.30f;
        const float BaseReelSpeed = 0.085f;       // distance units / second while reeling calmly
        const float SlackThreshold = 0.16f;
        const float SlackEscapeSeconds = 4.5f;
        const float WindupSeconds = 0.75f;
        const float TiredSeconds = 6.5f;
        const float LandedDistance = 0.02f;

        readonly FightParams p;
        readonly Random rand;

        // ---- Public state (read by HUD) ----
        public float Tension { get; private set; } = 0.25f;
        public float Distance { get; private set; }
        public float Stamina { get; private set; } = 1f;
        public FishBehaviour Behaviour { get; private set; } = FishBehaviour.Swim;
        /// <summary>-1 = fish pulls left, +1 = right.</summary>
        public int FishDir { get; private set; } = 1;
        /// <summary>-1..1 smoothed rod angle.</summary>
        public float RodAngle { get; private set; }
        /// <summary>Visual lateral position of the fish, -1..1.</summary>
        public float FishLateral { get; private set; }
        public float DangerTimer { get; private set; }
        public float SlackTimer { get; private set; }
        public float Elapsed { get; private set; }
        public float PullNow { get; private set; }
        public bool Reeling { get; private set; }
        public bool CounterSteering { get; private set; }
        public int SecondWinds { get; private set; }
        public FightOutcome Outcome { get; private set; } = FightOutcome.None;

        /// <summary>Normalised 0..1 of how close the line is to snapping.</summary>
        public float DangerRatio => p.Forgiveness <= 0 ? 0 : Math.Clamp(DangerTimer / p.Forgiveness, 0f, 1f);
        public float SlackRatio => Math.Clamp(SlackTimer / SlackEscapeSeconds, 0f, 1f);
        public float Progress => 1f - Math.Clamp(Distance, 0f, 1f);
        public bool Finished => Outcome != FightOutcome.None;

        // Events for effects / sound hooks
        public event Action<FishBehaviour> BehaviourChanged;

        float behaviourTimer;
        float behaviourDuration;

        public FightSimulation(FightParams parameters, int? seed = null)
        {
            p = parameters;
            rand = seed.HasValue ? new Random(seed.Value) : new Random();
            Distance = Math.Clamp(p.StartDistance, 0.3f, 1f);
            FishDir = rand.Next(2) == 0 ? -1 : 1;
            SetBehaviour(FishBehaviour.Swim, 1.5f + (float)rand.NextDouble());
        }

        public void Update(float dt, bool reelHeld, float steerInput)
        {
            if (Finished) return;
            dt = Math.Min(dt, 0.05f); // protect against frame hitches
            Elapsed += dt;

            Reeling = reelHeld;

            // --- rod angle follows the steer input smoothly ---
            RodAngle += (Math.Clamp(steerInput, -1f, 1f) - RodAngle) * (1f - MathF.Exp(-12f * dt));

            UpdateBehaviour(dt);
            UpdatePull(dt);
            UpdateTension(dt);
            UpdateStamina(dt);
            UpdateDistance(dt);
            UpdateFailureStates(dt);

            // Fish drifts sideways in the dash direction, drifts back to centre otherwise
            float latTarget = (Behaviour == FishBehaviour.Dash) ? FishDir * 0.9f : (Behaviour == FishBehaviour.Windup ? FishDir * 0.25f : 0f);
            FishLateral += (latTarget - FishLateral) * (1f - MathF.Exp(-4.5f * dt));

            if (Distance <= LandedDistance) Outcome = FightOutcome.Landed;
        }

        // ---------------------------------------------------------------- behaviour

        void SetBehaviour(FishBehaviour b, float duration)
        {
            bool changed = b != Behaviour;
            Behaviour = b;
            behaviourTimer = 0f;
            behaviourDuration = duration;
            if (changed) BehaviourChanged?.Invoke(b);
        }

        void UpdateBehaviour(float dt)
        {
            behaviourTimer += dt;

            // Stamina exhaustion interrupts calm phases (never a mid-dash)
            if (Stamina <= 0f && Behaviour != FishBehaviour.Tired && Behaviour != FishBehaviour.Dash && Behaviour != FishBehaviour.Windup)
            {
                SetBehaviour(FishBehaviour.Tired, TiredSeconds);
                return;
            }

            if (behaviourTimer < behaviourDuration) return;

            switch (Behaviour)
            {
                case FishBehaviour.Swim:
                    // higher aggression = shorter calm periods. Fish never bolts straight away.
                    FishDir = rand.Next(2) == 0 ? -1 : 1;
                    SetBehaviour(FishBehaviour.Windup, WindupSeconds);
                    break;
                case FishBehaviour.Windup:
                    SetBehaviour(FishBehaviour.Dash, 1.3f + (float)rand.NextDouble() * (0.7f + p.Power));
                    break;
                case FishBehaviour.Dash:
                    SetBehaviour(FishBehaviour.Recover, 0.9f);
                    break;
                case FishBehaviour.Recover:
                    SetBehaviour(FishBehaviour.Swim, CalmDuration());
                    break;
                case FishBehaviour.Tired:
                    // Second wind: the fish gets some stamina back but you're closer than before
                    SecondWinds++;
                    Stamina = Math.Max(0.30f, 0.45f - 0.08f * SecondWinds);
                    SetBehaviour(FishBehaviour.Swim, CalmDuration());
                    break;
            }
        }

        float CalmDuration()
        {
            float aggr = Math.Clamp(p.Aggression, 0.05f, 1f);
            float baseT = 3.6f - 2.2f * aggr;                 // 1.4s .. 3.4s
            return baseT * (0.8f + 0.4f * (float)rand.NextDouble());
        }

        // ---------------------------------------------------------------- physics

        void UpdatePull(float dt)
        {
            float target = Behaviour switch
            {
                FishBehaviour.Swim => 0.25f,
                FishBehaviour.Windup => 0.15f + 0.30f * (behaviourTimer / WindupSeconds), // little tugs that build
                FishBehaviour.Dash => 1.0f,
                FishBehaviour.Recover => 0.35f,
                FishBehaviour.Tired => 0.06f,
                _ => 0.25f
            };

            // Fast attack, lazy release = the dash feels like a *snap*, then eases off
            float k = target > PullNow ? 11f : 4f;
            PullNow += (target - PullNow) * (1f - MathF.Exp(-k * dt));
        }

        float CounterFactor()
        {
            // Rod angle opposite to fish direction reduces the load
            float counter = RodAngle * -FishDir;
            CounterSteering = counter > 0.35f && (Behaviour == FishBehaviour.Dash || Behaviour == FishBehaviour.Windup);
            if (Behaviour != FishBehaviour.Dash && Behaviour != FishBehaviour.Windup) return 1f;
            return counter > 0 ? 1f - 0.5f * Math.Clamp(counter, 0f, 1f)
                               : 1f + 0.2f * Math.Clamp(-counter, 0f, 1f);
        }

        float PullLoad()
        {
            float rodReduction = 1f / (0.75f + 0.25f * (1 + Math.Max(0, p.RodMargin)));
            float fishLoad = PullNow * (0.35f + 0.65f * Math.Clamp(p.Power, 0f, 1f));
            return fishLoad * CounterFactor() * rodReduction;
        }

        void UpdateTension(float dt)
        {
            float tired = Behaviour == FishBehaviour.Tired ? 0.6f : 1f;
            float pull = PullLoad();

            // Releasing the reel lets line slip: only part of the pull reaches the rod.
            float target = BaseTension + pull * (Reeling ? 1f : 0.5f) + (Reeling ? ReelTension * tired : 0f);

            float k = target > Tension ? 9f : 7f;
            Tension += (target - Tension) * (1f - MathF.Exp(-k * dt));
            Tension = Math.Clamp(Tension, 0f, 1.05f);
        }

        void UpdateStamina(float dt)
        {
            if (Behaviour == FishBehaviour.Tired) return;

            float drain = 0f;
            if (Tension >= SweetMin && Tension <= SweetMax + 0.1f) drain = 1f;
            else if (Tension > 0.25f && Tension < SweetMin) drain = 0.3f;

            // Skilful counter-steering during a dash wears the fish out faster
            if (CounterSteering) drain += 0.5f;

            float perSecond = 1f / Math.Max(2f, p.StaminaSeconds);
            Stamina -= drain * perSecond * dt;

            // Slack line lets the fish catch its breath
            if (Tension < SlackThreshold) Stamina += 0.04f * dt;

            Stamina = Math.Clamp(Stamina, 0f, 1f);
        }

        void UpdateDistance(float dt)
        {
            float reelRate = BaseReelSpeed * p.ReelSpeed;
            float change = 0f;

            if (Reeling)
            {
                float speedFactor = Behaviour switch
                {
                    FishBehaviour.Tired => 1.9f,
                    FishBehaviour.Recover => 0.8f,
                    FishBehaviour.Swim => 1.0f,
                    _ => 0.5f
                };

                // Reeling is only effective when the line isn't being yanked out of your hands
                float resist = Math.Clamp(PullNow * (0.35f + 0.65f * p.Power) * CounterFactor() * 1.4f, 0f, 0.95f);
                if (Behaviour == FishBehaviour.Tired) resist = 0f;
                change -= reelRate * speedFactor * (1f - resist);
            }

            // The fish pulls line out while dashing - harder if you let it run (not reeling) than the reel could hold.
            if (Behaviour == FishBehaviour.Dash)
            {
                float drag = 0.050f + 0.065f * p.Power;
                change += drag * (Reeling ? 0.35f : 1f) * PullNow;
            }

            // Slack: fish gains ground
            if (Tension < SlackThreshold && Behaviour != FishBehaviour.Tired) change += 0.03f;

            Distance = Math.Clamp(Distance + change * dt, 0f, 1.15f);
        }

        void UpdateFailureStates(float dt)
        {
            // Line overload with forgiveness: builds fast, recovers faster
            if (Tension > DangerStart)
            {
                float over = (Tension - DangerStart) / (1.0f - DangerStart);
                DangerTimer += dt * Math.Clamp(0.6f + over, 0.6f, 1.6f);
            }
            else
            {
                DangerTimer = Math.Max(0f, DangerTimer - dt * 1.8f);
            }

            if (DangerTimer >= p.Forgiveness)
            {
                Outcome = FightOutcome.LineSnapped;
                return;
            }

            if (Tension < SlackThreshold && Behaviour != FishBehaviour.Tired) SlackTimer += dt;
            else SlackTimer = Math.Max(0f, SlackTimer - dt * 2.0f);

            if (SlackTimer >= SlackEscapeSeconds || Distance >= 1.1f)
            {
                Outcome = FightOutcome.FishEscaped;
            }
        }
    }
}
