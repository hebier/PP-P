# PP-PATCH · 补丁仓库内容

> **2026-11-05 22:25 定案：这是一个普通新建的「公开空仓库」，不是 Fork。**
> 仓库里**只有补丁本身**（8 个文件）。上游全量源码由 workflow 每次运行时临时 clone 到
> GitHub runner 的临时目录，编译完即弃。所以不需要 Fork、不需要上游 git 历史、
> 不需要 `Sync fork`、不需要在本地 clone 上游改 csproj／插钩子。
>
> 状态：**未经编译验证**。本机无 .NET SDK；首次编译在 GitHub Actions 上完成。
> 锚点与类型名按 `v0.0.95` 缓存源码对齐，实际 tag 不同可能有偏差（见第 5 节）。

---

## 1. 仓库里到底有什么（就这 8 个文件）

```
.github/workflows/pp-patch-build-on-demand.yml   ← 唯一 workflow，只响应手动 Run workflow
tools/apply-patch.ps1                            ← 注入脚本：复制 .cs + 改 csproj + 插 3 处钩子
README.md                                        ← 本文件
src/ExpressPackingMonitoring/
   ├─ Config/AppConfig.Patch.cs                  ← 借 partial class 挂配置，旧 AppConfig.cs 一字不改
   └─ Services/
      ├─ DirectCaptureRecordingService.cs       实时主路（dshow + -c copy + 60s 滚动段 + 预览旁路）
      ├─ ClipByOrderService.cs                 按单 -c copy 裁切（即时回看入口）
      ├─ MjpegArchiveService.cs               闲时 QSV→H.264 + 烧单号时间（可让路调度）
      └─ PpPatchRuntime.cs                    全局胶水 + 启动哨兵日志
```

**新建仓库时把这 8 个文件按原路径放进去即可，一个多余文件都不用加。**
绝对路径 `D:\智能体\发货\补丁源码\` 与仓库根一一对应。

> **压缩包怎么放**：`PackingProof-补丁仓库-上传包.zip` 解压出来就是上面这 8 个文件，
> **解压得到的那一层 = GitHub 仓库根目录**，不要再套一层文件夹。
> `.github\`、`src\`、`tools\` 三个目录和 `README.md` 必须与 `.git` 同级；
> 其中 `.github\workflows\` 里那个 yml 必须在**默认分支**上，Actions 才会出现 Run workflow 按钮。
> 新建仓库时**不要**勾 "Add a README file"（会同名冲突）。详细步骤见包外的 `上传说明-补丁仓库.txt`。

5 个 `.cs` 全部自包含：不引用 `MainViewModel` / `ScanRecord` / `VideoDatabase`
等任何上游业务类型，只依赖 .NET BCL 与已确认存在的 `AppConfig` / `AppPaths` / `RuntimeLog`。

---

## 2. 一次 Run workflow 做了什么

| 步 | 动作 |
|---|---|
| 1 | `checkout` 本仓库（只有这 8 个文件，`fetch-depth: 1`） |
| 2 | `git ls-remote --tags --refs` 上游 → 筛 `vX.Y.Z` → 取**最新正式 tag**；留空即可，填了就校验；`main` 拒绝 |
| 3 | `git clone --depth 1 --branch <tag>` 上游到 runner 的临时 `upstream/` 目录 |
| 4 | `apply-patch.ps1` 复制 5 个 `.cs`、注入 `csproj` 5 行、注入 3 处 `// PP-PATCH` 钩子 |
| 5 | `Patch sentinel` 独立复核（防注入脚本被改松） |
| 6 | `setup-dotnet`（读 `upstream/global.json`）→ `restore` → `build -c Release` |
| 7 | 上传 `ExpressPackingMonitoring.dll` + `BUILD_INFO.txt` |

**不是**自动跑（只有 `workflow_dispatch`）、**不是**写回任何仓库（`contents: read`）、
**不是**打安装包（不 publish、不跑 test）。产物就是一个 dll，替换目标机那一个文件。

---

## 3. `apply-patch.ps1` 注入了什么

**① `csproj` +5 行**（项目 `EnableDefaultCompileItems=false`，漏了 build 会「成功」但代码不在程序集里）：

```xml
<Compile Include="Config\AppConfig.Patch.cs" />
<Compile Include="Services\DirectCaptureRecordingService.cs" />
<Compile Include="Services\ClipByOrderService.cs" />
<Compile Include="Services\MjpegArchiveService.cs" />
<Compile Include="Services\PpPatchRuntime.cs" />
```

**② 三处入口钩子**（`MainViewModel.Camera.cs` / `MainViewModel.Recording.cs` 原本就
`using ExpressPackingMonitoring.Services;`，脚本会检查，缺了自动补）：

| 入口 | 注入的短路逻辑 |
|---|---|
| `private void StartCamera()` | `if (Config.Patch?.IsDirect != false) { PpPatchRuntime.Start(Config); return; }` |
| `private async Task InternalStartRecordingAsync()` | `if (PpPatchRuntime.DirectEnabled) { PpPatchRuntime.BeginOrder(CurrentOrderId ?? ""); return; }` |
| `private async Task InternalStopRecordingAsync()` | `if (PpPatchRuntime.DirectEnabled) { PpPatchRuntime.EndOrder(); return; }` |

> **修正了一处旧稿错误**：起录用 `CurrentOrderId`（`MainViewModel.cs:612`），
> **不是** `_recordingOrderId` —— 后者在原方法 693 行才赋值，方法开头拿到的是上一单或 null，
> 会让 `BeginOrder("")` 被忽略。`Config`（`MainViewModel.cs:650`）是现有属性。

