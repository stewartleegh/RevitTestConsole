# RevitTestConsole

面向 Revit 二次开发（R2020–R2026+）的独立测试控制台基建。无需 Visual Studio Test Explorer、无需 Revit 进程即可在 CI 沙箱中运行纯单元测试，也可在装有 Revit 的机器上预载 RevitAPI 运行集成测试。

本项目从本人revit二开工具的测试基建抽取解耦而来，已去除所有业务代码依赖，可直接复制到任意 Revit 二次开发项目中使用。

## 架构分层

```
┌─────────────────────────────────────────────────────────┐
│  RevitTestConsole.Host（宿主，命令行 exe）                 │
│  Program.cs：程序集发现/加载、Revit 环境准备、               │
│  参数解析、结果汇总输出                                     │
├─────────────────────────────────────────────────────────┤
│  RevitTestConsole.Core（运行器）                           │
│  LightweightTestRunner：反射发现 NUnit fixture、            │
│  逐测试超时熔断、类别过滤                                   │
│  TestRunnerCommand/Application：Revit 进程内运行模式        │
│  （IExternalCommand 备选宿主，注入 RevitTestContext）       │
├─────────────────────────────────────────────────────────┤
│  RevitTestConsole.TestKit（测试工具包）                    │
│  RevitTestContext：UIApplication 静态注入/复位              │
│  RevitIntegrationTestBase：集成测试基类                     │
│  RevitPathResolver：Revit 安装路径探测（环境变量+默认目录）  │
├─────────────────────────────────────────────────────────┤
│  samples/SampleCommand.Tests（示例测试项目）                │
│  三类测试的写法示范：纯单元 / RevitAPI 冒烟 / 进程内集成     │
└─────────────────────────────────────────────────────────┘
```

依赖方向：Host → Core → TestKit；测试程序集 → TestKit + NUnit。
TestKit 刻意不引用 RevitAPI（上下文以 `object` 传递），保证测试程序集只依赖本 Kit 与 NUnit，可在无 Revit 的机器上编译运行。

## 目录结构

```
RevitTestConsole/
├── RevitTestConsole.sln
├── src/
│   ├── RevitTestConsole.Core/        # 运行器 + Revit 内宿主模式
│   ├── RevitTestConsole.TestKit/     # 测试上下文 / 基类 / 路径探测
│   └── RevitTestConsole.Host/        # 命令行宿主（本仓库的主入口）
└── samples/
    └── SampleCommand.Tests/          # 示例：*.Tests 程序集写法
```

## 三壳（3-shell）构建模型

每个项目一个目录、三个 csproj，编译出三个针对不同 Revit 版本区间的程序集（AssemblyName 相同、bin 目录不同）：

| csproj | 目标框架 | DefineConstants | 适配 Revit |
|---|---|---|---|
| `*.csproj`（共享名） | net48 | `R2022_R2024` | Revit 2022 / 2023 / 2024 |
| `*.R2020-R2021.csproj` | net48 | `R2020_R2021` | Revit 2020 / 2021 |
| `*.R2025Plus.csproj` | net8.0-windows | `R2025PLUS` | Revit 2025 / 2026+ |

源码中通过 `#if R2025PLUS` / `#if !R2025PLUS` 等条件编译适配 API 差异与运行时差异（.NET Framework vs .NET 8 的 native DLL 搜索行为不同，详见 Host `Program.cs` 内注释）。

`RevitAPI` / `RevitAPIUI` 仅编译期引用（`Private=False`，不随输出分发），通过环境变量定位：

```
setx Revit2024Path "C:\Program Files\Autodesk\Revit 2024"
setx Revit2026Path "C:\Program Files\Autodesk\Revit 2026"
```

未设置时自动探测默认安装目录，并按版本号回退（见 `RevitPathResolver`）。

## 快速开始

### 前置要求

- Windows x64
- .NET Framework 4.8 Developer Pack（net48 壳）
- .NET 8 SDK（R2025Plus 壳）
- 可选：本机安装 Revit（集成测试用；纯单元测试不需要）

