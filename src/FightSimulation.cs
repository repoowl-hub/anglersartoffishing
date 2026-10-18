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
    /// Pure, API-independent simulation of the fight inspired by realistic angling mechanics.
    ///
    /// Core loop:
    ///  - Holding [RMB] reels in line, pulling the fish closer and increasing line tension.
    ///  - When the fish dashes or thrashes, pull force surges. Reeling during a dash will spike tension
    ///    into the red snap danger zone. The player must feather or release [RMB] until the surge passes.
    ///  - Line tension maintained in the sweet zone (0.25 - 0.88) exhausts the fish's stamina.
    ///  - When stamina is depleted the fish is Tired: pull drops to near zero and the player can crank the reel in fast.
    /// </summary>
    public class FightSimulation
    {
        // ---- Tunables (feel) ----
        public const float SweetMin = 0.25f;
        public const float SweetMax = 0.85f;
        public const float DangerStart = 0.88f;

        const float BaseTension = 0.10f;
        const float ReelTension = 0.28f;
        const float BaseReelSpeed = 0.12f;        // distance units / second while reeling calmly
        const float SlackThreshold = 0.14f;
        const float SlackEscapeSeconds = 6.0f;
        const float WindupSeconds = 1.15f;
        const float TiredSeconds = 7.0f;
        const float LandedDistance = 0.02f;

        readonly FightParams p;
        readonly Random rand;

        // ---- Public state (read by HUD & water effects) ----
        public float Tension { get; private set; } = 0.25f;
        public float Distance { get; private set; }
        public float Stamina { get; private set; } = 1f;
        public FishBehaviour Behaviour { get; private set; } = FishBehaviour.Swim;
        /// <summary>-1 = fish pulls left, +1 = right.</summary>
        public int FishDir { get; private set; } = 1;
        /// <summary>Backwards compatibility.</summary>
        public float RodAngle => 0f;
        /// <summary>Visual lateral position of the fish, -1..1 (used for in-world splash & wake positioning).</summary>
        public float FishLateral { get; private set; }
        public float DangerTimer { get; private set; }
        public float SlackTimer { get; private set; }
        public float Elapsed { get; private set; }
        public float PullNow { get; private set; }
        public bool Reeling { get; private set; }
        public bool CounterSteering => false;
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

        public void Update(float dt, bool reelHeld, float steerInput = 0f)
        {
            Update(dt, reelHeld);
        }

        public void Update(float dt, bool reelHeld)
        {
            if (Finished) return;
            dt = Math.Min(dt, 0.05f); // protect against frame hitches
            Elapsed += dt;

            Reeling = reelHeld;

            UpdateBehaviour(dt);
            UpdatePull(dt);
            UpdateTension(dt);
            UpdateStamina(dt);
            UpdateDistance(dt);
            UpdateFailureStates(dt);

            // Fish drifts sideways in the dash direction, drifts back to centre otherwise (drives in-world splashes)
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
                    FishDir = rand.Next(2) == 0 ? -1 : 1;
                    SetBehaviour(FishBehaviour.Windup, WindupSeconds);
                    break;
                case FishBehaviour.Windup:
                    SetBehaviour(FishBehaviour.Dash, 0.9f + (float)rand.NextDouble() * (0.5f + 0.4f * p.Power));
                    break;
                case FishBehaviour.Dash:
                    SetBehaviour(FishBehaviour.Recover, 1.0f);
                    break;
                case FishBehaviour.Recover:
                    SetBehaviour(FishBehaviour.Swim, CalmDuration());
                    break;
                case FishBehaviour.Tired:
                    // Second wind: the fish gets a small amount of stamina back if not landed in time
                    SecondWinds++;
                    Stamina = Math.Max(0.25f, 0.38f - 0.08f * SecondWinds);
                    SetBehaviour(FishBehaviour.Swim, CalmDuration());
                    break;
            }
        }

        float CalmDuration()
        {
            float aggr = Math.Clamp(p.Aggression, 0.05f, 1f);
            float baseT = 4.2f - 1.8f * aggr;                 // 2.4s .. 4.2s
            return baseT * (0.8f + 0.4f * (float)rand.NextDouble());
        }

        // ---------------------------------------------------------------- physics

        void UpdatePull(float dt)
        {
            float target = Behaviour switch
            {
                FishBehaviour.Swim => 0.28f,
                FishBehaviour.Windup => 0.18f + 0.25f * (behaviourTimer / WindupSeconds), // building pre-dash tension
                FishBehaviour.Dash => 1.0f,
                FishBehaviour.Recover => 0.32f,
                FishBehaviour.Tired => 0.05f,
                _ => 0.25f
            };

            // Fast attack, smooth release = sudden jerk on dash, then eases off
            float k = target > PullNow ? 12f : 4f;
            PullNow += (target - PullNow) * (1f - MathF.Exp(-k * dt));
        }

        float PullLoad()
        {
            // Rod margin reduces stress: sturdier rods dampen the fish's pull force
            float rodReduction = 1f / (0.80f + 0.20f * (1 + Math.Max(0, p.RodMargin)));
            float fishLoad = PullNow * (0.35f + 0.65f * Math.Clamp(p.Power, 0f, 1f));
            return fishLoad * rodReduction;
        }

        void UpdateTension(float dt)
        {
            float tired = Behaviour == FishBehaviour.Tired ? 0.35f : 1f;
            float pull = PullLoad();

            // When reeling: line winches tight against pull + reel gear tension.
            // When not reeling: reel drag lets line slip, reducing tension.
            float reelLoad = Reeling ? (ReelTension * p.ReelSpeed * tired) : 0f;
            float target = BaseTension + pull * (Reeling ? 0.90f : 0.42f) + reelLoad;

            float k = target > Tension ? 9f : 14f;
            Tension += (target - Tension) * (1f - MathF.Exp(-k * dt));
            Tension = Math.Clamp(Tension, 0f, 1.05f);
        }

        void UpdateStamina(float dt)
        {
            if (Behaviour == FishBehaviour.Tired) return;

            // Fish stamina drains whenever tension is maintained in the active fight zone (0.25 - 0.88)
            float drain = 0f;
            if (Tension >= SweetMin && Tension <= DangerStart)
            {
                drain = 1.0f + 0.5f * ((Tension - SweetMin) / (DangerStart - SweetMin));
            }
            else if (Tension > SlackThreshold && Tension < SweetMin)
            {
                drain = 0.35f;
            }

            float perSecond = 1f / Math.Max(2f, p.StaminaSeconds);
            Stamina -= drain * perSecond * dt;

            // Slack line lets the fish catch its breath
            if (Tension < SlackThreshold) Stamina += 0.03f * dt;

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
                    FishBehaviour.Tired => 2.0f,
                    FishBehaviour.Recover => 0.9f,
                    FishBehaviour.Swim => 1.0f,
                    _ => 0.4f
                };

                // Winching in line
                float resist = Math.Clamp(PullLoad() * 1.15f, 0f, 0.92f);
                if (Behaviour == FishBehaviour.Tired) resist = 0f;
                change -= reelRate * speedFactor * (1f - resist);
            }

            // During dash: fish runs and pulls line out (more if player isn't holding reel)
            if (Behaviour == FishBehaviour.Dash)
            {
                float runSpeed = 0.035f + 0.045f * p.Power;
                change += runSpeed * (Reeling ? 0.25f : 1.0f) * PullNow;
            }

            // Slack line allows fish to swim away slowly
            if (Tension < SlackThreshold && Behaviour != FishBehaviour.Tired)
            {
                change += 0.025f;
            }

            Distance = Math.Clamp(Distance + change * dt, 0f, 1.15f);
        }

        void UpdateFailureStates(float dt)
        {
            // Line overload with forgiveness: builds fast, recovers faster
            if (Tension > DangerStart)
            {
                float over = (Tension - DangerStart) / (1.0f - DangerStart);
                DangerTimer += dt * Math.Clamp(0.5f + over * 0.8f, 0.5f, 1.3f);
            }
            else
            {
                DangerTimer = Math.Max(0f, DangerTimer - dt * 2.5f);
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
