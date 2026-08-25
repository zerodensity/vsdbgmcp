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
    }
}
