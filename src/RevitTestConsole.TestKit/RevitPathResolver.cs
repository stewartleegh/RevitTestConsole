using System;
using System.IO;
using System.Linq;

namespace RevitTestConsole.TestKit;

/// <summary>
/// Revit 安装路径解析：优先 Revit{version}Path 环境变量，其次 C/D 盘默认安装目录，
/// 最后按版本距离最近回退。另提供 RealDWG 共享目录定位。
/// </summary>
public static class RevitPathResolver
{
	private static readonly string[] DefaultRoots =
	{
		"C:\\Program Files\\Autodesk",
		"D:\\Program Files\\Autodesk"
	};

	private static readonly string[] AllVersions =
	{
		"2020", "2021", "2022", "2023", "2024", "2025", "2026"
	};

	/// <summary>
	/// 查找 Revit 安装路径（无目标版本，按 2020→2026 顺序回退）。
	/// 保留此重载仅为向后兼容；新调用方应使用 <see cref="FindRevitInstallation(string)"/>
	/// 并显式传入目标版本，以保证三版本（R2020-R2021 / R2022-R2024 / R2025Plus）
	/// 测试运行器加载到对应 Revit API。
	/// </summary>
	public static string FindRevitInstallation()
	{
		return FindRevitInstallation(null);
	}

	/// <summary>
	/// 按目标版本优先、最近版本回退的顺序查找 Revit 安装路径。
	/// </summary>
	/// <param name="targetVersion">目标 Revit 年份（如 "2020"/"2024"/"2026"）；为空则按 2020→2026 顺序回退。</param>
	/// <returns>Revit 安装目录；未找到返回 null。</returns>
	public static string FindRevitInstallation(string targetVersion)
	{
		if (!string.IsNullOrWhiteSpace(targetVersion))
		{
			string envPath = Environment.GetEnvironmentVariable($"Revit{targetVersion}Path");
			if (!string.IsNullOrWhiteSpace(envPath) && Directory.Exists(envPath))
			{
				return envPath;
			}

			foreach (string root in DefaultRoots)
			{
				string candidate = Path.Combine(root, $"Revit {targetVersion}");
				if (Directory.Exists(candidate))
				{
					return candidate;
				}
			}
		}

		string[] fallbackVersions = GetFallbackVersions(targetVersion);
		foreach (string version in fallbackVersions)
		{
			string envPath = Environment.GetEnvironmentVariable($"Revit{version}Path");
			if (!string.IsNullOrWhiteSpace(envPath) && Directory.Exists(envPath))
			{
				return envPath;
			}
		}

		foreach (string root in DefaultRoots)
		{
			foreach (string version in fallbackVersions)
			{
				string candidate = Path.Combine(root, $"Revit {version}");
				if (Directory.Exists(candidate))
				{
					return candidate;
				}
			}
		}

		return null;
	}

	/// <summary>按与目标版本的年份距离升序（同距取较小年份）生成回退版本序列。</summary>
	private static string[] GetFallbackVersions(string targetVersion)
	{
		if (string.IsNullOrWhiteSpace(targetVersion) || !int.TryParse(targetVersion, out int targetYear))
		{
			return AllVersions;
		}

		return AllVersions
			.Select(v => new { Version = v, Year = int.Parse(v), Distance = Math.Abs(int.Parse(v) - targetYear) })
			.OrderBy(x => x.Distance)
			.ThenBy(x => x.Year)
			.Select(x => x.Version)
			.ToArray();
	}

	/// <summary>
	/// 定位 RealDWG 共享目录（按 Revit 路径推断版本号，检查 acdb*.dll 验证有效性）。
	/// </summary>
	public static string FindRealDwgPath(string revitPath)
	{
		if (string.IsNullOrEmpty(revitPath))
		{
			return null;
		}
		string text = ExtractVersionFromPath(revitPath);
		if (text == null)
		{
			return null;
		}
		string[] array = new string[2]
		{
			"C:\\Program Files\\Common Files\\Autodesk Shared\\RealDWG Shared " + text,
			"D:\\Program Files\\Common Files\\Autodesk Shared\\RealDWG Shared " + text
		};
		string[] array2 = array;
		foreach (string text2 in array2)
		{
			// 验证目录有效性：RealDWG 2026 不再包含 ac1st24.dll，
			// 改为检查 acdb*.dll（AutoCAD 数据库核心，所有 RealDWG 版本均含）。
			if (Directory.Exists(text2) && Directory.GetFiles(text2, "acdb*.dll").Length > 0)
			{
				return text2;
			}
		}
		return null;
	}

	private static string ExtractVersionFromPath(string revitPath)
	{
		string fileName = Path.GetFileName(revitPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
		if (fileName == null)
		{
			return null;
		}
		string[] array = fileName.Split(' ');
		int result;
		return (array.Length >= 2 && int.TryParse(array[array.Length - 1], out result)) ? array[array.Length - 1] : null;
	}

	/// <summary>
	/// 公开包装，供 TestConsole 宿主的 FindFbxSdkPath 复用版本提取逻辑。
	/// </summary>
	public static string ExtractVersionFromPathPublic(string revitPath)
	{
		return ExtractVersionFromPath(revitPath);
	}
}
