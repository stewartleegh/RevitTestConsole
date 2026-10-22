using Autodesk.Revit.UI;
using NUnit.Framework;
using RevitTestConsole.TestKit;

namespace SampleCommand.Tests
{
	/// <summary>
	/// RevitIntegration 类别示例：继承 TestKit 的 RevitIntegrationTestBase。
	/// 无 Revit 上下文时基类的 OneTimeSetUp 会将整个夹具判为 Inconclusive
	/// （既不算通过也不算失败）；正常执行路径是通过本控制台在 Revit 进程内运行，
	/// 此时经 GetRevitApi&lt;UIApplication&gt;() 取回运行器注入的真实对象。
	/// </summary>
	[TestFixture]
	[Category("RevitIntegration")]
	public class RevitIntegrationTests : RevitIntegrationTestBase
	{
		[Test]
		public void UIApplication_IsInjectedByRunner()
		{
			UIApplication uiApplication = GetRevitApi<UIApplication>();

			Assert.That(uiApplication, Is.Not.Null);
		}

		[Test]
		public void RevitApplication_VersionName_IsNotEmpty()
		{
			UIApplication uiApplication = GetRevitApi<UIApplication>();

			Assert.That(uiApplication.Application.VersionName, Is.Not.Empty);
		}
	}
}
