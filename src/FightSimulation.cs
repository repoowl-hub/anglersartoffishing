using System;
using System.Collections.Generic;

namespace AnglersArtOfFishing
{
    public enum FishBehaviour
    {
        Swim,     // calm, light pull - the moment to reel
        Windup,   // telegraph: fish is about to make NextAction in FishDir
        Dash,     // short heavy burst - release / feather
        Recover,  // fish catching its breath after an action
        Tired,    // stamina depleted - fish barely resists, reel fast
        Run,      // long sustained run peeling line off - feather, don't horse it
        Jump,     // leaps clear of the water: keep the line tight in the air, ease off on the splash
        Shake,    // rapid head shakes - jittery tension, keep steady pressure
        Dive      // sounds / bulldogs for the bottom - heavy steady pull, line barely comes in
    }

    /// <summary>How a species fights, modelled on its real-life counterpart.</summary>
    public enum FightStyle
    {
        Darter,     // small panfish & baitfish: quick zig-zag bursts and tugs
        Jumper,     // trout, bass, mahi-mahi, arapaima: acrobatic leaps
        Runner,     // carp, salmon, tuna, mackerel: long line-peeling runs
        Ambusher,   // pike, pickerel, barracuda, piranha: explosive bursts then lulls
        Bulldog,    // catfish, wels, grouper, sturgeon: dogged deep dives
        Headshaker  // walleye, snapper, groundfish: head shakes and short dives
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
        /// <summary>0..1 how hard the fish pulls. Also stands in for the fish's size.</summary>
        public float Power = 0.5f;
        /// <summary>Seconds spent in the sweet zone needed to completely tire the fish.</summary>
        public float StaminaSeconds = 14f;
        /// <summary>0..1 how often the fish acts up.</summary>
        public float Aggression = 0.5f;
        /// <summary>Which move set the fish uses.</summary>
        public FightStyle Style = FightStyle.Headshaker;
        /// <summary>Pole tier bonus that dampens the fish's pull (0 = wood, 1 = bamboo).</summary>
        public int RodMargin = 0;
        /// <summary>Reel speed multiplier of the rod.</summary>
        public float ReelSpeed = 1f;
        /// <summary>Seconds of line-overload the rod tolerates before the line snaps.</summary>
        public float Forgiveness = 0.45f;
        /// <summary>Initial distance between angler and fish (0..1).</summary>
        public float StartDistance = 0.85f;
        /// <summary>Seconds of slack line before the fish throws the hook. Lower for lightly set hooks (stale bait).</summary>
        public float SlackEscapeSeconds = 6.0f;
    }

    /// <summary>
    /// Pure, API-independent simulation of the fight inspired by realistic angling mechanics.
    ///
    /// Core loop:
    ///  - Holding [RMB] reels in line, pulling the fish closer and increasing line tension.
    ///  - Between calm spells the fish makes moves picked from its <see cref="FightStyle"/>: dashes, long runs,
    ///    jumps, head shakes or deep dives. Each one loads the line differently and asks for a different response.
    ///  - Line tension maintained in the sweet zone (0.25 - 0.88) exhausts the fish's stamina.
    ///  - When stamina is depleted the fish is Tired: pull drops to near zero and the player can crank the reel in fast.
    ///  - Bigger fish (higher Power) move slower but for longer, and make one last surge when they first near the bank.
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
        const float WindupSeconds = 1.15f;
        const float TiredSeconds = 7.0f;
        const float LandedDistance = 0.02f;

        // Jump timeline as fractions of the move: burst up, airborne, re-entry slam
        const float JumpLaunchEnd = 0.20f;
        const float JumpAirEnd = 0.68f;
        /// <summary>Slack while the fish is in the air counts this much faster towards a thrown hook.</summary>
        const float AirborneSlackMul = 5f;

        // Last surge when a fish first comes close to the bank
        const float SurgeDistance = 0.22f;
        const float SurgeMinPower = 0.35f;

        class StyleProfile
        {
            public (FishBehaviour move, float weight)[] Moves;
            public FishBehaviour Opening;
            public FishBehaviour Surge;
            public float WindupMul = 1f;
            public float DurationMul = 1f;
            public float CalmMul = 1f;
            public float DashPull = 1f;
        }

