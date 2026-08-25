using System;

namespace VsDbgMcp
{
    /// <summary>
    /// Two things the native expression evaluator will not do, and what to do instead.
    ///
    /// Both refusals reach the caller as the engine's own text, which says neither whose
    /// limit it is nor what would work: "Nested function evaluation not supported." reads
    /// as though this server declined to try, and the refusal to bind a reference argument
    /// reads as a type error. Neither is ours to lift.
    ///
    /// The match is on the engine's wording, and a message that matches nothing is passed
    /// through exactly as it arrived. Advice attached to a refusal it does not fit would
    /// send a reader further off than the engine's own words do.
    /// </summary>
    public static class EngineRefusal
    {
        /// <summary>The engine's message with what to do about it, or the message unchanged.</summary>
        public static string Explain(string message)
        {
            var advice = Advice(message);
            if (advice == null) return message;

            // On its own line: the engine's text does not end in a period, so joined
            // with a space the two run together into one sentence that reads as the
            // engine's own words.
            return message.TrimEnd() + "\n" + advice;
        }

        /// <summary>What a caller can do about this refusal, or null when nothing here recognises it.</summary>
        public static string Advice(string message)
        {
            if (string.IsNullOrEmpty(message)) return null;

            if (Has(message, "nested function evaluation"))
            {
                return "That limit is the native expression evaluator's, not this server's: it will " +
                       "not run one call inside another. Evaluate the inner call on its own, then " +
                       "write the value it returned into the outer one.";
            }

            if (BindsAReference(message))
            {
                return "The native expression evaluator has no storage to bind a reference parameter " +
                       "to, so it cannot make this call at all. Take a block with scratch and pass a " +
                       "dereference of its address - f(*(Thing*)0x1a9fc170000) - then give it back with " +
                       "scratch_free. Where the evaluator will not name the type at all, which happens " +
                       "to a type in an anonymous namespace, cast the function instead and hand it the " +
                       "raw block: ((void(*)(void*))Module.exe!Namespace::f)((void*)0x1a9fc170000).";
            }

            return null;
        }

        /// <summary>
        /// The two ways the C++ evaluator says an argument will not bind to a reference
        /// parameter. Both name a reference type, which is what keeps this off an ordinary
        /// complaint about converting one value to another.
        /// </summary>
        static bool BindsAReference(string message)
        {
            if (Has(message, "reference of type") && Has(message, "cannot be initialized")) return true;

            return Has(message, "cannot convert argument") &&
                   (message.IndexOf("&'", StringComparison.Ordinal) >= 0 ||
                    message.IndexOf("&\"", StringComparison.Ordinal) >= 0);
        }

        static bool Has(string message, string part) =>
            message.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
