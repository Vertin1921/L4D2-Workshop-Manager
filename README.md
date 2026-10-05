# Left 4 Dead 2 Mod Manager（L4D2 专用 Mod 管理器）

> Windows 10 / 11 x64 桌面应用 · C# · .NET 8 · WPF · 暗黑（L4D2 黑 + 暗红）× macOS 风格界面
>
> **本程序由 DeepSeek Harness 大肥鱼生成** 🐟

## 这是什么

一个把《Left 4 Dead 2》的 **addons 与创意工坊订阅整理得井井有条**的桌面管理器：
它扫描你的 Mod 目录、读懂 VPK 里的 `addoninfo.txt`、只改后缀就能启用/禁用、
自动按类型分类、精确查出**哪些 Mod 互相冲突**、能在**程序内部直接逛创意工坊并一键下载**，
还支持**人物语音替换**（先自动 ZIP 备份原版、再替换、可一键还原）。
全部数据都是 JSON，放在 `%AppData%\L4D2ModManager`，拷走即可迁移。

## 它能帮你解决什么

| 你的烦恼 | 它怎么解决 |
| --- | --- |
| 装了 100+ 个 Mod，不知道谁和谁冲突 | **冲突检测**：解析每个 VPK 的内部文件清单 + CRC32 比对，区分「真冲突」「同时启用才冲突」「内容完全相同（无影响）」 |
| 想临时关掉某些 Mod，又不想删文件 | **启用/禁用只改后缀**：`xxx.vpk` ⟷ `xxx.vpk.disabled`，**绝不修改或删除 VPK 内容** |
| 一进游戏就崩，怀疑某个 Mod | **配置方案**（单人 / 联机 / 写实 / 枪械）一键切换整套启停组合，也可自建方案 |
| 想要某个人物的语音包 | **人物语音替换**：1/2 代 8 名生还者分别替换；**没有成功的 ZIP 备份就绝不替换原版**，支持一键还原 |
| 懒得开浏览器翻工坊找 Mod | **程序内创意工坊**：内嵌浏览 + 左上角「⬇ 下载 Mod」；也支持粘贴链接/ID 下载，三条下载通道自动兜底 |
| 下载列表太长、看不清进度 | **下载管理页**：进度条、实时速度、暂停 / 继续 / 重试 / 取消 / 打开文件 |
| 不知道自己装了什么 | **Mod 管理页**：名称/作者/描述/大小/时间/状态/分类/冲突数，支持搜索、排序、批量启停、删除确认 |

## 特色

- **界面**：L4D2 黑 + 暗红配色，Win11 圆角窗口 + DWM 毛玻璃，卡片式布局，丝滑的入场/悬停/切换动画（可关）；
- **零依赖**：安装版自带 .NET 运行时，**不需要预装任何东西**；也有便携版（解压即用、不写注册表）；
- **安装即自带卸载程序**：卸载只删**程序自己的文件**（按安装清单），同目录里的 Mod / 地图一律不动；
- **不联网上传任何数据**，不修改游戏本体文件（语音替换需要你确认，且先备份）；
- **完全开源**：源码即本仓库，一条命令就能自己编译出安装包（见 `docs/编译说明.md`）。


---


---

## 自动发布（GitHub Actions）

仓库内置 `.github/workflows/release.yml`：**推一个 tag，云端自动编译、打包并发布 Release**（附 SHA256 校验清单）。

```powershell
# 1) 本地升版本号（会同步改 Directory.Build.props 并重新打包，验证无误）
.\build\bump-version.ps1 -Minor          # 1.1.0 -> 1.2.0

# 2) 提交后用 tag 触发云端发布
git add -A
git commit -m "chore(release): v1.2.0"
git tag v1.2.0
git push origin main --tags
```

发布内容：

| 文件 | 说明 |
| --- | --- |
| `Setup.exe` | 单文件安装程序（含独立卸载程序） |
| `Setup.zip` | 安装包压缩版（安装程序 + 独立卸载程序） |
| `L4D2ModManager-win-x64-portable.zip` | 便携版（解压即用，不写注册表） |
| `SHA256SUMS.txt` | 全部产物的 SHA256，用于校验下载完整性 |