`StartCamera` 短路后不再启动 AForge `VideoCaptureDevice` —— UVC 摄像头同一刻只允许
一个进程打开，常驻 FFmpeg 与 AForge 物理互斥。旧录制代码**一行不删**，
`config.json` 设 `Patch.VideoPipelineMode=Legacy` 即可完整退回原链路。

**③ 严格锚点（关键设计）**：脚本用正则匹配「方法声明行 + 紧随的 `{` 行」，
匹配不到就 **throw，CI 变红、不出产物**。绝不「尽力而为」地插到别处 ——
插错位置的钩子会编出一个能跑、却静默退回 332 MB/s 卡顿链路的 dll，那是最难查的失败。
上游改了方法签名时，把 CI 日志发我改锚点正则即可，不要放宽检查。

---

## 4. 已知缺口（**不装懂，列出给你**）

| 项 | 说明 |
|---|---|
| **DB / 订单列表 / 语音播报** | 起停钩子 `return` 会跳过原有订单 UI 与 DB 写入，需要在 `PpPatchRuntime.OrderClipReady` 订阅里补回。第一批未接，需按实际 tag 的 `VideoDatabase` / `AddRecord` API 补一个订阅类。不影响「2K30 不卡、不丢、录完整」这个一票否决项，但**上线前必须补** |
| **预览显示接线** | `PreviewFrame` 事件已就绪，但还没人订阅 ⇒ 预览窗口会黑。需用现有 `HandleCameraFrame(Mat)` / `WriteableBitmap`（原地写 + `Freeze()`）接上，`ReducedColor2` 解 1280×720@30（默认档） |
| **优雅退出** | `PpPatchRuntime.Stop()` 尚未注入（退出处锚点未核实）。不调不丢已写完的段，但常驻 ffmpeg 子进程会残留、当前段尾部可能不完整 |
| **`strftime` 是否按 UTC 命名** | 已给子进程设 `TZ=UTC`；目标机那份 4.4.1 ffmpeg 需现场确认 `roll_*.mkv` 文件名是 UTC。若是本地时间，按单裁切的时间偏移会算错 |
| **自带 ffmpeg 能力** | 主路只需 `dshow`+`segment`+`copy`（已实测）；离线 `drawtext`/`h264_qsv` 缺失时代码自动回退 `libx264`，也可在 `ArchiveFfmpegPath` 指向完整版 exe |
| **编译** | 本机无 .NET SDK，5 个 `.cs` 与注入脚本都**未编译验证**。第一次 CI 若有签名偏差，把日志发我修，属正常成本 |

---

## 5. 相比「Fork + git merge」砍掉了什么

| 砍掉 | 原因 |
|---|---|
| Fork 按钮、上游全量 git 历史、全部 tag | 上游源码每次 `git clone` 临时拉取，跑完即弃；仓库里只需要补丁 |
| `Sync fork` / `Update branch` 陷阱、「落后 N 个提交」噪音 | 根本不存在 upstream 关联，没有可点的按钮 |
| 禁用上游自带 `CI` / `发布包构建` 两个 workflow | 新建仓库里只有我们自己那一个 yml |
| Fork 的 Actions 默认禁用 | 新建仓库 Actions 默认可用 |
| **本地 clone → 复制 .cs → 改 csproj → 插钩子 → 接事件 → 提 commit** | 全部由 `apply-patch.ps1` 在 runner 里做，用户一个文件都不用改 |

**换来的代价（要认）**：补丁从「git commit 三路合并自动适配上游」变成
「脚本按锚点注入」。上游改了那 3 个方法签名 → 锚点找不到 → **显式 CI 变红** → 发我日志改锚点。
不是静默失败，修法与原来同级；只是不再由 git 自动适配。

---

## 6. 你的动作（三步）

1. 目标机完成官方更新（拉最新正式版）。
2. 紧接着去 GitHub 点一次 **Run workflow**，`upstream_ref` **留空**。
3. 下载 artifact → 关掉 PackingProof → 覆盖 `app\ExpressPackingMonitoring.dll` → 启动。

`BUILD_INFO.txt` 自动记录 tag / commit / SHA256，出问题发我即可，平时不用打开。
「最新」会漂移：官方更新与跑 workflow 之间隔了几天、期间上游又发版，就先让目标机再更新一次。

---

## 7. 第一批不做（保住「只换一个 dll」）

`SettingsWindow.xaml(.cs)` 的「预览模式」下拉、`Resources/Strings*.resx` 三语词条
（改 resx 会生成卫星程序集 `zh-Hans\...resources.dll`）。档位先用 `config.json` 的
`Patch.PreviewMode` 或代码默认值控制，功能不缺，只是暂时没 UI 开关。

---

## 8. 验收（不用额外安排测试，正常打包顺带看）

1. 启动日志出现 `PP-PATCH: Direct pipeline active` —— 没有这行就是静默退回 Legacy。
2. `pp-raw\` 里持续出现 `roll_2026....Z.mkv`，每 60 秒一段，2K30 不卡。
3. 一单结束后 1–3 秒出现 `单号.mkv`，能播、约 45 秒、头尾不缺。
4. 打包中无转码进程；闲 5 分钟后转出 H.264（同路径），约 35 MB/45 秒，画面有单号时间。
5. 连续打包时滚动段不会撑满磁盘（≥48 h 保留 + 队列积压告警）。
