# Unity Agent Bridge

**让 AI 或外部脚本直接操作 Unity 编辑器。**

一个装进 Unity 的编辑器插件：在本地开一个只监听 `127.0.0.1` 的 HTTP 服务，把「建场景、摆物体、加组件、改属性、进播放、看日志、截图、建资源、打包」全部变成可远程调用的 JSON 命令。Claude / DeepSeek / GPT 这类能在你机器上执行命令的 AI，通过一个 CLI 就能自己把游戏搭出来。

```
┌──────────────┐   HTTP + Token    ┌─────────────────────────────┐
│  AI / 脚本    │ ───────────────▶ │  Unity 编辑器               │
│ unityctl.ps1 │   127.0.0.1:17899 │  ┌───────────────────────┐  │
│ unityctl.py  │ ◀─────────────── │  │ UnityAgentBridge 插件  │  │
└──────────────┘      JSON         │  │ 路由 → 主线程调度      │  │
                                   │  │ 场景/物体/组件/播放…   │  │
                                   │  └───────────────────────┘  │
                                   └─────────────────────────────┘
```

---

> **已验证**：Unity 2022.3.62f3c1（Windows）。插件在真实编辑器里跑通了 17 项端到端命令测试（建物体、改组件、读日志、建材质、鉴权、批量等全部通过），`cs.execute` 也能编译执行并返回结果（`return 6*7;` → `42`），域重载后服务自动恢复。

## 特性

| 能力 | 说明 |
|---|---|
| 场景与层级 | 新建 / 打开 / 保存场景，读取完整层级树，Build Settings 场景列表 |
| 物体 | 创建（立方体/球/胶囊/平面/相机/灯光/空物体）、查找、改变换、改名、换父级、复制、删除、选中 |
| 组件 | 添加、移除、启停、读取序列化属性、写入序列化属性、调用公开方法 |
| 播放模式 | 进入 / 退出 / 暂停 / 单步，切换后自动重连 |
| 日志 | 环形缓冲 3000 条，支持增量拉取（`since`）、按类型过滤、编译错误诊断 |
| 截图 | Game View / 任意相机 / Scene 视图，直接返回 base64 PNG 或落盘 |
| 资源 | 搜索、建材质（自动识别 URP / 内置管线）、建预制体、实例化预制体、写脚本、移动/删除、刷新 |
| 游戏 | 一键搭可玩场景骨架、构建播放器（Win64 / WebGL / Android / iOS / macOS / Linux） |
| 动态 C# | `cs.execute` 把你的 C# 代码编译进编辑器执行，能返回任意值——这是「什么都能干」的兜底 |
| 控制面板 | `Tools → Unity Agent Bridge`：端口、令牌、启停、自检、实时请求日志 |
| 安全 | 只绑本机回环、令牌鉴权、危险能力可关、日志与令牌都放在 `Library/`（不进版本库） |

---

## 快速开始

### 1. 安装插件

把 `UnityAgentBridge` 整个文件夹拷进工程的 `Assets/` 即可（或用脚本）：

```powershell
# 安装到已有工程
.\tools\install-plugin.ps1 -ProjectPath "D:\MyGame"

# 作为本地 UPM 包安装（不污染 Assets）
.\tools\install-plugin.ps1 -ProjectPath "D:\MyGame" -AsPackage

# 连工程一起新建（自动找本机 Unity）
.\tools\new-unity-project.ps1 -Path "D:\MyGame"
```

Unity 2021.3+ 均可，已在 **Unity 2022.3.62f3c1** 上实际验证。

### 2. 打开控制面板

菜单 **Tools → Unity Agent Bridge**。编辑器启动时服务会自动开启，面板上能看到状态、端口和令牌。

### 3. 第一条命令

```powershell
cd D:\MyGame
.\client\unityctl.ps1 ping
.\client\unityctl.ps1 go.create -ArgsJson '{"type":"cube","name":"Hero","position":[0,1,0]}'
```

