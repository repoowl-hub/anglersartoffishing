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

            if (!string.IsNullOrEmpty(p.Message))
            {
                bool good = p.Message.EndsWith("caught");
                ShowToast(good ? Lang.Get(p.Message, p.SpeciesName, p.SizeCm.ToString("0")) : Lang.Get(p.Message), good);
            }

            switch (newPhase)
            {
                case FishingPhase.Idle:
                    sim = null;
                    break;
                case FishingPhase.Waiting:
                    sim = null;
                    break;
                case FishingPhase.Bite:
                    biteWindow = Math.Max(0.5f, p.BiteWindow);
                    biteLeft = biteWindow;
                    hookSent = false;
                    flash = 1f;
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

                if (sim.Finished && reported == FightOutcome.None)
                {
                    reported = sim.Outcome;
                    channel.SendPacket(new FishingActionPacket
                    {
                        Action = 1,
                        Outcome = sim.Outcome == FightOutcome.Landed ? 2 : sim.Outcome == FightOutcome.LineSnapped ? 1 : 0,
                        FightSeconds = sim.Elapsed
                    });
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

        public void Dispose()
        {
            capi.Event.MouseMove -= OnMouseMove;
            capi.Event.UnregisterRenderer(this, EnumRenderStage.Ortho);
            ClearTextCache();
            white?.Dispose();
        }
    }
}
