# 地形与生态生成规则（WorldGen Rules）

> 整理自当前代码（资产数据版本 **v6**，`DefaultContent.DataVersion = 6`）。
> 用途：改规则时有据可查；也作为移植到其他项目（如草药师 / 炼金题材）的参考。
> 数值以 `GameBalance` / `DefaultContent` 的**默认值**为准；实际运行值以 `Assets/_Project/Data` 下的资产为准。

## 0. 文件索引

| 文件 | 内容 |
|---|---|
| `Assets/_Project/Scripts/Sim/World/WorldGen.cs` | 地形生成主流程（基础场 → 高山 → 悬崖坡道 → 水 → 连通性 → 派生场 → 肥力 → 超级营养土） |
| `Assets/_Project/Scripts/Sim/World/Biomes.cs` | 群系权重（高斯核归一化）、群系偏好投影、地表混色 |
| `Assets/_Project/Scripts/Sim/World/ResourceField.cs` | 资源实体放置（两遍）、生态地带、1 Hz 再生 / 枯死 / 播种 |
| `Assets/_Project/Scripts/Sim/World/GroundCover.cs` | 地被元胞自动机 |
| `Assets/_Project/Scripts/Sim/World/Seasons.cs` | 季节时钟与温度曲线 |
| `Assets/_Project/Scripts/Sim/World/TerrainHeights.cs` | 渲染用几何高度（悬崖、坡道、高山） |
| `Assets/_Project/Scripts/Sim/World/EnvGrid.cs` | 每 Tile 的环境数据 |
| `Assets/_Project/Scripts/Sim/Data/GameBalance.cs` | 全部可调参数 |
| `Assets/_Project/Scripts/Sim/Data/DefaultContent.cs` | 区域 / 群系 / 资源 / 地被的默认内容 |

**确定性**：同一种子 + 同一区域 → 同一张图。地形、资源、地被各用独立随机流（`RandomStreams.WorldGen / Resources / GroundCover`）。

地图默认 `384 × 384` Tile；地形渲染分块 32，资源索引分块 16。

---

## 1. 地形生成（`WorldGen.Generate`，按执行顺序）

### 1.1 基础场（Burst 并行 `BaseFieldsJob`）

每 Tile 计算 fBm Perlin（`octaves = 4`，每层频率 ×2、振幅 ×0.5，结果 × `noiseContrast = 1.6` 后夹到 [-1, 1]）：

| 场 | 公式 | 频率 |
|---|---|---|
| 温度 | `region.baseTemperature + n × temperatureOffset(4°C)` | `noiseFrequency = 0.02` |
| 湿度 | `saturate(region.baseHumidity + n × humidityOffset(0.2))` | 0.02 |
| 高度噪声 | `n × 0.5 + 0.5`（0..1） | 0.02 |
| 土壤原始噪声 | n（-1..1，稍后换算成含水量） | `soilNoiseFrequency = 0.04` |
| 肥力原始噪声 | n（另一偏移） | `fertilityNoiseFrequency = 0.035` |
| 酸度 | `saturate(region.baseAcidity + 土壤噪声 × 0.05)` | — |

### 1.2 群系权重（`Biomes.Weights`）

- 把温度归一化到 `[temperatureNormMin, temperatureNormMax] = [-10, 30]°C`，与湿度组成二维平面。
- 区域内最多 4 个群系，各有一个（温度, 湿度）中心。
- `w_i = exp(−d_i² / σ²)`，`σ = biomeSigma = 0.15`，再归一化 → `float4` 权重。
- 若全部权重极小，退化为最近群系的 one-hot。
- 结果：群系之间平滑过渡，无硬边界。资源 / 地被的群系偏好用 `dot(weights, affinity)` 求。

### 1.3 高山（硬障碍：地面与空中单位都不可通过）