        static readonly Dictionary<FightStyle, StyleProfile> Profiles = new Dictionary<FightStyle, StyleProfile>
        {
            [FightStyle.Darter] = new StyleProfile
            {
                Moves = new[] { (FishBehaviour.Dash, 0.55f), (FishBehaviour.Shake, 0.35f), (FishBehaviour.Run, 0.10f) },
                Opening = FishBehaviour.Dash, Surge = FishBehaviour.Dash,
                WindupMul = 0.6f, DurationMul = 0.6f, CalmMul = 0.7f
            },
            [FightStyle.Jumper] = new StyleProfile
            {
                Moves = new[] { (FishBehaviour.Jump, 0.45f), (FishBehaviour.Dash, 0.25f), (FishBehaviour.Shake, 0.20f), (FishBehaviour.Run, 0.10f) },
                Opening = FishBehaviour.Jump, Surge = FishBehaviour.Jump
            },
            [FightStyle.Runner] = new StyleProfile
            {
                Moves = new[] { (FishBehaviour.Run, 0.55f), (FishBehaviour.Dash, 0.20f), (FishBehaviour.Shake, 0.15f), (FishBehaviour.Jump, 0.10f) },
                Opening = FishBehaviour.Run, Surge = FishBehaviour.Run,
                DurationMul = 1.0f, CalmMul = 1.1f
            },
            [FightStyle.Ambusher] = new StyleProfile
            {
                Moves = new[] { (FishBehaviour.Dash, 0.50f), (FishBehaviour.Shake, 0.35f), (FishBehaviour.Jump, 0.15f) },
                Opening = FishBehaviour.Dash, Surge = FishBehaviour.Dash,
                WindupMul = 0.7f, DurationMul = 0.85f, CalmMul = 1.15f, DashPull = 1.15f
            },
            [FightStyle.Bulldog] = new StyleProfile
            {
                Moves = new[] { (FishBehaviour.Dive, 0.55f), (FishBehaviour.Shake, 0.25f), (FishBehaviour.Run, 0.20f) },
                Opening = FishBehaviour.Dive, Surge = FishBehaviour.Dive,
                WindupMul = 1.2f, DurationMul = 1.0f, CalmMul = 0.9f
            },
            [FightStyle.Headshaker] = new StyleProfile
            {
                Moves = new[] { (FishBehaviour.Shake, 0.55f), (FishBehaviour.Dive, 0.25f), (FishBehaviour.Dash, 0.20f) },
                Opening = FishBehaviour.Shake, Surge = FishBehaviour.Dive,
                WindupMul = 0.9f, DurationMul = 0.9f, CalmMul = 0.9f
            }
        };

        readonly FightParams p;
        readonly StyleProfile profile;
        readonly Random rand;

        // ---- Public state (read by HUD & water effects) ----
        public float Tension { get; private set; } = 0.25f;
        public float Distance { get; private set; }
        public float Stamina { get; private set; } = 1f;
        public FishBehaviour Behaviour { get; private set; } = FishBehaviour.Swim;
        /// <summary>The move the fish is winding up for (valid while Behaviour == Windup, and during that move).</summary>
        public FishBehaviour NextAction { get; private set; } = FishBehaviour.Dash;
        public FightStyle Style => p.Style;
        public float Power => p.Power;
        /// <summary>-1 = fish pulls left, +1 = right.</summary>
        public int FishDir { get; private set; } = 1;
        /// <summary>Backwards compatibility.</summary>
        public float RodAngle => 0f;
        /// <summary>Visual lateral position of the fish, -1..1 (used for in-world splash & wake positioning).</summary>
        public float FishLateral { get; private set; }
        /// <summary>0..1 progress through the current behaviour.</summary>
        public float BehaviourProgress => behaviourDuration <= 0 ? 1f : Math.Clamp(behaviourTimer / behaviourDuration, 0f, 1f);
        /// <summary>True while a jumping fish is clear of the water.</summary>
        public bool Airborne => Behaviour == FishBehaviour.Jump && BehaviourProgress >= JumpLaunchEnd && BehaviourProgress < JumpAirEnd;
        /// <summary>0..1 through the airborne arc, or -1 when not airborne.</summary>
        public float AirProgress => Airborne ? (BehaviourProgress - JumpLaunchEnd) / (JumpAirEnd - JumpLaunchEnd) : -1f;
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
        public float SlackRatio => Math.Clamp(SlackTimer / p.SlackEscapeSeconds, 0f, 1f);
        public float Progress => 1f - Math.Clamp(Distance, 0f, 1f);
        public bool Finished => Outcome != FightOutcome.None;

        // Events for effects / sound hooks
        public event Action<FishBehaviour> BehaviourChanged;

        float behaviourTimer;
        float behaviourDuration;
        bool openingPending = true;
        bool surgeUsed;
        bool surgeActive;

        /// <summary>Bigger fish do everything slower but for longer.</summary>
        float SizeMul => 0.75f + 0.6f * Math.Clamp(p.Power, 0f, 1f);

