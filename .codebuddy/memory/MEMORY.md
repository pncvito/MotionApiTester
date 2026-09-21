# MotionApiTester 长期记忆

## 设备 API 约定（OptoFidelity，2026-09-21 实测）

- **失败不抛异常**：方法普遍返回 `ValueTuple<bool, string>`，内部 catch 后 `return (false, ex.Message)`。
  判断调用成败必须看返回值首项，只看异常会把失败报成成功。
- **实例必须先初始化**：`OptoFidelity.BaseTester.BaseInterface` 的无参构造不会初始化内部状态，
  字段 `motionLogic` / `systemLogic` / `Bootstrapper` 为 null，此时任何动作方法都是一路 NullReferenceException
  （异常在方法体内被 catch，只以返回值形式冒出来）。
  public 初始化入口是 `BaseInterface.InitializeFixture(Action<string> logger, string dutTypeRecipeName)`；
  `BaseInterface.Init(string preConfigPath, string logPath, string dutTypeRecipeName)` 是 **internal**，反射树里不可见。
- **设备侧成员按声明类型分散在继承链上**（如 `BaseInterface.InitializeFixture` 与
  `EolSeriesBaseInterface.FixtureMoveToLoadUnloadingPosition`）。工具必须复用"同一个对象"，
  不能按成员的声明类型各建各的实例。`ReflectionEnumerator` 用 `DeclaredOnly`，所以树里看不到继承来的成员。

## 依赖解析的坑

- 机型 DLL（如 `OptoFidelity.BinocRainbowSyetemDisplayEolTester.dll`）用 Costura 内嵌了 70+ 个依赖
  （含 `CustomCore` / `Prism.DryIoc.Wpf` / `OptoFidelity.BaseTester`），**磁盘上没有对应文件是正常的**，
  不要再往 `Bin\` 里补这些 DLL。
- `AssemblyResolve` 是多播事件：单个处理器返回 null **不等于**解析失败。在处理器内部直接报"依赖缺失"
  会与体检结论自相矛盾。缺失结论只能在"最终是否加载进进程"确定之后给出。
- `Microsoft.VisualStudio.DesignTools.WpfTap.resources.dll` 缺失是 VS 调试器注入 WpfTap 引起的环境噪声，可忽略。

## 工程约定（沿用 CLAUDE.md）

- 旧格式 .csproj：新增文件要手工登记 `<Compile Include>`；属性手写，没有源生成器。
- 编译校验用 MSBuild：`C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe`
  （`.verify/Verify.csproj` 与 `tools/verify-structure.py` 在本机上并不存在，别当成现成工具用）。
- 设备目录：`D:\MotionApiTester\Bin`；真实 DLL 源目录 `D:\Motion\OptoMotionLib\src\Bin_Library\`（当前只有 LTSMC.dll）。
