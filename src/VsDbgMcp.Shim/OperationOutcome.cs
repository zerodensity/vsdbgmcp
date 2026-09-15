using System;
using System.Linq;
using VsDbgMcp.Contracts;

namespace VsDbgMcp.Shim
{
    /// <summary>
    /// What an operation came to, in the five words the model is given everywhere:
    /// pending, succeeded, failed, cancelled, unknown.
    ///
    /// One classifier, because operation_status's reply and the event line for the same
    /// operation are read side by side. Two sets of rules put "launch succeeded" above a
    /// message saying the outcome is unknown and its effects have to be inspected.
    ///
    /// The host's own states are many and kind-specific - running, bound, collected,
    /// pending-symbols - and none of them belongs in a reply. This is the only place
    /// that maps them.
    /// </summary>
    public static class OperationOutcome
    {
        public const string Pending = "pending";
        public const string Succeeded = "succeeded";
        public const string Failed = "failed";
        public const string Cancelled = "cancelled";
        public const string Unknown = "unknown";

        /// <summary>Host states that mean the thing that was asked for happened.</summary>
        static readonly string[] Worked =
        {
            "succeeded", "running", "stopped", "bound", "pending-symbols",
            "collecting", "collected", "interrupted", "aggregated"
        };

        public static string Of(OperationInfo operation)
        {
            if (operation == null) return Unknown;

            var state = operation.State ?? Unknown;
            var worked = Worked.Contains(state);
            var failed = state == "failed" || state == "rejected" || operation.Result?.Ok == false;

            // Uncertainty wins over everything. A record that closed without the host
            // saying what it closed as is not a record of success, and an idle Visual
            // Studio is not proof that the command did what it was asked.
            if (state.Contains(Unknown) || state == "issued" ||
                (operation.Historical && !operation.Terminal) ||
                (operation.Terminal && !worked && !failed && state != Cancelled)) return Unknown;

            if (!operation.Terminal) return Pending;
            if (state == Cancelled) return Cancelled;
            return failed ? Failed : Succeeded;
        }

        /// <summary>
        /// Whether the operation ended badly enough that nothing is coming after it.
        ///
        /// Unknown is deliberately not a failure here: a launch whose outcome nobody
        /// established may still have started a run, and withdrawing the expectation
        /// that owns it would report that run back to the session that asked for it.
        /// </summary>
        public static bool Failing(OperationInfo operation) => Of(operation) == Failed;
    }
}
