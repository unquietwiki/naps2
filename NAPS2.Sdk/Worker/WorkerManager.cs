﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using NAPS2.Platform;

namespace NAPS2.Worker
{
    /// <summary>
    /// A class to manage the lifecycle of NAPS2.Worker.exe instances and hook up the WCF channels.
    /// </summary>
    public static class WorkerManager
    {
        public const string WORKER_EXE_NAME = "NAPS2.Worker.exe";
        public static readonly string[] SearchDirs =
        {
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
            Path.GetDirectoryName(Assembly.GetEntryAssembly().Location)
        };

        private static string _workerExePath;

        private static BlockingCollection<WorkerContext> _workerQueue;

        private static string WorkerExePath
        {
            get
            {
                if (_workerExePath == null)
                {
                    foreach (var dir in SearchDirs)
                    {
                        _workerExePath = Path.Combine(dir, WORKER_EXE_NAME);
                        if (File.Exists(WorkerExePath))
                        {
                            break;
                        }
                    }
                }
                return _workerExePath;
            }
        }

        private static (Process, int) StartWorkerProcess()
        {
            var parentId = Process.GetCurrentProcess().Id;
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName = PlatformCompat.Runtime.ExeRunner ?? WorkerExePath,
                Arguments = PlatformCompat.Runtime.ExeRunner != null ? $"{WorkerExePath} {parentId}" : $"{parentId}",
                RedirectStandardOutput = true,
                UseShellExecute = false
            });
            if (proc == null)
            {
                throw new Exception("Could not start worker process");
            }

            if (PlatformCompat.System.CanUseWin32)
            {
                try
                {
                    var job = new Job();
                    job.AddProcess(proc.Handle);
                }
                catch
                {
                    proc.Kill();
                    throw;
                }
            }

            int port = int.Parse(proc.StandardOutput.ReadLine() ?? throw new Exception("Could not read worker port"));

            return (proc, port);
        }

        private static void StartWorkerService()
        {
            Task.Factory.StartNew(() =>
            {
                var (proc, port) = StartWorkerProcess();
                _workerQueue.Add(new WorkerContext { Service = new GrpcWorkerServiceAdapter(port), Process = proc });
            });
        }

        public static WorkerContext NextWorker()
        {
            StartWorkerService();
            return _workerQueue.Take();
        }

        public static void Init()
        {
            if (!PlatformCompat.Runtime.UseWorker)
            {
                return;
            }
            if (_workerQueue == null)
            {
                _workerQueue = new BlockingCollection<WorkerContext>();
                StartWorkerService();
            }
        }

        // TODO: This should not be set by default
        private static IWorkerServiceFactory _factory = new WorkerServiceFactory(); 

        public static IWorkerServiceFactory Factory
        {
            get => _factory;
            set => _factory = value ?? throw new ArgumentNullException(nameof(value));
        }
    }
}