- 山脊噪声：3 层 `(1 − |n|)²`，基础频率 `mountainFrequency = 0.007`。
- 低频遮罩：`smoothstep(maskTh − 0.15, maskTh + 0.25, n(频率 × 0.45))`，`mountainMaskThreshold = −0.1` → 山只在部分区域连成山脉。
- 地图边缘山带：`border = saturate(1 − 离边距离 / (borderMountainWidth(14) × wobble))`，`wobble = 0.55 + 0.45 × n(×0.06)`，宽度起伏，不是整齐的墙。
- `mountain = max(ridge × mask, border)`；`> mountainThreshold(0.74)` 为山核。
- 圆形膨胀 `mountainDilate = 2`，把相近山段连起来。
- 删除小于 `minMountainTiles = 80` 的山包。

### 1.4 悬崖与坡道

- `高度噪声 > cliffThreshold(0.62)` 且非山 → 悬崖（HeightLevel = 1，高一层）。
- 删除小于 `minCliffRegionTiles = 12` 的悬崖块。
- 每块悬崖的坡道数 = `clamp(崖边数 / rampEdgeTilesPerRamp(40), 1, maxRampsPerCliff(4))`。
- 坡道宽 `rampWidthMin..Max = 2..3`，纵深 2 格（崖边 + 内侧一格）。
- **只放在笔直崖边**：整段宽度及两端各 1 格都必须是同向崖边（外侧平地、内侧悬崖、都不是坡道 / 高山），否则换位置重试（最多 30 次）。避免锯齿轮廓上出现碎坡道。

### 1.5 水面

- 条件：平地、非山、`高度噪声 < waterThreshold(0.3)`、`湿度 > waterMinHumidity(0.6)`、四邻不接触坡道。
- 删除小于 `minWaterRegionTiles = 6` 的水塘。

### 1.6 连通性修复（`EnsureConnectivity`）

- 地面可走区域（悬崖算可走：有坡道 / 攀爬）做连通分量，最大者为主区。
- 其他区域 `< minPocketTiles(300)` → 填成山。
- 较大区域 → BFS 找到主区的最短路径，沿路径挖一条 `passWidth = 4` 宽的山口（清除山与水）。
- 循环直到只剩一个连通区（最多 64 轮）。

### 1.7 派生场

- **到水距离** `WaterDistance`：四邻 BFS（0 = 水，最大 255）。
- **山体深度** `ObstacleDepth`：到最近非山 Tile 的距离（渲染：山脚平台、岩石顶面、黑影）。
- **土壤含水量**（干旱图）：
  ```
  水 Tile = 1
  其他 = saturate(湿度 × soilHumidityWeight(0.6)
                 + 土壤噪声 × soilNoiseAmplitude(0.4)
                 + 水边加成 × 1.5      // 离水 ≤3：(4 − d) × 0.07
                 − 崖顶 0.1)
  ```
  湿度定基调，中频噪声做出干湿交错的斑块。

### 1.8 肥力（营养土）

```
水 / 山 = 0
其他 = saturate(0.5 + 肥力噪声 × 0.5 × fertilityContrast(1.6)
               + 水边加成        // 离水 ≤3：(4 − d) × 0.06
               − 崖顶 0.2)
```
高对比度让肥沃区和贫瘠区界线清楚。

### 1.9 超级营养土（肥力 > 1）

- 数量 = `round(Tile 数 / 96² × superFertilePer96(0.8))`（384 图约 13 个）。
- 中心：可走、非悬崖；以 `概率 = 当前肥力` 接受 → 越肥越容易成为热点。
- 半径 `superFertileRadiusMin..Max = 6..12`。
- 边缘 `edge = r × (0.75 + 0.45 × n(×0.18))`，强度 `s = 1 − smoothstep(0.25·edge, edge, d)`。
- `肥力 = max(原肥力, lerp(原肥力, superFertilePeak(1.5), s))`，中心为 1.5。

---

## 2. 生态与资源分布（`ResourceField`）

### 2.1 两遍放置

1. **巨型实体**（`footprint > 1`，占 2–3 格，阻挡地面单位）
   - 占地内：非水、同一高度层；占地及**外圈 1 格**：非山、非坡道、未被其他巨型占用 → 防止堵住山口和坡道。
   - 得分取占地内平均。
