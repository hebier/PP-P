# PP-PATCH · 操作手册

> 这个仓库只做一件事：**你点一次 Run workflow，它产出一个 `ExpressPackingMonitoring.dll`，你拿去覆盖目标机上那一个文件。**
> 照着下面做就行，不需要懂代码、不需要装 .NET、不需要 Fork 上游。

> **2026-11-06 · v3（补齐预览 + 设置选项）**
> 上一版被指出两处没做完：**预览窗口黑、设置里没有选项**。这一版都补上了：
> 预览帧已接到 WPF 画面；设置窗口会多出一个「补丁 / Patch」页签，可以切预览画质、切回官方原版链路。
> **如果你已经传过 v2：解压新 zip，把同名文件全部覆盖，另外新增的 2 个 `.cs` 也一起放进去，commit 后重跑即可。不要删任何文件。**

---

## 1. 第一次配置（只做一次）

### 1.1 建仓库

GitHub 右上角 `+` → **New repository**：

| 项 | 怎么选 |
|---|---|
| Owner | 你自己 |
| Repository name | 随意，例如 `pp-patch` |
| Public / Private | **Public**（Actions 免费、不计分钟） |
| Add a README file | **不要勾** |
| Add .gitignore | **不要勾** |
| Choose a license | **不要勾** |

**不要 Fork 上游**，这个仓库跟上游没有关联，也不用点 `Sync fork`。

### 1.2 把文件放进去

解压 `PackingProof-补丁仓库-上传包.zip`，**解压出来的那一层就是仓库根目录**，别再套一层文件夹：

```
仓库根/
├─ .github/workflows/pp-patch-build-on-demand.yml
├─ tools/apply-patch.ps1
├─ README.md
└─ src/ExpressPackingMonitoring/
   ├─ Config/    AppConfig.Patch.cs
   ├─ Services/  DirectCaptureRecordingService.cs, ClipByOrderService.cs,
   │             MjpegArchiveService.cs, PpPatchRuntime.cs
   ├─ ViewModels/ MainViewModel.Patch.cs      ← v3 新增：预览接线
   └─ UI/        SettingsWindow.Patch.cs     ← v3 新增：设置页签
```

