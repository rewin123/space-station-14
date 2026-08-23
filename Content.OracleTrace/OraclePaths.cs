using System.Diagnostics;
using System.IO;

namespace Content.OracleTrace;

/// <summary>
/// Где лежат сценарии и куда класть трассы, плюс отпечаток оригинала для
/// meta.json.
/// </summary>
public static class OraclePaths
{
    /// <summary>
    /// Переменная окружения-переопределение. Нужна, чтобы CI мог складывать
    /// трассы куда угодно, не трогая код.
    /// </summary>
    public const string DirEnvVar = "ORACLE_TRACE_DIR";

    private static string _cachedRoot;

    /// <summary>
    /// Корень каталога трасс — <c>ts_ss14/traces</c>.
    ///
    /// Путь ищется от каталога сборки вверх до каталога <c>ss14_ai</c>, затем
    /// вбок в соседний <c>ts_ss14</c>. Хардкодить абсолютный путь нельзя (он
    /// разный на машине разработчика и в CI), а «относительно cwd» ломается
    /// потому, что cwd у dotnet test — не корень репозитория.
    /// </summary>
    public static string TraceRoot()
    {
        if (_cachedRoot != null)
            return _cachedRoot;

        var env = Environment.GetEnvironmentVariable(DirEnvVar);
        if (!string.IsNullOrWhiteSpace(env))
            return _cachedRoot = Path.GetFullPath(env);

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && dir.Name != "ss14_ai")
            dir = dir.Parent;

        if (dir?.Parent == null)
        {
            throw new InvalidOperationException(
                $"не найден каталог оригинала ss14_ai выше {AppContext.BaseDirectory}; " +
                $"задай {DirEnvVar} явно");
        }

        var traces = Path.Combine(dir.Parent.FullName, "ts_ss14", "traces");
        if (!Directory.Exists(traces))
        {
            throw new DirectoryNotFoundException(
                $"каталог трасс {traces} не существует; задай {DirEnvVar} явно");
        }

        return _cachedRoot = traces;
    }

    /// <summary>Корень оригинала (репозиторий ss14_ai).</summary>
    public static string OriginRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && dir.Name != "ss14_ai")
            dir = dir.Parent;

        if (dir == null)
            throw new InvalidOperationException($"не найден каталог оригинала выше {AppContext.BaseDirectory}");

        return dir.FullName;
    }

    public static string ScenarioDir(string scenario) => Path.Combine(TraceRoot(), scenario);

    /// <summary>
    /// Коммит оригинала и признак «рабочее дерево грязное».
    ///
    /// Грязное дерево записывается ЧЕСТНО, а не прячется: трасса, снятая с
    /// незакоммиченных правок, не воспроизводима по одному только sha, и тот,
    /// кто будет разбирать расхождение через месяц, обязан это видеть.
    /// </summary>
    public static (string Sha, bool Dirty) OriginRevision() => Revision(OriginRoot());

    /// <summary>
    /// Коммит движка (подмодуль RobustToolbox). Пишется отдельно от коммита
    /// контента: симуляция дверей и контейнеров живёт наполовину в движке
    /// (шина событий, контейнеры, широкая фаза), и трасса, снятая на другом
    /// RobustToolbox, — другая трасса, сколько бы ни совпадал sha контента.
    /// </summary>
    public static (string Sha, bool Dirty) EngineRevision()
        => Revision(Path.Combine(OriginRoot(), "RobustToolbox"));

    private static (string Sha, bool Dirty) Revision(string root)
    {
        var sha = RunGit(root, "rev-parse HEAD")?.Trim();
        if (string.IsNullOrEmpty(sha))
            throw new InvalidOperationException($"git rev-parse HEAD в {root} ничего не вернул");

        var status = RunGit(root, "status --porcelain") ?? string.Empty;
        return (sha, status.Trim().Length > 0);
    }

    private static string RunGit(string workDir, string args)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var proc = Process.Start(psi);
        if (proc == null)
            throw new InvalidOperationException("не удалось запустить git");

        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        proc.WaitForExit();

        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"git {args} упал с кодом {proc.ExitCode}: {stderr}");

        return stdout;
    }
}
