using System.IO;
using System.Linq;
using Swarm.Core;
using Swarm.Data;
using Swarm.Rendering;
using Swarm.UI;
using Swarm.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UIElements;

namespace Swarm.EditorTools
{
    /// <summary>
    /// 生成 / 升级主场景 Assets/_Project/Scenes/Main.unity，以及配置资产和材质。
    /// 编译后自动：场景不存在 → 生成；场景里没有 GameRoot → 升级到 M1（去掉 M0 参考地面，加入世界）。
    /// 菜单：Swarm/Build Main Scene（重建），Swarm/Recreate Default Data（按 GDD 默认值重建配置资产）。
    /// </summary>
    [InitializeOnLoad]
    public static class M0SceneBuilder
    {
        const string ScenePath = "Assets/_Project/Scenes/Main.unity";
        const string DataRoot = "Assets/_Project/Data";
        const string ArtRoot = "Assets/_Project/Art";
        const string ThemePath = DataRoot + "/SwarmRuntimeTheme.tss";
        const string PanelSettingsPath = DataRoot + "/DebugPanelSettings.asset";
        const string BalancePath = DataRoot + "/GameBalance.asset";
        const string TerrainMatPath = ArtRoot + "/Terrain.mat";
        const string CoverMatPath = ArtRoot + "/GroundCover.mat";
        const string ResourceMatPath = ArtRoot + "/ResourceNode.mat";
        const string GameRootScript = "Assets/_Project/Scripts/Game/Core/GameRoot.cs";

        static M0SceneBuilder()
        {
            EditorApplication.delayCall += AutoRun;
        }