也可以在 GitHub 的 **Actions → Release → Run workflow** 里手动填版本号触发（无需打 tag）。

> 云端发布的是**未签名**版本：只有购买受信任 CA 的代码签名证书才能消除别人电脑上的 SmartScreen 提示；
> 想用自签名证书，可在本地 `build/build-release.ps1 -Sign` 打包（证书只在本机受信任）。

## ⚠️ 关于杀毒软件报毒 / SmartScreen 提示（重要，请先读）

**本程序会被部分杀毒软件报毒、并被 Windows SmartScreen 拦截，这是误报。** 原因很具体：

| 触发误报的行为 | 为什么这样做 |
| --- | --- |
| 单文件自包含安装程序（启动时把自身解压到 `%TEMP%`） | 让用户**无需预装 .NET** 就能安装，体积换便利 |
| 程序清单请求管理员权限（`requireAdministrator`） | 装在 `Program Files` 的 Steam 需要管理员权限才能重命名/删除 Mod |
| 卸载程序会**把自己复制到 `%TEMP%` 再执行** | Windows 不允许运行中的 exe 删除自己；这样做才能把程序目录**彻底清空**（否则会残留 `Uninstall.exe`） |
| 遍历并重命名大量 `.vpk` 文件 | 这就是「启用/禁用 Mod」的实现方式（只改后缀，**绝不修改或删除 VPK 内容**） |
| 未做商业代码签名 | 购买 CA 代码签名证书需要付费；只有**受信任 CA 的签名**才能让**别人**的电脑不再提示 SmartScreen，自签名证书只在本机有效 |

### 你可以这样做

1. **添加 Defender 排除项**（仓库内 `tools/Add-DefenderExclusion.ps1`，管理员运行即可）；
2. **解除文件锁定**再解压/运行：右键 → 属性 → 勾选「解除锁定」，或用 `tools/Unblock-Files.ps1`；
3. **使用文件夹版安装包**（`Setup.exe` + `payload.zip` 同级）——不做自解压，误报概率最低；
4. **自己编译**：`build/build-release.ps1` 一条命令出安装包（见 `docs/编译说明.md`），本机编译的产物永远不会被标记；
5. 用 Release 页提供的 **SHA256** 校验下载完整性（`tools/Prepare-Release.ps1` 生成哈希清单）。

> 程序**不会**联网上传任何数据，**不会**修改游戏本体文件（语音替换会先做 ZIP 备份并且需要你确认），所有数据都以 JSON 保存在 `%AppData%\L4D2ModManager`。源码全部在此仓库，可自行审阅与编译。

---

## 一、功能总览

