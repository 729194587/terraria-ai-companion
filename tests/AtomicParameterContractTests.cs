using System;
using System.IO;
using System.Collections.Generic;
using System.Text.Json;
using CompanionBridge;

// Only supplies the constructor type used by ReadLandingRegion in this pure
// parameter harness. No collision, navigation or Player execution is simulated.
namespace CompanionBridge.Motion { internal sealed record LandingRegion(float MinPlayerLeftX, float MaxPlayerLeftX, float SurfaceY); }

public static class AtomicParameterContractTests
{
    public static string Run(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var results = new List<object>();
        foreach (var sample in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            string name = sample.GetProperty("name").GetString();
            string op = sample.GetProperty("operation").GetString();
            var arguments = sample.TryGetProperty("wireArguments", out var wire) ? wire : sample.GetProperty("arguments");
            bool accepted;
            try
            {
                if (sample.TryGetProperty("backend", out var backend) && backend.GetString() == "server_ai")
                    accepted = AtomicParameters.ValidateServerAction(op, arguments) == null;
                else { AtomicParameters.Validate(op, arguments, sample.GetProperty("query").GetBoolean()); accepted = true; }
            }
            catch (AtomicParameterException) { accepted = false; }
            if (accepted != sample.GetProperty("accepted").GetBoolean()) throw new Exception("Parameter contract mismatch: " + name);
            results.Add(new { name, accepted });
        }
        return JsonSerializer.Serialize(results);
    }
}
