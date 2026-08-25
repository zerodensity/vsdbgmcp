using System;
using System.Globalization;
using Microsoft.VisualStudio.Debugger.Interop;
using VsDbgMcp.Contracts;

namespace VsDbgMcp.Host
{
    /// <summary>
    /// Memory taken out of the debuggee's own heap so that a call has somewhere to
    /// write.
    ///
    /// The expression evaluator will not make a temporary. A function that writes
    /// through a reference therefore has no argument that can be written for it, which
    /// is why calling one from eval was simply refused. The only storage in reach is
    /// the program's own, so this calls the program's allocator: real code, in the
    /// process being debugged, with everything that follows from that.
    /// </summary>
    static class Scratch
    {
        /// <summary>
        /// Win32's heap, reached through casts that hand the evaluator the signatures it
        /// will not look up for itself.
        ///
        /// Two rules of the native evaluator shape this. It only sees functions the
        /// program imports, and it refuses to call one it has no type information for -
        /// which is every function in a module whose symbols are not loaded, so a plain
        /// HeapAlloc(...) is refused where the cast form works. The C runtime's
        /// allocator is not used even where it resolves: malloc can be called and free
        /// could not be found in the same program, and a block that cannot be given back
        /// is a leak by construction. The heap flag is HEAP_ZERO_MEMORY, so a block is
        /// always cleared: an out-parameter the callee reads before it writes would
        /// otherwise read whatever was left there.
        /// </summary>
        const string Heap = "((void*(*)())GetProcessHeap)()";

        const string Take =
            "((void*(*)(void*,unsigned long,unsigned __int64))HeapAlloc)(" + Heap + ", 8, {0})";

        const string Release =
            "((int(*)(void*,unsigned long,void*))HeapFree)(" + Heap + ", 0, (void*){0})";

        /// <summary>Takes a block. Returns null and says why when the heap will not give one.</summary>
        public static ScratchBlock Allocate(IDebugStackFrame2 frame, int bytes, string type, out string error)
        {
            var result = Evaluate(frame, Fill(Take, bytes.ToString(CultureInfo.InvariantCulture)));
            if (!result.IsValid || !ScratchAddress.TryParse(result.Value, out var address))
            {
                error = "Could not allocate in the debuggee: " + Reason(result) +
                        ". Taking memory means running the program's own code, so it needs the program " +
                        "stopped somewhere that is safe - not inside the heap itself.";
                return null;
            }

            error = null;
            return new ScratchBlock
            {
                Address = ScratchAddress.Hex(address),
                Bytes = bytes,
                Type = type
            };
        }

        /// <summary>
        /// Gives the block back. False and a reason when it will not go, which is what
        /// freeing something twice looks like: the heap raises, and the evaluator says
        /// the call was aborted rather than answering.
        /// </summary>
        public static bool Free(IDebugStackFrame2 frame, ScratchBlock block, out string error)
        {
            var result = Evaluate(frame, Fill(Release, block.Address));
            if (result.IsValid && ScratchAddress.TryCount(result.Value, out var freed) && freed == 1)
            {
                error = null;
                return true;
            }

            error = Reason(result);
            return false;
        }

        /// <summary>How many bytes the type takes in the debuggee, or zero and a reason.</summary>
        public static int SizeOf(IDebugStackFrame2 frame, string type, out string error)
        {
            var result = Evaluate(frame, "sizeof(" + type + ")");
            if (result.IsValid && ScratchAddress.TryCount(result.Value, out var bytes))
            {
                error = null;
                return bytes;
            }

            error = "Could not size " + type + ": " + Reason(result) +
                    ". The evaluator names types out of the frame's own scope, and a type it will not " +
                    "reach there cannot be reached by qualifying it either - {,,Module}Name is refused " +
                    "in a type position. Pass bytes instead and read the block back with " +
                    "expand(typeModule), which is where the qualifier does work.";
            return 0;
        }

        static string Fill(string template, string argument) => template.Replace("{0}", argument);

        static EvalResult Evaluate(IDebugStackFrame2 frame, string expression) =>
            ExpressionEval.Evaluate(frame, new EvalOptions
            {
                Expression = expression,

                // Allocating is calling a function by definition, so the guard that keeps
                // eval from running the program by accident cannot apply here.
                AllowSideEffects = true
            });

        static string Reason(EvalResult result) =>
            !string.IsNullOrEmpty(result.Error) ? result.Error
            : !string.IsNullOrEmpty(result.Value) ? result.Value
            : "no reason given";
    }
}
