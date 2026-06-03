# PocketStrata（口袋地层）

在同一世界坐标保存建筑结构，并在「真实地形」与「口袋子世界」之间切换。支持单人存档与多人联机：进入者与旁观者可在同一片区域看到不同的方块布局。

**版本**：0.1  
**作者**：Mornia-Cherry  
**依赖**：ArknightsMod

---

## 功能概览

| 能力 | 说明 |
|------|------|
| 结构采集 | 手持「地形采集记录器」框选区域，保存为 `.pstrata` 文件 |
| 快速放置 | **F9** 在玩家脚下应用当前选中的结构 |
| 子世界会话 | 在指定矩形区域内用口袋结构替换真实地形（服务端为权威） |
| 多人视角 | **进入者**看到并改口袋地形；**旁观者**仍看到真实地形 |
| 自动还原 | 单人退出存档前、多人全部进入者离线后会尝试写回真实地形 |

---

## 快速开始

### 1. 采集并保存结构

1. 制作或获取 **地形采集记录器**。
2. 手持记录器：
   - **左键拖拽**：框选地形；
   - **右键**：切换「与（增加选区）/ 非（减少选区）」模式。
3. 在聊天栏执行：

```text
/ps save <结构名称>
```

结构文件保存在：

```text
Documents/My Games/Terraria/tModLoader/ModData/PocketStrata/Structures/
```

### 2. 选择结构并放置

```text
/ps list              # 列出已保存结构
/ps select <名称>     # 设为 F9 使用的结构
/ps apply <名称>      # 等同于 select 后按 F9
```

在合适位置按 **F9**，会在脚下以玩家脚底为锚点放置结构（多人时由服务端分块同步）。

### 3. 关闭子世界

- 再次按 **F9**（若当前已开启会话）可请求关闭（多人会发关闭请求到服务端）。
- 或使用调试命令 `/pocketview session off`（见下文）。

---

## 聊天命令 `/ps`

| 命令 | 作用 |
|------|------|
| `/ps save <名称>` | 将当前框选保存为 `.pstrata` |
| `/ps list` | 列出结构库中的名称 |
| `/ps select <名称>` | 选中结构，供 F9 使用 |
| `/ps apply <名称>` | 选中并应用（同 F9） |
| `/ps clear` | 清空当前框选 |
| `/ps folder` | 在聊天中显示结构目录路径 |
| `/ps messages` / `/ps msg` | 开关屏幕提示（聊天回复不受影响） |

---

## 调试命令 `/pocketview`

面向服主或单机调试，用于不依赖 `.pstrata` 文件直接划定会话区。

```text
/pocketview session on [宽] [高]   # 在脚下开启口袋会话（默认约 32×24 格）
/pocketview session off          # 关闭会话并尝试还原真实地形
/pocketview insider on           # 将当前玩家设为「进入者」
/pocketview insider off          # 取消进入者，恢复旁观者视角
```

**多人说明**：

- 客户端执行 `session on/off` 会向服务端发送请求包。
- 成为进入者后，服务端会下发口袋 chunk、真实备份 chunk，并在适当时机通过 `SendTileSquare` 同步地形。
- 其他玩家需自行执行 `/pocketview insider on` 才能看到口袋结构（并可交互）。

---

## 多人联机原理（简述）

- **服务端**在会话矩形内的 `Main.tile` 保存口袋结构，为权威数据。
- **进入者客户端**通过分块包重建 `_pocketTiles`，再写入本地 `Main.tile` 显示口袋建筑。
- **旁观者**不写入口袋地形；`SendSection` 钩子会对其发送真实备份外观。
- 非进入者无法在会话区内放置/破坏方块（服务端拦截相关网络包）。

所有玩家需使用**相同版本**的本模组，会话同步包内含结构名称等字段。

---

## 从源码构建

1. 将本目录放入 tModLoader 的 `ModSources` 文件夹。
2. **先关闭游戏内 tModLoader**，或在游戏内禁用本模组后再编译（否则可能出现 TML003 文件占用错误）。
3. 在项目目录执行：

```powershell
dotnet build
```

或在 tModLoader 模组开发环境中直接 Build/Rebuild。

---

## 项目结构

```text
PocketStrata/
├── Content/
│   ├── Commands/          # /ps、/pocketview
│   ├── IO/                # .pstrata 读写与结构库
│   ├── Items/             # 地形采集记录器
│   ├── Players/           # 框选、进入者视角 ModPlayer
│   └── Systems/           # 会话、分块网络、地形同步
├── Localization/          # 中/英本地化
├── PocketStrata.cs          # 网络包分发
└── README.md
```

---

## 常见问题

**F9 后看不见建筑？（多人）**

- 确认已执行 `/pocketview insider on` 或你是开启会话的触发玩家。
- 等待聊天出现「子世界已生成」类提示；大结构需数秒完成分块同步。
- 全员模组版本一致。

**单人退出后地形被写进存档？**

- 模组会在「保存并退出」前尝试还原未关闭的会话区；仍建议关闭会话后再存档。

**编译提示 TML003？**

- 关闭 Terraria/tModLoader 或禁用模组后重新 build。

---

## 许可与说明

本仓库为 tModLoader 模组源码。`description.txt` 为工坊简介；详细玩法以游戏内提示与上述命令为准。

若与 ArknightsMod 一同使用，请确保两个模组均已启用且版本兼容。
