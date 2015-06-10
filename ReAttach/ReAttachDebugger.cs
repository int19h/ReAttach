using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using EnvDTE80;
using EnvDTE90;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Debugger.Interop;
using Microsoft.VisualStudio.Shell.Interop;
using ReAttach.Contracts;
using ReAttach.Data;
using ReAttach.Extensions;
using ReAttach.Misc;

namespace ReAttach {
    public class ReAttachDebugger : IReAttachDebugger {
        public static readonly Guid DefaultPortSupplierId = new Guid("708c1eca-ff48-11d2-904f-00c04fa302a1");

        private readonly IReAttachPackage _package;
        private readonly IVsDebugger _debugger;
        private readonly DTE2 _dte;
        private readonly Debugger2 _dteDebugger;
        private readonly uint _cookie;
        private readonly Dictionary<Guid, string> _engines = new Dictionary<Guid, string>();

        public ReAttachDebugger(IReAttachPackage package) {
            _package = package;
            _debugger = package.GetService(typeof(SVsShellDebugger)) as IVsDebugger;
            _dte = _package.GetService(typeof(SDTE)) as DTE2;
            if (_dte != null)
                _dteDebugger = _dte.Debugger as Debugger2;

            if (_package == null || _debugger == null || _dte == null || _dteDebugger == null) {
                _package.Reporter.ReportError(
                    "Unable to get required services for ReAttachDebugger in ctor.");
                return;
            }

            if (_debugger.AdviseDebuggerEvents(this, out _cookie) != VSConstants.S_OK)
                _package.Reporter.ReportError("ReAttach: AdviserDebuggerEvents failed.");

            if (_debugger.AdviseDebugEventCallback(this) != VSConstants.S_OK)
                _package.Reporter.ReportError("AdviceDebugEventsCallback call failed in ReAttachDebugger ctor.");

            foreach (Transport transport in _dteDebugger.Transports) {
                foreach (Engine engine in transport.Engines) {
                    _engines[Guid.Parse(engine.ID)] = engine.Name;
                }
            }
        }

        public int Event(IDebugEngine2 engine, IDebugProcess2 process, IDebugProgram2 program,
            IDebugThread2 thread, IDebugEvent2 debugEvent, ref Guid riidEvent, uint attributes) {
            if (!(debugEvent is IDebugProcessCreateEvent2) &&
                !(debugEvent is IDebugProcessDestroyEvent2))
                return VSConstants.S_OK;

            var target = GetTargetFromProcess(process);
            if (target == null) {
                _package.Reporter.ReportWarning("Can't find target from process {0} ({1}). Event: {2}.",
                    process.GetName(), process.GetProcessId(), TypeHelper.GetDebugEventTypeName(debugEvent));
                return VSConstants.S_OK;
            }

            if (debugEvent is IDebugProcessCreateEvent2) {
                target.IsAttached = true;
                _package.History.Items.AddFirst(target);
                _package.Ui.Update();
            } else {
                target.IsAttached = false;
                _package.Ui.Update();
            }

            return VSConstants.S_OK;
        }

        public int OnModeChange(DBGMODE mode) {
            if (mode == DBGMODE.DBGMODE_Design)
                _package.History.Save();
            return VSConstants.S_OK;
        }