参数复杂（含引号、换行、C# 代码）时，把 JSON 写进文件更省事，避免命令行转义踩坑：

```powershell
'{"type":"cube","name":"Hero"}' | Set-Content args.json -Encoding utf8
.\client\unityctl.ps1 go.create -ArgsFile args.json
```

或 Python：

```bash
python client/unityctl.py ping
python client/unityctl.py go.create '{"type":"cube","name":"Hero"}'
```

或纯 curl：

```powershell
curl -s -X POST http://127.0.0.1:17899/cmd `
  -H "X-Bridge-Token: <你的令牌>" -H "Content-Type: application/json" `
  -d "{\"cmd\":\"hierarchy\",\"args\":{\"depth\":2}}"
```

---

## 服务端

### 配置

配置落在 `<工程>/Library/UnityAgentBridge/`（`Library` 默认不进版本库，避免令牌泄漏）：

| 文件 | 内容 |
|---|---|
| `settings.json` | 端口、是否自动启动、是否允许远程代码、令牌 |
| `token.txt` | 纯文本令牌，客户端自动读取 |
| `bridge.log` | 打开「详细日志」后的请求日志 |
| `jobs/*.result.json` | `cs.execute` 的执行结果 |
| `selftest.txt` | 自检报告 |

客户端按这个顺序找令牌：`--token` 参数 → `UNITY_BRIDGE_TOKEN` 环境变量 → `<工程>/Library/UnityAgentBridge/token.txt`。所以只要 `cd` 到工程目录，命令里不用带令牌。

### 无界面 / CI 下启动

批处理模式默认不启动服务（避免干扰 CI 构建）。需要时设环境变量：

```powershell
$env:UNITY_BRIDGE_AUTOSTART = "1"
Unity.exe -batchmode -projectPath "D:\MyGame" -logFile build.log   # 不加 -quit，服务会一直开着
```

此时可以用 `Tools → Unity Agent Bridge → 运行自检` 之外的纯外部方式驱动，适合自动化流水线。

### 生命周期

- 服务只在编辑器进程里存在，**关闭 Unity 即停止**。
- 脚本编译会触发域重载，服务会在重载后自动重启（端口不变）。客户端在那 1～2 秒内会连接失败，重试即可；`unityctl` 已经内置重试。
- 进入/退出播放模式同理：`editor.play` 会**立即返回** `scheduled`，之后轮询 `status` 直到 `isPlaying` 变化。
- 批处理模式（`-batchmode`）默认**不**自动启动，避免影响 CI。

---

## 协议

```
POST /cmd
X-Bridge-Token: <token>
Content-Type: application/json

{"cmd":"go.create","args":{"type":"cube"},"timeoutMs":60000}
```

响应：

```json
{"ok":true,"cmd":"go.create","ms":3,"result":{"id":-1234,"name":"Cube", ...}}
```

失败时 HTTP 仍为 200（传输层除外），body 为：

```json
{"ok":false,"cmd":"go.create","error":"找不到类型 Foo","code":"not_found"}
```

其他端点：

| 端点 | 说明 |
|---|---|
| `GET /` | 服务与工程信息 |
| `GET /ping` | 轻量健康检查（也走鉴权） |
| `GET /help` | 返回全部命令的参数说明 |

错误码：`bad_request` `bad_value` `not_found` `unknown_command` `unauthorized` `compiling` `timeout` `no_graphics` `unsupported` `exception`。

`timeoutMs` 是**主线程等待上限**：Unity 卡在编译、导入资源、模态对话框时，命令会以 `timeout` 失败而不是永久挂住。

---

## 命令一览

### 系统

| 命令 | 参数 | 说明 |
|---|---|---|
| `ping` | — | 健康检查 |
| `status` | — | 端口、场景、播放/编译状态、请求数 |
| `commands.list` | — | 全部命令及参数说明（也支持 `GET /help`） |
| `project.info` | — | 工程路径、产品名、版本、构建目标、场景列表 |
| `project.set` | `companyName? productName? version? defaultScreenWidth? defaultScreenHeight? fullScreenMode?` | 改 PlayerSettings |
| `batch` | `calls:[{cmd,args}] stopOnError?` | 一次请求串行执行多条命令 |
| `log.mark` | `message type?` | 在 Console 里打标记 |

