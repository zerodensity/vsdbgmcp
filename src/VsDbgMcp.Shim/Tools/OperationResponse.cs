using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using VsDbgMcp.Contracts;

namespace VsDbgMcp.Shim.Tools
{
    public sealed class OperationEvidence
    {
        public DateTime TimestampUtc { get; set; }
        public string Mode { get; set; }
        public bool? BuildBusy { get; set; }
        public string Error { get; set; }
    }

    public sealed class OperationDetails
    {
        public string RequestId { get; set; }
        public bool Historical { get; set; }
        public string ExecutionPhase { get; set; }
        public bool CommandInFlight { get; set; }
        public bool BlocksNewRequests { get; set; }
        public DateTime StartedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public DateTime? CommandStartedUtc { get; set; }
        public DateTime? CommandReturnedUtc { get; set; }
        public string EvidenceSource { get; set; }
        public OperationEvidence Observation { get; set; }
    }

    public sealed class OperationAction
    {
        [Description("Suggested tool to call next. This is guidance; no command is automatically issued.")]
        public string Tool { get; set; }
        [Description("Arguments for that tool, including the originating VS instance.")]
        public Dictionary<string, object> Arguments { get; set; }
    }

    public sealed class OperationResponse
    {
        public string OperationId { get; set; }
        public string Kind { get; set; }
        [Description("pending, succeeded, failed, cancelled, or unknown. Describes the requested operation; launch success does not mean the debuggee exited. Unknown never establishes success or safe repetition.")]
        public string State { get; set; }
        public string Message { get; set; }
        public OperationAction NextAction { get; set; }
        [Description("Retained result, when available, using the original tool's result format.")]
        public string Result { get; set; }
        [Description("Internal dispatch, timestamps and evidence. Included only when details=true.")]
        public OperationDetails Details { get; set; }

        public static OperationResponse From(OperationInfo operation, string instance, bool details = false)
        {
            if (operation == null) throw new ArgumentException("Operation was not returned by the host.");
            var outcome = OperationOutcome.Of(operation);
            var unknown = outcome == OperationOutcome.Unknown;
            var response = new OperationResponse { OperationId = operation.OperationId, Kind = operation.Kind,
                State = outcome,
                Details = details ? new OperationDetails { RequestId = operation.RequestId, Historical = operation.Historical,
                    ExecutionPhase = operation.ExecutionPhase, CommandInFlight = operation.CommandInFlight,
                    BlocksNewRequests = operation.BlocksNewRequests, StartedUtc = operation.StartedUtc, UpdatedUtc = operation.UpdatedUtc,
                    CommandStartedUtc = operation.CommandStartedUtc, CommandReturnedUtc = operation.CommandReturnedUtc,
                    EvidenceSource = operation.EvidenceSource, Observation = operation.Observation == null ? null :
                        new OperationEvidence { TimestampUtc = operation.Observation.TimestampUtc, Mode = operation.Observation.Mode,
                            BuildBusy = operation.Observation.BuildBusy, Error = operation.Observation.Error } } : null };
            OperationAction Action(string tool, params (string Key, object Value)[] args)
            {
                var arguments = new Dictionary<string, object>();
                if (instance != null) arguments["instance"] = instance;
                foreach (var arg in args) arguments[arg.Key] = arg.Value;
                return new OperationAction { Tool = tool, Arguments = arguments };
            }
            if (unknown && operation.Kind?.StartsWith("profile", StringComparison.Ordinal) == true)
            {
                response.Message = "Collector outcome unknown. Inspect the retained capture before choosing recovery or an explicit collector stop.";
                response.NextAction = new OperationAction { Tool = "profile_status", Arguments = new Dictionary<string, object>() };
            }
            else if (response.State == "pending" || (unknown && !operation.Historical && operation.BlocksNewRequests))
            {
                response.Message = unknown ? "Outcome unknown. Another request is still blocked; wait for the existing command."
                    : "Request pending. Waiting does not repeat or cancel it.";
                response.NextAction = Action("operation_status", ("operationId", operation.OperationId), ("waitSeconds", 30));
            }
            else if (unknown)
            {
                response.Message = "Outcome unknown. Inspect the command's effects before deciding whether to start a new request. Idle does not prove success or make repetition safe.";
                response.NextAction = operation.Kind == "build" ? Action("build_log", ("operationId", operation.OperationId)) :
                    operation.Kind == "breakpoint" ? Action("bp_list") :
                    operation.Profile?.CaptureId != null ? Action("profile_recover", ("captureId", operation.Profile.CaptureId)) : Action("debug_state");
            }
            else
            {
                response.Message = "Request " + response.State + ".";
                if (operation.Profile?.CaptureId != null)
                    response.NextAction = Action("profile_recover", ("captureId", operation.Profile.CaptureId));
            }
            if (operation.Historical) response.Message += " This is a saved result from an earlier host session; its request ID does not deduplicate commands in the current host.";
            if (operation.Message != null) response.Message += " " + operation.Message;
            if (operation.Observation?.Error != null) response.Message += " " + operation.Observation.Error;
            if (operation.PersistenceError != null) response.Message += " Result persistence failed: " + operation.PersistenceError;
            if (operation.Terminal)
                response.Result = operation.Build != null ? Render.Build(operation.Build) :
                    operation.Breakpoint != null ? Render.Breakpoint(operation.Breakpoint) :
                    operation.Result != null ? Render.Op(operation.Result, "Request succeeded.").Text : null;
            return response;
        }
    }
}
