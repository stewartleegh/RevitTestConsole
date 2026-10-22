using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using RevitTestConsole.Core;
using RevitTestConsole.TestKit;
using NUnit.Framework.Interfaces;

namespace RevitTestConsole.Host;

/// <summary>
/// 三个并行 Revit 版本的测试宿主（命令行）：
/// 1. 自动发现/显式指定测试程序集（*.Tests.dll）；
/// 2. 在无 Revit 的 CI 沙箱中安全运行纯单元测试（--no-revit）；
/// 3. 在有 Revit 的机器上为 RequiresRevitContext 类别预加载 RevitAPI。
/// </summary>
internal class Program
{
	// Fix 4: 预加载缓存，避免 AssemblyResolve 时调用 LoadFrom（递归 → StackOverflow 风险）。
	private static readonly Dictionary<string, Assembly> _preloadedAssemblies =
		new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
#if R2020_R2021
	private const string VersionSuffix = "R2020-R2021";
	private const string TargetRevitVersion = "2020";
#elif R2025PLUS
	private const string VersionSuffix = "R2025Plus";
	private const string TargetRevitVersion = "2026";
#else
	private const string VersionSuffix = "R2022-R2024";
	private const string TargetRevitVersion = "2024";
#endif

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern bool SetDllDirectory(string lpPathName);

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern IntPtr LoadLibrary(string lpFileName);

	// AddDllDirectory / SetDefaultDllDirectories：.NET 8 运行时默认调用 SetDefaultDllDirectories，
	// 导致 SetDllDirectory 被忽略、PATH 不参与 native DLL 搜索。
	// 需通过 AddDllDirectory 将 Revit 与 RealDWG 目录加入 USER_DIRS 搜索列表，
	// 否则 RevitAPIUI.dll 的传递依赖（adui25.dll → acdb25.dll）无法解析（errno=126）。
	private const uint LOAD_LIBRARY_SEARCH_USER_DIRS = 0x0400;
	private const uint LOAD_LIBRARY_SEARCH_DEFAULT_DIRS = 0x1000;

