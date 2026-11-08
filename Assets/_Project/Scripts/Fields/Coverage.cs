using System;
using System.Collections.Generic;
using Swarm.Buildings;
using Unity.Collections;
using Unity.Mathematics;

namespace Swarm.Fields
{
    /// <summary>
    /// 覆盖范围（GDD 4.4）= 巢心、信息素节点、哨所的圆形半径并集。建筑变化时立即重算。
    /// 笔刷只能在覆盖内绘制；覆盖内玩家通道不衰减；敌对虫巢和人类设施只在覆盖外生成。
    /// </summary>
    public sealed class CoverageMap : IDisposable
    {
        public readonly int Size;
        public NativeArray<byte> Covered;
        public int CoveredTiles { get; private set; }

        public CoverageMap(int size)
        {
            Size = size;
            Covered = new NativeArray<byte>(size * size, Allocator.Persistent);
        }

        public bool IsCovered(int x, int y) =>
            (uint)x < (uint)Size && (uint)y < (uint)Size && Covered[y * Size + x] != 0;

        public void Recompute(IReadOnlyList<Building> buildings)
        {
            for (int i = 0; i < Covered.Length; i++) Covered[i] = 0;
            foreach (var bld in buildings)
            {
                float r = bld.CoverageRadius;
                if (r <= 0f) continue;
                float2 c = new float2(bld.Tile) + 0.5f;
                int ri = (int)math.ceil(r);
                for (int dy = -ri; dy <= ri; dy++)
                for (int dx = -ri; dx <= ri; dx++)
                {
                    int x = bld.Tile.x + dx, y = bld.Tile.y + dy;
                    if ((uint)x >= (uint)Size || (uint)y >= (uint)Size) continue;
                    if (math.distancesq(new float2(x + 0.5f, y + 0.5f), c) <= r * r) Covered[y * Size + x] = 1;
                }
            }
            int n = 0;
            for (int i = 0; i < Covered.Length; i++) n += Covered[i];
            CoveredTiles = n;
        }

        public void Dispose()
        {
            if (Covered.IsCreated) Covered.Dispose();
        }
    }
}
