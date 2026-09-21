# MotionApiTester

加载 OptoFidelity 设备 DLL，通过反射枚举 API 树，并直接调用方法与构造函数；同时可解析原生 DLL 的导出表，生成 P/Invoke 模板。

一句话定位：**设备 API 的"探针台"** —— 用来确认某个方法能不能调、参数该填什么、失败到底是哪一步的问题。

- 形态：Windows 桌面应用（WPF / .NET Framework 4.8 / x64）
- 仓库：<https://github.com/pncvito/MotionApiTester>
- 面向使用者：本文档。面向开发者：`OVERVIEW.md`（分层、核心机制、数据流、设计取舍、技术债）与 `CLAUDE.md`（改代码前的硬性约束、构建与校验命令）

---

## ⚠️ 两条硬性前提

| 前提 | 原因 |
|---|---|
| **必须 .NET Framework 4.8** | 设备 DLL 用 Costura.Fody 打包了 70+ 个依赖，靠 `.cctor` 执行时才展开。.NET 8+ 会跳过 `.cctor`，内嵌依赖不会展开 |
| **必须 x64** | 设备 DLL、`LTSMC.dll` 与本工具 exe 全部是 x64。混用架构会得到 `BadImageFormatException: 此程序集是为其他处理器编译的` |

---

## 功能

**反射与调用**
- 枚举 类型 / 方法 / 构造函数 / 属性 / 字段（过滤编译器生成成员），三栏树种展示（Assembly → Namespace → Type → 分组 → 成员），支持搜索过滤、右键复制签名/完整名、展开折叠
- 调用方法、调用构造函数、读属性、读字段
- 参数装配：可选参数填默认值、logger 参数（`Action<string>`）自动注入、勾选后强制传 `null`
- JSON 高级输入：整组参数按位置覆盖逐参数输入（支持数组；对象类型会明确报错）
- `out` / `ref` 参数按元素类型给默认值，调用后的回填值一并显示
- 契约类型（接口 / 抽象类）自动找可无参构造的实现，并**复用同一个实例**（跨继承链，保证初始化作用到动作方法上）
- 参数值记忆：成功调用后记住参数值，选中方法时自动回填空着的参数，另有「🕘 回填上次」按钮

**结果与记录**
- 结果判定看两处：是否抛异常 **+** 返回值是否自报失败（设备 API 普遍返回 `(bool ok, string message)`）
- 实时日志面板（队列 + 定时刷新）、调用历史（持久化，**按设备隔离**）
- 日志导出：纯文本 / JSON（含调用历史），跟随设置里的"导出格式"

**原生 DLL**
- PE 解析：架构识别 + **导出表枚举**（名字 / 序号 / 转发 / 调用约定）
- 选中导出函数即生成 P/Invoke 模板，可复制
- **只读文件，绝不 `LoadLibrary`**（加载原生 DLL 会执行它的 `DllMain`，有副作用风险）

**其它**
- 多设备登记与切换（切换会重新加载该目录下的设备程序集）
- 浅色 / 深色 / 跟随系统主题（界面颜色全部走主题令牌，三个窗口一起生效）
- 依赖体检：区分「磁盘上有文件」与「能解析」，Costura 内嵌依赖不会被误报成缺失

---

## 环境要求

- Windows x64
- .NET Framework 4.8
- Visual Studio 2019 / 2022（或更高），或独立 MSBuild
- 设备 DLL（**不入库**，需自行放入 `src\Bin\MotionAPI\`）

---

## 快速开始

```powershell
git clone https://github.com/pncvito/MotionApiTester.git D:\MotionApiTester
```

**1. 放入设备 DLL** —— 设备 DLL 是厂商二进制，不入库（`.gitignore` 忽略了 `Bin\`），克隆后必须手动拷贝到 **`src\Bin\MotionAPI\`**。这个位置写在 `%APPDATA%\MotionApiTester\settings.json` 的 `DefaultDeviceDirectory` 里；想放别处也行，之后在设置窗口里改这一项（留空则按 exe 相对位置自动探测）。目录里通常是：

| 文件 | 作用 |
|---|---|
| `OptoFidelity.BaseTester.dll` | 共享基类（所有机型共用） |
| `OptoFidelity.{Model}.dll` | 机型实现，如 `OptoFidelity.BinocRainbowSyetemDisplayEolTester.dll` |
| `LTSMC.dll` | 雷赛电机原生 C++ SDK（可选） |
| `log4net.dll` | 日志组件（可选／随机型而定） |

**2. 还原并构建**（解决方案在 `src\` 下，不是仓库根目录）：

```powershell
$msbuild = "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"

