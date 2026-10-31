using System.Diagnostics;
using System.Text.RegularExpressions;
using Serilog;

namespace HackWizard.Core.Efi;

public sealed record SmbiosIdentity(string Model, string Serial, string Mlb, string SystemUuid);

public interface ISerialGenerator { SmbiosIdentity Generate(string model); }

/// <summary>Runs OpenCore's macserial.exe ("-m MODEL -g -n 1" prints "SERIAL | MLB").</summary>
public sealed partial class MacSerialGenerator : ISerialGenerator
{
    private readonly string _exe;
    public MacSerialGenerator(string macserialExe) => _exe = macserialExe;

    [GeneratedRegex(@"^\s*([A-Z0-9]{11,12})\s*\|\s*([A-Z0-9]{13,17})\s*$", RegexOptions.Multiline)]
    private static partial Regex Line();

    public SmbiosIdentity Generate(string model)
    {
        var (code, output) = ProcessRunner.Run(_exe, $"-m {model} -g -n 1", TimeSpan.FromSeconds(30));
        if (code != 0) throw new InvalidOperationException($"macserial failed ({code}): {output}");
        var m = Line().Match(output);
        if (!m.Success) throw new InvalidOperationException($"Unexpected macserial output for {model}: {output}");
        return new SmbiosIdentity(model, m.Groups[1].Value, m.Groups[2].Value, Guid.NewGuid().ToString().ToUpperInvariant());
    }
}

public sealed class FixedSerialGenerator : ISerialGenerator
{
    private readonly SmbiosIdentity _id;
    public FixedSerialGenerator(SmbiosIdentity id) => _id = id;
    public SmbiosIdentity Generate(string model) => _id with { Model = model };
}

public sealed record OcValidateResult(bool Ok, int ExitCode, string Output);

public static class OcValidate
{
    public static OcValidateResult Run(string ocvalidateExe, string configPath)
    {
        var (code, output) = ProcessRunner.Run(ocvalidateExe, $"\"{configPath}\"", TimeSpan.FromSeconds(60));
        Log.Information("ocvalidate exit {Code}: {Output}", code, output);
        return new OcValidateResult(code == 0, code, output);
    }
}

public static class ProcessRunner
{
    public static (int code, string output) Run(string exe, string args, TimeSpan timeout, string? workDir = null)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = workDir ?? Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {exe}");
        var so = p.StandardOutput.ReadToEndAsync(); var se = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit((int)timeout.TotalMilliseconds)) { try { p.Kill(true); } catch { } return (-1, "Timed out"); }
        return (p.ExitCode, (so.Result + se.Result).Trim());
    }
}
