using System.Globalization;
using System.Text;

namespace TapoCam.Core;

public sealed record ConnectionTestStep(string Name, bool Ok, TimeSpan Elapsed, string Detail);

public sealed record ConnectionTestReport(
    string Summary, bool Success, IReadOnlyList<ConnectionTestStep> Steps, string? ToolOutput = null)
{
    public static ConnectionTestReport Message(string summary) => new(summary, false, []);

    public string Details()
    {
        var text = new StringBuilder();
        foreach (var step in Steps)
            text.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"[{(step.Ok ? "OK" : "FALHA")}] {step.Name} ({step.Elapsed.TotalMilliseconds:0} ms): {CredentialMasker.MaskDiagnostic(step.Detail)}"));
        if (!string.IsNullOrWhiteSpace(ToolOutput))
        {
            text.AppendLine();
            text.AppendLine("Saída do ffmpeg:");
            text.Append(CredentialMasker.MaskDiagnostic(ToolOutput).TrimEnd());
        }
        return text.ToString().TrimEnd();
    }
}