2. 以已放置的**巨型植物**为中心生成林下带（`groveRadius = 7`，强度 `1 − smoothstep(0.35r, r, d)`）。
3. **普通实体**，成簇出现：
   - 簇中心通过接受采样后，再在 0.35–1.25 格半径内撒 `clusterMin..clusterMax − 1` 个。
   - 植物在超级营养土上：额外数量 × `(1 + 超级程度)`，最多翻倍。

每个资源区块（16×16）的期望簇数 = `densityPerChunk × Σ(得分 × 密度) / Tile 数`，小数部分按概率取整；每簇最多尝试 40 次，用 `rand × maxScore < 得分` 接受。

### 2.2 得分

```
Score = dot(群系权重, 资源群系偏好)
      + (悬崖 ? cliffAffinity : 0)
      + (离水 ≤ 2 ? waterEdgeAffinity : 0)
水 / 山 / 已被巨型占用 → 0
```

### 2.3 密度倍率

```
Density = scatterDensity(0.06) + ecoPatchDensity(7) × 地带强度
```
地带外几乎不出资源（零散 0.06），资源因此成片成群。

**地带强度按资源类型取**（多个地带取 max）：

| 资源 | 地带 |
|---|---|
| 植物 | **生境** × `habitatStrength(0.45)` × 肥力缩放 |
| 植物 & 喜水（`waterEdgeAffinity > 0`） | + 水边植被带 × `waterBeltStrength(1)` |
| 普通植物（非巨型） | + 林下带 × `groveStrength(0.9)` × 生境（下限 0.5） |
| 非植物 | 随机生态斑块 |
| 非植物 & 喜水 | + 水边植被带 |
| 矿物 | + 崖脚碎石带 × `cliffFootStrength(0.7)` |

**植物生境**（`Habitat`，0..1，边界用 `soft = 0.08` 的 smoothstep 柔化）：
```
wet  = smoothstep(mMin − 0.08, mMin + 0.08, 含水量) × (1 − smoothstep(mMax − 0.08, mMax + 0.08, 含水量))
fert = smoothstep(fMin − 0.08, fMin + 0.12, 肥力)
Habitat = max(floor, wet × fert)
```
- 肥力缩放：普通土 × `saturate(肥力)`；超级营养土 × `(1 + 超级程度 × superFertileDensityBoost(2.5))`。
- 超级程度 = `saturate((肥力 − 1) / (peak − 1))`；在超级营养土上生境下限 `floor = 超级程度 × 0.8`（要求放宽）。

**水边植被带**：离水 1..`waterBeltWidth(4)` 格，`saturate((1 − (d − 1)/4) × wobble × 1.15)`，`wobble = 0.75 + 0.25 n(×0.2)`，越近越密。

**崖脚碎石带**：平地到最近悬崖 / 高山的切比雪夫距离 ≤ `cliffFootWidth(2)`，强度 `1 − (d − 1)/(W + 1)`。

**随机生态斑块**：数量 `Tile 数 / 64² × ecoPatchesPer64(0.5)`；中心离水 1–5 格时权重 + `ecoPatchWaterBias(3)`；半径 5..12；边缘 `r × (0.8 + 0.4 n(×0.15))`，强度 `1 − smoothstep(0.45·edge, edge, d)`。

### 2.4 运行时（1 Hz `Tick`）

**再生速度**（`ResourceRules.RegrowRate`，0 = 停止）：
- 非 Timer 模式 / 不在 `regrowSeasons` / 湿度 < `minHumidity` → 0。
- 非植物：`1 − 0.5 × 毒性`。
- 植物：
  ```
  温度因子 = 温度 < coldStopTemperature(5°C) ? (stopsInCold ? 0 : 0.5) : 1
  rate = 温度因子 × (0.5 + 0.5 × 含水量) × (1 − 0.5 × 酸度) × (1 − 毒性)
  ```
