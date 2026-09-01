using ModelContextProtocol;

namespace VsDbgMcp.Shim
{
    /// <summary>
    /// What a tool answers with: the text, and whether the call failed.
    ///
    /// Text alone leaves both readers of a reply guessing. An agent has to recognise a
    /// failure from the words, and the panel inside Visual Studio shows a call that did
    /// nothing as one that worked. Reading the words back to decide would be worse than
    /// guessing: "No variable's name contains 'mesh'" is an answer and "there is no frame
    /// to list variables in" is not, and nothing in either sentence says which is which.
    /// Only the side that produced it knows, so that side says so here.
    ///
    /// An ordinary answer converts on its own, so only a failure has to be spelled out.
    /// </summary>
    public readonly struct Reply
    {
        Reply(string text, bool failed)
        {
            Text = text;
            Failed = failed;
        }

        public string Text { get; }
        public bool Failed { get; }

        /// <summary>The call did not do what it was asked, and this says why.</summary>
        public static Reply Bad(string text) => new Reply(text, true);

        public static implicit operator Reply(string text) => new Reply(text, false);
    }

    /// <summary>
    /// A tool call that failed, on its way out. Throwing is how the SDK is told to mark
    /// a result as an error without every tool having to hand back a protocol object.
    ///
    /// Its own type rather than the SDK's, so the one place that unwraps these can tell
    /// a debugger's refusal from a fault in the protocol and leave the latter alone.
    /// </summary>
    public sealed class ToolFailure : McpException
    {
        public ToolFailure(string message) : base(message)
        {
        }
    }
}
