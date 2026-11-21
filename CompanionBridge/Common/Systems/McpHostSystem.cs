using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.Config;

namespace CompanionBridge.Common.Systems;

public sealed class McpSettings : ModConfig
{
    public override ConfigScope Mode => ConfigScope.ClientSide;
    public bool EnableHttpMcp;
    public string ProjectDirectory = "";
}

public sealed class McpHostSystem : ModSystem
{
    private Process host;
    private Task<string> readiness;
    private bool attempted;
    private bool stopping;

    public override void PostUpdateEverything()
    {
        if (Main.dedServ) return;
        var settings = ModContent.GetInstance<McpSettings>();
        if (!settings.EnableHttpMcp || Main.gameMenu || Netplay.Disconnect)
        {
            Stop();
            return;
        }
        if (host != null)
        {
            if (host.HasExited)
            {
                int code = host.ExitCode;
                host.Dispose();
                host = null;
                readiness = null;
                if (!stopping) Report($"MCP host exited ({code}). See client.log; toggle MCP to retry.");
            }
            else if (!stopping && readiness?.IsCompletedSuccessfully == true)
            {
                var line = readiness.Result;
                if (line == "COMPANION_MCP_READY")
                    Report("MCP ready: http://127.0.0.1:18790/mcp");
                readiness = line == null || line == "COMPANION_MCP_READY"
                    ? null : host.StandardOutput.ReadLineAsync();
            }
            return;
        }
        if (attempted) return;
        attempted = true;
        if (Main.netMode != NetmodeID.MultiplayerClient || !ServerIpc.ServerAiSystem.Enabled ||
            Netplay.Connection.Socket?.GetRemoteAddress()?.IsLocalHost() != true)
        {
            Report("MCP requires local Host & Play with Server AI enabled.");
            return;
        }
        try
        {
            string root = string.IsNullOrWhiteSpace(settings.ProjectDirectory)
                ? Environment.GetEnvironmentVariable("COMPANION_PROJECT_ROOT") : settings.ProjectDirectory;
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
                throw new InvalidOperationException("Set the absolute project directory in MCP settings.");
            var python = Path.Combine(root, ".local", "lua-venv", "Scripts", "python.exe");
            if (!File.Exists(python) || !File.Exists(Path.Combine(root, "python", "agent.py")))
                throw new InvalidOperationException("Project directory or Python environment missing.");
            var token = Environment.GetEnvironmentVariable("TERRARIA_MCP_TOKEN")
                ?? Environment.GetEnvironmentVariable("TERRARIA_MCP_TOKEN", EnvironmentVariableTarget.User);
            if (string.IsNullOrEmpty(token))
                throw new InvalidOperationException("Configure TERRARIA_MCP_TOKEN for both the game and Codex.");
            int chatPort = CompanionBridge.Server?.Port ?? throw new InvalidOperationException("Chat bridge unavailable.");
            Program.LaunchParameters.TryGetValue("-companionaiport", out var aiArg);
            int aiPort = ServerIpc.AiReadOnlyServer.ParsePort(aiArg ?? Environment.GetEnvironmentVariable("COMPANION_AI_PORT"), chatPort);
            var info = new ProcessStartInfo(python) {
                WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (string arg in new[] { "-B", "python/agent.py", "--server-ai", "--external-brain",
                "--mcp-transport", "http", "--port", chatPort.ToString(), "--ai-port", aiPort.ToString() })
                info.ArgumentList.Add(arg);
            info.Environment["TERRARIA_MCP_TOKEN"] = token;
            info.Environment["COMPANION_MOD_MANAGED_MCP"] = "1";
            host = Process.Start(info);
            stopping = false;
            host.ErrorDataReceived += (_, e) => { if (e.Data != null) Mod.Logger.Info("[MCP] " + e.Data); };
            host.BeginErrorReadLine();
            readiness = host.StandardOutput.ReadLineAsync();
            Report($"Starting MCP host (PID {host.Id}).");
        }
        catch (Exception e)
        {
            if (host != null) Stop();
            attempted = true;
            Report("MCP startup failed: " + e.Message);
        }
    }

    private void Report(string message)
    {
        Mod.Logger.Info(message);
        if (!Main.gameMenu) Main.NewText(message);
    }

    private void Stop()
    {
        attempted = false;
        if (host == null || stopping) return;
        stopping = true;
        host.StandardInput.Close();
    }

    public override void OnWorldUnload() => Stop();
    public override void Unload()
    {
        Stop();
        host?.Dispose();
        host = null;
    }
}