	[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
	private static extern IntPtr AddDllDirectory(string lpDirectoryName);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool SetDefaultDllDirectories(uint dwFlags);

	private static int Main(string[] args)
	{
		// 将用户配置文件目录重定向到临时目录，避免沙箱拒绝写入 AppData/Documents/Desktop。
		// 所有通过 Environment.GetFolderPath(SpecialFolder.ApplicationData/MyDocuments/Desktop) 获取路径的
		// 代码都会指向可写位置，无需修改产品代码或测试代码。
		RedirectUserProfilePaths();

		// --no-revit：跳过所有 RevitAPI 预加载，用于纯单元测试运行。
		// 避免 .NET 8 + RevitAPI C++/CLI 模块 .cctor 与 hosting components 冲突导致死锁。
#if R2025PLUS
		// R2025Plus (.NET 8)：默认启用 --no-revit 规避 C++/CLI 死锁；传入 --revit 可显式加载。
		bool noRevit = !args.Any(a => string.Equals(a, "--revit", StringComparison.OrdinalIgnoreCase));
#else
		bool noRevit = args.Any(a => string.Equals(a, "--no-revit", StringComparison.OrdinalIgnoreCase));
#endif
		if (noRevit)
		{
			AppContext.SetSwitch("TestConsole.NoRevit", true);
			Console.WriteLine("[SETUP] --no-revit mode: skipping RevitAPI preloading to avoid C++/CLI deadlock");
		}
		string revitPath = FindRevitInstallation();
		// 解耦修复（源自独立仓库需求）：--no-revit 模式下允许主机完全无 Revit 安装。
		if (revitPath == null && !noRevit)
		{
			Console.ForegroundColor = ConsoleColor.Red;
			Console.Error.WriteLine("ERROR: Revit installation not found.");
			Console.Error.WriteLine($"Set Revit{TargetRevitVersion}Path environment variable or install Revit to a default location.");
			PrintUsage();
			Console.ResetColor();
			return 2;
		}
		string realDwgPath = null;
		if (revitPath == null)
		{
			// 无 Revit 且 --no-revit：强制置位，跳过全部 Revit 相关预载（纯单元测试场景）
			noRevit = true;
			Console.WriteLine("[SETUP] Revit installation not found; continuing in --no-revit mode (unit tests only)");
			Console.WriteLine("[SETUP] Revit native/managed environment preparation skipped");
		}
		else
		{
			Console.WriteLine("[SETUP] Revit path: " + revitPath);
			realDwgPath = RevitPathResolver.FindRealDwgPath(revitPath);
			if (realDwgPath != null)
			{
				Console.WriteLine("[SETUP] RealDWG path: " + realDwgPath);
			}
			else
			{
				Console.WriteLine("[SETUP] RealDWG path: not found (RevitAPIUI may fail to load)");
			}
			SetDllDirectory(revitPath);
#if R2025PLUS
			// .NET 8 运行时默认调用 SetDefaultDllDirectories，导致 SetDllDirectory 被忽略、PATH 不参与 native DLL 搜索。
			// 需通过 AddDllDirectory 将 Revit 与 RealDWG 目录加入 USER_DIRS 搜索列表，
			// 否则 RevitAPIUI.dll 的传递依赖（adui25.dll → acdb25.dll）无法解析（errno=126）。
			// .NET Framework 4.8（R2020-R2021 / R2022-R2024）不走此分支，SetDllDirectory 仍有效。
			SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS | LOAD_LIBRARY_SEARCH_USER_DIRS);
			AddDllDirectory(revitPath);
			if (realDwgPath != null)
			{
				AddDllDirectory(realDwgPath);
			}
			// Revit 2026 将 libfbxsdk-adsk.dll（RevitMFC.dll 的导入依赖）移至 Components\2026\<ver>\ 目录，
			// 不在 Revit 目录、RealDWG 目录或系统 PATH 中，需单独定位并加入搜索路径。
			string fbxSdkPath = FindFbxSdkPath(revitPath);
			if (fbxSdkPath != null)
			{
				Console.WriteLine("[SETUP] FBX SDK path: " + fbxSdkPath);
				AddDllDirectory(fbxSdkPath);
				// 预加载 libfbxsdk-adsk.dll，确保 RevitMFC.dll 的导入依赖在 LoadLibrary 前已就绪
				string fbxDll = Path.Combine(fbxSdkPath, "libfbxsdk-adsk.dll");
				if (File.Exists(fbxDll))
				{
					IntPtr fbxHandle = LoadLibrary(fbxDll);
					if (fbxHandle != IntPtr.Zero)
					{
						Console.WriteLine("[OK] Pre-loaded: libfbxsdk-adsk.dll (FBX SDK)");
					}
					else
					{
						Console.WriteLine($"[WARN] LoadLibrary failed for libfbxsdk-adsk.dll (errno={Marshal.GetLastWin32Error()})");
					}
				}
			}
			else
			{
				Console.WriteLine("[SETUP] FBX SDK path: not found (RevitMFC may fail to load)");
			}
#endif
			string currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
			string revitSearchPath = ((realDwgPath != null) ? (revitPath + ";" + realDwgPath) : revitPath);
			if (!currentPath.Split(';').Any((string p) => p.Trim().Equals(revitPath, StringComparison.OrdinalIgnoreCase)))
			{
				Environment.SetEnvironmentVariable("PATH", revitSearchPath + ";" + currentPath, EnvironmentVariableTarget.Process);
			}
			Environment.CurrentDirectory = revitPath;
			if (!noRevit)
			{
#if !R2025PLUS
				// R2025Plus: AddDllDirectory 已将 Revit/RealDWG 目录加入搜索路径，
				// OS 加载器可按需解析传递依赖，无需显式预加载 27 个 native DLL。
				// net48: SetDllDirectory 不如 AddDllDirectory 精确，仍需全量预加载确保依赖链完整。
				PreLoadNativeDependencies(revitPath);
#endif
			}
		}
		// Fix 4: --no-revit 模式下不将 Revit/RealDWG 目录加入搜索路径，
		// 防止 AssemblyResolve 懒加载 RevitAPI C++/CLI 程序集触发死锁。
		string[] assemblySearchPaths;
		if (noRevit)
		{
			assemblySearchPaths = new string[0];
		}
		else
		{
			assemblySearchPaths = (realDwgPath == null) ? new string[1] { revitPath } : new string[2] { revitPath, realDwgPath };
		}
		// Fix 4: 预加载 BaseDirectory 中的所有托管 DLL 到缓存（对齐 TestRunnerCommand 的两阶段策略）。
		PreloadAssembliesFromDirectory(AppDomain.CurrentDomain.BaseDirectory, noRevit);
		AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs e)
		{
			string name = new AssemblyName(e.Name).Name;
			// Cache hit — zero LoadFrom/LoadFile calls, zero recursion risk.
			Assembly cached;
			if (_preloadedAssemblies.TryGetValue(name, out cached))
			{
				return cached;
			}
			// Lazy-load via LoadFile (not LoadFrom) from search paths.
			// LoadFile avoids LoadFrom context recursion / StackOverflow.
			string[] array = assemblySearchPaths;
			foreach (string path in array)
			{
				string lazyPath = Path.Combine(path, name + ".dll");
				if (File.Exists(lazyPath))
				{
					try
					{
						Assembly asm = Assembly.LoadFile(lazyPath);
						_preloadedAssemblies[name] = asm;
						return asm;
					}
					catch
					{
						return null;
					}
				}
			}
			// 回退：从请求程序集所在目录查找依赖。
			// 用于解析通过 Assembly.LoadFile 加载的外部测试程序集（*.Tests.dll）的同目录依赖
			// （例如被测插件本体 DLL），这些依赖既不在应用基目录也不在 Revit/RealDWG 搜索路径中。
			Assembly requestingAssembly = e.RequestingAssembly;
			if (requestingAssembly != null)
			{
				string requestingDir = Path.GetDirectoryName(requestingAssembly.Location);
				if (!string.IsNullOrEmpty(requestingDir))
				{
					string candidate = Path.Combine(requestingDir, name + ".dll");
					if (File.Exists(candidate))
					{
						try
						{
							Assembly asm = Assembly.LoadFile(candidate);
							_preloadedAssemblies[name] = asm;
							return asm;
						}
						catch
						{
						}
					}
				}
			}
			return null;
	};
		// 显式预加载 Revit 托管程序集（RevitAPI.dll / RevitAPIUI.dll），
		// 供 RequiresRevitContext 类别的集成测试使用。
		// 单元测试不应触发 RevitAPI 模块初始化；
		// --no-revit 模式下跳过，避免 hosting components 冲突死锁。
		if (!noRevit)
		{
			// Fix 2: 超时保护 — 在独立线程加载，30s 超时后放弃并设置 noRevit 标志。
			if (!PreLoadManagedRevitAssemblies(revitPath))
			{
				noRevit = true;
			}
		}
		Console.WriteLine("[SETUP] Native search path configured. Starting tests...\n");
		LightweightTestRunner lightweightTestRunner = new LightweightTestRunner();
		string[] includeCategories = null;
		string[] excludeCategories = null;
		ParseCategoryArgs(args, ref includeCategories, ref excludeCategories);
		// Fix 1: --no-revit 模式下自动排除 RequiresRevitContext 类别（这些测试依赖 RevitAPI 类型）。
		if (noRevit)
		{
			excludeCategories = AutoExcludeCategory(excludeCategories, "RequiresRevitContext");
			Console.WriteLine("[SETUP] Auto-excluded category: RequiresRevitContext (no RevitAPI loaded)");
		}
		// SandboxSensitive 测试写入 Desktop/Downloads/Revit 安装目录等沙箱受限路径，
		// 沙箱会强制终止进程。始终排除以避免进程被杀。
		excludeCategories = AutoExcludeCategory(excludeCategories, "SandboxSensitive");
		Console.WriteLine("[SETUP] Auto-excluded category: SandboxSensitive");
		ParseTimeoutArg(args, lightweightTestRunner);

