using Autodesk.Revit.DB;
using NUnit.Framework;

namespace SampleCommand.Tests
{
	/// <summary>
	/// RequiresRevitContext 类别示例：直接使用 RevitAPI 托管类型。
	/// 这类用例必须运行在 Revit 进程内（由 Revit 测试控制台的 add-in 触发执行），
	/// 以 --no-revit 方式运行控制台时会被自动排除，既不执行也不计为失败。
	/// </summary>
	[TestFixture]
	[Category("RequiresRevitContext")]
	public class RevitApiSmokeTests
	{
		[Test]
		public void ElementId_CanBeConstructed_WithPositiveInteger()
		{
			// ElementId(long) 在 2025+ 生效，ElementId(int) 在 2024 及更早生效，
			// int 字面量可隐式转换为 long，两类构造器语法在三壳下均能编译。
			ElementId elementId = new ElementId(1001);

			Assert.That(elementId, Is.Not.EqualTo(ElementId.InvalidElementId));
		}

		[Test]
		public void BuiltInCategory_Walls_CanBeMappedToElementId()
		{
			ElementId wallCategoryId = new ElementId(BuiltInCategory.OST_Walls);

			Assert.That(wallCategoryId, Is.Not.EqualTo(ElementId.InvalidElementId));
		}
	}
}
