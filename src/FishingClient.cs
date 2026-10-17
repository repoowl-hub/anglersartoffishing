using System;
using System.Collections.Generic;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

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
        string speciesName = "";
        float biteWindow, biteLeft;
        bool hookSent;

        // Toast
        string toast;
        float toastLeft;
        bool toastGood;

        // Smoothed display values
        float dispTension, dispStamina = 1f, dispDistance = 1f;
        float time;
        float flash; // 0..1 white flash on state changes

        // Mouse steering
        const float MouseSteerSensitivity = 0.005f; // ~200px horizontal sweep = full +/-1 tilt
        const float CenterReturnSpeed = 0.35f;       // gradual return-to-center when mouse is stationary
        float steerTarget;

        // In-world water animation
        Vec3d baseBobber;
        Vec3d castOrigin;
        Vec3d currentFishPos;
        float swimPhase;
        float splashTimer;
        float rippleTimer;
        float shadowTimer;
        float soundTimer;
        float biteSplashTimer;

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

            api.Event.RegisterRenderer(this, EnumRenderStage.Ortho, "anglersartoffishing-hud");
            api.Event.MouseMove += OnMouseMove;
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
                bool good = p.Message.EndsWith("caught");
                ShowToast(good ? Lang.Get(p.Message, p.SpeciesName, p.SizeCm.ToString("0")) : Lang.Get(p.Message), good);
            }

            switch (newPhase)
            {
                case FishingPhase.Idle:
                    sim = null;
                    ResetFishVisuals();
                    break;
                case FishingPhase.Waiting:
                    sim = null;
                    ResetFishVisuals();
                    break;
                case FishingPhase.Bite:
                    biteWindow = Math.Max(0.5f, p.BiteWindow);
                    biteLeft = biteWindow;
                    hookSent = false;
                    flash = 1f;
                    biteSplashTimer = 0f;
                    break;
                case FishingPhase.Fight:
                    speciesName = p.SpeciesName;
                    steerTarget = 0f;
                    sim = new FightSimulation(new FightParams
                    {
                        Power = p.Power,
                        StaminaSeconds = p.StaminaSeconds,
                        Aggression = p.Aggression,
                        RodMargin = p.RodMargin,
                        ReelSpeed = p.ReelSpeed,
                        Forgiveness = p.Forgiveness,
                        StartDistance = p.StartDistance
                    });
                    sim.BehaviourChanged += _ => flash = 0.6f;
                    reported = FightOutcome.None;
                    dispTension = sim.Tension;
                    dispStamina = 1f;
                    dispDistance = sim.Distance;
                    flash = 1f;
                    InitFightVisuals();
                    break;
            }
            phase = newPhase;
        }

        /// <summary>Called from the rod item on the client when the player clicks with it.</summary>
        public void OnRodUsed()
        {
            if (phase == FishingPhase.Bite && !hookSent)
            {
                hookSent = true;
                channel.SendPacket(new FishingActionPacket { Action = 0 });
            }
        }

        void OnMouseMove(MouseEvent e)
        {
            if (phase == FishingPhase.Fight && sim != null && capi.Input.MouseGrabbed)
            {
                steerTarget = Math.Clamp(steerTarget + e.DeltaX * MouseSteerSensitivity, -1f, 1f);
                e.Handled = true;
            }
        }

        void ShowToast(string text, bool good)
        {
            toast = text;
            toastGood = good;
            toastLeft = 4f;
        }

        // ------------------------------------------------------------------ frame

        public void OnRenderFrame(float dt, EnumRenderStage stage)
        {
            time += dt;
            if (flash > 0) flash = Math.Max(0, flash - dt * 3f);
            if (toastLeft > 0) toastLeft -= dt;

            if (phase == FishingPhase.Bite)
            {
                biteLeft -= dt;
                UpdateBiteEffects(dt);
            }
            else if (phase == FishingPhase.Fight && sim != null)
            {
                bool reel = capi.Input.InWorldMouseButton.Right;

                // Passive return-to-center drift so player never runs out of mousepad space
                if (MathF.Abs(steerTarget) > 0.001f)
                {
                    steerTarget -= MathF.Sign(steerTarget) * Math.Min(MathF.Abs(steerTarget), CenterReturnSpeed * dt);
                }

                sim.Update(dt, reel, steerTarget);
                UpdateFightWaterEffects(dt);

                if (sim.Finished && reported == FightOutcome.None)
                {
                    reported = sim.Outcome;
                    channel.SendPacket(new FishingActionPacket
                    {
                        Action = 1,
                        Outcome = sim.Outcome == FightOutcome.Landed ? 2 : sim.Outcome == FightOutcome.LineSnapped ? 1 : 0,
                        FightSeconds = sim.Elapsed
                    });
                    SpawnOutcomeEffects(sim.Outcome);
                    phase = FishingPhase.Idle; // server answer with the final message follows
                }
            }

            DrawHud(dt);
        }

        // ------------------------------------------------------------------ HUD

        void DrawHud(float dt)
        {
            float s = (float)RuntimeEnv.GUIScale;
            float cx = capi.Render.FrameWidth / 2f;
            float baseY = capi.Render.FrameHeight * 0.70f;

            if (flash > 0) Rect(0, 0, capi.Render.FrameWidth, capi.Render.FrameHeight, 1, 1, 1, flash * 0.06f, 5);

            switch (phase)
            {
                case FishingPhase.Waiting:
                    {
                        int dots = (int)(time * 2.5f) % 4;
                        Text("Waiting for a bite" + new string('.', dots), cx, baseY, 20, 0.85, 0.92, 1, 0.9, true);
                        Text(Lang.Get("anglersartoffishing:hud-waiting-hint"), cx, baseY + 26 * s, 14, 0.8, 0.8, 0.8, 0.7, true);
                        break;
                    }
                case FishingPhase.Bite:
                    {
                        float pulse = 1f + 0.12f * MathF.Sin(time * 30f);
                        Text("!", cx, baseY - 60 * s, (int)(76 * pulse), 1, 0.85, 0.25, 1, true);
                        Text(Lang.Get("anglersartoffishing:hud-bite"), cx, baseY, 24, 1, 1, 1, 1, true);
                        float w = 220 * s, h = 8 * s;
                        Rect(cx - w / 2, baseY + 40 * s, w, h, 0, 0, 0, 0.6f);
                        Rect(cx - w / 2, baseY + 40 * s, w * Math.Clamp(biteLeft / biteWindow, 0, 1), h, 1, 0.8f, 0.25f, 1);
                        break;
                    }
                case FishingPhase.Fight:
                    if (sim != null) DrawFight(dt, s, cx, baseY);
                    break;
            }

            if (toastLeft > 0 && toast != null)
            {
                float a = Math.Clamp(toastLeft / 0.6f, 0, 1);
                if (toastGood) Text(toast, cx, capi.Render.FrameHeight * 0.30f, 28, 0.55, 1, 0.55, a, true);
                else Text(toast, cx, capi.Render.FrameHeight * 0.30f, 24, 1, 0.55, 0.5, a, true);
            }
        }

        void DrawFight(float dt, float s, float cx, float baseY)
        {
            // Smooth the displayed values (the simulation itself is already continuous)
            dispTension += (sim.Tension - dispTension) * (1f - MathF.Exp(-22f * dt));
            dispStamina += (sim.Stamina - dispStamina) * (1f - MathF.Exp(-10f * dt));
            dispDistance += (sim.Distance - dispDistance) * (1f - MathF.Exp(-14f * dt));

            float danger = sim.DangerRatio;
            float shake = danger > 0.05f ? MathF.Sin(time * 70f) * 3.5f * danger * s : 0f;

            float barW = 400 * s, barH = 24 * s;
            float x0 = cx - barW / 2 + shake;
            float yT = baseY;

            // ---------------- status line
            string status; double sr = 1, sg = 1, sb = 1;
            string dirArrow = sim.FishDir < 0 ? "<<" : ">>";
            string steerArrow = sim.FishDir < 0 ? ">>" : "<<";
            switch (sim.Behaviour)
            {
                case FishBehaviour.Windup: status = $"{dirArrow}  {Lang.Get("anglersartoffishing:hud-windup")}  {dirArrow}"; sr = 1; sg = 0.78; sb = 0.25; break;
                case FishBehaviour.Dash: status = $"{Lang.Get("anglersartoffishing:hud-dash")}  {steerArrow}"; sr = 1; sg = 0.35; sb = 0.3; break;
                case FishBehaviour.Recover: status = Lang.Get("anglersartoffishing:hud-recover"); sr = 0.8; sg = 0.85; sb = 0.9; break;
                case FishBehaviour.Tired: status = Lang.Get("anglersartoffishing:hud-tired"); sr = 0.45; sg = 1; sb = 0.55; break;
                default: status = Lang.Get("anglersartoffishing:hud-swim"); break;
            }
            Text(speciesName, cx, yT - 82 * s, 16, 0.85, 0.9, 1, 0.85, true);
            Text(status, cx, yT - 54 * s, 26, sr, sg, sb, 1, true);

            // ---------------- stamina (thin, above tension)
            float ySt = yT - 22 * s;
            Rect(x0, ySt, barW, 7 * s, 0, 0, 0, 0.55f);
            Rect(x0, ySt, barW * Math.Clamp(dispStamina, 0, 1), 7 * s, 1f, 0.6f, 0.2f, 0.95f);

            // ---------------- tension bar
            Rect(x0 - 3 * s, yT - 3 * s, barW + 6 * s, barH + 6 * s, 0.05f, 0.05f, 0.07f, 0.8f);
            Rect(x0, yT, barW, barH, 0.12f, 0.14f, 0.17f, 0.95f);

            // zones
            Rect(x0 + barW * FightSimulation.SweetMin, yT, barW * (FightSimulation.SweetMax - FightSimulation.SweetMin), barH, 0.3f, 0.9f, 0.45f, 0.30f);
            Rect(x0 + barW * FightSimulation.DangerStart, yT, barW * (1f - FightSimulation.DangerStart), barH, 1f, 0.2f, 0.2f, 0.35f);

            // fill
            float t = Math.Clamp(dispTension, 0f, 1f);
            GetTensionColor(t, out float fr, out float fg, out float fb);
            Rect(x0, yT + 3 * s, barW * t, barH - 6 * s, fr, fg, fb, 0.95f);
            // marker
            Rect(x0 + barW * t - 1.5f * s, yT - 5 * s, 3 * s, barH + 10 * s, 1, 1, 1, 1);
            if (danger > 0.02f)
            {
                float blink = 0.4f + 0.6f * MathF.Abs(MathF.Sin(time * 14f));
                Rect(x0 - 3 * s, yT - 3 * s, barW + 6 * s, 3 * s, 1, 0.15f, 0.15f, danger * blink);
                Rect(x0 - 3 * s, yT + barH, barW + 6 * s, 3 * s, 1, 0.15f, 0.15f, danger * blink);
            }

            // slack warning
            if (sim.SlackRatio > 0.05f)
            {
                Text(Lang.Get("anglersartoffishing:hud-slack"), cx, yT + barH + 8 * s, 15, 0.6, 0.8, 1, 0.5 + 0.5 * sim.SlackRatio, true);
            }

            // ---------------- direction track (fish vs rod)
            float yTr = yT + barH + 12 * s;
            float trackH = 14 * s;
            Rect(x0, yTr, barW, trackH, 0, 0, 0, 0.5f);
            Rect(cx - 1 * s, yTr, 2 * s, trackH, 1, 1, 1, 0.25f);

            // highlight the side to steer towards
            if (sim.Behaviour == FishBehaviour.Windup || sim.Behaviour == FishBehaviour.Dash)
            {
                float side = -sim.FishDir;
                float hx = side > 0 ? cx : x0;
                Rect(hx, yTr, barW / 2, trackH, 0.3f, 0.9f, 0.5f, sim.CounterSteering ? 0.45f : 0.2f);
            }

            float fx = cx + sim.FishLateral * (barW / 2 - 8 * s);
            float rx = cx + sim.RodAngle * (barW / 2 - 8 * s);
            Rect(fx - 8 * s, yTr - 3 * s, 16 * s, trackH + 6 * s, 1, 0.55f, 0.25f, 1);   // fish
            Rect(rx - 3 * s, yTr - 2 * s, 6 * s, trackH + 4 * s, 0.6f, 0.9f, 1, 1);      // rod

            // ---------------- distance / progress
            float yD = yTr + trackH + 10 * s;
            Rect(x0, yD, barW, 9 * s, 0, 0, 0, 0.55f);
            Rect(x0, yD, barW * Math.Clamp(1f - dispDistance, 0, 1), 9 * s, 0.35f, 0.65f, 1f, 0.95f);

            Text(Lang.Get("anglersartoffishing:hud-controls"), cx, yD + 14 * s, 13, 0.85, 0.85, 0.85, 0.6, true);
        }

        static void GetTensionColor(float t, out float r, out float g, out float b)
        {
            if (t < FightSimulation.SweetMin) { float k = t / FightSimulation.SweetMin; r = 0.3f; g = 0.55f + 0.2f * k; b = 0.9f - 0.3f * k; }
            else if (t < FightSimulation.SweetMax) { r = 0.35f; g = 0.9f; b = 0.45f; }
            else if (t < FightSimulation.DangerStart) { float k = (t - FightSimulation.SweetMax) / (FightSimulation.DangerStart - FightSimulation.SweetMax); r = 0.35f + 0.65f * k; g = 0.9f - 0.2f * k; b = 0.45f - 0.3f * k; }
            else { r = 1f; g = 0.25f; b = 0.2f; }
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
        }

        void ResetFishVisuals()
        {
            currentFishPos = null;
            castOrigin = null;
        }

        void UpdateBiteEffects(float dt)
        {
            if (baseBobber == null) return;
            biteSplashTimer += dt;
            if (biteSplashTimer >= 0.16f)
            {
                biteSplashTimer = 0f;

                // Periodic bite ripple expanding at the bobber
                var ripple = new SimpleParticleProperties(
                    1f, 2f,
                    ColorUtil.ToRgba(160, 220, 240, 255),
                    new Vec3d(baseBobber.X - 0.22, baseBobber.Y + 0.02, baseBobber.Z - 0.22),
                    new Vec3d(baseBobber.X + 0.22, baseBobber.Y + 0.04, baseBobber.Z + 0.22),
                    new Vec3f(-0.08f, 0.01f, -0.08f),
                    new Vec3f(0.08f, 0.03f, 0.08f),
                    0.55f, 0f, 0.15f, 0.35f, EnumParticleModel.Quad
                );
                ripple.ShouldSwimOnLiquid = true;
                ripple.OpacityEvolve = EvolvingNatFloat.create(EnumTransformFunction.LINEAR, -200f);
                ripple.SizeEvolve = EvolvingNatFloat.create(EnumTransformFunction.LINEAR, 0.28f);
                capi.World.SpawnParticles(ripple);

                // Small sharp tug splash
                var splash = new SimpleParticleProperties(
                    2f, 4f,
                    ColorUtil.ToRgba(210, 230, 250, 255),
                    new Vec3d(baseBobber.X - 0.1, baseBobber.Y + 0.05, baseBobber.Z - 0.1),
                    new Vec3d(baseBobber.X + 0.1, baseBobber.Y + 0.1, baseBobber.Z + 0.1),
                    new Vec3f(-0.5f, 0.8f, -0.5f),
                    new Vec3f(0.5f, 1.8f, 0.5f),
                    0.45f, 1.0f, 0.08f, 0.18f, EnumParticleModel.Quad
                );
                splash.ShouldDieInLiquid = true;
                capi.World.SpawnParticles(splash);
            }
        }

        void UpdateFightWaterEffects(float dt)
        {
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
                FishBehaviour.Windup => 32f,
                FishBehaviour.Tired => 4.5f,
                FishBehaviour.Recover => 5.5f,
                _ => 8.5f
            };
            swimPhase += dt * swimSpeed;

            // Forward/back darting surge
            double surge = sim.Behaviour == FishBehaviour.Dash
                ? MathF.Sin(swimPhase * 0.5f) * 0.45
                : MathF.Sin(swimPhase * 0.5f) * 0.22;

            // Lateral excursion spread (how far to left and right the fish swims)
            double maxLateral = Math.Clamp(castDist * 0.4, 2.0, 4.8);
            double lateralBase = sim.FishLateral * maxLateral;

            // S-curve tail undulation
            double lateralWiggle = sim.Behaviour switch
            {
                FishBehaviour.Dash => MathF.Sin(swimPhase) * 0.55,
                FishBehaviour.Windup => MathF.Sin(swimPhase) * 0.15, // rapid nervous vibration
                FishBehaviour.Tired => MathF.Sin(swimPhase) * 0.20,
                _ => MathF.Sin(swimPhase) * 0.38
            };

            double targetLateral = lateralBase + lateralWiggle;

            // World coordinates on water surface
            double targetX = castOrigin.X + fwdX * (targetDist + surge) + rightX * targetLateral;
            double targetY = baseBobber.Y;
            double targetZ = castOrigin.Z + fwdZ * (targetDist + surge) + rightZ * targetLateral;

            if (currentFishPos == null)
            {
                currentFishPos = new Vec3d(targetX, targetY, targetZ);
            }
            else
            {
                float followRate = sim.Behaviour == FishBehaviour.Dash ? 20f : 12f;
                currentFishPos.X += (targetX - currentFishPos.X) * (1f - MathF.Exp(-followRate * dt));
                currentFishPos.Y = targetY;
                currentFishPos.Z += (targetZ - currentFishPos.Z) * (1f - MathF.Exp(-followRate * dt));
            }

            double fx = currentFishPos.X;
            double fy = currentFishPos.Y;
            double fz = currentFishPos.Z;

            // 1. Water Wake & Expanding Surface Foam (continuous trail behind moving fish)
            rippleTimer += dt;
            if (rippleTimer >= 0.045f)
            {
                rippleTimer = 0f;
                var foam = new SimpleParticleProperties(
                    1f, 2f,
                    ColorUtil.ToRgba(175, 225, 242, 255),
                    new Vec3d(fx - 0.22, fy + 0.02, fz - 0.22),
                    new Vec3d(fx + 0.22, fy + 0.04, fz + 0.22),
                    new Vec3f(-0.1f, 0.01f, -0.1f),
                    new Vec3f(0.1f, 0.03f, 0.1f),
                    0.65f, 0f, 0.18f, 0.36f, EnumParticleModel.Quad
                );
                foam.ShouldSwimOnLiquid = true;
                foam.OpacityEvolve = EvolvingNatFloat.create(EnumTransformFunction.LINEAR, -220f);
                foam.SizeEvolve = EvolvingNatFloat.create(EnumTransformFunction.LINEAR, 0.32f);
                capi.World.SpawnParticles(foam);
            }

            // 2. Sub-surface Fish Silhouette (dark aquatic shadow following fish body)
            shadowTimer += dt;
            if (shadowTimer >= 0.08f)
            {
                shadowTimer = 0f;
                var shadow = new SimpleParticleProperties(
                    1f, 1f,
                    ColorUtil.ToRgba(135, 28, 52, 62),
                    new Vec3d(fx - 0.16, fy - 0.03, fz - 0.16),
                    new Vec3d(fx + 0.16, fy - 0.01, fz + 0.16),
                    new Vec3f(-0.03f, 0f, -0.03f),
                    new Vec3f(0.03f, 0f, 0.03f),
                    0.32f, 0f, 0.24f, 0.42f, EnumParticleModel.Quad
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
                FishBehaviour.Windup => 0.07f,
                FishBehaviour.Swim => 0.11f,
                _ => 0.18f
            };

            if (splashTimer >= splashInterval)
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
                if (sim.Behaviour == FishBehaviour.Dash || sim.Behaviour == FishBehaviour.Windup)
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
                FishBehaviour.Swim => 0.85f,
                _ => 1.4f
            };

            if (soundTimer >= soundInterval)
            {
                soundTimer = 0f;
                float vol = sim.Behaviour switch
                {
                    FishBehaviour.Dash => 0.65f,
                    FishBehaviour.Swim => 0.32f,
                    _ => 0.18f
                };
                capi.World.PlaySoundAt(new AssetLocation("game:sounds/player/swim"), fx, fy, fz, null, true, 20f, vol);
            }
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
                    ColorUtil.ToRgba(180, 230, 245, 255),
                    new Vec3d(fx - 0.35, fy + 0.02, fz - 0.35),
                    new Vec3d(fx + 0.35, fy + 0.04, fz + 0.35),
                    new Vec3f(-0.15f, 0.01f, -0.15f),
                    new Vec3f(0.15f, 0.04f, 0.15f),
                    0.8f, 0f, 0.2f, 0.5f, EnumParticleModel.Quad
                );
                foam.ShouldSwimOnLiquid = true;
                foam.OpacityEvolve = EvolvingNatFloat.create(EnumTransformFunction.LINEAR, -200f);
                foam.SizeEvolve = EvolvingNatFloat.create(EnumTransformFunction.LINEAR, 0.4f);
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
            capi.Event.MouseMove -= OnMouseMove;
            capi.Event.UnregisterRenderer(this, EnumRenderStage.Ortho);
            ResetFishVisuals();
            ClearTextCache();
            white?.Dispose();
        }
    }
}
