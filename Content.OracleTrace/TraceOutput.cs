using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;

namespace Content.OracleTrace;

/// <summary>
/// Запись артефактов сценария: cs.jsonl.zst и meta.json.
/// </summary>
public static class TraceOutput
{
    /// <summary>
    /// Сжатие — внешним zstd.
    ///
    /// ПОЧЕМУ не библиотекой: в BCL .NET zstd нет, а тянуть NuGet-пакет ради
    /// одного вызова пришлось бы через Directory.Packages.props — файл
    /// ОРИГИНАЛА, который трогать нельзя. Формат выбран не нами: tracediff
    /// читает .zst через zstdDecompressSync из node:zlib.
    /// </summary>
    private const string ZstdBinary = "zstd";

    public static void Write(string dir, string scenario, IReadOnlyList<string> lines, JsonObject meta)
    {
        Directory.CreateDirectory(dir);

        var jsonl = new StringBuilder();
        foreach (var line in lines)
            jsonl.Append(line).Append('\n');

        var plain = Path.Combine(Path.GetTempPath(), $"oracle-{scenario}-{Environment.ProcessId}.jsonl");
        File.WriteAllText(plain, jsonl.ToString(), new UTF8Encoding(false));

        var target = Path.Combine(dir, "cs.jsonl.zst");
        Compress(plain, target);
        File.Delete(plain);

        File.WriteAllText(Path.Combine(dir, "meta.json"), meta.ToJsonString(new System.Text.Json.JsonSerializerOptions
        {
            WriteIndented = true,
        }) + "\n", new UTF8Encoding(false));
    }

    private static void Compress(string source, string target)
    {
        var psi = new ProcessStartInfo(ZstdBinary)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        psi.ArgumentList.Add("-19");
        psi.ArgumentList.Add("-q");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(target);
        psi.ArgumentList.Add(source);

        Process proc;
        try
        {
            proc = Process.Start(psi);
        }
        catch (Exception e)
        {
            // Не подменяем .zst обычным .jsonl: tracediff выбирает декомпрессию
            // по расширению, и "почти правильный" файл под правильным именем
            // сломается позже и непонятнее, чем понятная ошибка здесь.
            throw new InvalidOperationException(
                $"не найден {ZstdBinary}; трасса пишется в .zst, потому что этот формат читает tracediff", e);
        }

        using (proc)
        {
            var stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            if (proc.ExitCode != 0)
                throw new InvalidOperationException($"{ZstdBinary} упал с кодом {proc.ExitCode}: {stderr}");
        }

        var info = new FileInfo(target);
        if (!info.Exists || info.Length == 0)
            throw new InvalidOperationException($"{target} не создан или пуст");
    }
}
