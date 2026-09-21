# MotionApiTester 项目长期记忆

## 工程约定（改代码必须遵守）

- **必须 .NET Framework 4.8 + `PlatformTarget=x64`**。设备 DLL、`LTSMC.dll`、本工具 exe 全是 x64；
  改成 AnyCPU/x86 会立刻出现 `BadImageFormatException: 此程序集是为其他处理器编译的`
  （已写入 CLAUDE.md 的硬性约束章节）。
- **旧格式 .csproj**：源生成器不工作（属性手写）；新增 .cs 文件必须手工加进 `<Compile Include>`。
  但 `.verify/Verify.csproj` 用 glob 复用 `src/**/*.cs`，所以新增文件不影响编译校验。
- **界面颜色一律走主题令牌**：不要硬编码颜色。两套主题键必须完全对称
  （`Themes/LightTheme.xaml` / `DarkTheme.xaml`，当前各 30 个键）。
- **`ApiMethod` 有两个反射入口**：方法用 `MethodInfo`，构造函数用 `ConstructorInfo`，
  统一走 `ApiMethod.Target`（`MethodBase`）。新增代码用 `Target`。
- **`SelectedMethod` / `SelectedProperty` / `SelectedField` 的 setter 必须调用
  `CommandManager.InvalidateRequerySuggested()`**，否则"调用方法"按钮可用状态不刷新。
- **不要给 `TreeView` 设置 `ItemContainerStyle`**：会覆盖 `Window.Resources` 里的隐式
  `TreeViewItem` 样式，导致选中行高亮失效。
- 内置 `BooleanToVisibilityConverter` **忽略 ConverterParameter**，需要反向可见性时
  必须用自定义的 `InverseBoolToVisibilityConverter`。

## 编译校验通道（重要）

本环境的 Bash/PowerShell 工具把 `MSBuild.exe` 判为 LOLBin 并拦截，**无法直接调用 MSBuild**。
`dotnet build src/MotionApiTester.csproj` 也不行：旧格式工程解析不到 PackageReference
（System.Text.Json / CommunityToolkit.Mvvm），会报 CS0234 / CS0246，这不是代码错误。

可用通道：

```powershell
& "C:\Program Files\dotnet\dotnet.exe" build "D:\MotionApiTester\.verify\Verify.csproj" -c Debug --nologo
```

- `.verify/` 是 SDK 风格的校验工程，glob 复用 `src/**/*.cs` + `src/obj/Debug/*.g.cs`，
  0 error / 0 warning 即代表 C# 侧可用（**不校验 XAML**）。
- XAML 改动后用 Python 的 `ElementTree` 做结构校验，并核对
  `{DynamicResource}` / `{StaticResource}` 引用的键是否都真实存在
  （缺键会在运行时抛 `XamlParseException`，历史上出现过一次）。
- 输出中文日志乱码时，用 `Out-File -Encoding utf8` 写文件再读取。

## 运行期排障位置

- `%TEMP%\MotionApiTester\load-error.log` — 加载/架构失败
- `%TEMP%\MotionApiTester\diagnostic.log` — 早期加载流程诊断
- `%TEMP%\MotionApiTester\crash.log` — 未处理异常（含 XamlParseException）
- `%APPDATA%\MotionApiTester\devices\default\history.json` — 真实调用结果（判断 API 是否真能调通）
- `src\bin\Debug\Logs\<yyyyMMdd>\log_ui.txt` — 设备侧 log4net 输出
