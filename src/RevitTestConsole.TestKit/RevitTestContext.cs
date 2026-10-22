using System;

namespace RevitTestConsole.TestKit;

/// <summary>
/// Revit 测试上下文：由运行器（Revit 内 add-in 或 TestConsole 宿主）在初始化时注入
/// 当前 UIApplication。刻意以 object 保存以避免 TestKit 对 RevitAPI 的硬引用，
/// 测试代码经 <see cref="RawUIApplication"/> 取回后再向具体 API 类型转换。
/// </summary>
public static class RevitTestContext
{
	private static object _uiApplication;

	private static bool _isInitialized;

	public static bool IsAvailable => _isInitialized;

	public static object RawUIApplication => _isInitialized ? _uiApplication : null;

	public static void Initialize(object uiApplication)
	{
		_uiApplication = uiApplication ?? throw new ArgumentNullException("uiApplication");
		_isInitialized = true;
	}

	/// <summary>复位上下文（每轮测试结束后调用，避免跨轮状态串扰）。</summary>
	public static void Reset()
	{
		_uiApplication = null;
		_isInitialized = false;
	}
}
