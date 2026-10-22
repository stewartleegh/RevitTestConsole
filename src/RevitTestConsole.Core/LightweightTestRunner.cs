using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using NUnit.Framework.Interfaces;

namespace RevitTestConsole.Core;

public class LightweightTestRunner
{
	private string[] _includeCategories;

	private string[] _excludeCategories;

	/// <summary>
	/// 每个测试方法的超时时间（毫秒）。超时后标记为失败并继续后续测试。
	/// 默认 60 秒。用于防止 .NET 8 + RevitAPI C++/CLI JIT 死锁导致整个运行器挂起。
	/// </summary>
	public int PerTestTimeoutMs { get; set; } = 60000;

	public TestRunResult RunTests(string assemblyPath, string[] includeCategories = null, string[] excludeCategories = null)
	{
		return RunTests(new string[] { assemblyPath }, includeCategories, excludeCategories);
	}

	public TestRunResult RunTests(string[] assemblyPaths, string[] includeCategories = null, string[] excludeCategories = null)
	{
		_includeCategories = includeCategories ?? new string[0];
		_excludeCategories = excludeCategories ?? new string[0];
		TestRunResult testRunResult = new TestRunResult();
		Stopwatch stopwatch = Stopwatch.StartNew();
		foreach (string assemblyPath in assemblyPaths)
		{
			// Use LoadFile instead of LoadFrom to load test assemblies into an
			// independent context. This is critical for avoiding .NET Framework's
			// "golden rule" where LoadFrom would silently return an already-loaded
			// assembly from the Load context (e.g., the host application's old Core.dll).
			// With LoadFile, CLR doesn't auto-consult the Load context for dependency
			// resolution, so our AssemblyResolve handler gets a chance to return the
			// correct (newer) version of the dependency.
			System.Diagnostics.Debug.WriteLine($"[TestRunner] Loading assembly: {assemblyPath}");
			Console.WriteLine($"[TestRunner] Loading assembly: {assemblyPath}");
			List<Type> list;
			try
			{
				Assembly assembly = Assembly.LoadFile(assemblyPath);
				Console.WriteLine($"[TestRunner] LoadFile done, discovering fixtures...");
				list = DiscoverFixtures(assembly, testRunResult.Warnings);
			}
			catch (Exception ex)
			{
				// 单个测试程序集加载/发现失败不应中断其余程序集的测试。
				// 常见原因：测试 DLL 与已加载的插件 DLL 版本漂移（如类型被移动/删除）。
				string warning = $"Failed to load test assembly {Path.GetFileName(assemblyPath)}: {ex.Message}";
				testRunResult.Warnings.Add(warning);
				Console.Error.WriteLine($"[TestRunner] ERROR: {warning}");
				continue;
			}
			Console.WriteLine($"[TestRunner] Discovered {list.Count} fixtures in {assemblyPath}");
			foreach (Type item in list)
			{
				System.Diagnostics.Debug.WriteLine($"[TestRunner] Running fixture: {item.FullName}");
				Console.WriteLine($"[TestRunner] Running fixture: {item.FullName}");
				TestFixtureResult testFixtureResult = RunFixture(item);
				testRunResult.FixtureResults.Add(testFixtureResult);
				foreach (TestMethodResult testResult in testFixtureResult.TestResults)
				{
					testRunResult.Total++;
					switch (testResult.Status)
					{
					case TestStatus.Passed:
						testRunResult.Passed++;
						break;
					case TestStatus.Failed:
						testRunResult.Failed++;
						break;
					case TestStatus.Inconclusive:
						testRunResult.Inconclusive++;
						break;
					case TestStatus.Skipped:
						testRunResult.Skipped++;
						break;
					}
				}
			}
		}
		stopwatch.Stop();
		testRunResult.Duration = stopwatch.Elapsed;
		return testRunResult;
	}