        public FightSimulation(FightParams parameters, int? seed = null)
        {
            p = parameters;
            profile = Profiles.TryGetValue(p.Style, out var prof) ? prof : Profiles[FightStyle.Headshaker];
            rand = seed.HasValue ? new Random(seed.Value) : new Random();
            Distance = Math.Clamp(p.StartDistance, 0.3f, 1f);
            FishDir = rand.Next(2) == 0 ? -1 : 1;
            // Short calm spell after the hook set, then the style's signature opening move
            SetBehaviour(FishBehaviour.Swim, 0.6f + 0.5f * (float)rand.NextDouble());
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

            // Fish drifts sideways while it acts up, drifts back to centre otherwise (drives in-world splashes)
            float latTarget = Behaviour switch
            {
                FishBehaviour.Dash => FishDir * 0.9f,
                FishBehaviour.Run => FishDir * 1.0f,
                FishBehaviour.Dive => FishDir * 0.3f,
                FishBehaviour.Jump => FishDir * 0.4f,
                FishBehaviour.Windup => FishDir * 0.25f,
                _ => 0f
            };
            float latRate = Behaviour == FishBehaviour.Run ? 2.0f : 4.5f;
            FishLateral += (latTarget - FishLateral) * (1f - MathF.Exp(-latRate * dt));

            if (Distance <= LandedDistance) Outcome = FightOutcome.Landed;
        }

        // ---------------------------------------------------------------- behaviour

        static bool IsMove(FishBehaviour b) =>
            b == FishBehaviour.Dash || b == FishBehaviour.Run || b == FishBehaviour.Jump || b == FishBehaviour.Shake || b == FishBehaviour.Dive;

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

            // A big fish that first sees the bank finds one last burst of energy
            if (!surgeUsed && Distance < SurgeDistance && p.Power >= SurgeMinPower && !IsMove(Behaviour) && Behaviour != FishBehaviour.Windup)
            {
                surgeUsed = true;
                surgeActive = true;
                Stamina = Math.Max(Stamina, 0.15f);
                StartWindup(profile.Surge);
                return;
            }

            // A spent fish gives up a long run or dive part way through
            if (Stamina <= 0f && (Behaviour == FishBehaviour.Run || Behaviour == FishBehaviour.Dive))
            {
                SetBehaviour(FishBehaviour.Recover, 0.5f);
                return;
            }

            // Stamina exhaustion interrupts calm phases (never mid-move)
            if (Stamina <= 0f && Behaviour != FishBehaviour.Tired && !IsMove(Behaviour) && Behaviour != FishBehaviour.Windup)
            {
                SetBehaviour(FishBehaviour.Tired, TiredSeconds);
                return;
            }

            if (behaviourTimer < behaviourDuration) return;