		// Fix（源自 revit二开 插件实战教训）: 不再写死测试程序集清单。两种发现方式：
		// (a) --assembly <路径或文件名>，可重复指定；
		// (b) 未指定 --assembly 时，自动发现宿主 exe 目录下全部 *.Tests.dll
		//     （排除 nunit*.dll 框架程序集与运行器自身）。
		List<string> assemblyPaths = ParseAssemblyArgs(args);
		if (assemblyPaths.Count == 0)
		{
			assemblyPaths.AddRange(DiscoverTestAssemblies(AppDomain.CurrentDomain.BaseDirectory));
		}
		if (assemblyPaths.Count == 0)
		{
			Console.ForegroundColor = ConsoleColor.Red;
			Console.Error.WriteLine("ERROR: No test assemblies found.");
			Console.Error.WriteLine();
			PrintUsage();
			Console.ResetColor();
			return 2;
		}

		// 逐程序集运行并汇总（对齐源 TestConsole 的多程序集聚合语义）。
		TestRunResult testRunResult = null;
		foreach (string assemblyPath in assemblyPaths)
		{
			Console.WriteLine("[SETUP] Loading test assembly: " + assemblyPath);
			TestRunResult incremental = lightweightTestRunner.RunTests(assemblyPath, includeCategories, excludeCategories);
			if (testRunResult == null)
			{
				testRunResult = incremental;
			}
			else
			{
				AggregateResults(ref testRunResult, incremental);
			}
		}

