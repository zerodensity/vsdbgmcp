using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace VsDbgMcp.Contracts
{
    public interface IProfileHost
    {
        Task<OpResult> ProfileBeginAsync(bool retainRaw, CancellationToken ct = default);
        Task<ProfileCollection> ProfileStopCaptureAsync(string captureId, CancellationToken ct = default);
        Task<ProfileCollection> ProfileRecoverAsync(string captureId, CancellationToken ct = default);
    }

    public interface IOperationHost
    {
        Task<OperationInfo> OperationStatusAsync(string id, int waitSeconds, CancellationToken ct = default);
        Task<List<OperationInfo>> OperationsAsync(CancellationToken ct = default);
        Task<StateObservation> ObserveAsync(CancellationToken ct = default);
    }

    public sealed class StateObservation
    {
        public string Mode { get; set; }
        public DateTime TimestampUtc { get; set; }
        public string EvidenceSource { get; set; }
        public string Error { get; set; }
        public int SessionGeneration { get; set; }
        public List<ProcessInfo> Processes { get; set; }
        public Intervention LastTransition { get; set; }
        public ProfileCollection ActiveProfile { get; set; }
        public ProfileCollection LastProfile { get; set; }
    }

    public sealed class Intervention
    {
        public DateTime TimestampUtc { get; set; }
        public string Kind { get; set; }
        public string Detail { get; set; }
        public int SessionGeneration { get; set; }
        public int? Pid { get; set; }
        public double? PauseDurationMs { get; set; }
    }

    public sealed class OperationInfo
    {
        public string OperationId { get; set; }
        public double DurationMs => ((CompletedUtc ?? UpdatedUtc) - StartedUtc).TotalMilliseconds;
        public string RequestId { get; set; }
        public string InstanceId { get; set; }
        public string HostEpoch { get; set; }
        public string Kind { get; set; }
        public string Solution { get; set; }
        public string Configuration { get; set; }
        public string State { get; set; }
        public bool Terminal { get; set; }
        public bool CancelRequested { get; set; }
        public DateTime StartedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public DateTime? CompletedUtc { get; set; }
        public DateTime? LastProgressUtc { get; set; }
        public string LastProgress { get; set; }
        public string EvidenceSource { get; set; }
        public string PersistenceError { get; set; }
        public string Message { get; set; }
        public string Request { get; set; }
        public string LogPath { get; set; }
        public string Executable { get; set; }
        public string WorkingDirectory { get; set; }
        public string Arguments { get; set; }
        public string StartupProject { get; set; }
        public StateObservation Observation { get; set; }
        public BuildResult Build { get; set; }
        public BreakpointInfo Breakpoint { get; set; }
        public OpResult Result { get; set; }
        public ProfileCollection Profile { get; set; }
    }

    public sealed class BuildRequest
    {
        public string RequestId { get; set; }
        public string Mode { get; set; } = "build";
        public string Project { get; set; }
        public string Configuration { get; set; }
        public string Platform { get; set; }
        public int WaitSeconds { get; set; } = 30;
    }

    public sealed class BuildLog
    {
        public string OperationId { get; set; }
        public long Offset { get; set; }
        public long NextOffset { get; set; }
        public bool HasMore { get; set; }
        public string Text { get; set; }
        public string Path { get; set; }
    }
}