### 构建

推荐用 VS 2022 打开 `RevitTestConsole.sln`，或在命令行用 MSBuild 构建宿主三壳：

```powershell
$msbuild = "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
$root = "D:\ai coder\Projects\RevitTestConsole"

# R2022-R2024 壳
& $msbuild "$root\src\RevitTestConsole.Host\RevitTestConsole.Host.csproj" /p:Configuration=Debug /restore
# R2020-R2021 壳
& $msbuild "$root\src\RevitTestConsole.Host\RevitTestConsole.Host.R2020-R2021.csproj" /p:Configuration=Debug /restore
# R2025Plus 壳（.NET 8）
& $msbuild "$root\src\RevitTestConsole.Host\RevitTestConsole.Host.R2025Plus.csproj" /p:Configuration=Debug /restore
```

输出位于 `src\RevitTestConsole.Host\bin\<壳名>\Debug\<TFM>\`。

### 运行测试（标准冒烟命令）

```powershell
# 纯单元测试：CI 沙箱 / 无 Revit 机器均可
& ".\src\RevitTestConsole.Host\bin\R2022-R2024\Debug\net48\RevitTestConsole.Host.exe" `
    --no-revit --exclude "RevitIntegration,RequiresRevitContext" --timeout 65000
```

- `--no-revit`：跳过 RevitAPI 预载与目录注入（R2025Plus 壳默认即此模式）
- `--exclude`：额外排除指定类别（`RequiresRevitContext` 在 `--no-revit` 下本就自动排除，此处显式写出便于核对）
- `--timeout 65000`：单测试超时 65s，防止 C++/CLI JIT 死锁挂起整个运行器
- 结果 XML 写出到宿主 exe 目录 `RevitTestConsole-TestResults.xml`，退出码 `0`=全过 / `1`=有失败 / `2`=参数或环境错误

### 命令行参数完整表

| 参数 | 说明 |
|---|---|
| `--assembly <path>` | 指定测试程序集，可重复；相对路径先按当前目录、再按宿主目录解析 |
| （省略 `--assembly`） | 自动发现宿主 exe 目录下全部 `*.Tests.dll`（排除 nunit*.dll 与自身） |
| `--include <list>` | 逗号分隔的类别白名单 |
| `--exclude <list>` | 逗号分隔的类别黑名单 |
| `--timeout <ms>` | 单测试超时（毫秒，`0` = 不超时，内联执行） |
| `--no-revit` | 跳过 RevitAPI 预载；无 Revit 安装的机器自动进入此模式 |
| `--revit` | 仅 R2025Plus 壳：显式要求预载 RevitAPI |

宿主每次运行都会自动：排除 `SandboxSensitive` 类别（防沙箱杀进程）；`--no-revit` 下自动排除 `RequiresRevitContext`；将用户配置目录（APPDATA 等）重定向到 exe 旁的 `TestProfile_<壳名>` 目录，保证写文件测试不污染真实用户目录。

## 测试类别纪律

| Category | 语义 | 运行环境 |
|---|---|---|
| （无类别） | 纯托管单元测试，不触碰 RevitAPI | 任意环境 |
| `RequiresRevitContext` | 直接引用 RevitAPI 托管类型（如 `ElementId`），需要已加载 RevitAPI | 有 Revit 的机器或 Revit 进程内；`--no-revit` 下自动排除 |
| `RevitIntegration` | 完整集成流程（开文档、执行命令等） | 同上，需显式 `--include` |
| `SandboxSensitive` | 写 Desktop/Downloads/Revit 安装目录等受限路径 | 宿主始终排除 |

**命名约定**：测试程序集 `AssemblyName` 必须以 `.Tests` 结尾，这是宿主自动发现的依据。

