using System.Threading.Tasks;
using NUnit.Framework;

namespace SampleCommand.Tests.TestRunnerPins
{
	/// <summary>
	/// 运行器隔离复现钉的跨 fixture 静态探针（非 fixture：无 TestFixture 特性、无实例
	/// [Test] 方法，运行器 DiscoverFixtures 的 fixture 判定不收编静态类）。
	/// </summary>
	internal static class TestRunnerIsolationPinsState
	{
		/// <summary>Pin2a async continuation 完成标记：仅在 await 完成之后置位。</summary>
		internal static volatile bool AsyncCompletionMarker;
	}

	/// <summary>
	/// Pin1a（断言残留源）：try-catch 吞掉失败断言。
	/// 机理：NUnit 3.13.3 的 TestExecutionContext 经 AsyncLocal 随 ExecutionContext 流动；
	/// 一旦 fixture 的 OneTimeSetUp/TearDown 在主线程（RunFixture 直接 Invoke）触碰
	/// NUnit 上下文（TestContext/Assert 均会触发），context 及其 CurrentResult 实例即进入
	/// 主线程 ExecutionContext，并经 new Thread 的 EC 捕获共享给其后所有测试线程——
	/// 整份运行共享一个 CurrentResult.AssertionResults 账本。level-0 失败断言无条件
	/// Append 进该账本后抛 AssertionException；异常被吞后残留仍在，毒化后续无关测试的
	/// Assert.Multiple（scope 退出时以残留文本抛出假失败）。
	/// 本钉的 OneTimeSetUp 负责形成共享通道（真实全量运行中由先序 fixture 的主线程触碰
	/// 自然触发；无触碰的隔离运行中各测试线程懒创建独立账本，缺陷不可见）。
	/// 本钉修复前后均为 Passed；泄漏受害者见 Pin1b。
	/// </summary>
	[TestFixture]
	[Category("RunnerPins")]
	public class Pin1aAssertionLeakSourceFixture
	{
		[OneTimeSetUp]
		public void OneTimeSetUp()
		{
			// 共享通道形成条件：OneTimeSetUp 在主线程执行，后续夹具 Victim 可见先行残留。
			_ = TestContext.CurrentContext;
		}

		[Test]
		public void SwallowedAssertionFailure_LeavesResidualInSharedLedger()
		{
			try
			{
				Assert.That(1 + 1, Is.EqualTo(3),
					"PIN1A_SOURCE_MARKER: failed assertion intentionally swallowed; its ledger entry leaks across tests under the built-in runner");
			}
			catch (AssertionException)
			{
				// 故意吞掉：模拟泄漏来源（观察者异常隔离吞断言、负向验证 try-catch 场景同理）。
			}
		}
	}

	/// <summary>
	/// Pin1b（泄漏受害者）：全过的 Assert.Multiple。
	/// 修复前必红（依赖 Pin1a 的 OneTimeSetUp 形成的主线程共享通道）：Multiple scope 退出
	/// 发现 Pin1a 残留账本即抛 AssertionException，message 为异源文本（含 PIN1A_SOURCE_MARKER），
	/// 可归因为跨测试残留泄漏而非本测试逻辑缺陷——判据：失败 message 与本测试断言文本不符
	/// 且堆栈顶为 NUnit.Framework.Assert.Multiple；
	/// 修复后（InvokeTestBody 起始清账本并复位 MultipleAssertLevel）必绿。
	/// NUnit 正式宿主下本钉恒绿——正式生命周期每测试独立 result，只有运行器的共享使其变红。
	/// </summary>
	[TestFixture]
	[Category("RunnerPins")]
	public class Pin1bMultipleVictimFixture
	{
		[Test]
		public void Multiple_AllPassing_NotPoisonedByResiduals()
		{
			Assert.Multiple(() =>
			{
				Assert.That(2 + 2, Is.EqualTo(4));
				Assert.That("pin1b", Is.Not.EqualTo("pin1a"));
			});
		}
	}

	/// <summary>
	/// Pin2a（async 假通过源）：await 之后置位完成标记。
	/// 修复前：运行器 Invoke 丢弃返回的 Task，方法体在首个 await 处即"返回"——
	/// 测试被误判 Passed（假通过），标记置位发生在线程池 continuation 中迟到执行；
	/// 修复后：GetAwaiter().GetResult() 真等待完成，标记在进入下一个测试前已置位。
	/// </summary>
	[TestFixture]
	[Category("RunnerPins")]
	public class Pin2aAsyncCompletionSourceFixture
	{
		[Test]
		public async Task AsyncBody_SetsCompletionMarkerOnlyAfterAwait()
		{
			await Task.Delay(300);
			TestRunnerIsolationPinsState.AsyncCompletionMarker = true;
		}
	}

	/// <summary>
	/// Pin2b（async 完成观察者）：
	/// 修复前必红：Pin2a 未被等待，观察者先于 continuation 运行，标记未置位；
	/// 修复后必绿：Pin2a 完成后才轮到本测试。300ms 延迟确保 fixture 串行切换
	/// （毫秒级）必先于标记置位，时序稳定；4 夹具按 FullName 排序连续执行
	/// （运行器 DiscoverFixtures 的 orderby t.FullName）。
	/// </summary>
	[TestFixture]
	[Category("RunnerPins")]
	public class Pin2bAsyncCompletionObserverFixture
	{
		[Test]
		public void PrecedingAsyncTest_MustCompleteBeforeObserverRuns()
		{
			Assert.That(TestRunnerIsolationPinsState.AsyncCompletionMarker, Is.True,
				"PIN2B_NOT_AWAITED: preceding async test continuation had not completed when this observer ran (runner Invoke does not await async Task)");
		}
	}
}