	private List<Type> DiscoverFixtures(Assembly assembly, List<string> warnings)
	{
		Type[] types;
		try
		{
			types = assembly.GetTypes();
		}
		catch (ReflectionTypeLoadException ex)
		{
			types = ex.Types ?? new Type[0];
			int failedCount = types.Count((Type t) => t == null);
			string warning = $"Partial type discovery in {assembly.GetName().Name}: {failedCount} type(s) failed to load " +
				"(likely version drift between test assemblies and already-loaded plugin assemblies — redeploy the Test Runner). " +
				string.Join("; ", (ex.LoaderExceptions ?? new Exception[0])
					.Where((Exception e) => e != null)
					.Select((Exception e) => e.Message)
					.Distinct());
			warnings?.Add(warning);
			Console.Error.WriteLine($"[TestRunner] WARNING: {warning}");
			if (ex.LoaderExceptions != null)
			{
				foreach (Exception loaderEx in ex.LoaderExceptions)
				{
					if (loaderEx != null)
					{
						Console.Error.WriteLine($"[TestRunner] LoaderException: {loaderEx.Message}");
						Console.Error.WriteLine($"[TestRunner] LoaderException stack: {loaderEx.StackTrace}");
					}
				}
			}
		}
		return (from t in types
			where t != null
			where t.GetCustomAttribute<TestFixtureAttribute>() != null || t.GetMethods(BindingFlags.Instance | BindingFlags.Public).Any((MethodInfo m) => m.GetCustomAttribute<TestAttribute>() != null)
			where !t.IsAbstract
			where MatchesCategories(t)
			orderby t.FullName
			select t).ToList();
	}

	private bool MatchesCategories(Type fixtureType)
	{
		List<string> source = (from c in fixtureType.GetCustomAttributes<CategoryAttribute>()
			select c.Name).ToList();
		if (_excludeCategories.Length != 0 && source.Any((string c) => _excludeCategories.Contains(c, StringComparer.OrdinalIgnoreCase)))
		{
			return false;
		}
		if (_includeCategories.Length != 0 && !source.Any((string c) => _includeCategories.Contains(c, StringComparer.OrdinalIgnoreCase)))
		{
			return false;
		}
		return true;
	}

	private bool MatchesTestCategories(MethodInfo method)
	{
		List<string> list = (from c in method.GetCustomAttributes<CategoryAttribute>()
			select c.Name).ToList();
		IEnumerable<string> collection = from c in method.DeclaringType.GetCustomAttributes<CategoryAttribute>()
			select c.Name;
		list.AddRange(collection);
		if (_excludeCategories.Length != 0 && list.Any((string c) => _excludeCategories.Contains(c, StringComparer.OrdinalIgnoreCase)))
		{
			return false;
		}
		if (_includeCategories.Length != 0 && !list.Any((string c) => _includeCategories.Contains(c, StringComparer.OrdinalIgnoreCase)))
		{
			return false;
		}
		return true;
	}