| 需求 | 实现情况 |
| --- | --- |
| 一、Mod 扫描与目录管理 | 启动自动探测 `SteamLibrary\steamapps\common\Left 4 Dead 2\left4dead2\addons` 与 `steamapps\workshop\content\550`；支持添加任意多个目录、删除、保存配置、下次启动自动读取、一键重新扫描 |
| 二、Mod 信息读取 | 解析 VPK 内 `addoninfo.txt`（名称/作者/描述/版本/标签/工坊 ID）；显示文件大小、创建时间、修改时间、当前状态；无 `addoninfo.txt` 时回退到文件名 / 工坊 ID |
| 三、Mod 状态管理 | 后缀管理：`xxx.vpk` ⟷ `xxx.vpk.disabled`；单个启用/禁用、多选批量、全部启用/禁用；**启停只重命名，绝不修改或删除 VPK 内容** |
| 四、排序与搜索 | 默认排序（禁用优先 + 名称）、名称 A-Z / Z-A、最近添加、最早添加、文件大小 ↑↓、按分类；搜索名称 / 作者 / 分类 / 标签 / 文件名 / 工坊 ID / 描述 |
| 五、Mod 分类 | 解析 VPK 内部路径自动分类：`models/weapons`→🔫武器、`models/survivors`→👤人物、`sound/player`→🎵音频、`scripts`→⚙脚本、`materials/vgui`→🎨UI、`maps/*.bsp`→🗺地图、其余→❓其他；并用 addoninfo 标签与文件名兜底 |
| 六、Steam 创意工坊 | 内置 WebView2 浏览器，**在程序内部**显示创意工坊页面（含图片/标题/作者/介绍），支持浏览、搜索、热门/最新/最高评分切换；`steam://` 链接会被拦截并在程序内解析处理，绝不打开外部浏览器 |
| 七、创意工坊下载 | 页面左上角固定「⬇ 下载 Mod」按钮：自动获取工坊 ID、名称、缩略图、文件信息并下载到 addons 目录（可在设置里改） |
| 八、复制链接下载 | 「Mod 管理」页面右上角「➕ 添加 Mod」：粘贴 `https://steamcommunity.com/sharedfiles/filedetails/?id=…`、`steam://url/CommunityFilePage/…` 或纯 ID，自动解析 ID → 获取信息 → 加入下载 → 完成后自动入库 |
| 九、下载管理 | 独立下载页面：当前下载、进度条、文件大小、实时速度、下载通道；支持暂停 / 继续 / 取消 / 重试 / 复制链接 / 打开文件；完成后自动扫描并加入列表 |
| 十、缩略图系统 | 优先读取 VPK 内 `addonimage.jpg/jpeg/png/bmp/gif`；没有则用创意工坊 `preview_url` 下载缓存；卡片显示图片、名称、作者、状态、大小、分类、冲突标记 |
| 十一、Mod 删除 | 卡片按钮：启用/禁用、打开目录、详情、删除；删除前弹窗确认（列出名称、文件名、路径、大小、状态），删除 VPK（含 `.disabled`）、缩略图缓存、数据库记录 |
| 十二、冲突检测 | 解析每个 VPK 的内部文件清单，找出被多个 Mod 修改的同一文件；区分「同时启用」的真实冲突与「潜在冲突」；显示冲突文件与涉及 Mod，可一键「保留此项、禁用其他」；自动忽略 `addoninfo.txt` / `addonimage.*` 等正常重复文件 |
| 十三、配置方案 | 内置：单人模式（全部启用）、联机模式（关脚本 + 关冲突）、写实模式（关武器 + 关脚本）、枪械模式（仅武器 + UI）；支持保存任意多个自定义方案、应用、删除、查看明细 |
| 十四、UI 设计 | 黑色 / 深灰 / 暗红配色；圆角窗口 + Win11 圆角 + DWM 毛玻璃（Acrylic）、卡片布局、按钮缩放动画、卡片淡入上移动画、悬停反馈；自绘标题栏与侧边栏导航 |
| 十五、拖放安装 | 把 `.vpk`（或含 vpk 的文件夹）拖入窗口 → 复制到 Mod 目录 → 解析 → 加入数据库，全程有拖放遮罩提示 |
| 十六、数据库 | `%AppData%\L4D2ModManager\ModDatabase.json`：名称、路径、工坊 ID、分类、状态、添加/下载时间、缩略图路径、内部文件索引、冲突计数；配置保存在同目录 `config.json` |
| 十七、开发要求 | 完整 VS 解决方案（4 个项目）、完整源码、Release x64 可运行 exe、单文件安装程序 `Setup.exe`、本编译说明 + 使用说明 + 16 项自动化自检 |

---

## 二、系统要求

| 项目 | 要求 |
| --- | --- |
| 操作系统 | Windows 10 1809+ / Windows 11（x64） |
| 运行（安装版） | 无需预装 .NET：`Setup.exe` 内含自包含发布版本 |
| 运行（源码编译） | .NET 8 SDK（8.0.100+） |
| 管理员权限 | **启动时自动请求**（弹一次 UAC）。装在 `C:\Program Files (x86)` 的 Steam 需要管理员权限才能启停 Mod；不希望提权时可在「设置 → 性能与权限」关闭或加 `--no-elevate` |
| 创意工坊页面 | Microsoft Edge **WebView2 运行时**（Win11 及较新 Win10 已内置；缺失时程序会提示，其余功能不受影响） |
| 可选 | `steamcmd.exe`（创意工坊下载的备用通道） |

---

## 三、目录结构

