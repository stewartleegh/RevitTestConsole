using Autodesk.Revit.Attributes;
using Autodesk.Revit.UI;
using System;
using System.Linq;

namespace RevitTestConsole.Core;

/// <summary>
/// Revit 进程内的 add-in 入口：注册 Ribbon 按钮触发 <see cref="TestRunnerCommand"/>，
/// 并在 REVITTESTCONSOLE_TESTRUNNER_AUTORUN=1 时经 Idling 事件自动执行（CI 模式）。
/// </summary>
[Transaction(TransactionMode.Manual)]
public class TestRunnerApplication : IExternalApplication
{
	private static bool _autorunExecuted;

	public Result OnStartup(UIControlledApplication application)
	{
		// CI 清单与 ProgramData 常驻清单并存启动时，同名 Ribbon 面板/按钮会抛冲突异常；
		// 功能区注册失败不阻断自动运行模式（autorun 仅需 Idling 事件）。
		try
		{
			string location = typeof(TestRunnerApplication).Assembly.Location;
			RibbonPanel panel = application.GetRibbonPanels()
				.FirstOrDefault(p => string.Equals(p.Name, "Revit Test Console", StringComparison.Ordinal))
				?? application.CreateRibbonPanel("Revit Test Console");
			PushButtonData buttonData = new PushButtonData("RevitTestConsoleRunner", "Revit Tests", location, typeof(TestRunnerCommand).FullName);
			PushButton button = panel.AddItem(buttonData) as PushButton;
			if (button != null)
			{
				button.ToolTip = "Run Revit Test Console tests inside Revit";
			}
		}
		catch (Exception)
		{
			// 面板/按钮与既有安装冲突时忽略——不影响下方 autorun 注册。
		}
		// 非交互自动运行模式（CI / 脚本驱动）：REVITTESTCONSOLE_TESTRUNNER_AUTORUN=1 时
		// 在首个 Idling 空闲点自动执行测试（不弹类别对话框），结果路径等经
		// REVITTESTCONSOLE_TESTRUNNER_* 环境变量传入，详见 TestRunnerCommand.RunHeadless。
		if (string.Equals(
			Environment.GetEnvironmentVariable(TestRunnerCommand.AutorunEnabledEnvVar),
			"1",
			StringComparison.Ordinal))
		{
			application.Idling += OnAutorunIdling;
		}
		return Result.Succeeded;
	}

	private static void OnAutorunIdling(object sender, Autodesk.Revit.UI.Events.IdlingEventArgs e)
	{
		if (_autorunExecuted)
		{
			return;
		}
		_autorunExecuted = true;
		UIApplication uiApplication = sender as UIApplication;
		if (uiApplication != null)
		{
			uiApplication.Idling -= OnAutorunIdling;
			TestRunnerCommand.RunHeadless(uiApplication);
		}
	}

	public Result OnShutdown(UIControlledApplication application)
	{
		return Result.Succeeded;
	}
}
