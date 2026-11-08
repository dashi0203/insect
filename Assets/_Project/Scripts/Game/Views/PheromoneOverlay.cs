using System;
using Swarm.Core;
using Swarm.Data;
using Swarm.Fields;
using Swarm.UI;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Swarm.Views
{
    /// <summary>
    /// 热力图叠加层（GDD 3.2 / 14.4）：把信息素场打包成三张 RGBAHalf 纹理交给地形 Shader。
    /// 场每 5 Hz 更新一次，这里最多每帧上传一次（MarkDirty → LateUpload）。
    /// 叠加层索引 0–8 = 信息素通道，9 = 归巢场，10 = 覆盖范围。
    /// </summary>
    public sealed class PheromoneOverlay : IDisposable
    {
        public const int Count = 11;
        public const int Homing = 9, CoverageIndex = 10;

        static readonly int HeatAId = Shader.PropertyToID("_SwarmHeatA");
        static readonly int HeatBId = Shader.PropertyToID("_SwarmHeatB");
        static readonly int HeatCId = Shader.PropertyToID("_SwarmHeatC");
        static readonly int MaskAId = Shader.PropertyToID("_SwarmHeatMaskA");
        static readonly int MaskBId = Shader.PropertyToID("_SwarmHeatMaskB");
        static readonly int MaskCId = Shader.PropertyToID("_SwarmHeatMaskC");
        static readonly int ColorsId = Shader.PropertyToID("_SwarmHeatColors");
        static readonly int MapSizeId = Shader.PropertyToID("_SwarmMapSize");
        static readonly int ShowCoverageId = Shader.PropertyToID("_SwarmShowCoverage");
        static readonly int CoverDimId = Shader.PropertyToID("_SwarmCoverDim");

        readonly SimulationWorld _world;
        readonly Texture2D _texA, _texB, _texC;
        NativeArray<half4> _packA, _packB, _packC;
        float4 _invA, _invB;
        float _invDanger;
        bool _dirty = true;

        readonly bool[] _show = new bool[Count];

        /// <summary>各叠加层的显示颜色（命令卡图例也用它）。</summary>
        public Color[] Colors { get; }

        public PheromoneOverlay(SimulationWorld world)
        {
            _world = world;
            int size = world.Env.Size, n = world.Env.Length;
            _packA = new NativeArray<half4>(n, Allocator.Persistent);
            _packB = new NativeArray<half4>(n, Allocator.Persistent);
            _packC = new NativeArray<half4>(n, Allocator.Persistent);
            _texA = MakeTexture(size, "HeatA");
            _texB = MakeTexture(size, "HeatB");
            _texC = MakeTexture(size, "HeatC");
            Shader.SetGlobalTexture(HeatAId, _texA);
            Shader.SetGlobalTexture(HeatBId, _texB);
            Shader.SetGlobalTexture(HeatCId, _texC);
            Shader.SetGlobalFloat(MapSizeId, size);
            Colors = BuildColors(world.Balance, out _invA, out _invB, out _invDanger);
            var vecs = new Vector4[12];
            for (int k = 0; k < Count; k++) vecs[k] = Colors[k];
            Shader.SetGlobalVectorArray(ColorsId, vecs);
        }

        static Texture2D MakeTexture(int size, string name) =>
            new Texture2D(size, size, TextureFormat.RGBAHalf, false, true)
            {
                name = name,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };

        static Color[] BuildColors(GameBalance b, out float4 invA, out float4 invB, out float invDanger)
        {
            var channels = b.pheromoneChannels ?? GameBalance.DefaultPheromoneChannels();
            float Inv(PheromoneChannel c)
            {
                foreach (var s in channels) if (s.channel == c) return 1f / math.max(0.01f, s.displayScale);
                return 1f;
            }
            Color Col(PheromoneChannel c)
            {
                foreach (var s in channels) if (s.channel == c) return s.color;
                return Color.white;
            }
            invA = new float4(Inv(PheromoneChannel.Gather), Inv(PheromoneChannel.Rally), Inv(PheromoneChannel.Avoid), Inv(PheromoneChannel.Trail));
            invB = new float4(Inv(PheromoneChannel.Alarm), Inv(PheromoneChannel.ScentProtein), Inv(PheromoneChannel.ScentMineral), Inv(PheromoneChannel.ScentBuild));
            invDanger = Inv(PheromoneChannel.Danger);

            var colors = new Color[Count];
            for (int c = 0; c < Pheromones.ChannelCount; c++) colors[c] = Col((PheromoneChannel)c);
            colors[Homing] = new Color(0.25f, 0.55f, 1f);
            colors[CoverageIndex] = new Color(0.4f, 1f, 0.9f);
            return colors;
        }

        public void MarkDirty() => _dirty = true;

        /// <summary>场有变化时打包并上传纹理（每帧最多一次）。</summary>
        public void UploadIfDirty()
        {
            if (!_dirty) return;
            _dirty = false;
            _world.Pheromones.Pack(_packA, _packB, _packC, _world.Coverage.Covered, _invA, _invB, _invDanger,
                _world.Balance.homingDisplayRange);
            _texA.SetPixelData(_packA, 0); _texA.Apply(false);
            _texB.SetPixelData(_packB, 0); _texB.Apply(false);
            _texC.SetPixelData(_packC, 0); _texC.Apply(false);
        }

        /// <summary>按玩家开关（命令卡）+ 当前笔刷（选中笔刷时自动显示对应通道和覆盖边界）写入 Shader 掩码。</summary>
        public void PushGlobals(BrushTool tool, bool[] toggled)
        {
            for (int k = 0; k < Count; k++) _show[k] = toggled != null && k < toggled.Length && toggled[k];
            if (tool == BrushTool.Gather) _show[0] = true;
            if (tool == BrushTool.Rally) _show[1] = true;
            if (tool == BrushTool.Avoid) _show[2] = true;
            if (tool == BrushTool.Erase) _show[0] = _show[1] = _show[2] = true;
            if (tool != BrushTool.None) _show[CoverageIndex] = true;

            float F(bool v) => v ? 1f : 0f;
            Shader.SetGlobalVector(MaskAId, new Vector4(F(_show[0]), F(_show[1]), F(_show[2]), F(_show[3])));
            Shader.SetGlobalVector(MaskBId, new Vector4(F(_show[4]), F(_show[5]), F(_show[6]), F(_show[7])));
            Shader.SetGlobalVector(MaskCId, new Vector4(F(_show[8]), 0f, F(_show[Homing]), 0f));
            Shader.SetGlobalFloat(ShowCoverageId, F(_show[CoverageIndex]));

            bool any = false;
            for (int k = 0; k < CoverageIndex; k++) any |= _show[k];
            Shader.SetGlobalFloat(CoverDimId, any ? 1f : 0f);
        }

        public void Dispose()
        {
            if (_packA.IsCreated) _packA.Dispose();
            if (_packB.IsCreated) _packB.Dispose();
            if (_packC.IsCreated) _packC.Dispose();
            if (_texA != null) Object.Destroy(_texA);
            if (_texB != null) Object.Destroy(_texB);
            if (_texC != null) Object.Destroy(_texC);
        }
    }
}