	private TestFixtureResult RunFixture(Type fixtureType)
	{
		TestFixtureResult testFixtureResult = new TestFixtureResult
		{
			FixtureName = fixtureType.FullName
		};
		Stopwatch stopwatch = Stopwatch.StartNew();
		object obj = null;
		try
		{
			obj = Activator.CreateInstance(fixtureType);
		}
		catch (Exception ex)
		{
			TestMethodResult item = new TestMethodResult
			{
				TestName = fixtureType.FullName + ".ctor",
				FullName = fixtureType.FullName + ".ctor",
				Status = TestStatus.Failed,
				Message = (ex.InnerException?.Message ?? ex.Message),
				StackTrace = (ex.InnerException?.StackTrace ?? ex.StackTrace)
			};
			testFixtureResult.TestResults.Add(item);
			stopwatch.Stop();
			testFixtureResult.Duration = stopwatch.Elapsed;
			return testFixtureResult;
		}
		List<MethodInfo> methodsWithAttribute = GetMethodsWithAttribute(fixtureType, typeof(OneTimeSetUpAttribute), baseFirst: true);
		List<MethodInfo> methodsWithAttribute2 = GetMethodsWithAttribute(fixtureType, typeof(OneTimeTearDownAttribute), baseFirst: false);
		List<MethodInfo> methodsWithAttribute3 = GetMethodsWithAttribute(fixtureType, typeof(SetUpAttribute), baseFirst: true);
		List<MethodInfo> methodsWithAttribute4 = GetMethodsWithAttribute(fixtureType, typeof(TearDownAttribute), baseFirst: false);
		List<MethodInfo> list = (from m in fixtureType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
			where m.GetCustomAttribute<TestAttribute>() != null
			where MatchesTestCategories(m)
			select m).ToList();
		try
		{
			InvokeMethods(obj, methodsWithAttribute);
		}
		catch (Exception ex2)
		{
			Exception ex3 = ((ex2 is TargetInvocationException ex4) ? ex4.InnerException : ex2);
			bool flag = ex3 is InconclusiveException;
			foreach (MethodInfo item3 in list)
			{
				testFixtureResult.TestResults.Add(new TestMethodResult
				{
					TestName = item3.Name,
					FullName = fixtureType.FullName + "." + item3.Name,
					Status = ((!flag) ? TestStatus.Failed : TestStatus.Inconclusive),
					Message = "OneTimeSetUp failed: " + (ex3?.Message ?? ex2.Message),
					StackTrace = (ex3?.StackTrace ?? ex2.StackTrace)
				});
			}
			InvokeMethodsSafe(obj, methodsWithAttribute2);
			stopwatch.Stop();
			testFixtureResult.Duration = stopwatch.Elapsed;
			return testFixtureResult;
		}
		foreach (MethodInfo item4 in list)
		{
			TestMethodResult item2 = RunSingleTest(obj, item4, methodsWithAttribute3, methodsWithAttribute4);
			testFixtureResult.TestResults.Add(item2);
		}
		InvokeMethodsSafe(obj, methodsWithAttribute2);
		stopwatch.Stop();
		testFixtureResult.Duration = stopwatch.Elapsed;
		return testFixtureResult;
	}

