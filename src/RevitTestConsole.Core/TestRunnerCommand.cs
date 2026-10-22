using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using RevitTestConsole.TestKit;

namespace RevitTestConsole.Core;

/// <summary>
/// Revit 进程内的测试执行命令（IExternalCommand）。
/// 交互入口弹出类别过滤对话框；CI/autorun 入口见 <see cref="RunHeadless"/>。
/// 测试程序集不写死清单，改为自动发现本 dll 同目录下的 *.Tests.dll。
/// </summary>
[Transaction(TransactionMode.Manual)]
public class TestRunnerCommand : IExternalCommand
{
	private static readonly string[] DefaultExcludeCategories = new string[] { "RevitIntegration" };

	private static bool _assemblyResolverInstalled;

	// Cache of preloaded assemblies (loaded via LoadFile to avoid Load context conflicts).
	// Populated once in InstallAssemblyResolver so that the AssemblyResolve handler never
	// needs to call Assembly.LoadFrom (which causes recursive resolution → StackOverflow
	// inside Revit's already-deep call stack).
	// Fix 4（源自本人的revit二开工具实战教训）: resolve 处理器只查缓存、零 LoadFrom 调用。
	private static readonly Dictionary<string, Assembly> _preloadedAssemblies = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);

	public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
	{
		try
		{
			string[] excludeCategories = ShowCategoryFilterDialog();
			if (excludeCategories == null)
			{
				return Result.Cancelled;
			}

			TestRunResult testRunResult = RunTestsCore(commandData.Application, null, excludeCategories, 60000);

			string text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "RevitTestConsole-TestResults.xml");
			TestResultWriter.WriteResults(testRunResult, text);

			string excludeInfo = (excludeCategories.Length > 0)
				? $"\nExcluded categories: {string.Join(", ", excludeCategories)}"
				: "\nAll categories included";
			string warningInfo = (testRunResult.Warnings.Count > 0)
				? $"\n\nWarnings ({testRunResult.Warnings.Count}):\n- {string.Join("\n- ", testRunResult.Warnings)}"
				: "";
			string mainContent = $"{testRunResult.Total} tests: {testRunResult.Passed} passed, {testRunResult.Failed} failed, {testRunResult.Inconclusive} inconclusive, {testRunResult.Skipped} skipped{excludeInfo}\n\nDuration: {testRunResult.Duration.TotalSeconds:F1}s{warningInfo}\n\nResults saved to:\n{text}";
			TaskDialog dialog = new TaskDialog("Revit Test Console")
			{
				MainIcon = (testRunResult.Failed > 0) ? TaskDialogIcon.TaskDialogIconWarning : TaskDialogIcon.TaskDialogIconInformation,
				MainInstruction = (testRunResult.Failed > 0) ? "Some tests failed" : "All tests passed",
				MainContent = mainContent,
				CommonButtons = TaskDialogCommonButtons.Ok
			};
			dialog.Show();
			return Result.Succeeded;
		}
		catch (Exception ex)
		{
			message = ex.Message;
			TaskDialog errorDialog = new TaskDialog("Revit Test Console - Error")
			{
				MainIcon = TaskDialogIcon.TaskDialogIconError,
				MainInstruction = "Test runner failed",
				MainContent = ex.Message + "\n\n" + ex.StackTrace,
				CommonButtons = TaskDialogCommonButtons.Ok
			};
			errorDialog.Show();
			return Result.Failed;
		}
	}

	/// <summary>
	/// 测试执行核心（交互/自动两入口共享）：安装程序集解析器 → 初始化
	/// <see cref="RevitTestContext"/> → 自动发现插件目录下的 *.Tests.dll → 运行测试 → 复位上下文。
	/// </summary>
	/// <param name="application">当前 UIApplication。</param>
	/// <param name="includeCategories">包含类别（null/空 = 不限）。</param>
	/// <param name="excludeCategories">排除类别。</param>
	/// <param name="perTestTimeoutMs">每用例超时（毫秒），≤0 用运行器默认值。</param>
	private static TestRunResult RunTestsCore(UIApplication application, string[] includeCategories, string[] excludeCategories, int perTestTimeoutMs)
	{
		InstallAssemblyResolver();

		RevitTestContext.Initialize(application);
		LightweightTestRunner lightweightTestRunner = new LightweightTestRunner();
		// 始终赋值：0 = 内联执行（测试体在调用线程/Revit API 上下文），>0 = 工作线程 + 超时守护。
		lightweightTestRunner.PerTestTimeoutMs = perTestTimeoutMs;
		// Fix（源自本人的revit二开工具实战教训）: 不写死测试程序集清单，改为自动发现插件目录下
		// 以 .Tests.dll 结尾的程序集——新测试项目只需把 DLL 复制到本目录即可被发现。
		string pluginDir = Path.GetDirectoryName(typeof(TestRunnerCommand).Assembly.Location) ?? "";
		string[] assemblyPaths = DiscoverTestAssemblies(pluginDir);
		try
		{
			return lightweightTestRunner.RunTests(assemblyPaths, includeCategories, excludeCategories);
		}
		finally
		{
			RevitTestContext.Reset();
		}
	}

	/// <summary>
	/// 自动发现目录下的测试程序集（*.Tests.dll）。
	/// 排除 nunit*.dll 框架程序集与运行器自身（RevitTestConsole.Core / RevitTestConsole.TestKit），
	/// 结果按文件名排序保证跨机器运行顺序稳定。
	/// </summary>
	private static string[] DiscoverTestAssemblies(string pluginDir)
	{
		List<string> paths = new List<string>();
		if (!string.IsNullOrEmpty(pluginDir) && Directory.Exists(pluginDir))
		{
			foreach (string dllFile in Directory.GetFiles(pluginDir, "*.Tests.dll"))
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
		}
		paths.Sort(StringComparer.OrdinalIgnoreCase);
		return paths.ToArray();
	}

	// ---- 非交互自动运行模式（CI / 脚本驱动） ----
	// 由 TestRunnerApplication 在环境变量 REVITTESTCONSOLE_TESTRUNNER_AUTORUN=1 时经 Idling 触发。
	// 环境变量：
	//   REVITTESTCONSOLE_TESTRUNNER_INCLUDE  逗号分隔的包含类别（默认空 = 不限）
	//   REVITTESTCONSOLE_TESTRUNNER_EXCLUDE  逗号分隔的排除类别（默认 RevitIntegration，与交互默认一致）
	//   REVITTESTCONSOLE_TESTRUNNER_OUTPUT   结果 XML 输出路径（默认 Desktop\RevitTestConsole-TestResults.xml）
	//   REVITTESTCONSOLE_TESTRUNNER_TIMEOUT  每用例超时毫秒（默认 0 = 内联执行：真实 Revit 内测试体
	//                                        须在 API 上下文线程运行，见 LightweightTestRunner 内联分支）
	// 完成后写 <output>.done 哨兵文件供外部脚本探测；异常落盘 <output>.error。

	public const string AutorunEnabledEnvVar = "REVITTESTCONSOLE_TESTRUNNER_AUTORUN";

	public static void RunHeadless(UIApplication application)
	{
		string outputPath = Environment.GetEnvironmentVariable("REVITTESTCONSOLE_TESTRUNNER_OUTPUT");
		if (string.IsNullOrWhiteSpace(outputPath))
		{
			outputPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "RevitTestConsole-TestResults.xml");
		}
		string logPath = outputPath + ".log";
		TextWriter originalOut = Console.Out;
		StreamWriter logWriter = null;
		try
		{
			string directory = Path.GetDirectoryName(outputPath);
			if (!string.IsNullOrEmpty(directory))
			{
				Directory.CreateDirectory(directory);
			}
			logWriter = new StreamWriter(logPath, false) { AutoFlush = true };
			Console.SetOut(logWriter);
			Console.WriteLine($"[AUTORUN] Started at {DateTime.Now:O}");

			string[] includeCategories = SplitCategoryList(Environment.GetEnvironmentVariable("REVITTESTCONSOLE_TESTRUNNER_INCLUDE"));
			string[] excludeCategories = SplitCategoryList(Environment.GetEnvironmentVariable("REVITTESTCONSOLE_TESTRUNNER_EXCLUDE"));
			if (includeCategories == null && excludeCategories == null)
			{
				// 与交互入口默认口径一致：未显式指定时排除 RevitIntegration。
				excludeCategories = DefaultExcludeCategories;
			}
			int perTestTimeoutMs = 0;
			string timeoutEnv = Environment.GetEnvironmentVariable("REVITTESTCONSOLE_TESTRUNNER_TIMEOUT");
			int parsed;
			if (int.TryParse(timeoutEnv, out parsed) && parsed > 0)
			{
				perTestTimeoutMs = parsed;
			}
			Console.WriteLine($"[AUTORUN] include=[{string.Join(",", includeCategories ?? new string[0])}] "
				+ $"exclude=[{string.Join(",", excludeCategories ?? new string[0])}] perTestTimeoutMs={perTestTimeoutMs}");

			TestRunResult result = RunTestsCore(application, includeCategories, excludeCategories, perTestTimeoutMs);
			TestResultWriter.WriteResults(result, outputPath);
			Console.WriteLine($"[AUTORUN] Finished: total={result.Total} passed={result.Passed} failed={result.Failed} "
				+ $"inconclusive={result.Inconclusive} skipped={result.Skipped} duration={result.Duration.TotalSeconds:F1}s");
			foreach (string warning in result.Warnings)
			{
				Console.WriteLine("[AUTORUN-WARN] " + warning);
			}
		}
		catch (Exception ex)
		{
			string errorText = ex.ToString();
			for (Exception current = ex; current != null; current = current.InnerException)
			{
				FileLoadException fileLoad = current as FileLoadException;
				if (fileLoad != null && !string.IsNullOrWhiteSpace(fileLoad.FusionLog))
				{
					errorText += Environment.NewLine + fileLoad.FusionLog;
				}
			}
			errorText += Environment.NewLine + "Runner: " + typeof(TestRunnerCommand).Assembly.Location;
			errorText += Environment.NewLine + "LightweightTestRunner: " + typeof(LightweightTestRunner).Assembly.Location;
			foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
			{
				if (assembly.GetName().Name == "nunit.framework")
				{
					errorText += Environment.NewLine + "Loaded NUnit: " + assembly.GetName().FullName + " from " + assembly.Location;
				}
			}
			Console.WriteLine("[AUTORUN-ERROR] " + errorText);
			try
			{
				File.WriteAllText(outputPath + ".error", errorText);
			}
			catch
			{
				// 落盘失败不再向上抛，保证 .done 哨兵仍能写出。
			}
		}
		finally
		{
			Console.SetOut(originalOut);
			if (logWriter != null)
			{
				logWriter.Dispose();
			}
			try
			{
				File.WriteAllText(outputPath + ".done", DateTime.Now.ToString("O"));
			}
			catch
			{
				// 哨兵写失败时外部脚本按超时兜底杀进程。
			}
		}
	}

	private static string[] SplitCategoryList(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
		{
			return null;
		}
		return value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
	}

	private string[] ShowCategoryFilterDialog()
	{
		TaskDialog dlg = new TaskDialog("Revit Test Console - Category Filter")
		{
			MainIcon = TaskDialogIcon.TaskDialogIconInformation,
			MainInstruction = "Select test categories to run",
			MainContent = "Choose which tests to execute:\n\n· Run All — execute every test\n· Default — skip RevitIntegration (recommended)\n· Unit Tests Only — skip RevitIntegration and Integration",
			AllowCancellation = true
		};
		dlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Run All Tests", "Execute all tests regardless of category");
		dlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Default (exclude RevitIntegration)", "Skip tests that require deep Revit model interaction");
		dlg.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "Unit Tests Only", "Skip RevitIntegration and Integration categories");

		TaskDialogResult result = dlg.Show();

		switch (result)
		{
		case TaskDialogResult.CommandLink1:
			return new string[0];
		case TaskDialogResult.CommandLink2:
			return DefaultExcludeCategories;
		case TaskDialogResult.CommandLink3:
			return new string[] { "RevitIntegration", "Integration" };
		default:
			return null;
		}
	}

	private static string _pluginDir;

	private static void InstallAssemblyResolver()
	{
		if (_assemblyResolverInstalled)
		{
			return;
		}
		_pluginDir = Path.GetDirectoryName(typeof(TestRunnerCommand).Assembly.Location) ?? "";
		if (!string.IsNullOrEmpty(_pluginDir))
		{
			// Phase 1: Preload ALL dlls from our plugin directory into a cache.
			// We use Assembly.LoadFile (not LoadFrom) to load into an independent
			// context, bypassing the .NET Framework "golden rule" where LoadFrom
			// would silently return an already-loaded older assembly from the Load
			// context (e.g. the host add-in's older Core.dll that lacks newer types).
			//
			// By preloading everything upfront, the AssemblyResolve handler never
			// calls Assembly.LoadFrom at resolution time. This eliminates the
			// recursive resolution chain that causes StackOverflowException inside
			// Revit's already-deep call stack (3-4 AssemblyResolve handlers × 18+
			// transitive dependencies = stack overflow).
			PreloadAllDependencies(_pluginDir);

			// Phase 2: Register a lightweight resolver that only returns cached
			// assemblies — zero LoadFrom calls, zero recursion risk.
			AppDomain.CurrentDomain.AssemblyResolve += delegate(object sender, ResolveEventArgs args)
			{
				string assemblyName = new AssemblyName(args.Name).Name;
				Assembly cached;
				if (_preloadedAssemblies.TryGetValue(assemblyName, out cached))
				{
					return cached;
				}
				return null;
			};
		}
		_assemblyResolverInstalled = true;
	}

	/// <summary>
	/// Preloads all DLLs from the plugin directory using Assembly.LoadFile.
	/// LoadFile loads into an independent context, so even if the host add-in has
	/// already loaded an older version of an assembly into the Load context,
	/// we get a separate Assembly object with all the types we need.
	/// </summary>
	private static void PreloadAllDependencies(string pluginDir)
	{
		foreach (string dllFile in Directory.GetFiles(pluginDir, "*.dll"))
		{
			string simpleName = Path.GetFileNameWithoutExtension(dllFile);
			try
			{
				AssemblyName expectedName = AssemblyName.GetAssemblyName(dllFile);
				if (simpleName == "nunit.framework")
				{
					Console.WriteLine($"[AUTORUN-INFO] NUnit expected: {expectedName.FullName} from {dllFile}");
					foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
					{
						if (a.GetName().Name == simpleName)
						{
							Console.WriteLine($"[AUTORUN-INFO] NUnit loaded candidate: {a.GetName().FullName} from {a.Location}");
						}
					}
				}
				// Reuse an existing assembly only when its full identity matches the file.
				// A same-named older NUnit must not satisfy a newer test assembly reference.
				bool alreadyLoaded = false;
				foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
				{
					if (string.Equals(a.GetName().FullName, expectedName.FullName, StringComparison.Ordinal))
					{
						_preloadedAssemblies[simpleName] = a;
						alreadyLoaded = true;
						break;
					}
				}
				if (!alreadyLoaded)
				{
					Assembly loaded = Assembly.LoadFile(dllFile);
					_preloadedAssemblies[simpleName] = loaded;
				}
			}
			catch (Exception ex)
			{
				if (simpleName == "nunit.framework")
				{
					Console.WriteLine($"[AUTORUN-WARN] NUnit preload failed: {ex}");
				}
			}
		}
		Assembly cachedNunit;
		if (_preloadedAssemblies.TryGetValue("nunit.framework", out cachedNunit))
		{
			Console.WriteLine($"[AUTORUN-INFO] NUnit cached: {cachedNunit.GetName().FullName} from {cachedNunit.Location}");
		}
	}
}
