using System;
using System.Collections.Generic;
using NUnit.Framework.Interfaces;

namespace RevitTestConsole.Core;

public class TestMethodResult
{
	public string TestName { get; set; }

	public string FullName { get; set; }

	public TestStatus Status { get; set; }

	public string Message { get; set; }

	public string StackTrace { get; set; }

	public TimeSpan Duration { get; set; }

	public List<string> Categories { get; } = new List<string>();
}
