using System;
using System.Collections.Generic;
using VsDbgMcp.Contracts;

namespace VsDbgMcp.Shim.Tools
{
    // Model-facing metadata deliberately excludes the collector's private GUID.
    // Persistence and the host RPC contract retain it for collector recovery.
    public sealed class ProfileResponse
    {
        public string CaptureId { get; set; }
        public bool? RawAvailable { get; set; }
        public string InstanceId { get; set; }
        public string HostEpoch { get; set; }
        public int SessionGeneration { get; set; }
        public DateTime StartedUtc { get; set; }
        public DateTime? EndedUtc { get; set; }
        public DateTime? ProcessStartedUtc { get; set; }
        public string Configuration { get; set; }
        public string Executable { get; set; }
        public string Status { get; set; }
        public bool RetainRaw { get; set; }
        public List<ModuleInfo> Modules { get; set; }
        public List<Intervention> Interventions { get; set; }
        public string MetadataPath { get; set; }
        public string Path { get; set; }
        public string ProcessName { get; set; }
        public int Pid { get; set; }
        public double Seconds { get; set; }
        public string Error { get; set; }

        public static ProfileResponse From(ProfileCollection profile)
        {
            if (profile == null) return null;
            var instance = profile.InstanceId;
            var separator = instance?.LastIndexOf('#') ?? -1;
            if (separator >= 0 && int.TryParse(instance.Substring(separator + 1), out var pid))
                instance = pid.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return new ProfileResponse
            {
                CaptureId = profile.CaptureId, RawAvailable = profile.RawAvailable, InstanceId = instance,
                HostEpoch = profile.HostEpoch, SessionGeneration = profile.SessionGeneration,
                StartedUtc = profile.StartedUtc, EndedUtc = profile.EndedUtc, ProcessStartedUtc = profile.ProcessStartedUtc,
                Configuration = profile.Configuration, Executable = profile.Executable, Status = profile.Status,
                RetainRaw = profile.RetainRaw, Modules = profile.Modules, Interventions = profile.Interventions,
                MetadataPath = profile.MetadataPath, Path = profile.Path, ProcessName = profile.ProcessName,
                Pid = profile.Pid, Seconds = profile.Seconds, Error = profile.Error
            };
        }
    }

    public sealed class DebugStateResponse
    {
        public string Mode { get; set; }
        public DateTime TimestampUtc { get; set; }
        public string EvidenceSource { get; set; }
        public string Error { get; set; }
        public bool? BuildBusy { get; set; }
        public int SessionGeneration { get; set; }
        public List<ProcessInfo> Processes { get; set; }
        public Intervention LastTransition { get; set; }
        public ProfileResponse ActiveProfile { get; set; }
        public ProfileResponse LastProfile { get; set; }

        public static DebugStateResponse From(StateObservation state) => new DebugStateResponse
        {
            Mode = state.Mode, TimestampUtc = state.TimestampUtc, EvidenceSource = state.EvidenceSource,
            Error = state.Error, BuildBusy = state.BuildBusy, SessionGeneration = state.SessionGeneration,
            Processes = state.Processes, LastTransition = state.LastTransition,
            ActiveProfile = ProfileResponse.From(state.ActiveProfile), LastProfile = ProfileResponse.From(state.LastProfile)
        };
    }
}