        public ReAttachTarget GetTargetFromProcess(IDebugProcess2 debugProcess) {
            var process = (IDebugProcess3)debugProcess;
            var pid = process.GetProcessId();

            Guid transportId = DefaultPortSupplierId;
            string transportName = "Default";
            var serverName = "";
            bool isLocal = false;

            IDebugPort2 port;
            if (debugProcess.GetPort(out port) == VSConstants.S_OK && port != null) {
                IDebugPortSupplier2 portSupplier;
                if (port.GetPortSupplier(out portSupplier) == VSConstants.S_OK && portSupplier != null) {

                    Guid guid;
                    if (portSupplier.GetPortSupplierId(out guid) == VSConstants.S_OK) {
                        string tmp;
                        if (portSupplier.GetPortSupplierName(out tmp) == VSConstants.S_OK && tmp != null) {
                            transportName = tmp;
                        }

                        transportId = guid;
                        if (guid == DefaultPortSupplierId) {
                            IDebugCoreServer2 server;
                            if (debugProcess.GetServer(out server) == VSConstants.S_OK && server != null) {
                                var server3 = server as IDebugCoreServer3;
                                if (server3 != null && server3.QueryIsLocal() == VSConstants.S_OK) {
                                    isLocal = true;
                                }
                            }
                        }
                    }

                    if (!isLocal) {
                        string tmp;
                        if (port.GetPortName(out tmp) == VSConstants.S_OK && tmp != null) {
                            serverName = tmp;
                        }
                    }
                }
            }

            var target = _package.History.Items.Find(transportId, serverName, pid);
            if (target != null)
                return target;

            var user = "";
            var security = process as IDebugProcessSecurity2;
            if (security != null) {
                string tmp;
                if (security.GetUserName(out tmp) == VSConstants.S_OK && tmp != null) {
                    user = tmp;
                }
            }

            var path = process.GetFilename();

            target = new ReAttachTarget(transportId, transportName, pid, path, user, serverName);

            var rawEngines = new GUID_ARRAY[1];
            if (process.GetEngineFilter(rawEngines) == VSConstants.S_OK && rawEngines[0].dwCount > 0) {
                var pointer = rawEngines[0].Members;
                var engineCount = rawEngines[0].dwCount;
                for (var i = 0; i < engineCount; i++) {
                    var engineId = (Guid)Marshal.PtrToStructure(pointer + (i * 16), typeof(Guid));
                    target.Engines.Add(engineId);
                }
            }

            return target;
        }

        public bool ReAttach(ReAttachTarget target) {
            if (target == null)
                return false;
            List<Process3> candidates;
            if (!target.IsLocal) {
                Transport transport = null;
                foreach (Transport t in _dteDebugger.Transports) {
                    Guid tid;
                    if (Guid.TryParse(t.ID, out tid) && tid == target.TransportId) {
                        transport = t;
                        break;
                    }
                }
                if (transport == null)
                    return false;
                var processes = _dteDebugger.GetProcesses(transport, target.ServerName).OfType<Process3>();
                candidates = processes.Where(p => p.Name == target.ProcessPath).ToList();
            } else {
                var processes = _dteDebugger.LocalProcesses.OfType<Process3>();
                candidates = processes.Where(p =>
                    p.Name == target.ProcessPath &&
                    p.UserName == target.ProcessUser).ToList();

                if (!candidates.Any()) // Do matching on processes running in exclusive mode. 
                {
                    candidates = processes.Where(p =>
                        p.Name == target.ProcessName &&
                        string.IsNullOrEmpty(p.UserName)).ToList();
                }
            }

            if (!candidates.Any())
                return false;

            Process3 process = null; // First try to use the pid.
            if (target.ProcessId > 0)
                process = candidates.FirstOrDefault(p => p.ProcessID == target.ProcessId);

            // If we don't have an exact match, just go for the highest PID matching.
            if (process == null) {
                var maxPid = candidates.Max(p => p.ProcessID);
                process = candidates.FirstOrDefault(p => p.ProcessID == maxPid);
            }

            if (process == null)
                return false;

            try {
                if (target.Engines != null && target.Engines.Any()) {
                    var engines = target.Engines.Where(e => _engines.ContainsKey(e)).Select(e => _engines[e]).ToArray();
                    process.Attach2(engines);
                } else {
                    process.Attach();
                }
                return true;
            } catch (COMException e) {
                // It's either this or returning this HRESULT to shell with Shell.ReportError method, shows UAC box btw.
                const int E_ELEVATION_REQUIRED = unchecked((int)0x800702E4);
                Marshal.ThrowExceptionForHR(E_ELEVATION_REQUIRED);
                return false;
            } catch (Exception e) {
                _package.Reporter.ReportError("Unable to ReAttach to process {0} ({1}) based on target {2}. Message: {3}.",
                    process.Name, process.ProcessID, target, e.Message);
            }
            return false;
        }
    }
}