		string resultsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RevitTestConsole-TestResults.xml");
		TestResultWriter.WriteResults(testRunResult, resultsPath);
		Console.WriteLine("\n" + new string('=', 60));
		Console.WriteLine($"  Total:   {testRunResult.Total}");
		Console.WriteLine($"  Passed:  {testRunResult.Passed}");
		Console.WriteLine($"  Failed:  {testRunResult.Failed}");
		Console.WriteLine($"  Inconclusive: {testRunResult.Inconclusive}");
		Console.WriteLine($"  Skipped: {testRunResult.Skipped}");
		Console.WriteLine($"  Duration: {testRunResult.Duration.TotalSeconds:F2}s");
		foreach (string warning in testRunResult.Warnings)
		{
			Console.WriteLine("  [WARN] " + warning);
		}
		Console.WriteLine(new string('=', 60));
		Console.WriteLine("\nResults saved to: " + resultsPath);
		if (testRunResult.Failed > 0)
		{
			Console.ForegroundColor = ConsoleColor.Red;
			Console.WriteLine("\nFailed tests:");
			foreach (TestFixtureResult fixtureResult in testRunResult.FixtureResults)
			{
				foreach (TestMethodResult testResult in fixtureResult.TestResults)
				{
					if (testResult.Status == TestStatus.Failed)
					{
						Console.WriteLine("  [FAIL] " + testResult.FullName);
						Console.WriteLine("         " + testResult.Message);
					}
				}
			}
			Console.ResetColor();
		}
		return (testRunResult.Failed > 0) ? 1 : 0;
	}

	private static void AggregateResults(ref TestRunResult aggregate, TestRunResult incremental)
	{
		aggregate.Total += incremental.Total;
		aggregate.Passed += incremental.Passed;
		aggregate.Failed += incremental.Failed;
		aggregate.Inconclusive += incremental.Inconclusive;
		aggregate.Skipped += incremental.Skipped;
		aggregate.Duration += incremental.Duration;
		aggregate.Warnings.AddRange(incremental.Warnings);
		aggregate.FixtureResults.AddRange(incremental.FixtureResults);
	}

	/// <summary>
	/// 打印命令行用法。
	/// </summary>
	private static void PrintUsage()
	{
		Console.Error.WriteLine("Usage: RevitTestConsole.Host [options]");
		Console.Error.WriteLine();
		Console.Error.WriteLine("Options:");
		Console.Error.WriteLine("  --assembly <path>   Test assembly to run (repeatable). Relative paths resolve");
		Console.Error.WriteLine("                      against the current directory, then the host exe directory.");
		Console.Error.WriteLine("                      When omitted, all *.Tests.dll next to the host exe are");
		Console.Error.WriteLine("                      discovered automatically (nunit*.dll excluded).");
		Console.Error.WriteLine("  --include <list>    Comma-separated categories to include.");
		Console.Error.WriteLine("  --exclude <list>    Comma-separated categories to exclude.");
		Console.Error.WriteLine("  --timeout <ms>      Per-test timeout in milliseconds (0 = inline execution).");
		Console.Error.WriteLine("  --no-revit          Skip RevitAPI preloading (net48 shells; R2025Plus default).");
		Console.Error.WriteLine("  --revit             Force RevitAPI preloading (R2025Plus shell only).");
	}

	/// <summary>
	/// 解析命令行参数 --include / --exclude，支持 "--opt value" 与 "--opt=value" 两种形式。
	/// 末尾缺值时输出警告而非静默忽略。
	/// </summary>
	private static void ParseCategoryArgs(string[] args, ref string[] includeCategories, ref string[] excludeCategories)
	{
		const string includeOpt = "--include";
		const string excludeOpt = "--exclude";
		for (int i = 0; i < args.Length; i++)
		{
			string arg = args[i];
			if (TryGetOptionValue(args, ref i, includeOpt, out string includeValue))
			{
				includeCategories = includeValue.Split(',');
			}
			else if (TryGetOptionValue(args, ref i, excludeOpt, out string excludeValue))
			{
				excludeCategories = excludeValue.Split(',');
			}
		}
	}

	/// <summary>
	/// 解析 --assembly 参数（可重复）：收集测试程序集路径（相对路径先按当前目录解析，
	/// 不存在时回退到宿主 exe 目录，支持"只给文件名"的用法）。找不到的条目报错跳过。
	/// </summary>
	private static List<string> ParseAssemblyArgs(string[] args)
	{
		List<string> paths = new List<string>();
		const string assemblyOpt = "--assembly";
		for (int i = 0; i < args.Length; i++)
		{
			if (TryGetOptionValue(args, ref i, assemblyOpt, out string assemblyValue))
			{
				if (string.IsNullOrWhiteSpace(assemblyValue))
				{
					continue;
				}
				string resolved = ResolveAssemblyPath(assemblyValue);
				if (resolved == null)
				{
					Console.Error.WriteLine($"[ERROR] Test assembly not found: {assemblyValue}");
				}
				else
				{
					paths.Add(resolved);
				}
			}
		}
		return paths;
	}

	/// <summary>相对路径双目录解析（当前目录 → 宿主 exe 目录）；绝对路径直接校验存在性。</summary>
	private static string ResolveAssemblyPath(string rawPath)
	{
		if (Path.IsPathRooted(rawPath))
		{
			return File.Exists(rawPath) ? Path.GetFullPath(rawPath) : null;
		}
		string cwdCandidate = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, rawPath));
		if (File.Exists(cwdCandidate))
		{
			return cwdCandidate;
		}
		string baseDirCandidate = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, rawPath));
		if (File.Exists(baseDirCandidate))
		{
			return baseDirCandidate;
		}
		return null;
	}

	/// <summary>
	/// 自动发现目录下的测试程序集（*.Tests.dll）。
	/// 排除 nunit*.dll 框架程序集与运行器自身（RevitTestConsole.Core / RevitTestConsole.TestKit），
	/// 结果按文件名排序保证跨机器运行顺序稳定。
	/// </summary>
	private static List<string> DiscoverTestAssemblies(string directory)
	{
		List<string> paths = new List<string>();
		if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
		{
			return paths;
		}
		foreach (string dllFile in Directory.GetFiles(directory, "*.Tests.dll"))
		{
			string fileName = Path.GetFileName(dllFile);
			if (fileName.StartsWith("nunit", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			if (string.Equals(fileName, "RevitTestConsole.Core.dll", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(fileName, "RevitTestConsole.TestKit.dll", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}
			paths.Add(dllFile);
		}
		paths.Sort(StringComparer.OrdinalIgnoreCase);
		return paths;
	}

	/// <summary>
	/// 解析 --timeout 参数（毫秒），设置每测试超时。
	/// 用于防止 .NET 8 + RevitAPI JIT 死锁导致运行器挂起。
	/// </summary>
	private static void ParseTimeoutArg(string[] args, LightweightTestRunner runner)
	{
		const string timeoutOpt = "--timeout";
		for (int i = 0; i < args.Length; i++)
		{
			if (TryGetOptionValue(args, ref i, timeoutOpt, out string timeoutValue))
			{
				if (int.TryParse(timeoutValue, out int ms) && ms > 0)
				{
					runner.PerTestTimeoutMs = ms;
					Console.WriteLine($"[SETUP] Per-test timeout set to {ms}ms");
				}
				else
				{
					Console.Error.WriteLine($"[WARN] Invalid --timeout value '{timeoutValue}'; using default {runner.PerTestTimeoutMs}ms");
				}
			}
		}
	}

	/// <summary>
	/// 统一的选项取值器：支持 "--opt value" 与 "--opt=value" 两种形式。
	/// 匹配成功时前进索引并输出值。
	/// </summary>
	private static bool TryGetOptionValue(string[] args, ref int index, string optionName, out string value)
	{
		string arg = args[index];
		// 形式1: --option value
		if (string.Equals(arg, optionName, StringComparison.Ordinal))
		{
			if (index + 1 < args.Length)
			{
				value = args[++index];
				return true;
			}
			Console.Error.WriteLine($"[WARN] Option '{optionName}' is missing a value; ignored.");
			value = null;
			return false;
		}
		// 形式2: --option=value
		string prefix = optionName + "=";
		if (arg.StartsWith(prefix, StringComparison.Ordinal))
		{
			value = arg.Substring(prefix.Length);
			return true;
		}
		value = null;
		return false;
	}

	private static void PreLoadNativeDependencies(string revitPath)
	{
		// 按依赖顺序预加载：叶子依赖优先，避免 LoadLibrary 触发传递依赖解析失败（errno=126）。
		// acdb25.dll（RealDWG）→ adui25.dll → anav.dll → RevitMFC.dll 的所有 Revit 侧依赖 → RevitMFC.dll → RevitAPIUI.dll
		string[] array = new string[]
		{
			"adui25.dll", "anav.dll",
			"sfl400asu.dll", "ot1000asu.dll", "RWUXThemeSU2015.dll", "og1100asu.dll",
			"Utility.dll", "RevitDBCore.dll", "RevitDB.dll", "MaterialDB.dll", "PersistenceDB.dll",
			"Graphics.dll", "AdApplicationFrame.dll", "RevitUI.dll", "Interface.dll", "GeomUtil.dll",
			"ATFRevitCore.dll", "DesktopMFC.dll", "PointCloudAccess.dll", "EssentialsDB.dll",
			"AddInManagerUI.dll", "FamilyDB.dll", "AnalysisAppsDB.dll",
			"RevitMFC.dll",
			"RevitAPIFoundation.dll", "RevitAPIUI.dll", "AdWindows.dll"
		};
		string[] array2 = array;
		foreach (string text in array2)
		{
			string text2 = Path.Combine(revitPath, text);
			if (File.Exists(text2))
			{
				IntPtr intPtr = LoadLibrary(text2);
				if (intPtr == IntPtr.Zero)
				{
					Console.WriteLine($"[WARN] LoadLibrary failed for {text} (errno={Marshal.GetLastWin32Error()})");
				}
				else
				{
					Console.WriteLine("[OK] Pre-loaded: " + text);
				}
			}
		}
	}

	/// <summary>
	/// 显式通过 CLR Assembly.LoadFrom 加载 Revit 托管程序集（RevitAPI.dll / RevitAPIUI.dll）。
	/// 仅靠 native LoadLibrary 无法让 CLR 在类型解析时找到托管程序集；
	/// 此处预加载可避免测试 SetUp 中首次反射引用 Autodesk.Revit.DB 类型时同步阻塞。
	/// Fix 2: 在独立线程加载，30s 超时后放弃（避免 .NET 8 C++/CLI .cctor 死锁导致进程假死）。
	/// </summary>
	/// <returns>true 表示加载完成；false 表示超时（调用方应设置 noRevit 标志）。</returns>
	private static bool PreLoadManagedRevitAssemblies(string revitPath)
	{
		string[] managedAssemblies = new string[] { "RevitAPI.dll", "RevitAPIUI.dll", "RevitAPIFoundation.dll" };
		var loadThread = new Thread(() =>
		{
			foreach (string name in managedAssemblies)
			{
				string path = Path.Combine(revitPath, name);
				if (!File.Exists(path))
				{
					Console.WriteLine($"[WARN] Managed assembly not found: {name}");
					continue;
				}
				try
				{
					Assembly asm = Assembly.LoadFrom(path);
					Console.WriteLine($"[OK] Pre-loaded managed: {name} ({asm.FullName})");
				}
				catch (Exception ex)
				{
					Console.WriteLine($"[WARN] LoadFrom failed for {name}: {ex.Message}");
				}
			}
		})
		{ IsBackground = true };

		loadThread.Start();
		if (!loadThread.Join(30000))
		{
			Console.WriteLine("[WARN] PreLoadManagedRevitAssemblies timed out after 30s — likely C++/CLI .cctor deadlock. Continuing without RevitAPI.");
			return false;
		}
		return true;
	}

	private static string FindRevitInstallation()
	{
		return RevitPathResolver.FindRevitInstallation(TargetRevitVersion);
	}

	/// <summary>
	/// 在 Common Files\Autodesk Shared\Components\{ver}\ 下查找 libfbxsdk-adsk.dll。
	/// Revit 2026 将 FBX SDK 从 Revit 安装目录移至此处。
	/// </summary>
	private static string FindFbxSdkPath(string revitPath)
	{
		if (string.IsNullOrEmpty(revitPath))
		{
			return null;
		}
		string version = RevitPathResolver.ExtractVersionFromPathPublic(revitPath);
		if (version == null)
		{
			return null;
		}
		string[] roots =
		{
			"C:\\Program Files\\Common Files\\Autodesk Shared\\Components\\" + version,
			"D:\\Program Files\\Common Files\\Autodesk Shared\\Components\\" + version
		};
		foreach (string root in roots)
		{
			if (!Directory.Exists(root))
			{
				continue;
			}
			// Components\{ver}\ 下有版本子目录（如 1.7.0），取最新版本
			string[] versionDirs = Directory.GetDirectories(root);
			Array.Sort(versionDirs, StringComparer.OrdinalIgnoreCase);
			Array.Reverse(versionDirs);
			foreach (string verDir in versionDirs)
			{
				if (File.Exists(Path.Combine(verDir, "libfbxsdk-adsk.dll")))
				{
					return verDir;
				}
			}
		}
		return null;
	}

	/// <summary>
	/// Fix 1: 将指定类别追加到排除列表（去重）。
	/// </summary>
	private static string[] AutoExcludeCategory(string[] existing, string category)
	{
		if (existing == null || existing.Length == 0)
		{
			return new string[] { category };
		}
		if (existing.Any(c => string.Equals(c, category, StringComparison.OrdinalIgnoreCase)))
		{
			return existing;
		}
		string[] result = new string[existing.Length + 1];
		existing.CopyTo(result, 0);
		result[existing.Length] = category;
		return result;
	}

	/// <summary>
	/// Fix 4: 预加载目录中所有托管 DLL 到缓存。
	/// 使用 LoadFile（非 LoadFrom）避免 LoadFrom context 递归解析。
	/// skipRevitAssemblies=true 时跳过 RevitAPI/RevitAPIUI/RevitAPIFoundation 避免 C++/CLI 死锁。
	/// </summary>
	private static void PreloadAssembliesFromDirectory(string dir, bool skipRevitAssemblies)
	{
		if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
		{
			return;
		}
		foreach (string dllFile in Directory.GetFiles(dir, "*.dll"))
		{
			string simpleName = Path.GetFileNameWithoutExtension(dllFile);
			if (_preloadedAssemblies.ContainsKey(simpleName))
			{
				continue;
			}
			if (skipRevitAssemblies && IsRevitManagedAssembly(simpleName))
			{
				continue;
			}
			try
			{
				_preloadedAssemblies[simpleName] = Assembly.LoadFile(dllFile);
			}
			catch
			{
				// Native-only or invalid managed assembly — skip silently.
			}
		}
	}

	private static bool IsRevitManagedAssembly(string name)
	{
		return string.Equals(name, "RevitAPI", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(name, "RevitAPIUI", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(name, "RevitAPIFoundation", StringComparison.OrdinalIgnoreCase);
	}

	/// <summary>
	/// 将 APPDATA、LOCALAPPDATA、USERPROFILE 重定向到临时目录，
	/// 使 Environment.GetFolderPath(SpecialFolder.*) 返回沙箱可写路径。
	/// 同时创建对应的子目录结构，确保首次写入时目录存在。
	/// </summary>
	private static void RedirectUserProfilePaths()
	{
		// 使用宿主 exe 自身目录作为基准（沙箱保证可写），而非 GetTempPath()（在沙箱中可能同样被拒绝）。
		string baseDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestProfile_" + VersionSuffix);
		string appDataDir = Path.Combine(baseDir, "AppData", "Roaming");
		string localAppDataDir = Path.Combine(baseDir, "AppData", "Local");

		Directory.CreateDirectory(appDataDir);
		Directory.CreateDirectory(localAppDataDir);

		// APPDATA / LOCALAPPDATA 在 .NET Framework 和 .NET Core 中均直接读取环境变量，设置后立即生效。
		// USERPROFILE 设置后，.NET Core 8 的 GetFolderPath(SpecialFolder.Desktop/MyDocuments) 会拼接
		// {USERPROFILE}\Desktop / {USERPROFILE}\Documents；net48 则仍走注册表，但主要失败路径（AppData 日志/配置）已覆盖。
		Environment.SetEnvironmentVariable("USERPROFILE", baseDir);
		Environment.SetEnvironmentVariable("APPDATA", appDataDir);
		Environment.SetEnvironmentVariable("LOCALAPPDATA", localAppDataDir);

		Console.WriteLine($"[SETUP] User profile redirected to: {baseDir}");
	}
}
