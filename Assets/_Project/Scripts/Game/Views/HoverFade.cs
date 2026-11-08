using Swarm.Input;
using UnityEngine;

namespace Swarm.Views
{
    /// <summary>
    /// 鼠标悬停虚化：射线先与巨型实体的圆柱求交，再与地形求交，取较近者。
    /// 命中巨型实体 → 该实体虚化；命中高山或山脚 1 Tile 内 → 光标附近山体虚化。结果写入 Shader 全局量。
    /// </summary>
    public sealed class HoverFade
    {
        static readonly int FadeId = Shader.PropertyToID("_SwarmFade");
        static readonly int FadeRadiusId = Shader.PropertyToID("_SwarmFadeRadius");
        static readonly int PropFadeId = Shader.PropertyToID("_SwarmPropFade");
        static readonly int PropFadeRadiusId = Shader.PropertyToID("_SwarmPropFadeRadius");

        readonly WorldPicker _picker;
        readonly int _mapSize;
        readonly float _mountainFadeRadius;

        Vector3 _fadeCenter, _propCenter;
        float _fadeAmount, _propAmount, _propRadius = 4f;

        public HoverFade(WorldPicker picker, int mapSize, float mountainFadeRadius)
        {
            _picker = picker;
            _mapSize = mapSize;
            _mountainFadeRadius = mountainFadeRadius;
        }

        public void Update(Camera cam)
        {
            float mountainTarget = 0f, propTarget = 0f;
            if (cam != null && SwarmInput.HasPointer && !SwarmInput.PointerOverUi)
            {
                var ray = cam.ScreenPointToRay(SwarmInput.PointerPosition);
                bool terrainHit = _picker.PickTerrain(ray, out var hit);
                float terrainT = terrainHit ? Vector3.Distance(ray.origin, hit) : float.MaxValue;
                int giant = _picker.PickGiant(ray, out float giantT);

                if (giant >= 0 && giantT <= terrainT + 0.01f)
                {
                    var g = _picker.Giants[giant];
                    propTarget = 1f;
                    _propCenter = new Vector3(g.Center.x, g.Ground, g.Center.y);
                    _propRadius = g.Radius;
                }
                else if (terrainHit)
                {
                    int x = Mathf.Clamp((int)hit.x, 0, _mapSize - 1), y = Mathf.Clamp((int)hit.z, 0, _mapSize - 1);
                    if (_picker.NearMountain(x, y))
                    {
                        mountainTarget = 1f;
                        _fadeCenter = _fadeAmount < 0.01f
                            ? hit
                            : Vector3.Lerp(_fadeCenter, hit, 1f - Mathf.Exp(-20f * Time.unscaledDeltaTime));
                    }
                }
            }
            float k = Time.unscaledDeltaTime * 4f;
            _fadeAmount = Mathf.MoveTowards(_fadeAmount, mountainTarget, k);
            _propAmount = Mathf.MoveTowards(_propAmount, propTarget, k);
            Shader.SetGlobalVector(FadeId, new Vector4(_fadeCenter.x, _fadeCenter.y, _fadeCenter.z, _fadeAmount));
            Shader.SetGlobalFloat(FadeRadiusId, _mountainFadeRadius);
            Shader.SetGlobalVector(PropFadeId, new Vector4(_propCenter.x, _propCenter.y, _propCenter.z, _propAmount));
            Shader.SetGlobalFloat(PropFadeRadiusId, _propRadius);
        }
    }
}