- **网页方式**：仓库页 → `Add file` → `Upload files` → 把 `.github\`、`src\`、`tools\` 三个文件夹和 `README.md` 一起拖进去（文件夹整体拖，别拆开）→ 填 commit 标题 → `Commit changes`。
- **命令行方式（更稳）**：`git clone` 你的仓库 → 把 zip 内容解压进去（与 `.git` 同级）→ `git add -A` → `git commit -m "PP-PATCH v3"` → `git push`。

**验收**：仓库根目录能看到上面 **10 个文件**，`.github/workflows/` 里有那个 yml，且它在**默认分支**（通常是 `main`）上。

### 1.3 确认按钮在

`Actions` 页 → 左侧出现 `pp-patch-build-on-demand` → 右侧有 `Run workflow`。
看不到就检查：yml 是不是在默认分支、Actions 是否被禁用、是不是多套了一层文件夹。

---

## 2. 每次要出新 dll（三步，连着做完）

| 步 | 动作 |
|---|---|
| **1** | **目标机先完成官方更新**（软件自己拉最新正式版）。什么都不用记。 |
| **2** | 紧接着去 GitHub：`Actions` → `pp-patch-build-on-demand` → `Run workflow` → `upstream_ref` **留空** → 点绿色按钮。等 3–10 分钟。 |
| **3** | 下载 artifact `pp-patched-dll-<版本号>`（里面有 dll 和 `BUILD_INFO.txt`）→ **完全退出 PackingProof** → 覆盖 `app\ExpressPackingMonitoring.dll` → 从原启动器启动。 |

**三步要连着做完**：「最新正式版」是会变的，官方更新完隔几天才跑、期间上游又发了新版，就会编出新版 dll 配旧 `app\`，启动直接报错。真拖久了，先让目标机再更新一次就行。

`BUILD_INFO.txt` 不用打开，出问题发我即可。

### 装好后应该看到什么

1. **预览窗口有画面** —— 直采模式下摄像头被常驻 FFmpeg 独占，预览走旁路 JPEG 解码，不再依赖原来的 AForge 链路。
2. **设置窗口最后一页多一个「补丁 / Patch」页签** —— 里面有两项：
   - **预览画质**：流畅 1280×720@30（默认）／均衡 1280×720@15／省资源 640×360@15。**改完立刻生效**，只影响预览小图，录像始终是原始 2K30。
   - **录像链路**：直采（推荐）／原版 Legacy。切到「原版」就是退回官方那条卡顿链路，**需重启软件生效**，用于对比排障。
   - 页签底部有当前状态：链路、预览档、录像分辨率帧率、滚动段／按单文件目录。
3. 启动日志里有 `PP-PATCH: Direct pipeline active`。

> 设置项写在 `%LOCALAPPDATA%\ExpressPackingMonitoring\pp-patch.json`，**绝不改官方的 config.json**。
> 页签文字是中文＋英文并列，不跟随界面语言——跟随语言要改 `.resx`，会多出几个语言资源 dll，就破坏「只换一个 dll」了。

### 想确认补丁真的生效

启动后日志里出现这行就对了：

```
PP-PATCH: Direct pipeline active
```

没有这行 = 实际还在跑原来那条卡顿链路（配置被设成 Legacy 了，或 dll 没换成功）。

### 想退回官方原版（任意时候）

最简单：设置 → 「补丁 / Patch」→ 录像链路选 **原版 / Legacy** → 重启。
或者把备份的旧 dll 拷回去。

---

## 3. 我修了 bug / 出了新版补丁包，你要做什么

1. 解压新 zip。
2. **把同名文件全部覆盖**到仓库里（路径不变、文件名不变），**新增的文件也一起放进去，不要删任何文件**。
3. commit / push（网页 `Upload files` 覆盖同名文件也行）。
4. 重跑一次 `Run workflow`，再下载替换 dll。

不需要重建仓库、不需要重新 Fork、不需要重新 clone 上游。

---

## 4. 出问题对照表

| 现象 | 原因 / 动作 |
|---|---|
| CI 变红、`error CS...` | 把完整 build 日志发我。多半是上游改了入口方法签名，我改注入锚点正则，你重跑 |
| CI 变红、`PATCH REGRESSION: AppConfig shadow` | 有人把旧版 `AppConfig.Patch.cs` 传回去了，覆盖成新版即可 |
| CI 变红、`HOOK ANCHOR NOT FOUND` / `LINE ANCHOR NOT UNIQUE` | 上游改了 `StartCamera()` 或 `InitializeComponent()` 所在结构，把日志发我改锚点 |
| 找不到 `Run workflow` 按钮 | yml 不在默认分支 / Actions 被禁用 / 多套了一层文件夹 |
| 下载到两个 dll，或替换后程序起不来 | 看 CI 日志里 `main dll:` 那行，必须来自 `src/ExpressPackingMonitoring/bin/Release`，**不能是 Launcher 的** |
| 启动直接报错（缺程序集之类） | 目标机官方更新和跑 workflow 没连着做，版本错配；先让目标机再更新一次 |
| 没有 `PP-PATCH: Direct pipeline active` | dll 没换成功，或 pp-patch.json 里被写成 `Legacy` |
| **预览还是黑** | 先看日志有没有 `preview attached to VideoFrame`；有这行却仍黑，多半是预览被界面设置里的「关闭实时预览」关掉了，或有"无信号"遮罩，把启动日志发我 |
| **设置里没有「补丁 / Patch」页签** | 用的是 v2 的 dll；按第 3 节覆盖文件重跑一次 workflow |
| 订单列表不写记录 / 语音不播报 | 仍是已知缺口，见第 7 节；不影响录完整 |

---

## 5. 第一次 CI 为什么失败（已修，留个记录）

第一次 build 报 **71 个 CS0436 + 8 个 CS0117**，全部由同一个错误引起：
`AppConfig.Patch.cs` 里写了 `public partial class AppConfig`。**真正的 `AppConfig` 在 `ExpressPackingMonitoring.Core` 程序集里**，主项目 `Config/AppConfig.cs` 里那个 partial 是 `ScanRecord` —— 我看错了行。源码里再声明一个同名类会把上游类型整个遮蔽，于是全项目所有 `AppConfig.DefaultXxx` 常量都找不到。

**修法**：配置改成独立的 `PpPatchConfig` 静态类，补丁代码不再定义、也不再引用 `AppConfig`。注入脚本和 workflow 各加了一道哨兵，防止以后又写回去。

顺手修了另一个隐患：Launcher 项目也会输出一个叫 `ExpressPackingMonitoring.dll` 的文件，原来递归查找可能抓错；现在只在主项目 `bin/Release` 下找。

---

## 6. 附录：10 个文件各干什么

| 文件 | 作用 |
|---|---|
| `.github/workflows/pp-patch-build-on-demand.yml` | 唯一 workflow。只响应手动 `Run workflow`：取最新正式 tag → 临时 clone 上游 → 注入补丁 → `build -c Release` → 上传 dll |
| `tools/apply-patch.ps1` | 在 runner 里复制 7 个 `.cs`、给 csproj 加 7 行、插 4 处 `// PP-PATCH` 钩子；锚点找不到就**显式报错** |
| `Config/AppConfig.Patch.cs` | `PpPatchConfig` + `PatchOptions`；读写 `pp-patch.json`，就地生效，不碰上游类型 |
| `Services/DirectCaptureRecordingService.cs` | 实时主路：FFmpeg 直采 MJPEG、`-c copy` 写 60 秒滚动段 + 预览旁路 |
| `Services/ClipByOrderService.cs` | 扫码结束后按单 `-c copy` 裁切，1–3 秒出 `单号.mkv` |
| `Services/MjpegArchiveService.cs` | 闲时串行转 H.264、烧单号时间，可让路 |
| `Services/PpPatchRuntime.cs` | 全局胶水、启停单、事件、启动哨兵日志 |
| **`ViewModels/MainViewModel.Patch.cs`** | **v3：订阅 `PreviewFrame` → WIC 降采样解码 → 赋值 `VideoFrame`；单帧在途、忙则丢，不反压主路** |
| **`UI/SettingsWindow.Patch.cs`** | **v3：运行时给设置窗口加「补丁 / Patch」页签（预览画质、录像链路、当前状态），全代码建 UI** |
| `README.md` | 本手册 |

