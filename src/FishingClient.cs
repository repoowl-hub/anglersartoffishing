using System;
using System.Collections.Generic;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace AnglersArtOfFishing
{
    /// <summary>
    /// Client side: runs the fight simulation every rendered frame (for smooth, responsive feel)
    /// and draws the HUD. The server only learns about the outcome.
    /// </summary>
    public class FishingClient : IRenderer, IDisposable
    {
        readonly ICoreClientAPI capi;
        readonly IClientNetworkChannel channel;

        FishingPhase phase = FishingPhase.Idle;
        FightSimulation sim;
        FightOutcome reported = FightOutcome.None;
        long bobberEntityId;

        public bool InFight => phase == FishingPhase.Fight;

        // Toast
        string toast;
        float toastLeft;
        bool toastGood;

        // Smoothed display values
        float dispTension, dispDistance = 1f;
        float time;
        float flash; // 0..1 white flash on state changes

        readonly BobberMotionRenderer motionRenderer;
        readonly FishModelRenderer fishModel;
        float fishDepth = 0.25f;

        // In-world water animation
        Vec3d baseBobber;
        Vec3d castOrigin;
        Vec3d currentFishPos;
        double lastRestingBobberY;
        float swimPhase;
        float splashTimer;
        float rippleTimer;
        float shadowTimer;
        float soundTimer;
        float biteSplashTimer;
        bool wasAirborne;

        // Rendering resources
        LoadedTexture white;
        readonly TextTextureUtil textUtil;
        readonly Dictionary<string, LoadedTexture> textCache = new Dictionary<string, LoadedTexture>();

        public double RenderOrder => 0.95;
        public int RenderRange => 10;

        public FishingClient(ICoreClientAPI api)
        {
            capi = api;
            channel = api.Network.RegisterChannel(AnglersArtModSystem.ChannelName)
                .RegisterMessageType<FishingStatePacket>()
                .RegisterMessageType<FishingActionPacket>()
                .SetMessageHandler<FishingStatePacket>(OnState);

            textUtil = new TextTextureUtil(api);
            white = new LoadedTexture(api);
            using (var surface = new ImageSurface(Format.Argb32, 2, 2))
            using (var ctx = new Context(surface))
            {
                ctx.SetSourceRGBA(1, 1, 1, 1);
                ctx.Paint();
                api.Gui.LoadOrUpdateCairoTexture(surface, false, ref white);
            }

            motionRenderer = new BobberMotionRenderer(this);
            api.Event.RegisterRenderer(motionRenderer, EnumRenderStage.Before, "anglersartoffishing-motion");
            api.Event.RegisterRenderer(this, EnumRenderStage.Ortho, "anglersartoffishing-hud");
            fishModel = new FishModelRenderer(api);
            api.Event.RegisterRenderer(fishModel, EnumRenderStage.Opaque, "anglersartoffishing-fish");
        }

        // ------------------------------------------------------------------ network / input

        void OnState(FishingStatePacket p)
        {
            var newPhase = (FishingPhase)p.Phase;

            if (p.BobberX != 0 || p.BobberY != 0 || p.BobberZ != 0)
            {
                baseBobber = new Vec3d(p.BobberX, p.BobberY, p.BobberZ);
            }

            if (!string.IsNullOrEmpty(p.Message))
            {
                ShowToast(Lang.Get(p.Message), p.Message.EndsWith("caught"));
            }

            switch (newPhase)
            {
                case FishingPhase.Idle:
                    sim = null;
                    bobberEntityId = 0;
                    ResetFishVisuals();
                    // The server ran StopFishing, but the player's own animations are client side and the client never
                    // ran it (clicks are blocked during the fight), so drop the pole-holding stance here
                    capi.World.Player?.Entity?.AnimManager?.StopAnimation("fishingpole-idle");
                    VanillaPolePatches.HiddenFishEntityId = 0;
                    break;
                case FishingPhase.Fight:
                    bobberEntityId = p.BobberEntityId;
                    fishModel.Load(p.FishEntityCode);
                    VanillaPolePatches.HiddenFishEntityId = p.HiddenFishEntityId;
                    fishDepth = 0.6f;
                    sim = new FightSimulation(new FightParams
                    {
                        Power = p.Power,
                        StaminaSeconds = p.StaminaSeconds,
                        Aggression = p.Aggression,
                        Style = (FightStyle)p.Style,
                        RodMargin = p.RodMargin,
                        ReelSpeed = p.ReelSpeed,
                        Forgiveness = p.Forgiveness,
                        StartDistance = p.StartDistance,
                        SlackEscapeSeconds = p.SlackEscapeSeconds
                    });
                    sim.BehaviourChanged += _ => flash = 0.6f;
                    reported = FightOutcome.None;
                    dispTension = sim.Tension;
                    dispDistance = sim.Distance;
                    flash = 1f;
                    InitFightVisuals();
                    break;
            }
            phase = newPhase;
        }

        void ShowToast(string text, bool good)
        {
            toast = text;
            toastGood = good;
            toastLeft = 4f;
        }

        // ------------------------------------------------------------------ frame

        public void OnBeforeRenderFrame(float dt)
        {
            time += dt;

            if (phase == FishingPhase.Fight && sim != null)
            {
                bool reel = capi.Input.InWorldMouseButton.Right;
                sim.Update(dt, reel);
                UpdateFightWaterEffects(dt);

                if (sim.Finished && reported == FightOutcome.None)
                {
                    reported = sim.Outcome;
                    channel.SendPacket(new FishingActionPacket
                    {
                        Outcome = sim.Outcome == FightOutcome.Landed ? 2 : sim.Outcome == FightOutcome.LineSnapped ? 1 : 0,
                        FightSeconds = sim.Elapsed
                    });
                    SpawnOutcomeEffects(sim.Outcome);
                    phase = FishingPhase.Idle; // server answer with the final message follows
                }
            }
            else
            {
                UpdateBiteBobberMotion(dt);
            }
        }

        public void OnRenderFrame(float dt, EnumRenderStage stage)
        {
            if (flash > 0) flash = Math.Max(0, flash - dt * 3f);
            if (toastLeft > 0) toastLeft -= dt;

            DrawHud(dt);
        }

        // ------------------------------------------------------------------ HUD

        void DrawHud(float dt)
        {
            float s = (float)RuntimeEnv.GUIScale;
            float cx = capi.Render.FrameWidth / 2f;
            float baseY = capi.Render.FrameHeight * 0.70f;

            if (flash > 0) Rect(0, 0, capi.Render.FrameWidth, capi.Render.FrameHeight, 1, 1, 1, flash * 0.06f, 5);

            // Diegetic bite feedback: bobber dips and splashes in-world. No exclamation mark or text.
            if (phase == FishingPhase.Fight && sim != null) DrawFight(dt, s, cx, baseY);

            if (toastLeft > 0 && toast != null)
            {
                float a = Math.Clamp(toastLeft / 0.6f, 0, 1);
                if (toastGood) Text(toast, cx, capi.Render.FrameHeight * 0.30f, 28, 0.55, 1, 0.55, a, true);
                else Text(toast, cx, capi.Render.FrameHeight * 0.30f, 24, 1, 0.55, 0.5, a, true);
            }
        }

        void DrawFight(float dt, float s, float cx, float baseY)
        {
            // Smooth the displayed tension and distance
            dispTension += (sim.Tension - dispTension) * (1f - MathF.Exp(-22f * dt));
            dispDistance += (sim.Distance - dispDistance) * (1f - MathF.Exp(-14f * dt));

            float danger = sim.DangerRatio;
            float shake = danger > 0.05f ? MathF.Sin(time * 70f) * 4f * danger * s : 0f;

            float barW = 440 * s, barH = 24 * s;
            float x0 = cx - barW / 2 + shake;
            float yT = baseY;

            // ---------------- Horizontal Continuous Gradient Tension Bar (Fishing Planet Style)
            // Outer bezel frame
            Rect(x0 - 3 * s, yT - 3 * s, barW + 6 * s, barH + 6 * s, 0.03f, 0.04f, 0.06f, 0.92f);
            Rect(x0 - 1 * s, yT - 1 * s, barW + 2 * s, barH + 2 * s, 0.18f, 0.22f, 0.28f, 0.60f);

            // Dark glass track slot
            Rect(x0, yT, barW, barH, 0.05f, 0.06f, 0.08f, 0.95f);

            // Faint backplate gradient underlay (shows the gauge spectrum faintly at 14% opacity)
            int bgSlices = 70;
            float sliceW = (barW - 2 * s) / bgSlices;
            for (int i = 0; i < bgSlices; i++)
            {
                float u = (i + 0.5f) / bgSlices;
                GetTensionGradientColor(u, out float ur, out float ug, out float ub);
                Rect(x0 + 1 * s + i * sliceW, yT + 1 * s, sliceW + 0.5f, barH - 2 * s, ur, ug, ub, 0.14f);
            }

            // Continuous active tension fill
            float t = Math.Clamp(dispTension, 0f, 1f);
            float fillW = (barW - 2 * s) * t;
            if (fillW > 0.5f)
            {
                int fillSlices = Math.Max(2, (int)(bgSlices * t));
                float fSliceW = fillW / fillSlices;
                for (int i = 0; i < fillSlices; i++)
                {
                    float u = (i + 0.5f) / bgSlices;
                    GetTensionGradientColor(u, out float fr, out float fg, out float fb);
                    Rect(x0 + 1 * s + i * fSliceW, yT + 1 * s, fSliceW + 0.5f, barH - 2 * s, fr, fg, fb, 0.96f);
                }

                // Top highlight shine line along active fill for 3D glass sheen
                Rect(x0 + 1 * s, yT + 1 * s, fillW, 1.5f * s, 1f, 1f, 1f, 0.28f);
            }

            // Leading indicator needle pip
            float needleX = x0 + 1 * s + fillW;
            GetTensionGradientColor(t, out float nr, out float ng, out float nb);
            // Outer needle shadow
            Rect(needleX - 2.5f * s, yT - 4 * s, 5 * s, barH + 8 * s, 0.02f, 0.03f, 0.05f, 0.70f);
            // Tinted pip
            Rect(needleX - 1.5f * s, yT - 3 * s, 3 * s, barH + 6 * s, nr, ng, nb, 0.85f);
            // Crisp white core
            Rect(needleX - 0.75f * s, yT - 2 * s, 1.5f * s, barH + 4 * s, 1f, 1f, 1f, 1.0f);

            // Red overload pulsing border (danger > 0.02)
            if (danger > 0.02f)
            {
                float blink = 0.5f + 0.5f * MathF.Abs(MathF.Sin(time * 18f));
                float da = danger * blink * 0.95f;
                Rect(x0 - 4 * s, yT - 4 * s, barW + 8 * s, 2.5f * s, 1f, 0.15f, 0.15f, da);
                Rect(x0 - 4 * s, yT + barH + 1.5f * s, barW + 8 * s, 2.5f * s, 1f, 0.15f, 0.15f, da);
                Rect(x0 - 4 * s, yT - 4 * s, 2.5f * s, barH + 8 * s, 1f, 0.15f, 0.15f, da);
                Rect(x0 + barW + 1.5f * s, yT - 4 * s, 2.5f * s, barH + 8 * s, 1f, 0.15f, 0.15f, da);
            }

            // Slack line warning (if line is left completely loose for too long)
            if (sim.SlackRatio > 0.05f)
            {
                float slackAlpha = 0.5f + 0.5f * sim.SlackRatio;
                Text(Lang.Get("anglersartoffishing:hud-slack"), cx, yT - 18 * s, 14, 0.5, 0.8, 1.0, slackAlpha, true);
            }

            // ---------------- Digital Distance & Controls readout
            double distMeters;
            var player = capi.World.Player?.Entity;
            if (currentFishPos != null && player != null)
            {
                distMeters = currentFishPos.DistanceTo(player.Pos.XYZ);
            }
            else
            {
                distMeters = 18.0 * dispDistance;
            }

            Text($"Distance: {distMeters:0.0}m", cx, yT + barH + 10 * s, 16, 0.90, 0.95, 1.0, 0.95, true);
            Text(Lang.Get("anglersartoffishing:hud-controls"), cx, yT + barH + 34 * s, 13, 0.80, 0.82, 0.86, 0.65, true);
        }

        static void GetTensionGradientColor(float u, out float r, out float g, out float b)
        {
            u = Math.Clamp(u, 0f, 1f);
            if (u <= 0.15f)
            {
                // Deep Blue -> Cyan
                float k = u / 0.15f;
                r = 0.12f * (1f - k) + 0.10f * k;
                g = 0.40f * (1f - k) + 0.78f * k;
                b = 0.95f * (1f - k) + 0.92f * k;
            }
            else if (u <= 0.45f)
            {
                // Cyan -> Emerald Green
                float k = (u - 0.15f) / 0.30f;
                r = 0.10f * (1f - k) + 0.18f * k;
                g = 0.78f * (1f - k) + 0.92f * k;
                b = 0.92f * (1f - k) + 0.32f * k;
            }
            else if (u <= 0.72f)
            {
                // Emerald Green -> Rich Golden Yellow (Optimal winching sweet zone)
                float k = (u - 0.45f) / 0.27f;
                r = 0.18f * (1f - k) + 0.96f * k;
                g = 0.92f * (1f - k) + 0.88f * k;
                b = 0.32f * (1f - k) + 0.10f * k;
            }
            else if (u <= 0.88f)
            {
                // Golden Yellow -> Vivid Orange (Heavy strain warning)
                float k = (u - 0.72f) / 0.16f;
                r = 0.96f * (1f - k) + 1.00f * k;
                g = 0.88f * (1f - k) + 0.45f * k;
                b = 0.10f * (1f - k) + 0.08f * k;
            }
            else
            {
                // Vivid Orange -> Deep Crimson Red (Overload snap zone)
                float k = (u - 0.88f) / 0.12f;
                r = 1.00f;
                g = 0.45f * (1f - k) + 0.15f * k;
                b = 0.08f * (1f - k) + 0.15f * k;
            }
        }

        // ------------------------------------------------------------------ drawing helpers

        void Rect(float x, float y, float w, float h, float r, float g, float b, float a, float z = 20)
        {
            if (w <= 0 || h <= 0) return;
            // Texture is premultiplied-alpha rendered, so premultiply the tint
            capi.Render.Render2DTexturePremultipliedAlpha(white.TextureId, x, y, w, h, z, new Vec4f(r * a, g * a, b * a, a));
        }

        void Text(string text, float cx, float y, int size, double r, double g, double b, double a, bool centered)
        {
            if (string.IsNullOrEmpty(text)) return;
            string key = $"{text}|{size}|{(int)(r * 255)}|{(int)(g * 255)}|{(int)(b * 255)}";
            if (!textCache.TryGetValue(key, out var tex))
            {
                if (textCache.Count > 200) ClearTextCache();
                var font = CairoFont.WhiteMediumText().WithFontSize(size).WithColor(new double[] { r, g, b, 1 }).WithWeight(FontWeight.Bold);
                tex = textUtil.GenTextTexture(text, font);
                textCache[key] = tex;
            }
            float x = centered ? cx - tex.Width / 2f : cx;
            // alpha fade is approximated by drawing only when sufficiently visible
            if (a > 0.02) capi.Render.Render2DLoadedTexture(tex, x, y, 30);
        }

        void ClearTextCache()
        {
            foreach (var t in textCache.Values) t.Dispose();
            textCache.Clear();
        }

        // ------------------------------------------------------------------ water animation

        void InitFightVisuals()
        {
            var player = capi.World.Player?.Entity;
            if (player != null)
            {
                castOrigin = player.Pos.XYZ.Clone();
            }
            if (baseBobber != null)
            {
                currentFishPos = baseBobber.Clone();
            }
            swimPhase = 0f;
            splashTimer = 0f;
            rippleTimer = 0f;
            shadowTimer = 0f;
            soundTimer = 0f;
            wasAirborne = false;
        }

        void ResetFishVisuals()
        {
            fishModel?.Unload();
            currentFishPos = null;
            castOrigin = null;
            baseBobber = null;
            lastRestingBobberY = 0;
        }

        void UpdateBiteBobberMotion(float dt)
        {
            var slot = capi.World.Player?.InventoryManager?.ActiveHotbarSlot;
            if (slot?.Itemstack?.Collectible is ItemFishingPole)
            {
                var bobber = VanillaPolePatches.GetBobber(capi.World, slot.Itemstack);
                if (bobber != null && bobber.Alive)
                {
                    bool biting = bobber.WatchedAttributes.GetBool(VanillaPolePatches.BiteAttr);
                    if (biting)
                    {
                        if (lastRestingBobberY == 0 || Math.Abs(bobber.Pos.Y - lastRestingBobberY) > 1.2)
                        {
                            lastRestingBobberY = bobber.Pos.Y;
                        }

                        // Bobber visibly plunges downward into water with erratic tugging
                        double dip = 0.28 + 0.08 * MathF.Sin(time * 30f);
                        float pitch = 0.52f + 0.12f * MathF.Sin(time * 24f);
                        float roll = 0.18f * MathF.Cos(time * 20f);

                        bobber.Pos.Y = lastRestingBobberY - dip;
                        bobber.Pos.Pitch = pitch;
                        bobber.Pos.Roll = roll;

                        var interp = bobber.GetBehavior<EntityBehaviorInterpolatePosition>();
                        if (interp != null)
                        {
                            interp.pN.y = bobber.Pos.Y;
                            interp.pL.y = bobber.Pos.Y;
                            interp.positionQueue.Clear();
                            interp.queueCount = 0;
                            interp.currentPitch = pitch;
                            interp.targetPitch = pitch;
                            interp.currentRoll = roll;
                            interp.targetRoll = roll;
                        }

                        var cm = capi.ModLoader.GetModSystem<ClothManager>();
                        var rope = cm?.GetClothSystemAttachedToEntity(bobber.EntityId);
                        if (rope != null)
                        {
                            rope.LastPoint.Pos.Set(bobber.Pos.X, bobber.Pos.Y, bobber.Pos.Z);
                            rope.CanRip = false;
                        }

                        SpawnBiteVisuals(bobber.Pos.X, lastRestingBobberY, bobber.Pos.Z, dt);
                    }
                    else
                    {
                        lastRestingBobberY = bobber.Pos.Y;
                    }
                }
                else
                {
                    lastRestingBobberY = 0;
                }
            }
            else
            {
                lastRestingBobberY = 0;
            }
        }

        void SpawnBiteVisuals(double x, double surfaceY, double z, float dt)
        {
            biteSplashTimer += dt;
            if (biteSplashTimer >= 0.12f)
            {
                biteSplashTimer = 0f;

                var ripple = new SimpleParticleProperties(
                    1f, 2f,
                    ColorUtil.ToRgba(205, 238, 252, 255),
                    new Vec3d(x - 0.08, surfaceY + 0.01, z - 0.08),
                    new Vec3d(x + 0.08, surfaceY + 0.03, z + 0.08),
                    new Vec3f(-0.04f, 0.01f, -0.04f),
                    new Vec3f(0.04f, 0.02f, 0.04f),
                    0.50f, 0f, 0.08f, 0.16f, EnumParticleModel.Quad
                );
                ripple.ShouldSwimOnLiquid = true;
                ripple.OpacityEvolve = EvolvingNatFloat.create(EnumTransformFunction.LINEAR, -200f);
                ripple.SizeEvolve = EvolvingNatFloat.create(EnumTransformFunction.LINEAR, 0.08f);
                capi.World.SpawnParticles(ripple);

                var splash = new SimpleParticleProperties(
                    2f, 3f,
                    ColorUtil.ToRgba(215, 235, 255, 255),
                    new Vec3d(x - 0.06, surfaceY + 0.02, z - 0.06),
                    new Vec3d(x + 0.06, surfaceY + 0.06, z + 0.06),
                    new Vec3f(-0.35f, 0.6f, -0.35f),
                    new Vec3f(0.35f, 1.4f, 0.35f),
                    0.40f, 1.0f, 0.07f, 0.15f, EnumParticleModel.Quad
                );
                splash.ShouldDieInLiquid = true;
                capi.World.SpawnParticles(splash);
            }
        }

        void UpdateFightWaterEffects(float dt)
        {
            if (baseBobber == null && bobberEntityId != 0)
            {
                var b = capi.World.GetEntityById(bobberEntityId);
                if (b != null) baseBobber = b.Pos.XYZ.Clone();
            }
            if (baseBobber == null || sim == null) return;
            var player = capi.World.Player?.Entity;
            if (player == null) return;

            if (castOrigin == null)
            {
                castOrigin = player.Pos.XYZ.Clone();
            }
            else
            {
                // Smoothly drift cast origin towards player so line tracks reeling into player's hands
                castOrigin.X += (player.Pos.X - castOrigin.X) * Math.Min(1f, 3f * dt);
                castOrigin.Z += (player.Pos.Z - castOrigin.Z) * Math.Min(1f, 3f * dt);
            }

            double dx = baseBobber.X - castOrigin.X;
            double dz = baseBobber.Z - castOrigin.Z;
            double castDist = Math.Sqrt(dx * dx + dz * dz);
            if (castDist < 0.5) castDist = 0.5;

            double fwdX = dx / castDist;
            double fwdZ = dz / castDist;
            double rightX = -fwdZ;
            double rightZ = fwdX;

            // Minimum distance from player when fully reeled in
            double minDist = Math.Min(1.6, castDist * 0.25);
            // sim.Distance: 1.0 = at baseBobber, 0.0 = landed at minDist
            double targetDist = minDist + (castDist - minDist) * Math.Clamp(sim.Distance, 0f, 1f);

            // Swimming animation rate depends on fish state
            float swimSpeed = sim.Behaviour switch
            {
                FishBehaviour.Dash => 22f,
                FishBehaviour.Run => 18f,
                FishBehaviour.Shake => 42f,   // rapid head-shaking
                FishBehaviour.Dive => 6f,
                FishBehaviour.Jump => 12f,
                FishBehaviour.Windup => 32f,
                FishBehaviour.Tired => 4.5f,
                FishBehaviour.Recover => 5.5f,
                _ => 8.5f
            };
            swimPhase += dt * swimSpeed;

            // Forward/back darting surge
            double surge = sim.Behaviour == FishBehaviour.Dash || sim.Behaviour == FishBehaviour.Run
                ? MathF.Sin(swimPhase * 0.5f) * 0.45
                : MathF.Sin(swimPhase * 0.5f) * 0.22;

            // Lateral excursion spread (how far to left and right the fish swims)
            double maxLateral = Math.Clamp(castDist * 0.4, 2.0, 4.8);
            double lateralBase = sim.FishLateral * maxLateral;

            // S-curve tail undulation
            double lateralWiggle = sim.Behaviour switch
            {
                FishBehaviour.Dash => MathF.Sin(swimPhase) * 0.55,
                FishBehaviour.Run => MathF.Sin(swimPhase) * 0.45,
                FishBehaviour.Shake => MathF.Sin(swimPhase) * 0.12,   // tight, violent side-to-side
                FishBehaviour.Dive => MathF.Sin(swimPhase) * 0.10,
                FishBehaviour.Windup => MathF.Sin(swimPhase) * 0.15, // rapid nervous vibration
                FishBehaviour.Tired => MathF.Sin(swimPhase) * 0.20,
                _ => MathF.Sin(swimPhase) * 0.38
            };

            double targetLateral = lateralBase + lateralWiggle;

            // Vertical bobbing and line tension dive
            double dip = Math.Clamp(sim.Tension * 0.16 + (sim.Behaviour == FishBehaviour.Dash ? 0.10 : 0.0), 0.0, 0.28);
            double thrash = MathF.Sin(swimPhase * 2.2f) * (sim.Behaviour == FishBehaviour.Shake ? 0.07 : 0.035);
            bool windingUp = sim.Behaviour == FishBehaviour.Windup;

            // Diving fish drag the bobber well under; a fish about to dive starts pulling it down
            if (sim.Behaviour == FishBehaviour.Dive) dip = 0.42 + 0.06 * MathF.Sin(swimPhase * 0.7f);
            else if (windingUp && sim.NextAction == FishBehaviour.Dive) dip += 0.18 * sim.BehaviourProgress;
            // A fish about to jump rises towards the surface
            else if (windingUp && sim.NextAction == FishBehaviour.Jump) dip *= 1.0 - 0.8 * sim.BehaviourProgress;

            // Jumping fish leave the water in an arc - bigger fish leap higher
            double rise = sim.Airborne ? MathF.Sin(MathF.PI * sim.AirProgress) * (0.5 + 0.9 * sim.Power) : 0.0;

            // World coordinates on water surface
            double targetX = castOrigin.X + fwdX * (targetDist + surge) + rightX * targetLateral;
            // With the real fish model on show the fish itself leaps; otherwise the bobber stands in for it
            bool showFish = fishModel.Loaded;
            double targetY = baseBobber.Y - dip + (sim.Airborne ? 0.0 : thrash) + (showFish ? 0.0 : rise);
            double targetZ = castOrigin.Z + fwdZ * (targetDist + surge) + rightZ * targetLateral;

            double prevX = currentFishPos?.X ?? targetX;
            double prevZ = currentFishPos?.Z ?? targetZ;

            if (currentFishPos == null)
            {
                currentFishPos = new Vec3d(targetX, targetY, targetZ);
            }
            else
            {
                float followRate = sim.Behaviour == FishBehaviour.Dash || sim.Behaviour == FishBehaviour.Run ? 20f : 12f;
                currentFishPos.X += (targetX - currentFishPos.X) * (1f - MathF.Exp(-followRate * dt));
                currentFishPos.Y = targetY;
                currentFishPos.Z += (targetZ - currentFishPos.Z) * (1f - MathF.Exp(-followRate * dt));
            }

            double fx = currentFishPos.X;
            double fy = currentFishPos.Y;
            double fz = currentFishPos.Z;

            // Movement yaw for realistic bobber orientation
            double moveDx = fx - prevX;
            double moveDz = fz - prevZ;
            float swimYaw = Math.Abs(moveDx) > 0.001 || Math.Abs(moveDz) > 0.001
                ? (float)Math.Atan2(moveDx, moveDz)
                : (float)Math.Atan2(fwdX, fwdZ);

            float tiltPitch = sim.Airborne && !showFish ? -0.9f + 1.8f * sim.AirProgress   // nose up on the way out, nose down on re-entry
                            : (float)(0.20f + 0.30f * sim.Tension);
            if (showFish) UpdateFishModel(dt, fx, fz, swimYaw, rise);
            float tiltRoll = MathF.Sin(swimPhase) * (sim.Behaviour == FishBehaviour.Shake ? 0.6f : 0.22f);

            // Make the vanilla bobber entity and the attached rope follow the foaming fish in world
            if (bobberEntityId != 0)
            {
                var bobber = capi.World.GetEntityById(bobberEntityId);
                if (bobber != null && bobber.Alive)
                {
                    bobber.Pos.SetPos(fx, fy, fz);
                    bobber.Pos.Yaw = swimYaw;
                    bobber.Pos.Pitch = tiltPitch;
                    bobber.Pos.Roll = tiltRoll;

                    var interp = bobber.GetBehavior<EntityBehaviorInterpolatePosition>();
                    if (interp != null)
                    {
                        interp.pN.x = fx; interp.pN.y = fy; interp.pN.z = fz;
                        interp.pL.x = fx; interp.pL.y = fy; interp.pL.z = fz;
                        interp.positionQueue.Clear();
                        interp.queueCount = 0;
                        interp.currentYaw = swimYaw;
                        interp.targetYaw = swimYaw;
                        interp.currentPitch = tiltPitch;
                        interp.targetPitch = tiltPitch;
                        interp.currentRoll = tiltRoll;
                        interp.targetRoll = tiltRoll;
                    }

                    var cm = capi.ModLoader.GetModSystem<ClothManager>();
                    var rope = cm?.GetClothSystemAttachedToEntity(bobberEntityId);
                    if (rope != null)
                    {
                        rope.LastPoint.Pos.Set(fx, fy, fz);
                        rope.CanRip = false;
                    }
                }
            }

            // Big splash as a jumping fish leaves and re-enters the water
            if (sim.Airborne != wasAirborne)
            {
                wasAirborne = sim.Airborne;
                SpawnJumpSplash(fx, baseBobber.Y, fz, reentry: !sim.Airborne);
            }
            bool surfaceTrail = !sim.Airborne && sim.Behaviour != FishBehaviour.Dive;

            // 1. Water Wake & Expanding Surface Foam (continuous trail behind moving fish)
            rippleTimer += dt;
            if (surfaceTrail && rippleTimer >= 0.045f)
            {
                rippleTimer = 0f;
                var foam = new SimpleParticleProperties(
                    1f, 2f,
                    ColorUtil.ToRgba(215, 240, 252, 255),
                    new Vec3d(fx - 0.07, fy + 0.02, fz - 0.07),
                    new Vec3d(fx + 0.07, fy + 0.04, fz + 0.07),
                    new Vec3f(-0.03f, 0.01f, -0.03f),
                    new Vec3f(0.03f, 0.02f, 0.03f),
                    0.48f, 0f, 0.05f, 0.11f, EnumParticleModel.Quad
                );
                foam.ShouldSwimOnLiquid = true;
                foam.OpacityEvolve = EvolvingNatFloat.create(EnumTransformFunction.LINEAR, -220f);
                foam.SizeEvolve = EvolvingNatFloat.create(EnumTransformFunction.LINEAR, 0.05f);
                capi.World.SpawnParticles(foam);
            }

            // 2. Sub-surface Fish Silhouette (dark aquatic shadow following fish body)
            shadowTimer += dt;
            if (surfaceTrail && shadowTimer >= 0.08f)
            {
                shadowTimer = 0f;
                var shadow = new SimpleParticleProperties(
                    1f, 1f,
                    ColorUtil.ToRgba(140, 25, 45, 55),
                    new Vec3d(fx - 0.08, fy - 0.03, fz - 0.08),
                    new Vec3d(fx + 0.08, fy - 0.01, fz + 0.08),
                    new Vec3f(-0.02f, 0f, -0.02f),
                    new Vec3f(0.02f, 0f, 0.02f),
                    0.28f, 0f, 0.09f, 0.16f, EnumParticleModel.Quad
                );
                shadow.ShouldSwimOnLiquid = true;
                shadow.OpacityEvolve = EvolvingNatFloat.create(EnumTransformFunction.LINEAR, -180f);
                capi.World.SpawnParticles(shadow);
            }

            // 3. Dynamic Splashes and Droplets (spray shooting up into air)
            splashTimer += dt;
            float splashInterval = sim.Behaviour switch
            {
                FishBehaviour.Dash => 0.05f,
                FishBehaviour.Shake => 0.06f,
                FishBehaviour.Run => 0.06f,
                FishBehaviour.Windup => 0.07f,
                FishBehaviour.Swim => 0.11f,
                FishBehaviour.Dive => 0.25f,
                _ => 0.18f
            };

            if (!sim.Airborne && splashTimer >= splashInterval)
            {
                splashTimer = 0f;

                float minCount = 1f, maxCount = 2f;
                float minUp = 1.0f, maxUp = 2.0f;
                float horizSpread = 0.5f;

                switch (sim.Behaviour)
                {
                    case FishBehaviour.Dash:
                        minCount = 4f; maxCount = 8f;
                        minUp = 1.8f; maxUp = 3.3f;
                        horizSpread = 1.1f;
                        break;
                    case FishBehaviour.Run:
                        minCount = 3f; maxCount = 6f;
                        minUp = 1.2f; maxUp = 2.4f;
                        horizSpread = 1.0f;
                        break;
                    case FishBehaviour.Shake:
                        minCount = 2f; maxCount = 4f;
                        minUp = 1.4f; maxUp = 2.6f;
                        horizSpread = 0.5f;
                        break;
                    case FishBehaviour.Dive:
                        minCount = 1f; maxCount = 1f;
                        minUp = 0.3f; maxUp = 0.7f;
                        horizSpread = 0.2f;
                        break;
                    case FishBehaviour.Windup:
                        minCount = 2f; maxCount = 4f;
                        minUp = 0.6f; maxUp = 1.4f;
                        horizSpread = 0.4f;
                        break;
                    case FishBehaviour.Swim:
                        minCount = 2f; maxCount = 4f;
                        minUp = 1.1f; maxUp = 2.1f;
                        horizSpread = 0.65f;
                        break;
                    case FishBehaviour.Tired:
                    case FishBehaviour.Recover:
                        minCount = 1f; maxCount = 2f;
                        minUp = 0.5f; maxUp = 1.1f;
                        horizSpread = 0.35f;
                        break;
                }

                var drops = new SimpleParticleProperties(
                    minCount, maxCount,
                    ColorUtil.ToRgba(215, 235, 255, 255),
                    new Vec3d(fx - 0.14, fy + 0.04, fz - 0.14),
                    new Vec3d(fx + 0.14, fy + 0.12, fz + 0.14),
                    new Vec3f(-horizSpread, minUp, -horizSpread),
                    new Vec3f(horizSpread, maxUp, horizSpread),
                    0.5f, 1.0f, 0.08f, 0.22f, EnumParticleModel.Quad
                );
                drops.ShouldDieInLiquid = true;
                capi.World.SpawnParticles(drops);

                // Bubbles churn underwater during active bursts
                if (sim.Behaviour == FishBehaviour.Dash || sim.Behaviour == FishBehaviour.Windup || sim.Behaviour == FishBehaviour.Dive)
                {
                    var bubbles = new SimpleParticleProperties(
                        1f, 3f,
                        ColorUtil.ToRgba(180, 240, 255, 255),
                        new Vec3d(fx - 0.18, fy - 0.22, fz - 0.18),
                        new Vec3d(fx + 0.18, fy - 0.05, fz + 0.18),
                        new Vec3f(-0.1f, 0.35f, -0.1f),
                        new Vec3f(0.1f, 0.75f, 0.1f),
                        0.45f, -0.2f, 0.05f, 0.12f, EnumParticleModel.Quad
                    );
                    bubbles.ShouldDieInAir = true;
                    capi.World.SpawnParticles(bubbles);
                }
            }

            // 4. In-world Water Audio
            soundTimer += dt;
            float soundInterval = sim.Behaviour switch
            {
                FishBehaviour.Dash => 0.42f,
                FishBehaviour.Shake => 0.35f,
                FishBehaviour.Run => 0.5f,
                FishBehaviour.Swim => 0.85f,
                FishBehaviour.Dive => 1.6f,
                _ => 1.4f
            };

            if (!sim.Airborne && soundTimer >= soundInterval)
            {
                soundTimer = 0f;
                float vol = sim.Behaviour switch
                {
                    FishBehaviour.Dash => 0.65f,
                    FishBehaviour.Run => 0.5f,
                    FishBehaviour.Shake => 0.4f,
                    FishBehaviour.Swim => 0.32f,
                    FishBehaviour.Dive => 0.12f,
                    _ => 0.18f
                };
                capi.World.PlaySoundAt(new AssetLocation("game:sounds/player/swim"), fx, fy, fz, null, true, 20f, vol);
            }
        }

        void UpdateFishModel(float dt, double fx, double fz, float swimYaw, double rise)
        {
            // How far under the surface the fish swims: deep on dives, right at the top when spent or at the bank
            double depth = sim.Behaviour switch
            {
                FishBehaviour.Dive => 1.1,
                FishBehaviour.Tired => 0.05,
                FishBehaviour.Shake => 0.12,
                FishBehaviour.Dash or FishBehaviour.Run => 0.35,
                _ => 0.25
            };
            if (sim.Distance < 0.15f) depth = Math.Min(depth, 0.06);
            fishDepth += (float)(depth - fishDepth) * (1f - MathF.Exp(-3f * dt));

            fishModel.Pos.Set(fx, baseBobber.Y - fishDepth + rise, fz);
            fishModel.Yaw = swimYaw;
            fishModel.Pitch = sim.Airborne ? 0.9f - 1.8f * sim.AirProgress      // nose up leaving the water, nose down re-entering
                            : sim.Behaviour == FishBehaviour.Dive ? -0.5f : 0f;
            fishModel.Roll = sim.Behaviour switch
            {
                FishBehaviour.Tired => 1.3f,                                   // rolls onto its side when spent
                FishBehaviour.Shake => MathF.Sin(swimPhase) * 0.5f,
                _ => MathF.Sin(swimPhase * 0.5f) * 0.12f
            };
        }

        void SpawnJumpSplash(double x, double surfaceY, double z, bool reentry)
        {
            float size = 0.6f + 0.6f * sim.Power;
            var splash = new SimpleParticleProperties(
                12f * size, 20f * size,
                ColorUtil.ToRgba(220, 238, 255, 255),
                new Vec3d(x - 0.25 * size, surfaceY + 0.02, z - 0.25 * size),
                new Vec3d(x + 0.25 * size, surfaceY + 0.12, z + 0.25 * size),
                new Vec3f(-1.0f, reentry ? 2.6f : 1.8f, -1.0f),
                new Vec3f(1.0f, reentry ? 4.6f : 3.4f, 1.0f),
                0.7f, 1.0f, 0.10f, 0.26f, EnumParticleModel.Quad
            );
            splash.ShouldDieInLiquid = true;
            capi.World.SpawnParticles(splash);

            string sound = sim.Power >= 0.6f ? "game:sounds/environment/largesplash1"
                         : sim.Power >= 0.3f ? "game:sounds/environment/mediumsplash"
                         : "game:sounds/environment/smallsplash";
            capi.World.PlaySoundAt(new AssetLocation(sound), x, surfaceY, z, null, true, 24f, reentry ? 0.9f : 0.6f);
        }

        void SpawnOutcomeEffects(FightOutcome outcome)
        {
            if (currentFishPos == null) return;
            double fx = currentFishPos.X, fy = currentFishPos.Y, fz = currentFishPos.Z;

            if (outcome == FightOutcome.Landed)
            {
                var splash = new SimpleParticleProperties(
                    12f, 18f,
                    ColorUtil.ToRgba(220, 240, 255, 255),
                    new Vec3d(fx - 0.25, fy + 0.05, fz - 0.25),
                    new Vec3d(fx + 0.25, fy + 0.15, fz + 0.25),
                    new Vec3f(-0.9f, 1.8f, -0.9f),
                    new Vec3f(0.9f, 3.4f, 0.9f),
                    0.65f, 1.0f, 0.12f, 0.28f, EnumParticleModel.Quad
                );
                splash.ShouldDieInLiquid = true;
                capi.World.SpawnParticles(splash);

                var foam = new SimpleParticleProperties(
                    3f, 6f,
                    ColorUtil.ToRgba(215, 240, 250, 255),
                    new Vec3d(fx - 0.14, fy + 0.02, fz - 0.14),
                    new Vec3d(fx + 0.14, fy + 0.04, fz + 0.14),
                    new Vec3f(-0.05f, 0.01f, -0.05f),
                    new Vec3f(0.05f, 0.03f, 0.05f),
                    0.5f, 0f, 0.07f, 0.14f, EnumParticleModel.Quad
                );
                foam.ShouldSwimOnLiquid = true;
                foam.OpacityEvolve = EvolvingNatFloat.create(EnumTransformFunction.LINEAR, -200f);
                foam.SizeEvolve = EvolvingNatFloat.create(EnumTransformFunction.LINEAR, 0.08f);
                capi.World.SpawnParticles(foam);

                capi.World.PlaySoundAt(new AssetLocation("game:sounds/player/swim"), fx, fy, fz, null, true, 22f, 0.85f);
            }
            else
            {
                var splash = new SimpleParticleProperties(
                    8f, 12f,
                    ColorUtil.ToRgba(190, 220, 245, 255),
                    new Vec3d(fx - 0.2, fy + 0.05, fz - 0.2),
                    new Vec3d(fx + 0.2, fy + 0.12, fz + 0.2),
                    new Vec3f(-0.7f, 1.2f, -0.7f),
                    new Vec3f(0.7f, 2.4f, 0.7f),
                    0.5f, 1.0f, 0.1f, 0.22f, EnumParticleModel.Quad
                );
                splash.ShouldDieInLiquid = true;
                capi.World.SpawnParticles(splash);

                capi.World.PlaySoundAt(new AssetLocation("game:sounds/player/swim"), fx, fy, fz, null, true, 18f, 0.6f);
            }
        }

        public void Dispose()
        {
            capi.Event.UnregisterRenderer(motionRenderer, EnumRenderStage.Before);
            capi.Event.UnregisterRenderer(this, EnumRenderStage.Ortho);
            capi.Event.UnregisterRenderer(fishModel, EnumRenderStage.Opaque);
            fishModel.Dispose();
            ResetFishVisuals();
            ClearTextCache();
            white?.Dispose();
        }

        class BobberMotionRenderer : IRenderer
        {
            readonly FishingClient client;

            public double RenderOrder => 0.95;
            public int RenderRange => 9999;

            public BobberMotionRenderer(FishingClient client)
            {
                this.client = client;
            }

            public void OnRenderFrame(float dt, EnumRenderStage stage)
            {
                client.OnBeforeRenderFrame(dt);
            }

            public void Dispose() { }
        }
    }
}