```
l4d2-mod-manager/
├─ L4D2ModManager.sln                 # Visual Studio 解决方案（Debug/Release × x64）
├─ Directory.Build.props              # 全局编译属性
├─ NuGet.config                       # 仓库级包源配置
├─ README.md                          # 本文件
├─ docs/
│  ├─ 编译说明.md                     # 详细编译/发布/打包说明（含 VS 步骤）
│  └─ 使用说明.md                     # 面向使用者的功能说明与排错
├─ build/
│  ├─ env.ps1                         # 编译环境准备（自动定位 dotnet，便携模式）
│  ├─ build.ps1                       # 日常编译
│  ├─ test.ps1                        # 运行自检
│  └─ build-release.ps1               # 一键发布：exe + Setup.exe + 便携包
├─ installer/
│  └─ L4D2ModManager.iss              # 可选的 Inno Setup 6 脚本
├─ src/
│  ├─ L4D2ModManager.Core/            # 核心业务层（net8.0-windows，不依赖 WPF）
│  │  ├─ Models/                      # ModItem / 分类 / 状态 / 方案 / 冲突 / 下载 / 工坊信息
│  │  └─ Services/
│  │     ├─ Vpk/                      # VPK 解析器（v1/v2、预载、外部分卷）、KeyValues、addoninfo
│  │     ├─ Steam/                    # Steam 安装与库目录探测（注册表 + libraryfolders.vdf）
│  │     ├─ Mods/                     # 扫描、数据库、状态、分类、冲突、方案、缩略图、门面服务
│  │     ├─ Workshop/                 # Steam Web API 客户端
│  │     └─ Downloads/                # 下载管理器 + 三种下载通道
│  ├─ L4D2ModManager.App/             # WPF 前端（net8.0-windows，UseWPF，x64）
│  │  ├─ Themes/Theme.xaml            # 配色、控件模板、动画
│  │  ├─ Views/ ViewModels/           # 6 个页面（Mod/工坊/下载/冲突/方案/设置）
│  │  ├─ Dialogs/ Services/ Interop/  # 通用对话框、服务容器、窗口特效（毛玻璃/圆角）
│  │  └─ Assets/app.ico               # 程序图标（含生成脚本 make-icon.py）
│  └─ L4D2ModManager.Setup/           # 安装程序（单文件 Setup.exe，自包含载荷）
└─ tests/
   └─ L4D2ModManager.Tests/           # 自检程序（16 项，含真实 VPK 验证）
```

---

## 四、快速开始

### 4.1 用 Visual Studio

1. 安装 **Visual Studio 2022**（17.8+），勾选工作负载 **「.NET 桌面开发」**（含 .NET 8 SDK）。
2. 打开 `L4D2ModManager.sln`。
3. 顶部选择 **Release** + **x64**，右键 `L4D2ModManager.App` → **设为启动项目** → F5 运行。
4. 单文件安装程序：生成根目录下的 `Setup.exe` 需要先生成载荷，建议直接用命令行脚本（见 4.3）。

### 4.2 用命令行（推荐）

```powershell
# 日常编译
pwsh -File .\build\build.ps1

# 运行自检（16 项，含 VPK 解析、启停、冲突、方案、安装/删除等）
pwsh -File .\build\test.ps1
pwsh -File .\build\test.ps1 -AutoDetectSteamDirectory   # 顺便用本机真实 Mod 验证

# 一键发布：artifacts\Setup.exe + 便携版 + 便携压缩包
pwsh -File .\build\build-release.ps1 -RealVpkDirectory "D:\Program Files (x86)\Steam\steamapps\common\Left 4 Dead 2\left4dead2\addons"
```

手动命令（等价的 dotnet 命令）：

```powershell
dotnet restore  src\L4D2ModManager.App\L4D2ModManager.App.csproj --configfile NuGet.config
dotnet build    L4D2ModManager.sln -c Release -p:Platform=x64
dotnet publish  src\L4D2ModManager.App -c Release -r win-x64 --self-contained true -o artifacts\app
```

### 4.3 生成安装包

