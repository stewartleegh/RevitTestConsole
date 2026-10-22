using System;
using System.Collections.Generic;

namespace RevitTestConsole.Core;

public class TestFixtureResult
{
	public string FixtureName { get; set; }

	public List<TestMethodResult> TestResults { get; } = new List<TestMethodResult>();

	public TimeSpan Duration { get; set; }
}