- 计时累加 `dt × rate`，达到 `regrowSeconds` 时补满。

**毒性枯死**：植物所在 Tile 毒性 > `plantToxicDeathThreshold(0.7)` 持续 `plantToxicDeathSeconds(30s)` → 枯死（`Alive = 0`，位置保留）。

**重新播种**：每 `reseedIntervalSeconds(60s)`，每区块平均毒性 < `reseedToxicityThreshold(0.2)` 时复活该区块一株枯死植物（原位，储量从 0 开始再生）。

**秋季**：进入秋季时，`AutumnRefresh` 模式的资源（落叶堆）补满。

---

## 3. 地被（`GroundCoverField`，元胞自动机）

**适宜度**：温度、湿度在 [min, max] 外 → 0；否则 `dot(群系权重, 偏好) + (悬崖 ? cliffAffinity : 0)`，≥ `suitabilityThreshold(0.35)` 为适宜。

**初始化**：每 Tile 取适宜度最高的地被；
`覆盖度 = saturate((适宜度 − 阈值) × 1.5 + (斑块噪声(×0.08, 0..1) − 0.45) × 1.4)`，≤ 0.02 不放。

**每步**（`coverIntervalSeconds = 5s`，Burst）：
- 水 / 山 → 清空。
- 已有地被：适宜且 8 邻有同类 → `+coverGrow(0.05)`；不适宜 → `−coverShrink(0.05)`；再 `−毒性 × coverToxicityFactor(0.1)`；降到 0 清空。
- 空 Tile：8 邻中覆盖度 ≥ `coverDrawThreshold(0.3)` 最多的那种地被，若此处适宜且毒性扣减 < 增长 → 以 `coverGrow` 进驻。

---

## 4. 季节（`SeasonClock`）

- 一年 `yearLength = 1200s`；冬季占 `region.winterFraction`，春夏秋平分其余。顺序：春 → 夏 → 秋 → 冬，每局从春季开始。
- 温度偏移：盛夏（夏季中点）`+A`，隆冬（冬季中点）`−A`，`A = seasonAmplitude = 10°C`；降温段与升温段长度可不同，各用半个余弦平滑过渡。
- 相关阈值：`coldStopTemperature = 5°C`（植物停长）、`freezeTemperature = 0°C`。

---

## 5. 默认内容（`DefaultContent`）

### 5.1 区域

| 区域 | 基准温度 | 基准湿度 | 冬季比例 | 基准酸度 | 群系（中心温度, 湿度） |
|---|---|---|---|---|---|
| 腐殖森林 `humus_forest` | 18°C | 0.75 | 0.20 | 0.05 | 腐殖林地 (17, 0.79)、菌类沼泽 (20, 0.92)、灌木草地 (21, 0.58)、岩石高地 (14, 0.62) |
| 酸雨湿地 `acid_wetland` | 20°C | 0.90 | 0.15 | 0.20 | 苇荡浅沼 (21, 0.98)、菌类沼泽 (19, 0.82)、河岸林 (17, 0.90) |
| 北境冻原 `northern_tundra` | 4°C | 0.45 | 0.40 | 0.03 | 苔原 (2, 0.40)、针叶林缘 (7, 0.55)、岩石高地 (5, 0.30) |

酸雨湿地每 6 分钟一次酸雨；远雾间隔分别为 8 / 12 / 16 分钟。

### 5.2 群系（8 个）

腐殖林地、菌类沼泽、灌木草地、岩石高地、苇荡浅沼、河岸林、苔原、针叶林缘。

### 5.3 资源（13 种，5 种巨型）