**为什么设置 UI 用代码建、不改 XAML / .resx**：4000 行 XAML 注入易被上游改崩；改 `.resx` 会生成 `zh-Hans` / `ja-JP` 卫星程序集，部署就不止一个 dll。现在两者都不碰 ⇒ **仍只替换一个 dll**。

不 publish、不跑 test、不打安装包、不写回任何仓库。

---

## 7. 附录：已知缺口

已补（v3）：**预览画面**、**设置里的预览画质与链路开关**。

仍未补（都不影响「2K30 不卡、不丢、录完整」）：

- **订单 DB / 列表 / 语音播报**：起停钩子短路后原逻辑被跳过，要在 `OrderClipReady` 订阅里补回。按单 MKV 文件本身能正常产出、能回看。
- **`Stop()` 未注入**：不丢已写完的段，但退出时 ffmpeg 子进程可能残留。
- **`strftime` 是否按 UTC 命名**：目标机那份 ffmpeg 需现场确认一次；若段名是本地时间，按单裁切会偏移。
- **预览的"无信号"遮罩 / 摄像头健康状态**：`StartCamera()` 短路后上游状态位没走，某些界面可能仍盖一层提示，需现场看一次。
- 本机无 .NET SDK，补丁源码和注入脚本都以 GitHub Actions 的编译结果为准。
