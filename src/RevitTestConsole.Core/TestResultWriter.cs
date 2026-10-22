using System;
using System.Linq;
using System.Text;
using System.Xml;
using NUnit.Framework.Interfaces;

namespace RevitTestConsole.Core;

public static class TestResultWriter
{
	public static void WriteResults(TestRunResult result, string filePath)
	{
		XmlWriterSettings settings = new XmlWriterSettings
		{
			Indent = true,
			Encoding = Encoding.UTF8,
			OmitXmlDeclaration = false
		};
		using XmlWriter xmlWriter = XmlWriter.Create(filePath, settings);
		xmlWriter.WriteStartDocument();
		xmlWriter.WriteStartElement("test-run");
		xmlWriter.WriteAttributeString("id", "0");
		xmlWriter.WriteAttributeString("name", "RevitTestConsole.Tests");
		xmlWriter.WriteAttributeString("fullname", "RevitTestConsole.Tests");
		xmlWriter.WriteAttributeString("testcasecount", result.Total.ToString());
		xmlWriter.WriteAttributeString("result", (result.Failed > 0) ? "Failed" : ((result.Passed > 0) ? "Passed" : "Inconclusive"));
		xmlWriter.WriteAttributeString("total", result.Total.ToString());
		xmlWriter.WriteAttributeString("passed", result.Passed.ToString());
		xmlWriter.WriteAttributeString("failed", result.Failed.ToString());
		xmlWriter.WriteAttributeString("inconclusive", result.Inconclusive.ToString());
		xmlWriter.WriteAttributeString("skipped", result.Skipped.ToString());
		xmlWriter.WriteAttributeString("duration", result.Duration.TotalSeconds.ToString("F3"));
		xmlWriter.WriteAttributeString("start-time", DateTime.Now.ToString("o"));
		xmlWriter.WriteAttributeString("end-time", DateTime.Now.ToString("o"));
		foreach (TestFixtureResult fixtureResult in result.FixtureResults)
		{
			xmlWriter.WriteStartElement("test-suite");
			xmlWriter.WriteAttributeString("id", fixtureResult.FixtureName.GetHashCode().ToString());
			xmlWriter.WriteAttributeString("name", fixtureResult.FixtureName);
			xmlWriter.WriteAttributeString("fullname", fixtureResult.FixtureName);
			xmlWriter.WriteAttributeString("type", "TestFixture");
			xmlWriter.WriteAttributeString("testcasecount", fixtureResult.TestResults.Count.ToString());
			bool flag = fixtureResult.TestResults.Any((TestMethodResult t) => t.Status == TestStatus.Failed);
			xmlWriter.WriteAttributeString("result", flag ? "Failed" : "Passed");
			xmlWriter.WriteAttributeString("duration", fixtureResult.Duration.TotalSeconds.ToString("F3"));
			foreach (TestMethodResult testResult in fixtureResult.TestResults)
			{
				xmlWriter.WriteStartElement("test-case");
				xmlWriter.WriteAttributeString("id", testResult.FullName.GetHashCode().ToString());
				xmlWriter.WriteAttributeString("name", testResult.TestName);
				xmlWriter.WriteAttributeString("fullname", testResult.FullName);
				xmlWriter.WriteAttributeString("result", testResult.Status.ToString());
				xmlWriter.WriteAttributeString("duration", testResult.Duration.TotalSeconds.ToString("F3"));
				if (testResult.Categories.Count > 0)
				{
					xmlWriter.WriteStartElement("properties");
					foreach (string category in testResult.Categories)
					{
						xmlWriter.WriteStartElement("property");
						xmlWriter.WriteAttributeString("name", "Category");
						xmlWriter.WriteAttributeString("value", category);
						xmlWriter.WriteEndElement();
					}
					xmlWriter.WriteEndElement();
				}
				if (testResult.Status == TestStatus.Failed && (!string.IsNullOrEmpty(testResult.Message) || !string.IsNullOrEmpty(testResult.StackTrace)))
				{
					xmlWriter.WriteStartElement("failure");
					if (!string.IsNullOrEmpty(testResult.Message))
					{
						xmlWriter.WriteStartElement("message");
						xmlWriter.WriteCData(testResult.Message);
						xmlWriter.WriteEndElement();
					}
					if (!string.IsNullOrEmpty(testResult.StackTrace))
					{
						xmlWriter.WriteStartElement("stack-trace");
						xmlWriter.WriteCData(testResult.StackTrace);
						xmlWriter.WriteEndElement();
					}
					xmlWriter.WriteEndElement();
				}
				else if ((testResult.Status == TestStatus.Inconclusive || testResult.Status == TestStatus.Skipped) && !string.IsNullOrEmpty(testResult.Message))
				{
					xmlWriter.WriteStartElement("reason");
					xmlWriter.WriteStartElement("message");
					xmlWriter.WriteCData(testResult.Message);
					xmlWriter.WriteEndElement();
					xmlWriter.WriteEndElement();
				}
				xmlWriter.WriteEndElement();
			}
			xmlWriter.WriteEndElement();
		}
		xmlWriter.WriteEndElement();
		xmlWriter.WriteEndDocument();
	}
}