**内置运行器的限制**：`LightweightTestRunner` 仅识别 `[TestFixture]` / `[Test]`，不支持 `[TestCase]` 参数化用例——请写普通 `[Test]` 方法并在方法内用局部变量组织数据（见 `SampleCommandTests.cs`）。

## 示例项目

`samples/SampleCommand.Tests` 演示三类测试的标准写法：

- `SampleCommandTests.cs` — 纯单元测试
- `RevitApiSmokeTests.cs` — `RequiresRevitContext` 冒烟测试（只构造托管类型，不开文档）
- `RevitIntegrationTests.cs` — 继承 `RevitIntegrationTestBase` 的进程内集成测试

示例项目被三个 Host 壳以 `ProjectReference` 引用，构建后 `SampleCommand.Tests.dll` 自动落入 Host bin 目录参与自动发现。接入你自己的项目时，替换/移除该 `ProjectReference`，或改用 `--assembly` 显式指定即可。

## 接入新的 Revit 二次开发项目

1. **复制三件套**：`src/RevitTestConsole.Core`、`src/RevitTestConsole.TestKit`、`src/RevitTestConsole.Host` 到你的解决方案（保持目录相对关系，或同步修改 csproj 中的相对引用路径）。
2. **新建测试项目** `<你的模块>.Tests`（三壳模式），引用 `RevitTestConsole.TestKit` 对应壳，测试代码按类别纪律标注。
3. **对接宿主**（二选一）：
   - 简单做法：在 Host csproj 中添加对你的 `.Tests` 项目的 `ProjectReference`，随构建自动落入 Host bin；
   - 灵活做法：构建后把 `.Tests.dll` 复制到 Host bin 目录，或运行时用 `--assembly` 指定路径。
4. **（可选）Revit 进程内运行**：把 Core 的 `TestRunnerCommand` 作为 `IExternalCommand` 注册进 Revit 的 add-in 清单，即可在真实 Revit UI 线程中运行同一批测试（集成测试的最佳保真度）。
5. 在 csproj 中把 `Revit{版本}Path` 的 HintPath 换成你实际面向的 Revit 版本（或统一走默认安装目录探测）。

## 工程经验（内嵌于代码注释，此处索引）

宿主 `Program.cs` 中的 Fix N 注释记录了实战踩坑结论，移植或修改前建议先读：

| 标记 | 主题 | 结论 |
|---|---|---|
| Fix 1 | `--no-revit` 自动排除 | 无 RevitAPI 时跳过 `RequiresRevitContext`，不误报失败 |
| Fix 2 | 托管程序集预载超时 | 独立线程 30s 超时放弃，防 .NET 8 C++/CLI `.cctor` 死锁假死 |
| Fix 4 | 程序集加载上下文 | `Assembly.LoadFile`（非 LoadFrom）+ BaseDirectory 预缓存，防 `AssemblyResolve` 递归 StackOverflow |
| .NET 8 | native 搜索路径 | 运行时默认 `SetDefaultDllDirectories` 令 `SetDllDirectory`/PATH 失效，必须 `AddDllDirectory`；Revit 2026 的 `libfbxsdk-adsk.dll` 需单独定位（Components 共享目录） |

无 Revit 机器运行 `--no-revit` 时，宿主自动跳过全部 Revit 环境准备继续执行——纯单元测试场景完全零依赖。

## 来源与许可

代码抽取自本人revit二开项目（NUnit 3.13.3 + 自建 LightweightTestRunner + TestConsole 宿主 + RevitTestContext 三层基建），解耦变更包括：

- 命名空间统一为 `RevitTestConsole.*`，移除 二开工具前缀与业务引用
- 宿主测试程序集改为自动发现 / `--assembly` 指定，不再写死程序集清单
- 新增 `RevitPathResolver` 公共路径探测（原内嵌于宿主）
- 新增无 Revit 机器的 `--no-revit` 降级支持（原实现要求必须找到 Revit 安装）
- R2025Plus 壳默认 `--no-revit`（规避 .NET 8 + RevitAPI 死锁），`--revit` 显式开启
