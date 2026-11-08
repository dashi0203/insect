using Unity.Mathematics;

namespace Swarm.World
{
    /// <summary>地图指纹（FNV-1a 64）：用于验证“同一种子生成同一张地图”。</summary>
    public static class WorldHash
    {
        const ulong Offset = 14695981039346656037UL;
        const ulong Prime = 1099511628211UL;

        public static ulong Compute(EnvGrid env, ResourceField resources)
        {
            ulong h = Offset;
            for (int i = 0; i < env.Length; i++)
            {
                h = Mix(h, env.HeightLevel[i]);
                h = Mix(h, env.Flags[i]);
                h = Mix(h, env.CoverType[i]);
                h = Mix(h, (uint)math.round(env.TemperatureBase[i] * 100f));
                h = Mix(h, (uint)math.round(env.Humidity[i] * 1000f));
                float4 w = env.BiomeWeights[i];
                h = Mix(h, (uint)math.round(w.x * 255f) | ((uint)math.round(w.y * 255f) << 8)
                           | ((uint)math.round(w.z * 255f) << 16) | ((uint)math.round(w.w * 255f) << 24));
            }
            if (resources != null && resources.Nodes.IsCreated)
            {
                for (int n = 0; n < resources.Nodes.Length; n++)
                {
                    var node = resources.Nodes[n];
                    h = Mix(h, node.Def);
                    h = Mix(h, (uint)math.round(node.Position.x * 100f));
                    h = Mix(h, (uint)math.round(node.Position.y * 100f));
                }
            }
            return h;
        }

        static ulong Mix(ulong h, uint v)
        {
            for (int k = 0; k < 4; k++)
            {
                h ^= (v >> (k * 8)) & 0xFF;
                h *= Prime;
            }
            return h;
        }
    }
}
