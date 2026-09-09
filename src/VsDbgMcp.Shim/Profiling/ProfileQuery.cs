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
        public bool Details { get; set; } = true;
        public string Focus { get; set; }
        public bool RawTree { get; set; }

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
            var fields = new System.Collections.Generic.List<string>();
            if (Function != null) fields.Add("function");
            if (Thread != null) fields.Add("thread");
            if (Module != null) fields.Add("module");
            if (Sort != Self) fields.Add("sort");
            if (Focus != null) fields.Add("focus");
            if (comparing && fields.Count > 0)
                return "against conflicts with " + string.Join(", ", fields) + ". Valid request: {\"capture\":1,\"against\":2}.";
            if (Function != null && (Sort != Self || Focus != null))
                return "function conflicts with sort/focus. Valid request: {\"function\":\"Name\",\"thread\":123}. Use focus with sort=\"inclusive\" for a subtree.";
            if (Focus != null && Sort != Inclusive)
                return "focus requires sort=inclusive. Valid request: {\"sort\":\"inclusive\",\"focus\":\"Name\"}.";
            return null;
        }
    }
}