            switch (Behaviour)
            {
                case FishBehaviour.Swim:
                    var move = openingPending ? profile.Opening : PickMove();
                    openingPending = false;
                    // Head shakes need no telegraph; everything else winds up first
                    if (move == FishBehaviour.Shake) { NextAction = move; SetBehaviour(move, MoveDuration(move)); }
                    else StartWindup(move);
                    break;
                case FishBehaviour.Windup:
                    // The last surge near the bank is a short burst, not a full-length move
                    SetBehaviour(NextAction, MoveDuration(NextAction) * (surgeActive ? 0.6f : 1f));
                    surgeActive = false;
                    break;
                case FishBehaviour.Dash:
                case FishBehaviour.Run:
                case FishBehaviour.Jump:
                case FishBehaviour.Shake:
                case FishBehaviour.Dive:
                    // Leaping clear of the water costs the fish a lot of energy
                    if (Behaviour == FishBehaviour.Jump) Stamina = Math.Max(0f, Stamina - 0.04f);
                    SetBehaviour(FishBehaviour.Recover, RecoverDuration(Behaviour));
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

        void StartWindup(FishBehaviour move)
        {
            NextAction = move;
            FishDir = rand.Next(2) == 0 ? -1 : 1;
            SetBehaviour(FishBehaviour.Windup, WindupSeconds * profile.WindupMul * (0.85f + 0.3f * p.Power));
        }

        FishBehaviour PickMove()
        {
            float total = 0f;
            foreach (var (_, w) in profile.Moves) total += w;
            float r = (float)rand.NextDouble() * total;
            foreach (var (m, w) in profile.Moves)
            {
                r -= w;
                if (r <= 0f) return m;
            }
            return profile.Moves[profile.Moves.Length - 1].move;
        }

        float MoveDuration(FishBehaviour move)
        {
            float r = (float)rand.NextDouble();
            float d = move switch
            {
                FishBehaviour.Dash => 0.9f + r * (0.5f + 0.4f * p.Power),
                FishBehaviour.Run => (1.8f + r * 1.6f) * SizeMul,
                FishBehaviour.Jump => 1.25f * (0.9f + 0.3f * p.Power),
                FishBehaviour.Shake => 1.0f + r * 1.0f,
                FishBehaviour.Dive => (1.8f + r * 1.6f) * SizeMul,
                _ => 1f
            };
            // Jumps have a fixed arc; other moves stretch with the style
            return move == FishBehaviour.Jump ? d : d * profile.DurationMul;
        }

        static float RecoverDuration(FishBehaviour move) => move switch
        {
            FishBehaviour.Run => 1.2f,
            FishBehaviour.Jump => 0.8f,
            FishBehaviour.Shake => 0.5f,
            _ => 1.0f
        };

        float CalmDuration()
        {
            float aggr = Math.Clamp(p.Aggression, 0.05f, 1f);
            float baseT = 4.2f - 1.8f * aggr;                 // 2.4s .. 4.2s
            return baseT * profile.CalmMul * (0.85f + 0.3f * p.Power) * (0.8f + 0.4f * (float)rand.NextDouble());
        }

        // ---------------------------------------------------------------- physics

        void UpdatePull(float dt)
        {
            float t = BehaviourProgress;
            float target = Behaviour switch
            {
                FishBehaviour.Swim => 0.26f,
                FishBehaviour.Windup => NextAction switch
                {
                    FishBehaviour.Jump => 0.18f + 0.12f * t,   // fish rising: line eases slightly
                    FishBehaviour.Dive => 0.30f + 0.30f * t,   // fish turning its head down
                    _ => 0.18f + 0.32f * t                     // building pre-dash tension
                },
                FishBehaviour.Dash => profile.DashPull,
                FishBehaviour.Run => 0.74f,
                FishBehaviour.Jump => t < JumpLaunchEnd ? 0.75f : t < JumpAirEnd ? 0f : 0.95f,
                FishBehaviour.Shake => Math.Max(0f, 0.40f + 0.30f * MathF.Sin(behaviourTimer * MathF.PI * 2f * 4.5f)),
                FishBehaviour.Dive => 0.82f,
                FishBehaviour.Recover => 0.28f,
                FishBehaviour.Tired => 0.04f,
                _ => 0.25f
            };

            // Fast attack on surges so tension spikes rapidly; smooth release. Jumps and head shakes snap both ways.
            float k = Behaviour == FishBehaviour.Jump || Behaviour == FishBehaviour.Shake ? 20f
                    : target > PullNow ? 16f : 5f;
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
            // When not reeling: reel drag lets line slip, dropping tension safely.
            float reelLoad = Reeling ? (ReelTension * p.ReelSpeed * tired) : 0f;
            float target = BaseTension + pull * (Reeling ? 0.95f : 0.40f) + reelLoad;

            // Very responsive tension rise on reeling/surges (k = 18f), fast drop on reel release (k = 14f)
            float k = target > Tension ? 18f : 14f;
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
                // Steady pressure on a diving or running fish wears it down faster
                if (Behaviour == FishBehaviour.Dive) drain *= 1.3f;
                else if (Behaviour == FishBehaviour.Run) drain *= 1.15f;
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
                    FishBehaviour.Jump => Airborne ? 1.2f : 0.4f, // taking up slack while the fish is in the air
                    FishBehaviour.Shake => 0.5f,
                    FishBehaviour.Run => 0.45f,
                    FishBehaviour.Dive => 0.55f,                 // you can't winch a sounding fish off the bottom
                    _ => 0.4f
                };

                // Winching in line
                float resist = Math.Clamp(PullLoad() * 1.15f, 0f, 0.92f);
                if (Behaviour == FishBehaviour.Tired || Airborne) resist = 0f;
                change -= reelRate * speedFactor * (1f - resist);
            }

            float runSpeed = 0.035f + 0.045f * p.Power;
            switch (Behaviour)
            {
                // Dash: fish bolts and pulls line out (more if player isn't holding reel)
                case FishBehaviour.Dash:
                    change += runSpeed * (Reeling ? 0.25f : 1.0f) * PullNow;
                    break;
                // Run: sustained, slightly slower line loss that reeling only halves
                case FishBehaviour.Run:
                    change += runSpeed * 0.8f * (Reeling ? 0.5f : 1.0f) * PullNow;
                    break;
                // Dive: fish sounds and takes a little line
                case FishBehaviour.Dive:
                    change += 0.006f * (Reeling ? 0.5f : 1.0f);
                    break;
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

            // A slack line while the fish is airborne is the classic way to lose a jumper
            if (Tension < SlackThreshold && Behaviour != FishBehaviour.Tired) SlackTimer += dt * (Airborne ? AirborneSlackMul : 1f);
            else SlackTimer = Math.Max(0f, SlackTimer - dt * 2.0f);

            if (SlackTimer >= p.SlackEscapeSeconds || Distance >= 1.1f)
            {
                Outcome = FightOutcome.FishEscaped;
            }
        }
    }
}