| 资源 | 类型 | 储量 / 再生 | 群系偏好 | 生境（含水量, 最低肥力） | 其他 |
|---|---|---|---|---|---|
| 菌簇 | 蛋白 | 25 / 60s | 菌沼 1, 腐殖 0.6 | 0.65–1, 0.55 | 湿度 ≥ 0.5；簇 3–6 |
| 草籽丛 | 蛋白 | 15 / 90s | 灌草 1, 苔原 0.8 | 0.12–0.58, 0.25 | 仅春夏再生；簇 3–7 |
| 针叶落籽 | 蛋白 | 20 / 180s | 针叶 1 | 0.25–0.75, 0.35 | 寒冷不停长；簇 2–4 |
| 蕨丛 | 建材 | 40 / 120s | 腐殖 1, 河岸 1 | 0.52–1, 0.5 | 春夏秋；簇 2–5 |
| 芦苇 | 建材 | 50 / 150s | 苇荡 1 | 0.78–1, 0.2 | 春夏秋；水边 0.6；簇 4–8 |
| 落叶堆 | 建材 | 20 / 秋季刷新 | 腐殖 1, 河岸 1, 针叶 0.6 | 0.4–0.9, 0.55 | |
| 矿物露头 | 矿物 | 120 / 不再生 | 岩石 1 | — | 悬崖 0.8；簇 2–4 |
| 盐渍泥 | 矿物 | 40 / 300s | 苇荡 0.6, 菌沼 0.3, 河岸 0.3 | — | 水边 1.0 |
| **巨型树蕨** | 建材 | 无限 | 腐殖 1, 河岸 0.8 | 0.58–1, 0.6 | 3×3 |
| **古松** | 建材 | 无限 | 针叶 1, 苔原 0.3 | 0.28–0.8, 0.45 | 3×3 |
| **巨苇丛** | 建材 | 无限 | 苇荡 1 | 0.8–1, 0.2 | 2×2；水边 0.5 |
| **巨菌** | 蛋白 | 400 / 600s | 菌沼 1 | 0.7–1, 0.6 | 2×2；湿度 ≥ 0.5 |
| **巨型矿脉** | 矿物 | 600 / 不再生 | 岩石 1, 苔原 0.3 | — | 2×2；悬崖 0.6 |

### 5.4 地被（3 种）

| 地被 | 温度 | 湿度 | 群系偏好 | 其他 |
|---|---|---|---|---|
| 苔藓 | 0–28°C | 0.55–1 | 腐殖 1, 河岸 1, 针叶 0.8, 苇荡 0.3 | |
| 地衣 | −30–16°C | 0–0.7 | 苔原 1, 岩石 0.8, 针叶 0.4 | 悬崖 +0.5 |
| 菌毯 | 5–32°C | 0.7–1 | 菌沼 1 | |

---

## 6. 版本历史（`DataVersion`）

- v1：M1 基础世界生成
- v2：巨型 / 无限资源、成簇、组合外观、高山
- v3：384 地图、大山脉、生态斑块、巨型比例
- v4：规则地带、零散资源大减、簇更大
- v5：肥力 / 干旱图驱动的植物生境
- v6：超级营养土、植物密度按肥力缩放

---

## 7. 移植备注（草药师 / 炼金题材）

这套规则与"虫群"玩法的耦合很少，生态底层基本可以原样复用：

- **可直接复用**：第 1 节全部地形生成、第 2.2–2.3 节的得分与地带、植物生境、第 3 节地被、第 4 节季节。
- **需要替换**：资源类型（`ResourceKind`：蛋白 / 建材 / 矿物 → 草药 / 矿石 / 菌类等炼金材料）、采集方（工虫 → 采药人）、信息素 / 巢穴相关参数。
- **天然契合的设计点**：
  - 生境（含水量 + 肥力）→ 每种草药有自己的产地，喜湿草药长在水边，耐旱草药长在干燥斑块。
  - 超级营养土热点 → 稀有草药产地，需要探索；一簇数量翻倍。
  - 季节再生窗口 + 寒冷停长 → 草药的采摘季。
  - 毒性枯死 / 低毒重新播种 → 污染、净化类玩法。
  - 地被（苔藓、地衣、菌毯）→ 可作为低阶采集物或环境指示。
- **依赖**：Unity.Mathematics（`noise.cnoise`、`math`）、Unity.Collections、Unity.Burst / Jobs。`EnvGrid` 是所有场的容器，移植时一并带走。
