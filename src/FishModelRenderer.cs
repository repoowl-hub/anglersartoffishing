using System;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace AnglersArtOfFishing
{
    /// <summary>
    /// Draws the hooked species' own vanilla model during a fight. The shape and texture come straight from the fish
    /// entity type (already loaded and baked into the entity atlas on the client), so modded fish work too.
    /// Rendered in the opaque pass: below the surface the translucent water shows it as a dark shape, and it only
    /// fully appears when it jumps or is brought up to the bank.
    /// </summary>
    public class FishModelRenderer : IRenderer
    {
        /// <summary>Vanilla fish shapes face -X; this turns that onto the yaw convention (yaw 0 = +Z).</summary>
        const float YawOffset = GameMath.PIHALF;

        readonly ICoreClientAPI capi;
        readonly Matrixf modelMat = new Matrixf();

        MeshRef mesh;
        int textureId;
        float scale = 1f;
        /// <summary>Mouth position in mesh space (blocks); the model hangs from it.</summary>
        Vec3f mouth = new Vec3f(0.5f, 0.25f, 0.5f);

        /// <summary>World position of the fish's mouth, i.e. the hook.</summary>
        public readonly Vec3d Pos = new Vec3d();
        public float Yaw, Pitch, Roll;
        public bool Loaded => mesh != null;

        public double RenderOrder => 0.4;
        public int RenderRange => 64;

        public FishModelRenderer(ICoreClientAPI capi)
        {
            this.capi = capi;
        }

        /// <summary>Tessellates the model for the given fish entity code. Returns false (and draws nothing) if it can't.</summary>
        public bool Load(string entityCode)
        {
            Unload();
            if (string.IsNullOrEmpty(entityCode)) return false;

            try
            {
                var props = capi.World.GetEntityType(new AssetLocation(entityCode));
                var client = props?.Client;
                if (client == null) return false;

                Shape shape = client.LoadedShape;
                if (shape == null && client.Shape?.Base != null)
                {
                    shape = Shape.TryGet(capi, client.Shape.Base.Clone().WithPathPrefixOnce("shapes/").WithPathAppendixOnce(".json"));
                }

                var tex = client.FirstTexture;
                if (shape == null || tex == null) return false;
                if (tex.Baked == null)
                {
                    tex = tex.Clone();
                    tex.Bake(capi.Assets);
                }

                AssetLocation texPath = tex.Baked?.BakedName ?? tex.Base;
                if (texPath == null || !capi.EntityTextureAtlas.GetOrInsertTexture(texPath, out _, out var texPos) || texPos == null) return false;

                capi.Tesselator.TesselateShape("anglersartoffishing-fish", shape, out MeshData meshData, new SingleTexSource(capi.EntityTextureAtlas, texPos), null, 0, 0, 0, null, null);
                mouth = FindMouth(meshData);
                mesh = capi.Render.UploadMesh(meshData);
                textureId = capi.EntityTextureAtlas.AtlasTextures[texPos.atlasNumber].TextureId;
                scale = client.Size > 0 ? client.Size : 1f;
                return true;
            }
            catch (Exception e)
            {
                capi.Logger.Warning("[AnglersArtOfFishing] Could not build fish model for {0}: {1}", entityCode, e.Message);
                Unload();
                return false;
            }
        }

        public void Unload()
        {
            mesh?.Dispose();
            mesh = null;
        }

        public void OnRenderFrame(float dt, EnumRenderStage stage)
        {
            if (mesh == null) return;
            var camPos = capi.World.Player?.Entity?.CameraPos;
            if (camPos == null) return;

            var rapi = capi.Render;
            rapi.GlDisableCullFace();

            var prog = rapi.PreparedStandardShader((int)Pos.X, (int)Pos.Y, (int)Pos.Z);
            prog.Tex2D = textureId;
            prog.ModelMatrix = modelMat
                .Identity()
                .Translate(Pos.X - camPos.X, Pos.Y - camPos.Y, Pos.Z - camPos.Z)
                .RotateY(Yaw + YawOffset)
                .RotateZ(-Pitch)          // nose is -X, so a positive Z turn would dip it
                .RotateX(Roll)
                .Scale(scale, scale, scale)
                .Translate(-mouth.X, -mouth.Y, -mouth.Z)
                .Values;
            prog.ViewMatrix = rapi.CameraMatrixOriginf;
            prog.ProjectionMatrix = rapi.CurrentProjectionMatrix;
            rapi.RenderMesh(mesh);
            prog.Stop();

            rapi.GlEnableCullFace();
        }

        public void Dispose() => Unload();

        /// <summary>
        /// Where the hook sits. Fish shapes face -X; walking back from the nose, the mouth is the first slice where the
        /// body reaches 35% of its full height, which skips thin bills and whiskers (a marlin's sword). Works on the
        /// tessellated quads, so element rotations are already applied.
        /// </summary>
        static Vec3f FindMouth(MeshData m)
        {
            const int Bins = 32;
            const float HeightShare = 0.35f;
            float[] xyz = m.xyz;
            int quads = m.VerticesCount / 4;
            if (quads == 0) return new Vec3f(0.5f, 0.25f, 0.5f);

            float minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            for (int v = 0; v < quads * 4; v++)
            {
                minX = Math.Min(minX, xyz[v * 3]); maxX = Math.Max(maxX, xyz[v * 3]);
                minZ = Math.Min(minZ, xyz[v * 3 + 2]); maxZ = Math.Max(maxZ, xyz[v * 3 + 2]);
            }
            float width = Math.Max(1e-4f, (maxX - minX) / Bins);

            var lo = new float[Bins];
            var hi = new float[Bins];
            for (int i = 0; i < Bins; i++) { lo[i] = float.MaxValue; hi[i] = float.MinValue; }

            for (int q = 0; q < quads; q++)
            {
                float qx0 = float.MaxValue, qx1 = float.MinValue, qy0 = float.MaxValue, qy1 = float.MinValue;
                for (int v = q * 4; v < q * 4 + 4; v++)
                {
                    qx0 = Math.Min(qx0, xyz[v * 3]); qx1 = Math.Max(qx1, xyz[v * 3]);
                    qy0 = Math.Min(qy0, xyz[v * 3 + 1]); qy1 = Math.Max(qy1, xyz[v * 3 + 1]);
                }
                int i0 = Math.Clamp((int)((qx0 - minX) / width), 0, Bins - 1);
                int i1 = Math.Clamp((int)((qx1 - minX) / width), 0, Bins - 1);
                for (int i = i0; i <= i1; i++)
                {
                    lo[i] = Math.Min(lo[i], qy0);
                    hi[i] = Math.Max(hi[i], qy1);
                }
            }

            float maxHeight = 0f;
            for (int i = 0; i < Bins; i++) maxHeight = Math.Max(maxHeight, hi[i] - lo[i]);
            for (int i = 0; i < Bins; i++)
            {
                if (hi[i] - lo[i] >= HeightShare * maxHeight)
                {
                    return new Vec3f(minX + i * width, (lo[i] + hi[i]) / 2f, (minZ + maxZ) / 2f);
                }
            }
            return new Vec3f(minX, 0.25f, (minZ + maxZ) / 2f);
        }

        /// <summary>Maps every texture code in the shape to the fish's one entity texture.</summary>
        class SingleTexSource : ITexPositionSource
        {
            readonly ITextureAtlasAPI atlas;
            readonly TextureAtlasPosition pos;

            public SingleTexSource(ITextureAtlasAPI atlas, TextureAtlasPosition pos)
            {
                this.atlas = atlas;
                this.pos = pos;
            }

            public TextureAtlasPosition this[string textureCode] => pos;
            public Size2i AtlasSize => atlas.Size;
        }
    }
}