        static void AutoRun()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling) return;
            if (!File.Exists(ScenePath)) { Build(); return; }
            bool dataUpgraded = UpgradeData();
            string guid = AssetDatabase.AssetPathToGUID(GameRootScript);
            if (!string.IsNullOrEmpty(guid) && !File.ReadAllText(ScenePath).Contains(guid))
            {
                UpgradeToM1();
                return;
            }
            if (dataUpgraded) RewireScene();
            EnsurePropShader();
        }

        /// <summary>资源实体材质改用 Swarm/Prop（支持巨型植物悬停虚化）。</summary>
        static void EnsurePropShader()
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(ResourceMatPath);
            var shader = Shader.Find("Swarm/Prop");
            if (mat == null || shader == null || mat.shader == shader) return;
            mat.shader = shader;
            mat.enableInstancing = true;
            EditorUtility.SetDirty(mat);
            AssetDatabase.SaveAssets();
            Debug.Log("[Swarm] ResourceNode material switched to Swarm/Prop.");
        }

        /// <summary>
        /// 旧版配置资产升级到 DefaultContent.DataVersion：补上缺少的资源实体资产，
        /// 并把新增字段（占地、无限、采集速度、成簇、外观）设为 GDD 默认值。其他已调整的数值不动。
        /// </summary>
        static bool UpgradeData()
        {
            var balance = AssetDatabase.LoadAssetAtPath<GameBalance>(BalancePath);
            if (balance == null || balance.dataVersion >= DefaultContent.DataVersion) return false;

            var d = DefaultContent.Create();
            int from = balance.dataVersion;
            if (from < 3)
            {
                // v3 大改了地图尺寸、山脉和资源分布参数：整体换成新默认值
                EditorJsonUtility.FromJsonOverwrite(EditorJsonUtility.ToJson(d.balance), balance);
                balance.name = "GameBalance";
            }
            if (from >= 3 && from < 4)
            {
                // v4：资源分布改为规则地带，零散资源大减
                balance.ecoPatchesPer64 = d.balance.ecoPatchesPer64;
                balance.ecoPatchDensity = d.balance.ecoPatchDensity;
                balance.scatterDensity = d.balance.scatterDensity;
            }
            if (from >= 3 && from < 6)
            {
                // v6：普通生境变稀，密度集中到超级营养土
                balance.habitatStrength = d.balance.habitatStrength;
                // v5：植物改由生境驱动，随机斑块只给非植物资源
                balance.ecoPatchesPer64 = d.balance.ecoPatchesPer64;
            }
            var biomeById = LoadAll<BiomeDef>("Biomes").ToDictionary(x => x.id);
            var existing = LoadAll<ResourceNodeDef>("ResourceNodes").ToDictionary(x => x.id);
            int added = 0;
            for (int i = 0; i < d.resources.Length; i++)
            {
                var def = d.resources[i];
                if (existing.TryGetValue(def.id, out var asset))
                {
                    asset.infinite = def.infinite;
                    asset.harvestRatePerSecond = def.harvestRatePerSecond;
                    asset.footprint = def.footprint;
                    asset.densityPerChunk = def.densityPerChunk;
                    asset.clusterMin = def.clusterMin;
                    asset.clusterMax = def.clusterMax;
                    asset.shape = def.shape;
                    asset.size = def.size;
                    asset.sizeJitter = def.sizeJitter;
                    asset.color = def.color;
                    asset.secondaryColor = def.secondaryColor;
                    asset.moistureMin = def.moistureMin;
                    asset.moistureMax = def.moistureMax;
                    asset.fertilityMin = def.fertilityMin;
                    EditorUtility.SetDirty(asset);
                    continue;
                }
                for (int k = 0; k < def.biomes.Count; k++)
                {
                    var bw = def.biomes[k];
                    if (bw.biome != null && biomeById.TryGetValue(bw.biome.id, out var b)) bw.biome = b;
                    def.biomes[k] = bw;
                }
                Save(def, "ResourceNodes", $"{i}_{def.id}");
                added++;
            }
            balance.dataVersion = DefaultContent.DataVersion;
            EditorUtility.SetDirty(balance);
            AssetDatabase.SaveAssets();
            Debug.Log($"[Swarm] Config data upgraded to v{DefaultContent.DataVersion}: {added} resource defs added, existing defs got new fields.");
            return true;
        }

        static void RewireScene()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var wc = Object.FindFirstObjectByType<GameRoot>();
            if (wc == null) return;
            var so = new SerializedObject(wc);
            SetArray(so.FindProperty("regions"), LoadAll<RegionDef>("Regions"));
            SetArray(so.FindProperty("resourceDefs"), LoadAll<ResourceNodeDef>("ResourceNodes"));
            SetArray(so.FindProperty("coverDefs"), LoadAll<GroundCoverDef>("GroundCovers"));
            so.ApplyModifiedPropertiesWithoutUndo();
            EditorSceneManager.SaveScene(scene);
            Debug.Log("[Swarm] Main scene re-wired to current config assets.");
        }

        [MenuItem("Swarm/Build Main Scene")]
        public static void Build()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            var panelSettings = EnsurePanelSettings();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = new Color(0.45f, 0.47f, 0.5f);
            var lightGo = new GameObject("Directional Light");
            var light = lightGo.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.1f;
            light.shadows = LightShadows.Soft;
            lightGo.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            new GameObject("GameLoop").AddComponent<GameLoop>();

            var camGo = new GameObject("Main Camera") { tag = "MainCamera" };
            var cam = camGo.AddComponent<Camera>();
            cam.fieldOfView = 40f;
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 600f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.08f, 0.09f, 0.1f);
            camGo.AddComponent<AudioListener>();
            camGo.AddComponent<RtsCamera>();

            var uiGo = new GameObject("DebugPanel");
            uiGo.AddComponent<UIDocument>().panelSettings = panelSettings;
            uiGo.AddComponent<DebugPanel>();

            AddWorld();

            EditorSceneManager.SaveScene(scene, ScenePath);
            var others = EditorBuildSettings.scenes.Where(s => s.path != ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) }.Concat(others).ToArray();
            Debug.Log($"[Swarm] Main scene built: {ScenePath}");
        }

        static void UpgradeToM1()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            var ground = GameObject.Find("ReferenceGround");
            if (ground != null) Object.DestroyImmediate(ground);
            if (Object.FindFirstObjectByType<GameRoot>() == null) AddWorld();
            EditorSceneManager.SaveScene(scene);
            Debug.Log("[Swarm] Main scene upgraded to M1 (GameRoot added, reference ground removed).");
        }

        static void AddWorld()
        {
            var data = EnsureData();
            var go = new GameObject("World");
            var wc = go.AddComponent<GameRoot>();
            var so = new SerializedObject(wc);
            so.FindProperty("balance").objectReferenceValue = data.balance;
            SetArray(so.FindProperty("regions"), data.regions);
            SetArray(so.FindProperty("resourceDefs"), data.resources);
            SetArray(so.FindProperty("coverDefs"), data.covers);
            so.FindProperty("terrainMaterial").objectReferenceValue = EnsureMaterial(TerrainMatPath, "Swarm/Terrain", false);
            so.FindProperty("groundCoverMaterial").objectReferenceValue = EnsureMaterial(CoverMatPath, "Swarm/GroundCover", false);
            so.FindProperty("resourceMaterial").objectReferenceValue =
                EnsureMaterial(ResourceMatPath, "Swarm/Prop", true);
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        static void SetArray(SerializedProperty prop, Object[] items)
        {
            prop.arraySize = items.Length;
            for (int i = 0; i < items.Length; i++)
                prop.GetArrayElementAtIndex(i).objectReferenceValue = items[i];
        }

        // ---------------------------------------------------------------- config assets

        [MenuItem("Swarm/Recreate Default Data")]
        static void RecreateDefaultData()
        {
            if (!EditorUtility.DisplayDialog("Recreate default data",
                    "Overwrite all config assets under Assets/_Project/Data with GDD defaults?", "Overwrite", "Cancel"))
                return;
            foreach (var sub in new[] { "Biomes", "Regions", "ResourceNodes", "GroundCovers" })
                AssetDatabase.DeleteAsset($"{DataRoot}/{sub}");
            AssetDatabase.DeleteAsset(BalancePath);
            EnsureData();
            Debug.Log("[Swarm] Default data recreated. Rebuild the scene (Swarm/Build Main Scene) to rewire references.");
        }

        static DefaultContent.Set EnsureData()
        {
            if (AssetDatabase.LoadAssetAtPath<GameBalance>(BalancePath) == null)
            {
                var d = DefaultContent.Create();
                foreach (var b in d.biomes) Save(b, "Biomes", b.id);
                for (int i = 0; i < d.regions.Length; i++) Save(d.regions[i], "Regions", $"{i}_{d.regions[i].id}");
                for (int i = 0; i < d.resources.Length; i++) Save(d.resources[i], "ResourceNodes", $"{i}_{d.resources[i].id}");
                for (int i = 0; i < d.covers.Length; i++) Save(d.covers[i], "GroundCovers", $"{i}_{d.covers[i].id}");
                AssetDatabase.CreateAsset(d.balance, BalancePath);
                AssetDatabase.SaveAssets();
                Debug.Log("[Swarm] Config assets created from GDD defaults in " + DataRoot);
            }

            return new DefaultContent.Set
            {
                balance = AssetDatabase.LoadAssetAtPath<GameBalance>(BalancePath),
                regions = LoadAll<RegionDef>("Regions"),
                resources = LoadAll<ResourceNodeDef>("ResourceNodes"),
                covers = LoadAll<GroundCoverDef>("GroundCovers"),
                biomes = LoadAll<BiomeDef>("Biomes"),
            };
        }

        static void Save(ScriptableObject obj, string folder, string file)
        {
            EnsureFolder(DataRoot, folder);
            AssetDatabase.CreateAsset(obj, $"{DataRoot}/{folder}/{file}.asset");
        }

        static T[] LoadAll<T>(string folder) where T : Object
        {
            string dir = $"{DataRoot}/{folder}";
            if (!AssetDatabase.IsValidFolder(dir)) return new T[0];
            return AssetDatabase.FindAssets($"t:{typeof(T).Name}", new[] { dir })
                .Select(AssetDatabase.GUIDToAssetPath)
                .OrderBy(SortKey)
                .ThenBy(p => p, System.StringComparer.Ordinal)
                .Select(AssetDatabase.LoadAssetAtPath<T>)
                .Where(a => a != null)
                .ToArray();
        }

        /// <summary>按文件名的数字前缀排序（"10_x" 排在 "9_x" 之后）。</summary>
        static int SortKey(string path)
        {
            string file = Path.GetFileNameWithoutExtension(path);
            int u = file.IndexOf('_');
            return u > 0 && int.TryParse(file.Substring(0, u), out int n) ? n : int.MaxValue;
        }

        static void EnsureFolder(string parent, string name)
        {
            if (!AssetDatabase.IsValidFolder($"{parent}/{name}")) AssetDatabase.CreateFolder(parent, name);
        }

        static Material EnsureMaterial(string path, string shaderName, bool instancing)
        {
            var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat != null) return mat;
            var shader = Shader.Find(shaderName);
            if (shader == null)
            {
                Debug.LogError($"[Swarm] Shader not found: {shaderName}");
                shader = Shader.Find("Universal Render Pipeline/Unlit");
            }
            mat = new Material(shader) { enableInstancing = instancing };
            AssetDatabase.CreateAsset(mat, path);
            return mat;
        }

        static PanelSettings EnsurePanelSettings()
        {
            if (!File.Exists(ThemePath))
            {
                File.WriteAllText(ThemePath, "@import url(\"unity-theme://default\");\n");
                AssetDatabase.ImportAsset(ThemePath);
            }
            var theme = AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(ThemePath);
            var ps = AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelSettingsPath);
            if (ps == null)
            {
                ps = ScriptableObject.CreateInstance<PanelSettings>();
                AssetDatabase.CreateAsset(ps, PanelSettingsPath);
            }
            ps.themeStyleSheet = theme;
            ps.scaleMode = PanelScaleMode.ConstantPhysicalSize;
            EditorUtility.SetDirty(ps);
            AssetDatabase.SaveAssets();
            return ps;
        }
    }
}
