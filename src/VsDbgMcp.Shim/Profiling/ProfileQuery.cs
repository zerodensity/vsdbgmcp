namespace VsDbgMcp.Shim.Profiling
{
    /// <summary>
    /// Which reading of a capture was asked for.
    ///
    /// Every one of these narrows or reorders the same counted stacks; none of them
    /// collects anything or changes what was measured. A profile is expensive to take
    /// and free to re-read, which is the whole reason the counts are kept.
    /// </summary>
    public sealed class ProfileQuery
    {
        public const string Self = "self";
        public const string Inclusive = "inclusive";
        public const string ByModule = "module";

        public string Sort { get; set; } = Self;

        /// <summary>One function, with who called it and which of its lines were hit.</summary>
        public string Function { get; set; }

        /// <summary>Only frames in modules whose name contains this.</summary>
        public string Module { get; set; }

        /// <summary>Only samples taken in this thread.</summary>
        public int? Thread { get; set; }

        /// <summary>How many rows before the rest is folded into one line.</summary>
        public int Top { get; set; } = 12;

        /// <summary>
        /// Refuses a query that asked for two readings at once, or null when it asked
        /// for one.
        ///
        /// Only one can be answered, and the code that answers takes the first it finds.
        /// Left alone, a tree of one module returns a tree of everything: the right
        /// shape, the wrong subject, and nothing in the reply admitting which of the two
        /// questions it chose.
        /// </summary>
        public string Conflict(bool comparing)
        {
            var asked = new System.Collections.Generic.List<string>();

            if (comparing) asked.Add("against");
            if (Function != null) asked.Add("function");
            if (Thread != null) asked.Add("thread");
            if (Sort != Self) asked.Add("sort: " + Sort);

            // A module narrows the plain listing and nothing else, so it clashes with
            // any other reading rather than only with particular ones.
            if (Module != null && asked.Count > 0) asked.Add("module");

            if (asked.Count < 2) return null;

            return string.Join(" and ", asked.ToArray()) +
                   " are different readings of the profile, and this answers one at a time. " +
                   "Ask for one, then the other; the profile is kept, so the second is free.";
        }
    }
}