### 场景

| 命令 | 参数 | 说明 |
|---|---|---|
| `scene.info` | — | 场景名、路径、是否未保存、根对象数 |
| `scene.new` | `name? save? empty?` | 新建场景，可顺手存到 `Assets/Scenes/` |
| `scene.open` | `path saveFirst?` | 打开场景（默认先自动保存当前场景，不弹对话框） |
| `scene.save` / `scene.saveAs` | `path?` | 保存 |
| `scene.list` | — | 工程内所有场景、是否在 Build Settings |
| `scene.buildSettings` | `scenes?` | 读/写 Build Settings 场景列表 |
| `scene.dirty` | — | 列出未保存的场景 |

### 物体

| 命令 | 参数 | 说明 |
|---|---|---|
| `hierarchy` | `depth?` | 层级树，`depth=0` 只看根 |
| `go.create` | `type name? parent? position? rotation? scale? tag? layer? lightType? color? intensity?` | `type`: `empty/cube/sphere/capsule/cylinder/plane/quad/camera/light` |
| `go.get` | `target includeChildren? depth?` | 变换、组件列表、预制体来源 |
| `go.find` | `name? tag? component? path? limit?` | 多条件查找 |
| `go.set` | `target name? active? tag? layer? parent? position? localPosition? rotation? localRotation? scale? localScale? static?` | 除变换外都走 Undo |
| `go.delete` | `target` | 删除（可撤销） |
| `go.duplicate` | `target name? offset?` | 复制 |
| `go.select` | `target? add?` | 同步编辑器选中状态 |
| `transform.lookAt` | `target point` | 朝向某点 |

**`target` 写法**：`#实例ID`（最快，`go.get` 会返回）、完整路径 `Root/Child`、对象名；留空或 `"sel"` 表示当前选中对象。

### 组件

| 命令 | 参数 | 说明 |
|---|---|---|
| `comp.list` | `target` | 对象上的组件 |
| `comp.add` | `target type allowDuplicates?` | 已存在则直接返回 |
| `comp.get` | `target type depth?` | 序列化属性列表（含 `path`） |
| `comp.set` | `target type field value` | 按 `path` 写；`field:"enabled"` 是快捷写法 |
| `comp.remove` | `target type` | 移除 |
| `comp.enabled` | `target type enabled` | 启停 |
| `comp.call` | `target type method args?` | 调公开实例方法 |

```powershell
# 例：把刚体质量改成 5，并读回来
.\client\unityctl.ps1 comp.set -ArgsJson '{"target":"Hero","type":"Rigidbody","field":"m_Mass","value":5}'
.\client\unityctl.ps1 comp.get -ArgsJson '{"target":"Hero","type":"Rigidbody"}'
```

### 播放模式与编辑器

| 命令 | 参数 | 说明 |
|---|---|---|
| `editor.play` / `editor.stop` | — | 切换播放模式，**立即返回**，客户端轮询 `status` |
| `editor.pause` | `paused?` | 暂停/继续 |
| `editor.step` | — | 单步 |
| `editor.select` / `editor.selection` | `target? add?` | 选中状态 |
| `editor.menu` | `path` | 执行任意编辑器菜单，如 `GameObject/3D Object/Cube` |
| `editor.undo` / `editor.redo` | — | 撤销/重做 |
| `editor.save` | — | `AssetDatabase.SaveAssets` |
| `editor.compile` | — | 请求重新编译 |
| `editor.compileErrors` | `count?` | 最近的脚本编译错误 |
| `editor.focus` | — | 把编辑器窗口切到前台 |

### 日志

| 命令 | 参数 | 说明 |
|---|---|---|
| `console.get` | `count? since? types? filter?` | 结构化日志，`since` 增量拉取 |
| `console.tail` | `count?` | 纯文本最近 N 条 |
| `console.clear` | — | 清空桥接层缓存 |