	private TestMethodResult RunSingleTest(object instance, MethodInfo testMethod, List<MethodInfo> setUpMethods, List<MethodInfo> tearDownMethods)
	{
		string testName = instance.GetType().FullName + "." + testMethod.Name;
		System.Diagnostics.Debug.WriteLine($"[TestRunner] >>> START: {testName}");
		Console.WriteLine($"[TestRunner] >>> START: {testName}");
		TestMethodResult testMethodResult = new TestMethodResult
		{
			TestName = testMethod.Name,
			FullName = instance.GetType().FullName + "." + testMethod.Name
		};
		IEnumerable<string> collection = from c in testMethod.GetCustomAttributes<CategoryAttribute>()
			select c.Name;
		testMethodResult.Categories.AddRange(collection);
		IgnoreAttribute customAttribute = testMethod.GetCustomAttribute<IgnoreAttribute>();
		if (customAttribute != null)
		{
			testMethodResult.Status = TestStatus.Skipped;
			testMethodResult.Message = GetAttributeReason(customAttribute);
			return testMethodResult;
		}
		ExplicitAttribute customAttribute2 = testMethod.GetCustomAttribute<ExplicitAttribute>();
		if (customAttribute2 != null)
		{
			testMethodResult.Status = TestStatus.Skipped;
			testMethodResult.Message = GetAttributeReason(customAttribute2) ?? "Explicit test";
			return testMethodResult;
		}

		// 确定超时：NUnit [Timeout] 属性优先，否则使用 PerTestTimeoutMs
		int timeoutMs = PerTestTimeoutMs;
		try
		{
			TimeoutAttribute timeoutAttr = testMethod.GetCustomAttribute<TimeoutAttribute>();
			if (timeoutAttr != null)
			{
				var timeoutProp = typeof(TimeoutAttribute).GetProperty("Timeout",
					BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				if (timeoutProp != null)
				{
					int attrTimeout = (int)timeoutProp.GetValue(timeoutAttr);
					if (attrTimeout > 0)
						timeoutMs = attrTimeout;
				}
			}
		}
		catch { /* 忽略 TimeoutAttribute 读取失败 */ }

		Stopwatch stopwatch = Stopwatch.StartNew();

		Exception testException = null;
		bool setUpFailed = false;
		bool timedOut = false;

		void InvokeTestBody()
		{
			try
			{
				Console.WriteLine($"[TestRunner]     SetUp: {string.Join(", ", setUpMethods.Select(m => m.Name))}");
				InvokeMethods(instance, setUpMethods);
				Console.WriteLine($"[TestRunner]     SetUp done");
			}
			catch (Exception ex)
			{
				setUpFailed = true;
				testException = ex;
				return;
			}
			try
			{
				Console.WriteLine($"[TestRunner]     invoking: {testMethod.Name}");
				testMethod.Invoke(instance, null);
			}
			catch (Exception ex)
			{
				testException = ex;
			}
			try
			{
				InvokeMethods(instance, tearDownMethods);
			}
			catch (Exception ex)
			{
				if (testException == null)
					testException = ex;
			}
		}

		if (timeoutMs <= 0)
		{
			// 内联执行（无超时守护）：真实 Revit 内的集成测试必须在调用线程（Revit API
			// 上下文）执行——工作线程直调 Revit API 会报 "not within an API context"，
			// 而 ExternalEvent 封送又会因 API 线程被 Idling 处理程序占用形成死锁。
			InvokeTestBody();
		}
		else
		{
			// 在独立线程上运行测试，超时后放弃线程并继续后续测试。
			// .NET 8 + RevitAPI C++/CLI 的 JIT 死锁会导致线程永久阻塞，
			// 但主线程可以在 Join 超时后继续执行不依赖 RevitAPI 类型的测试。
			Thread testThread = new Thread(InvokeTestBody)
			{ IsBackground = true };

			testThread.Start();

			if (!testThread.Join(timeoutMs))
			{
				timedOut = true;
				Console.WriteLine($"[TestRunner]     *** TIMEOUT after {timeoutMs}ms, abandoning thread ***");
				Console.Out.Flush();
			}
		}

		stopwatch.Stop();
		testMethodResult.Duration = stopwatch.Elapsed;

		if (timedOut)
		{
			testMethodResult.Status = TestStatus.Failed;
			bool noRevit = AppContext.TryGetSwitch("TestConsole.NoRevit", out bool noRevitValue) && noRevitValue;
			testMethodResult.Message = noRevit
				? $"Test timed out after {timeoutMs}ms (thread abandoned). RevitAPI was not loaded (--no-revit); this is a genuine hang (e.g. lock/SyncBlock wait), not a RevitAPI JIT deadlock."
				: $"Test timed out after {timeoutMs}ms (thread abandoned — likely RevitAPI JIT deadlock in .NET 8)";
		}
		else if (setUpFailed)
		{
			testMethodResult.Status = TestStatus.Failed;
			testMethodResult.Message = "SetUp failed: " + (testException.InnerException?.Message ?? testException.Message);
			testMethodResult.StackTrace = testException.InnerException?.StackTrace ?? testException.StackTrace;
		}
		else if (testException != null)
		{
			Exception innerException = (testException is TargetInvocationException ex4) ? ex4.InnerException : testException;
			if (innerException is AssertionException ex3)
			{
				testMethodResult.Status = TestStatus.Failed;
				testMethodResult.Message = ex3.Message;
				testMethodResult.StackTrace = ex3.StackTrace;
			}
			else if (innerException is InconclusiveException ex5)
			{
				testMethodResult.Status = TestStatus.Inconclusive;
				testMethodResult.Message = ex5.Message;
				testMethodResult.StackTrace = ex5.StackTrace;
			}
			else if (innerException is IgnoreException ex6)
			{
				testMethodResult.Status = TestStatus.Skipped;
				testMethodResult.Message = ex6.Message;
				testMethodResult.StackTrace = ex6.StackTrace;
			}
			else if (innerException is SuccessException)
			{
				testMethodResult.Status = TestStatus.Passed;
			}
			else
			{
				testMethodResult.Status = TestStatus.Failed;
				testMethodResult.Message = innerException?.Message ?? testException.Message;
				testMethodResult.StackTrace = innerException?.StackTrace ?? testException.StackTrace;
			}
		}
		else
		{
			testMethodResult.Status = TestStatus.Passed;
		}

		System.Diagnostics.Debug.WriteLine($"[TestRunner] <<< END: {testName} => {testMethodResult.Status}");
		return testMethodResult;
	}

	private List<MethodInfo> GetMethodsWithAttribute(Type type, Type attributeType, bool baseFirst = false)
	{
		List<MethodInfo> list = new List<MethodInfo>();
		Type type2 = type;
		while (type2 != null && type2 != typeof(object))
		{
			MethodInfo[] methods = type2.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			foreach (MethodInfo method in methods)
			{
				if (method.GetCustomAttribute(attributeType) != null && !list.Any((MethodInfo m) => m.MetadataToken == method.MetadataToken))
				{
					// baseFirst=true: SetUp/OneTimeSetUp 需要基类先执行（NUnit 标准行为），用 Insert(0) 让后遍历到的基类方法排在前面。
					// baseFirst=false: TearDown/OneTimeTearDown 需要子类先执行，用 Add 保持遍历顺序（子类→基类）。
					if (baseFirst)
					{
						list.Insert(0, method);
					}
					else
					{
						list.Add(method);
					}
				}
			}
			type2 = type2.BaseType;
		}
		return list;
	}

	private void InvokeMethods(object instance, List<MethodInfo> methods)
	{
		foreach (MethodInfo method in methods)
		{
			method.Invoke(instance, null);
		}
	}

	private void InvokeMethodsSafe(object instance, List<MethodInfo> methods)
	{
		foreach (MethodInfo method in methods)
		{
			try
			{
				method.Invoke(instance, null);
			}
			catch
			{
				// 清理性反射调用（OneTimeTearDown/TearDown）失败不应覆盖已判定的测试结果，
				// 此处有意静默吞掉，与 RunFixture 主路径"仅在无测试异常时才计入失败"同一口径。
			}
		}
	}

	private static string GetAttributeReason(Attribute attr)
	{
		try
		{
			PropertyInfo property = attr.GetType().GetProperty("Reason", BindingFlags.Instance | BindingFlags.Public);
			if (property != null)
			{
				return property.GetValue(attr) as string;
			}
			FieldInfo field = attr.GetType().GetField("_reason", BindingFlags.Instance | BindingFlags.NonPublic);
			if (field != null)
			{
				return field.GetValue(attr) as string;
			}
			ParameterInfo parameterInfo = (from c in attr.GetType().GetConstructors()
				where c.GetParameters().Length != 0
				select c).SelectMany((ConstructorInfo c) => c.GetParameters()).FirstOrDefault((ParameterInfo p) => p.ParameterType == typeof(string));
			if (parameterInfo != null)
			{
				FieldInfo fieldInfo = attr.GetType().GetFields(BindingFlags.Instance | BindingFlags.NonPublic).FirstOrDefault();
				if (fieldInfo != null)
				{
					return fieldInfo.GetValue(attr) as string;
				}
			}
			return attr.ToString();
		}
		catch
		{
			return attr.GetType().Name;
		}
	}
}
