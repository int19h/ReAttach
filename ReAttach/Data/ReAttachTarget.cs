using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ReAttach.Data
{
	public class ReAttachTarget
	{
		public int ProcessId { get; set; }
		public string ProcessName { get; set; }
		public string ProcessPath { get; set; }
		public string ProcessUser { get; set; }
		public string ServerName { get; set; }
		public bool IsAttached { get; set; }
        public Guid TransportId { get; set; }
        public string TransportName { get; set; }
		public List<Guid> Engines { get; set; }

        public bool IsDefaultTransport
        {
            get { return TransportId == ReAttachDebugger.DefaultPortSupplierId; }
        }

        public bool IsLocal
        {
            get { return IsDefaultTransport && string.IsNullOrEmpty(ServerName); }
        }


        public ReAttachTarget(int pid, string path, string user, string serverName = "")
            : this(ReAttachDebugger.DefaultPortSupplierId, "Default", pid, path, user, serverName)
        {
        }

		public ReAttachTarget(Guid transportId, string transportName, int pid, string path, string user, string serverName = "") 
		{
			try
			{
				ProcessName = Path.GetFileName(path);
			}
			catch
			{
				ProcessName = path;
			}
			ProcessId = pid;
			ProcessPath = path;
			ProcessUser = user ?? "";
			ServerName = serverName ?? "";
            TransportId = transportId;
            TransportName = transportName;
			Engines = new List<Guid>();
		}

		public override bool Equals(object obj)
		{
			var other = obj as ReAttachTarget;
			if (other == null)
				return false;
			return ProcessPath.Equals(other.ProcessPath, StringComparison.OrdinalIgnoreCase) &&
				ProcessUser.Equals(other.ProcessUser, StringComparison.OrdinalIgnoreCase) &&
				ServerName.Equals(other.ServerName, StringComparison.OrdinalIgnoreCase);
		}

		public override int GetHashCode()
		{
			return ProcessPath.ToLower().GetHashCode() + 
				ProcessUser.ToLower().GetHashCode() + 
				ServerName.ToLower().GetHashCode();
		}

		public override string ToString()
		{
            var result = new StringBuilder(ProcessName);
            result.Append(" (");

            if (!string.IsNullOrEmpty(ProcessUser)) {
                result.Append(ProcessUser + "@");
            }

            result.Append(ServerName);
            result.Append(")");

            if (!IsDefaultTransport && !string.IsNullOrEmpty(TransportName)) {
                result.Append(" [" + TransportName + "]");
            }

            return result.ToString();
		}
	}
}