### 截图

| 命令 | 参数 | 说明 |
|---|---|---|
| `screenshot.game` | `width? height? path? inline?` | 播放模式用 `ScreenCapture`（含 UI）；编辑模式渲染主相机 |
| `screenshot.camera` | `camera? width? height? path? inline?` | 指定相机 |
| `screenshot.scene` | `width? height? path? inline?` | 当前 Scene 视图 |

给 `path` 就直接落盘（省带宽），给 `inline:true` 则返回 `base64`。

### 资源

| 命令 | 参数 | 说明 |
|---|---|---|
| `asset.find` | `filter? type? folder? limit?` | 搜索资源 |
| `asset.info` | `path` | 资源信息 |
| `asset.createMaterial` | `path color? shader? metallic? smoothness?` | 建材质，自动选 URP Lit / Standard |
| `asset.setMaterialProperty` | `path property value` | 改材质属性 |
| `asset.createPrefab` | `target path` | 场景对象存成预制体 |
| `asset.instantiatePrefab` | `path parent? position?` | 实例化（保留预制体连接） |
| `asset.createScript` | `path content` | 写 C# 脚本并刷新 |
| `asset.refresh` | `force?` | 刷新资源库 |
| `asset.delete` / `asset.move` | `path` / `from,to` | 删除 / 移动 |
| `asset.folders` | `path?` | 列目录 |

### 游戏与构建

| 命令 | 参数 | 说明 |
|---|---|---|
| `game.bootstrap` | `ground? light? camera? player?` | 一键生成可玩场景骨架 |
| `game.playOnce` | — | 保存场景 + 进播放 |
| `game.build` | `target? outputPath? scenes? development? allowDebugging?` | 构建播放器 |
| `game.buildTarget` | `target` | 切换构建目标 |

```powershell
.\client\unityctl.ps1 game.bootstrap
.\client\unityctl.ps1 game.build -ArgsJson '{"target":"win64","outputPath":"Builds/Win64/Game.exe"}' -TimeoutMs 900000
```

### 动态 C#（`cs.execute`）

把一段 C# 代码当作**方法体**执行，能 `return` 任意值（基础类型、Vector3、GameObject、List、Dictionary 都可以）。

```powershell
.\client\unityctl.ps1 cs.execute -ArgsJson '{"code":"var go = GameObject.Find(\"Hero\"); return go.transform.position;"}' -Wait
```

原理：插件把代码写进 `Assets/UnityAgentBridgeGenerated/DynamicJob_<id>.cs`，Unity 编译 + 域重载后由 `[InitializeOnLoadMethod]` 执行，结果写进 `Library/UnityAgentBridge/jobs/<id>.result.json`，然后脚本自动删除。

因为编译会重载域、杀掉当前 HTTP 连接，所以流程是**异步**的：

```
cs.execute  →  {"jobId":"j2026...","status":"scheduled"}
cs.status   →  {"status":"pending"}  或  {"status":"done","result":...}
```

CLI 的 `-Wait` / `--wait` 会帮你轮询。**代码写错时**编译会失败，用 `cs.status` 看 `errors`，再用 `cs.discard` 删掉坏文件即可恢复。

---

## 用 AI 从零做一个游戏

把仓库路径和令牌交给 AI，然后按下面的节奏推进即可（整个过程都是上面那些命令）：

1. **建工程 + 装插件**
   ```powershell
   .\tools\new-unity-project.ps1 -Path "D:\Games\CatRunner"
   ```
2. **搭骨架**：`game.bootstrap`，再用 `go.create` / `go.set` 摆关卡、`asset.createMaterial` 上色。
3. **写玩法**：`asset.createScript` 写 `PlayerController.cs`，`comp.add` 挂到物体上，`comp.set` 填字段。
4. **跑起来**：`editor.play`，`console.get` 看报错，`screenshot.game` 看画面。
5. **迭代**：改完 `asset.refresh`，再 `editor.play`；复杂逻辑用 `cs.execute` 一次性做完。
6. **出包**：`game.build`。

