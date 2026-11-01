using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace AnglersArtOfFishing
{
    /// <summary>
    /// A lure has no float: when a cast bobber carries one of our lures (items with <c>lureStyles</c>), the vanilla
    /// bobber is hidden and the lure's own model is drawn in its place, nose pointing back at the angler.
    /// The bobber's bait reaches the client through its "baitStack" watched attribute (written on spawn).
    /// </summary>
    public class LureBobberRenderer : IRenderer
    {
        const float ScanInterval = 0.5f;
        const float ScanRange = 48f;

        readonly ICoreClientAPI capi;
        readonly Matrixf modelMat = new Matrixf();
        readonly Dictionary<string, MultiTextureMeshRef> meshes = new Dictionary<string, MultiTextureMeshRef>();
        readonly Dictionary<long, Item> lureBobbers = new Dictionary<long, Item>();
        float scanAccum = ScanInterval;

        /// <summary>Bobber entity ids currently drawn as a lure (the vanilla bobber renderer skips these).</summary>
        public static readonly HashSet<long> HiddenBobbers = new HashSet<long>();

        public double RenderOrder => 0.41;
        public int RenderRange => 64;

        public LureBobberRenderer(ICoreClientAPI capi)
        {
            this.capi = capi;
        }

        /// <summary>True if this bobber is being drawn as a lure.</summary>
        public static bool IsLureBobber(long bobberId) => HiddenBobbers.Contains(bobberId);

        public void OnRenderFrame(float dt, EnumRenderStage stage)
        {
            var player = capi.World.Player?.Entity;
            if (player == null) return;

            scanAccum += dt;
            if (scanAccum >= ScanInterval)
            {
                scanAccum = 0f;
                Scan(player.Pos.XYZ);
            }
            if (lureBobbers.Count == 0) return;

            var rapi = capi.Render;
            var cam = player.CameraPos;
            rapi.GlDisableCullFace();
            foreach (var kv in lureBobbers)
            {
                var bobber = capi.World.GetEntityById(kv.Key);
                if (bobber == null || !bobber.Alive) continue;
                var mesh = GetMesh(kv.Value);
                if (mesh == null) continue;

                var pos = bobber.Pos;
                // Lure models face -Z: turn the nose back along the line towards the angler
                float yaw = (float)Math.Atan2(player.Pos.X - pos.X, player.Pos.Z - pos.Z) - GameMath.PI;

                var prog = rapi.PreparedStandardShader((int)pos.X, (int)pos.Y, (int)pos.Z);
                prog.ModelMatrix = modelMat
                    .Identity()
                    .Translate(pos.X - cam.X, pos.Y - cam.Y, pos.Z - cam.Z)
                    .RotateY(yaw)
                    .Translate(-0.5f, -0.12f, -0.5f)
                    .Values;
                prog.ViewMatrix = rapi.CameraMatrixOriginf;
                prog.ProjectionMatrix = rapi.CurrentProjectionMatrix;
                rapi.RenderMultiTextureMesh(mesh, "tex");
                prog.Stop();
            }
            rapi.GlEnableCullFace();
        }

        void Scan(Vec3d around)
        {
            lureBobbers.Clear();
            HiddenBobbers.Clear();
            var found = capi.World.GetEntitiesAround(around, ScanRange, ScanRange, e => e is EntityBobber && e.Alive);
            foreach (var e in found)
            {
                var lure = LureOn((EntityBobber)e);
                if (lure == null) continue;
                lureBobbers[e.EntityId] = lure;
                HiddenBobbers.Add(e.EntityId);
            }
        }

        Item LureOn(EntityBobber bobber)
        {
            var bait = bobber.BaitStack ?? bobber.WatchedAttributes.GetItemstack("baitStack");
            if (bait == null) return null;
            if (bait.Collectible == null) bait.ResolveBlockOrItem(capi.World);
            return bait.Collectible is Item item && item.Attributes?["lureStyles"].Exists == true ? item : null;
        }

        MultiTextureMeshRef GetMesh(Item item)
        {
            string key = item.Code.ToString();
            if (meshes.TryGetValue(key, out var mesh)) return mesh;
            try
            {
                capi.Tesselator.TesselateItem(item, out MeshData data);
                mesh = capi.Render.UploadMultiTextureMesh(data);
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[AnglersArtOfFishing] Could not build lure model for {0}: {1}", key, e.Message);
                mesh = null;
            }
            meshes[key] = mesh;
            return mesh;
        }

        public void Dispose()
        {
            foreach (var m in meshes.Values) m?.Dispose();
            meshes.Clear();
            HiddenBobbers.Clear();
        }
    }
}
