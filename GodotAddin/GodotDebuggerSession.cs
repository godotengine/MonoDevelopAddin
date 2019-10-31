﻿using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Mono.Debugging.Client;
using Mono.Debugging.Soft;
using GodotAddin.Utils;
using System.Collections.Generic;

namespace GodotAddin
{
    public class GodotDebuggerSession : SoftDebuggerSession
    {
        bool attached;
        private NetworkStream godotRemoteDebuggerStream;
        private GodotExecutionCommand GodotCmd;

        public void SendReloadScipts()
        {
            switch (GodotCmd.ExecutionType)
            {
                case ExecutionType.Launch:
                    GodotVariantEncoder.Encode(
                        new List<GodotVariant> { "reload_scripts" },
                        godotRemoteDebuggerStream
                    );
                    break;
                case ExecutionType.PlayInEditor:
                case ExecutionType.Attach:
                    GodotCmd.GodotIdeClient.SendReloadScripts();
                    break;
                default:
                    throw new NotImplementedException(GodotCmd.ExecutionType.ToString());
            }
        }

        private struct ThreadStartArgs
        {
            public bool IsStdErr;
            public StreamReader Stream;
        }

        private string GetGodotExecutablePath()
        {
            if (Settings.AlwaysUseConfiguredExecutable)
                return Settings.GodotExecutablePath;

            string godotPath = GodotCmd.GodotIdeClient.GodotEditorExecutablePath;

            if (string.IsNullOrEmpty(godotPath) || !File.Exists(godotPath))
                return Settings.GodotExecutablePath;

            return godotPath;
        }

        protected override void OnRun(DebuggerStartInfo startInfo)
        {
            var godotStartInfo = (GodotDebuggerStartInfo)startInfo;

            GodotCmd = godotStartInfo.GodotCmd;

            switch (GodotCmd.ExecutionType)
            {
                case ExecutionType.PlayInEditor:
                {
                    attached = false;
                    StartListening(godotStartInfo, out var assignedDebugPort);

                    string host = "127.0.0.1";

                    GodotCmd.GodotIdeClient.SendPlay(host, assignedDebugPort);

                    // TODO: Read the editor player stdout and stderr somehow

                    break;
                }
                case ExecutionType.Launch:
                {
                    attached = false;
                    StartListening(godotStartInfo, out var assignedDebugPort);

                    // Listener to replace the Godot editor remote debugger.
                    // We use it to notify the game when assemblies should be reloaded.
                    var remoteDebugListener = new TcpListener(IPAddress.Any, 0);
                    remoteDebugListener.Start();
                    remoteDebugListener.AcceptTcpClientAsync().ContinueWith(OnGodotRemoteDebuggerConnected);

                    string workingDir = startInfo.WorkingDirectory;
                    string host = "127.0.0.1";
                    int remoteDebugPort = ((IPEndPoint)remoteDebugListener.LocalEndpoint).Port;

                    // Launch Godot to run the game and connect to our remote debugger

                    var processStartInfo = new ProcessStartInfo(GetGodotExecutablePath())
                    {
                        Arguments = $"--path {workingDir} --remote-debug {host}:{remoteDebugPort}",
                        WorkingDirectory = workingDir,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    // Tells Godot to connect to the mono debugger we just started
                    processStartInfo.EnvironmentVariables["GODOT_MONO_DEBUGGER_AGENT"] =
                        "--debugger-agent=transport=dt_socket" +
                        $",address={host}:{assignedDebugPort}" +
                        ",server=n";
                    
                    var process = Process.Start(processStartInfo);

                    // Listen for StdOut and StdErr

                    var stdOutThread = new Thread(OutputReader)
                    {
                        Name = "Godot StandardOutput Reader",
                        IsBackground = true
                    };
                    stdOutThread.Start(new ThreadStartArgs {
                        IsStdErr = false, Stream = process.StandardOutput
                    });

                    var stdErrThread = new Thread(OutputReader)
                    {
                        Name = "Godot StandardError Reader",
                        IsBackground = true
                    };
                    stdErrThread.Start(new ThreadStartArgs {
                        IsStdErr = true, Stream = process.StandardError
                    });

                    OnDebuggerOutput(false, $"Godot PID:{process.Id}{Environment.NewLine}");

                    break;
                }
                case ExecutionType.Attach:
                {
                    attached = true;
                    StartConnecting(godotStartInfo);
                    break;
                }
                default:
                    throw new NotImplementedException(GodotCmd.ExecutionType.ToString());
            }
        }

        private async Task OnGodotRemoteDebuggerConnected(Task<TcpClient> task)
        {
            var tcpClient = task.Result;
            godotRemoteDebuggerStream = tcpClient.GetStream();
            byte[] buffer = new byte[1000];
            while (tcpClient.Connected)
            {
                // There is no library to decode this messages, so
                // we just pump buffer so it doesn't go out of memory
                var readBytes = await godotRemoteDebuggerStream.ReadAsync(buffer, 0, buffer.Length);
            }
        }

        protected override bool HandleException(Exception ex)
        {
            // When we attach to running Mono process it sends us AssemblyLoad, ThreadStart and other
            // delayed events, problem is, that we send VM_START command back since we have to do that on
            // every event, but in this case when Mono is sending delayed events when we attach
            // runtime is not really suspended, hence it's throwing this exceptions, just ignore...
            if (attached && ex is Mono.Debugger.Soft.VMNotSuspendedException)
                return true;
            return base.HandleException(ex);
        }

        protected override void OnExit()
        {
            if (attached)
                base.OnDetach();
            else
                base.OnExit();
        }

        private void OutputReader(object args)
        {
            var startArgs = (ThreadStartArgs)args;

            foreach (string line in startArgs.Stream.EnumerateLines())
            {
                try
                {
                    OnTargetOutput(startArgs.IsStdErr, line + Environment.NewLine);
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                }
            }
        }
    }
}