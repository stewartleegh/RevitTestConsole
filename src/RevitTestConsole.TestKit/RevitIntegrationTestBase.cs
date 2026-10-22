using NUnit.Framework;

namespace RevitTestConsole.TestKit;

/// <summary>
/// Revit 集成测试基类：测试类继承本类并标记 [Category("RevitIntegration")]。
/// 无 Revit 上下文（TestConsole --no-revit 模式 / add-in 未注入）时整个夹具
/// 经 OneTimeSetUp 判 Inconclusive，避免被误报为失败。
/// </summary>
public abstract class RevitIntegrationTestBase
{
	[OneTimeSetUp]
	public void VerifyRevitContext()
	{
		if (!RevitTestContext.IsAvailable)
		{
			Assert.Inconclusive("Revit context not available. Integration tests must be run via the Revit Test Console runner inside Revit.");
		}
	}

	/// <summary>取回运行器注入的 Revit API 对象（如 UIApplication；上下文未初始化时为 null）。</summary>
	protected T GetRevitApi<T>() where T : class
	{
		return RevitTestContext.RawUIApplication as T;
	}
}