& $msbuild "D:\MotionApiTester\src\MotionApiTester.sln" -t:restore -v:minimal
& $msbuild "D:\MotionApiTester\src\MotionApiTester.sln" -p:Configuration=Debug "-p:Platform=Any CPU" -m -v:minimal
```

> `.sln` 里的平台名是 `Any CPU`，实际架构由 csproj 的 `PlatformTarget=x64` 决定，不要改。
> 也不要用 `dotnet build` 编这个旧格式工程（会解析不到 `PackageReference`，报 CS0234 / CS0246，那不是代码问题）。
> Debug 的输出目录由 csproj 的 `OutputPath` 指定，当前是 **`src\Bin\`**（不是默认的 `bin\Debug\`）—— exe 与 NuGet 依赖都落在那里；Release 仍输出到 `src\bin\Release\`。

**3. 运行**：

```powershell
D:\MotionApiTester\src\Bin\MotionApiTester.exe
```

---

## 使用流程

1. **加载 DLL**（`Ctrl+O`，或工具栏「📂 加载 DLL」）。工具会扫描目录 → 机型模糊匹配 → 加载程序集 → 把依赖解析路径指向该目录。
2. **选方法**：左侧 API 树里点一个方法 / 构造函数 / 属性 / 字段。
3. **先初始化，再动** —— 这是最容易踩的坑：设备 API 的无参构造**不会**初始化内部状态（`motionLogic` / `systemLogic` 等字段仍是 null），此时任何动作方法都会返回 `NullReferenceException`。正确顺序是：

   ```
   BaseInterface.InitializeFixture(Action<string> logger, string dutTypeRecipeName)   ← 先调这个（logger 自动注入）
   EolSeriesBaseInterface.FixtureMoveToLoadUnloadingPosition(...)                     ← 再调动作方法
   ```

   两者必须落在**同一个实例**上，工具已保证这一点（跨继承链复用实例）；如果 `InitializeFixture` 自己返回 false，日志里会给出它自己的错误信息。
4. **填参数**：可用「📋 填默认」填可选参数默认值、「🕘 回填上次」回填上次成功的值，或用底部 JSON 框整组覆盖。
5. **调用**（`F5`）→ 看下方结果卡片与实时日志。
6. **看原生 DLL**：右栏切到「原生 DLL」页 → 选 DLL → 选导出函数 → 生成 / 复制 P/Invoke 模板（模板只是骨架，没有签名信息，**必须照厂商 `.h` 核对**）。

> 结果怎么看：设备 API 普遍把失败包在返回值里（`return (false, ex.Message)`），不向调用方抛异常。工具两条都判，所以看到红色结果卡片时，先读返回值里的那句话。

---

## 快捷键

| 键 | 动作 |
|---|---|
| `Ctrl+O` | 重新加载当前设备目录 |
| `F5` | 调用选中项 |
| `Ctrl+F` | 聚焦搜索框 |
| `Esc` | 清空搜索 |
| `Ctrl+L` | 清空日志 |
| `Ctrl+H` | 清空历史 |

---

## 数据与日志位置

**配置与用户数据**（`%APPDATA%\MotionApiTester\`）

| 文件 | 内容 |
|---|---|
| `settings.json` | 主题 / 日志最大行数 / 导出格式 / 调用超时 / 设备 DLL 目录（留空=自动探测）/ 额外依赖搜索路径 |
| `devices.json` | 已登记的设备列表 |
| `devices\<机型 DLL 名>\history.json` | 调用历史，**按设备隔离** |
| `param-values.json` | 方法参数值记忆（键 = 声明类型全名 + `MethodBase.ToString()`） |

**诊断日志**（`%TEMP%\MotionApiTester\`）

| 文件 | 内容 |
|---|---|
| `crash.log` | 未处理异常（全局兜底） |
| `load-error.log` | 程序集加载失败 |
| `enum-errors.log` | 反射枚举个别类型失败 |
| `audit.log` | 依赖体检结果（逐条列出每个引用的归属） |
| `resolve-errors.log` | `AssemblyResolve` 解析异常 |

> 该目录还会被设备库 / Costura 写入 `<哈希>_*.dll`、`diagnostic.log` 之类的运行期产物，可随时删除。

**其它路径**

| 路径 | 说明 |
|---|---|
| `src\Bin\` | Debug 构建输出（csproj 的 `OutputPath`，已在 `.gitignore` 中） |
| `src\Bin\MotionAPI\` | 设备 DLL 目录（`settings.json` 的 `DefaultDeviceDirectory` 指向这里；留空则按 exe 相对位置自动探测） |
| `D:\MotionConfig\ConfigHardware\MachineType.json` | 机型字符串（只读，用于机型匹配） |

---

## 目录结构

```
D:\MotionApiTester\
├─ doc\                     MotionAPI 说明文档（厂商培训资料，未入库）
├─ src\                     工程本体
│  ├─ MotionApiTester.sln / .csproj
│  ├─ Bin\                  Debug 构建输出（csproj 的 OutputPath，已 gitignore）
│  │  └─ MotionAPI\         设备 DLL（厂商二进制，未入库；settings.json 指向这里）
│  ├─ App.xaml(.cs)         应用级样式（输入控件隐含样式）+ 全局异常兜底
│  ├─ MainWindow.xaml(.cs)  主界面、快捷键、IL 签名、结果卡片配色
│  ├─ Models\               ApiMethod / ApiAssembly / ApiProperty / DeviceProfile / CallHistoryItem …
│  ├─ Services\             反射枚举、调用、依赖解析、原生 DLL 解析、主题、持久化 …
│  ├─ ViewModels\           MainViewModel（7 个 partial）+ TreeBuilder / RelayCommand / Converters
│  ├─ Views\                SettingsWindow / DeviceWizardWindow
│  └─ Themes\               LightTheme.xaml / DarkTheme.xaml
├─ CLAUDE.md                开发者指引（硬性约束 / 架构 / 调用链路）
└─ OVERVIEW.md              开发者指引（分层 / 机制 / 数据流 / 技术债）
```

> ⚠️ 设备 DLL 现在放在**构建输出目录内部**（`src\Bin\MotionAPI\`），而 `Bin\` 已被 `.gitignore` 忽略 —— VS 的「清理」、手动删 `bin`、`git clean -xdf` 都会把设备 DLL 一起删掉，而这些厂商二进制（`LTSMC.dll` 除外）没有可靠的重取来源。**建议另存一份备份**，或把设备库挪到不参与构建清理的位置。

---

## 已知限制

- **导出表没有签名**：只有函数名与序号，生成的 P/Invoke 模板需按厂商 `.h` 核对参数类型与调用约定
- **"取消"与"超时"都拦不住设备侧**：`MethodBase.Invoke` 是同步阻塞调用，取消只对尚未开始的任务有效；超时只是让界面不再干等。**不要因为超时就重复下发动作指令**
- **"卸载"只清界面与缓存**：`Assembly.Load` 加载的程序集无法从 AppDomain 卸载，要彻底复位得重启进程
- **JSON 高级参数不支持对象类型**（数组已支持），遇到对象会明确报错并按 `null` 传入
- **参数记忆只记成功调用**用过的值（失败多半是参数本身有问题，记下来会一直沿用错的）；接口节点与类节点各记各的（如 `IBaseInterface.InitializeFixture` 与 `BaseInterface.InitializeFixture` 是两套）
- **接口实现必须可无参构造**，否则会给出明确错误信息
- **搜索每次按键全量重建整棵树**，加载大型程序集（5MB Costura）时会有卡顿
- 依赖缺失报告会延迟约 400ms 裁决（`AssemblyResolve` 是多播事件，单个处理器未命中 ≠ 解析失败）