`build\build-release.ps1` 会自动完成：发布主程序 → 压缩为安装载荷 `payload.zip` →
编译安装程序 → 输出到 `artifacts\`：

| 产物 | 说明 |
| --- | --- |
| `Setup\` + `Setup.zip` | **推荐**：`Setup.exe`（约 1 MB 的普通 .NET 程序）+ 同目录 `payload.zip`（62 MB）。**不使用单文件自解压、不内嵌大资源**，是杀软误报最少的形态 |
| `Setup.exe` + `payload.zip`（artifacts 根目录） | 同上两者的便捷副本，注意必须放在同一目录 |
| `L4D2ModManager-win-x64\` + `-portable.zip` | 便携版（解压即用，不写注册表、不建快捷方式） |

> 想要"单个 exe 就能分发"时：`build-release.ps1 -SingleFileInstaller`（载荷内嵌 + 可选 `-CompressInstaller`）。
> 代价是 Defender 等启发式引擎更容易误报 —— **如果你的杀软报毒，请优先用上面的文件夹版**。

安装程序支持：

```powershell
Setup.exe                       # 图形向导安装（清单已声明 requireAdministrator，会直接弹 UAC）
Setup.exe /S /dir=D:\L4D2MM     # 静默安装（提权由清单负责）
Setup.exe /S /nodesktop /nostartmenu
Setup.exe /uninstall            # 卸载（图形向导）
Setup.exe /uninstall /S /removedata   # 静默卸载并删除用户数据
```

安装后也可以直接用主程序卸载（控制面板「应用和功能」里的卸载项正是这条命令）：

```powershell
L4D2ModManager.exe --uninstall              # 询问是否保留用户数据
L4D2ModManager.exe --uninstall --silent --removedata
```


也可以改用 Inno Setup：编译 `installer\L4D2ModManager.iss`（需先生成 `artifacts\L4D2ModManager-win-x64`）。

---

## 五、使用要点

1. **首次启动**：程序会自动探测 Steam，把 L4D2 的 `addons` 与创意工坊内容目录加入扫描列表，然后自动扫描。
2. **启停 Mod**：卡片上的「启用 / 禁用」按钮（或右键菜单）只重命名文件后缀 `→ .vpk.disabled`，游戏与 VPK 内容不受影响。
3. **批量操作**：左侧勾选框可多选，然后「启用所选 / 禁用所选」；「启用全部 / 禁用全部」作用于整个列表；「启用当前列表 / 禁用当前列表」只作用于当前筛选结果。
4. **创意工坊**：进入「创意工坊」页，在程序内浏览搜索；打开某个 Mod 页面后点左上角「⬇ 下载 Mod」；也可以用右侧面板直接粘贴链接 / ID。
5. **下载通道**：程序按顺序尝试 ① Steam UGC 直链（支持断点续传、暂停） ② 已订阅的 Steam 工坊缓存 ③ `steamcmd`。若都失败，在 Steam 里订阅该 Mod 后回到「下载管理」点「重试」即可（会自动从订阅缓存复制）。
6. **冲突检测**：扫描后自动检测（可在设置关闭）；「冲突检测」页可按文件查看涉及哪些 Mod，支持「保留此项」（禁用同一冲突中的其他 Mod）。
7. **配置方案**：把当前启停状态保存为自定义方案；内置 4 套预设按分类 + 冲突自动计算，新 Mod 会自动纳入规则。
8. **拖放安装**：把 `.vpk` 直接拖进窗口即可安装到下载目录并入库。
   以管理员身份运行时 Windows 会阻止资源管理器拖放，此时请用「📂 添加 Mod 文件」按钮（效果相同）。
9. **管理员权限**：启动时自动弹 UAC 并以管理员身份运行（与清单里写 `requireAdministrator` 等效，但可用设置或 `--no-elevate` 关闭）。未提权时界面顶部会出现提示条，可一键「以管理员身份重启」。
10. **流畅度**：毛玻璃默认关闭（DWM 每帧重算模糊会明显降低滚动帧率），「设置 → 性能与权限」里可打开；同页还会显示当前是硬件加速还是软件渲染。

详细说明见 [docs/使用说明.md](docs/使用说明.md)。

---

## 六、技术实现要点

* **VPK 解析（自研，无第三方依赖）** — `Core/Services/Vpk/VpkReader.cs`
  支持 VPK v1/v2 头部、目录树（含根目录 `" "` 约定与 `0xFFFF` 条目终止符）、
  预载数据（preload 与数据区拼接）、内嵌数据（`archive_index = 0x7FFF`，偏移相对数据区起点）、
  外部数据分卷（`xxx_000.vpk`）；已被 **106 个真实 L4D2 Mod（36,812 个内部文件）** 验证，0 失败。
* **KeyValues 解析** — 自研词法/语法分析，支持引号、注释、嵌套块、重复键（`addonTag`），
  对无引号值与 GBK/UTF-8 中文均容错。
* **状态管理** — 只做文件重命名（`File.Move`），并在重命名前检查目标是否已存在，避免覆盖任何文件。
* **增量扫描** — 数据库缓存 VPK 内部文件索引与磁盘指纹（大小 + 修改时间），文件未变时跳过解析；
  实测 106 个真实 Mod（约 5 GB）首次扫描 0.5 秒、二次扫描命中缓存。
* **冲突检测** — 按规范化内部路径分组，忽略 `addoninfo.txt` / `addonimage.*` 等必然重复的文件。
* **方案系统** — 预设规则 + 按文件名的单独覆盖（`Overrides`），内置方案不可删除但可另存为自定义方案。
* **下载管理** — `SemaphoreSlim` 控制并发、`ManualResetEventSlim` 实现暂停、`HttpClient` + `Range` 实现断点续传、
  速度按 400 ms 窗口统计；完成后识别 VPK / ZIP 载荷并落盘到目标目录。
* **UI 技术** — `WindowChrome` 自绘标题栏（不使用 `AllowsTransparency`，保证 WebView2 正常渲染）+
  `DwmSetWindowAttribute` 圆角与深色标题栏 + 可选 `SetWindowCompositionAttribute` 毛玻璃 +
  页面切换淡入上移、卡片悬停位移、按钮缩放等 **只用 Opacity / RenderTransform 的 GPU 动画**。
* **流畅度优化** —
  毛玻璃默认关闭（可在设置中开启）；缩略图在**线程池解码后 Freeze**（不再占用 UI 线程）并**串行分批**加载；
  列表用 `RangeObservableCollection.ReplaceAll` **一次 Reset** 取代逐条 `Add`；
  虚拟化开启 `Recycling` + 预取一页；缩略图使用 LowQuality 插值；移除了虚拟化列表里逐项的进入动画
  （快速滚动时会不断重放，是卡顿主因）。界面还会提示当前是否启用硬件加速。
* **减少杀软误报的做法** —
  卸载不再生成 `.cmd` 脚本、不再调用 `cmd /c rmdir /s /q`（改为纯 .NET 重试删除 +
  `MoveFileEx(MOVEFILE_DELAY_UNTIL_REBOOT)` 延迟清理）；安装程序默认**不做二次压缩**
  （压缩的自解压单文件是启发式引擎较敏感的组合，需要小体积时可 `-CompressInstaller`）；
  所有 exe 都带完整的公司/产品/描述/版权/版本信息。**建议正式分发前用代码签名证书签名**，见常见问题。
* **管理员权限** — 启动时通过 `ShellExecute` + `runas` 自提升（`App/Services/ElevationService.cs`），
  并提供 `--no-elevate`、设置开关与界面提示条；安装程序在图形向导下同样自动请求提权。
* **无界面模式**（便于排错与自动化）：
  ```powershell
  L4D2ModManager.exe --diagnose      # 环境诊断（.NET、WebView2、Steam 路径）
  L4D2ModManager.exe --scan <目录>   # 只扫描并输出统计，不打开界面
  L4D2ModManager.exe --selftest-ui [目录]  # 加载全部页面与模板做界面自检（不显示窗口）
  L4D2ModManager.exe --uninstall     # 卸载（--silent 静默，--removedata 同时删除用户数据）
  ```
* **安装/卸载（自研，无第三方工具）** — `Core/Services/Deployment/AppDeployment.cs` 提供
  快捷方式（WScript.Shell COM）、卸载注册表项、文件清理（自身占用时通过独立 cmd 进程延迟删除）；
  安装程序项目负责把 62 MB 载荷自解压并调用这些能力。

---

## 七、数据位置

| 内容 | 路径 |
| --- | --- |
| 配置（目录、选项、方案） | `%AppData%\L4D2ModManager\config.json` |
| Mod 数据库 | `%AppData%\L4D2ModManager\ModDatabase.json` |
| 缩略图缓存 | `%AppData%\L4D2ModManager\thumbnails\` |
| 下载临时文件 | `%AppData%\L4D2ModManager\downloads\` |
| 日志 | `%AppData%\L4D2ModManager\logs\` |
| 内置浏览器数据 | `%LocalAppData%\L4D2ModManager\WebView2\` |

便携模式：在 exe 同目录放一个 `portable.txt`，数据会保存到 exe 目录下的 `Data\`。
也可以用环境变量 `L4D2MM_DATA_DIR` 指定数据目录（自动化测试即用此方式隔离）。

---

## 八、常见问题

**Q：双击 Setup.exe（或主程序）弹出「Windows 已保护你的电脑 / 发行者：发布者未知」？**
这是 **SmartScreen**，不是杀毒报毒；触发条件是"文件带下载标记（Mark of the Web）+ 没有数字签名"。
按顺序任选一种解决：

1. **解除下载标记**（最快，无需管理员）：
   ```powershell
   pwsh -File .\tools\Unblock-Files.ps1                 # 处理 artifacts 目录
   pwsh -File .\tools\Unblock-Files.ps1 -Path "D:\L4D2MM"
   ```
   或右键文件 → 属性 → 勾选「解除锁定」→ 确定（**压缩包先解锁再解压**，里面的文件就不会带标记）。
   弹窗里点「更多信息 → 仍要运行」也能直接放行。
2. **给产物签名**（推荐，同时消除"未知发布者"）：
   ```powershell
   pwsh -File .\tools\New-DevCertificate.ps1     # 生成本机开发证书并加入信任
   pwsh -File .\tools\Sign-Artifacts.ps1         # 签名 artifacts 下的 exe
   pwsh -File .\build\build-release.ps1 -Sign    # 或构建时直接签名
   ```
   自签名证书**只在本机生效**；分发给别人需购买 OV/EV 代码签名证书，然后
   `pwsh -File .\tools\Sign-Artifacts.ps1 -PfxPath your.pfx -PfxPassword ***`。
3. 或改用 Inno Setup 方案（`installer\L4D2ModManager.iss`）生成安装包后再签名。

**Q：我要把程序发给别人，对方会不会被拦？**

分两种情况，请如实告知对方：

| 对方会看到 | 原因 | 解决 |
| --- | --- | --- |
| **SmartScreen「未知发布者」**（一定出现） | 对方下载 zip 会重新打上"下载标记"，而程序未用**受信任 CA** 的证书签名 | 让对方先解锁压缩包再解压（包里的 `先读我.txt` 已写明步骤），或购买 OV/EV 证书签名后分发 |
| **杀软报毒**（概率事件） | 未签名 + 新文件缺乏流行度信誉 + 会重命名 Steam 目录文件 | 让对方加白名单；到 [微软误报提交](https://www.microsoft.com/en-us/wdsi/filesubmission) 提交（附文件哈希与源码地址）；长期方案是签名 |

**最省事的做法**：用 `tools\Prepare-Release.ps1` 生成分发包 —— 它会自动解除下载标记、
（可选）签名、并生成一个包含安装包 + 便携包 + `先读我.txt`（写明解锁/加白/哈希）+ `Unblock-Files.ps1` 的 zip：

```powershell
pwsh -File .\tools\Prepare-Release.ps1 -Sign     # 输出 dist\L4D2ModManager-<版本>-win-x64.zip
```
发送时**发整个 zip，不要发裸 exe**（即时通讯工具可能二次扫描、改名、甚至加壳），
并提醒对方"先看压缩包里的先读我.txt"。

> 实话实说：**未签名程序无法保证在所有机器上都不被拦**。要让陌生用户零提示地安装，
> 唯一可靠的办法是购买代码签名证书（OV 约 ¥800–2000/年，EV 更贵但可立即获得 SmartScreen 信誉）
> 并对 `Setup.exe` 与 `L4D2ModManager.exe` 签名。

**Q：杀毒软件报毒 / 提示可疑程序怎么办？**
本项目是自编译、**未签名**的 .NET 程序，加上会重命名 Steam 目录里的 `.vpk`，容易被启发式规则误判。
已经做的结构性削减（相比早期版本误报面小很多）：

| 改动 | 说明 |
| --- | --- |
| 安装包不再自解压 | 改为**普通文件夹形式**：小的 `Setup.exe` + 同目录 `payload.zip`，不做单文件自解压 |
| 不再内嵌 62MB 二进制资源 | 载荷改为外置 zip 文件读取 |
| 卸载不再调用 `cmd /c rmdir /s /q` | 改为纯 .NET 重试删除 + 系统"重启后删除"登记 |
| 安装程序清单声明提权 | 安装器是清单 `requireAdministrator`，而不是运行时 `runas` 自提升（后者是误报诱因） |
| 完整版本元数据 | 公司 / 产品 / 文件说明 / 版权 / 版本 |

如果仍然被拦（不同厂商规则差异很大），按以下顺序处理：

1. **加白名单**：把安装目录（或 `payload.zip`）加入 Defender 排除项。
   仓库自带脚本：`pwsh -File .\tools\Add-DefenderExclusion.ps1`（需管理员）；
2. **提交误报**：<https://www.microsoft.com/en-us/wdsi/filesubmission> 选择 "Software developer"，
   附上本仓库源码地址，通常 1–3 天解除；
3. **代码签名**（最有效）：
   `signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 artifacts\Setup\L4D2ModManager.Setup.exe`
   —— 签名后 SmartScreen 不再拦截，主流杀软也不会把"未知发布者"当成默认风险；
4. 或改用 Inno Setup 生成安装包（`installer\L4D2ModManager.iss`）后再签名。

**Q：动画看起来少 / 滚动不流畅 / 帧数低？**
先看「设置 → 性能与权限」里的**图形加速**一行：
* 如果显示「软件渲染」，说明没启用硬件加速（常见于远程桌面、虚拟机或旧显卡驱动）——更新驱动、关闭远程桌面后动画会明显变快；
* 毛玻璃（Acrylic）默认关闭，因为它由 DWM 每帧重算，开启后会明显降低滚动帧率；想更炫可以在该页打开；
* 界面动画都基于 Opacity / RenderTransform（不触发布局），列表已改为一次性刷新 + 虚拟化预取，
  缩略图在线程池解码并串行加载 —— 106 个真实 Mod 的列表滚动应该是顺滑的。

**Q：拖放没反应？**
如果程序以**管理员身份**运行，Windows（UIPI）会阻止资源管理器把文件拖进管理员窗口——这是系统限制，不是程序问题。
请改用「📂 添加 Mod 文件」按钮（批量选择 `.vpk`，功能完全相同），或在「设置」里关闭启动时提权后重启再拖放。

**Q：创意工坊页面空白 / 提示内置浏览器不可用？**
安装 [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)（Win11 已内置）。
可用 `L4D2ModManager.exe --diagnose` 查看检测结果；即使不可用，其余功能（扫描/启停/冲突/下载）都不受影响。

**Q：点下载后失败？**
创意工坊内容对匿名接口并不总是开放，程序会依次尝试三种通道。最稳的做法是：在 Steam 中订阅该 Mod
（文件会出现在 `steamapps\workshop\content\550\<id>`），回到「下载管理」点「重试」，程序会自动复制。
国内网络访问 `api.steampowered.com` 可能需要加速器。

**Q：启用/禁用会不会破坏 Mod？**
不会。程序只重命名文件后缀，并在重命名前检查目标文件是否存在；删除是单独的显式操作，且有二次确认。

**Q：冲突检测报了很多冲突正常吗？**
常见共享库（如各种 VScript 插件都会带 `scripts/vscripts/director_base_addon.nut`）会大量重复，
程序已区分「同时启用」与「潜在冲突」，并可按 Mod 一键禁用其他冲突项。

**Q：如何把 Mod 装到别的盘？**
「设置 → Mod 目录 → 添加目录」加入任意包含 `.vpk` 的文件夹，并把「下载保存目录」指向它。

---

## 九、已知限制与扩展方向

* 创意工坊 UGC 直链是否可用由 Valve 决定；不可用时依赖「Steam 订阅缓存」或 `steamcmd`（本设计已内置三种通道）。
* `steamcmd` 对 L4D2 工坊内容常要求账号拥有游戏，因此匿名下载失败时会给出明确提示与替代方案。
* 版本管理、Mod 合并打包、`.vpk` 内容预览、按工坊订阅状态自动同步等，都可在 `Core/Services` 下按现有接口继续扩展。