一个真实片段：

```powershell
$u = ".\client\unityctl.ps1"
& $u game.bootstrap
& $u go.create -ArgsJson '{"type":"capsule","name":"Player","position":[0,1,0],"tag":"Player"}'
& $u comp.add -ArgsJson '{"target":"Player","type":"Rigidbody"}'
& $u comp.set -ArgsJson '{"target":"Player","type":"Rigidbody","field":"m_Mass","value":1}'
& $u editor.play
Start-Sleep 5
& $u screenshot.game -ArgsJson '{"width":1280,"height":720,"path":"Shots/play.png"}'
& $u console.get -ArgsJson '{"types":["error","warning"],"count":20}'
```

建议给 AI 的第一条指令就说明：**先用 `commands.list` 摸清有哪些命令，拿不准就用 `cs.execute`**。

---

## 安全

- 只绑定 `127.0.0.1`，且用**独占**端口（不会和别的进程共享端口）。
- 除 `GET /`、`/ping`、`/help` 外，所有命令都要 `X-Bridge-Token`，令牌 32 位随机，存在 `Library/` 里。
- `cs.execute` 等于「在本机执行任意编辑器代码」，风险等同于有人操作你的 Unity。不放心就在面板里关掉 **Allow remote code**，其它命令照常可用。
- 不要把 `Library/UnityAgentBridge/token.txt` 提交到版本库（`Library` 本身默认就被忽略）。
- 需要暴露给局域网/远程时，请自己加反向代理和 TLS，不要直接把端口开到 `0.0.0.0`。

---

## 常见问题

**连不上（connection refused）**
Unity 没开、面板里服务没启动，或端口被改过。`unityctl.ps1 ping` 会提示具体端口。两个客户端都内置了重试，偶发的本机连接抖动会自动重试 3 次。

**命令行里的 JSON 引号被吃掉了**
Windows 下多层引号转义很容易出错，把参数写进文件用 `-ArgsFile`（PS）或 `--args-file`（Python）。

**`{"code":"compiling"}`**
Unity 正在编译脚本。等一两秒重试；批量脚本里建议先轮询 `status` 的 `isCompiling`。

**调用 `editor.play` 之后连接断了一下**
进入播放模式会重载域，服务随之中断并在 1～2 秒后自动恢复，重试即可。

**`cs.status` 一直是 `pending` 且 `compileFailed:true`**
`cs.execute` 的代码编译不过。看 `errors`，然后 `cs.discard {jobId}` 清掉坏脚本。

**截图是黑的 / 没有 UI**
编辑模式下用的是相机渲染，不含 Screen Space Overlay 的 UI；播放模式下走 `ScreenCapture`，才是最终画面。

**`-batchmode -nographics` 下截图失败**
没有图形设备时会返回 `no_graphics`，这是预期行为。

**端口被占用**
面板里换一个端口（默认 17899），客户端用 `-Port` 或 `--port` 跟着改；`settings.json` 会自动更新。

---

## 目录结构

```
unity-agent-bridge/
├─ UnityAgentBridge/            # 整个文件夹拷进 Assets/ 或 Packages/
│  ├─ package.json
│  └─ Editor/
│     ├─ UnityAgentBridge.Editor.asmdef
│     ├─ Core/                  # 服务、路由、JSON、主线程调度、对象/属性、动态代码
│     ├─ Commands/              # 各命令模块
│     └─ UI/                    # 控制面板
├─ client/
│  ├─ unityctl.ps1              # Windows 客户端（无依赖）
│  └─ unityctl.py               # 跨平台客户端（仅标准库）
└─ tools/
   ├─ install-plugin.ps1        # 装到已有工程
   ├─ new-unity-project.ps1     # 新建工程 + 装插件
   └─ smoke-test.ps1            # 对运行中的 Unity 做冒烟测试
```

## License

MIT
