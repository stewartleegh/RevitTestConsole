using System;
using System.Collections.Generic;

namespace RevitTestConsole.Core;

public class TestRunResult
{
	public int Total { get; set; }

	public int Passed { get; set; }

	public int Failed { get; set; }

	public int Inconclusive { get; set; }

	public int Skipped { get; set; }

	public TimeSpan Duration { get; set; }

	/// <summary>
	/// 运行过程中的非致命警告（如某个程序集部分类型加载失败）。
	/// Console 输出在 Revit 内不可见，警告需通过结果对话框呈现给用户。
	/// </summary>
	public List<string> Warnings { get; } = new List<string>();

	public List<TestFixtureResult> FixtureResults { get; } = new List<TestFixtureResult>();
}
