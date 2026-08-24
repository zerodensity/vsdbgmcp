using System.Collections.Generic;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Debugger.Interop;
using VsDbgMcp.Contracts;

namespace VsDbgMcp.Host
{
    /// <summary>
    /// Reads stack frames out of the debug engine.
    ///
    /// These interfaces report failure through HRESULTs rather than exceptions, so the
    /// code checks return codes and there is nothing here to catch.
    /// </summary>
    static class FrameReader
    {
        const enum_FRAMEINFO_FLAGS Flags =
            enum_FRAMEINFO_FLAGS.FIF_FUNCNAME |
            enum_FRAMEINFO_FLAGS.FIF_MODULE |
            enum_FRAMEINFO_FLAGS.FIF_LANGUAGE |
            enum_FRAMEINFO_FLAGS.FIF_FRAME |
            enum_FRAMEINFO_FLAGS.FIF_FUNCNAME_MODULE |
            enum_FRAMEINFO_FLAGS.FIF_FUNCNAME_ARGS;

        public static Frame TopFrame(IDebugThread2 thread)
        {
            var frames = Frames(thread, 1);
            return frames.Count > 0 ? frames[0] : null;
        }

        public static List<Frame> Frames(IDebugThread2 thread, int count)
        {
            var result = new List<Frame>();
            foreach (var info in Enumerate(thread, count))
            {
                result.Add(Describe(info, result.Count));
                if (result.Count >= count) break;
            }
            return result;
        }

        public static IDebugStackFrame2 FrameAt(IDebugThread2 thread, int index)
        {
            if (index < 0) return null;

            var position = 0;
            foreach (var info in Enumerate(thread, index + 1))
            {
                if (position++ == index) return info.m_pFrame;
            }
            return null;
        }

        /// <summary>
        /// The top frames themselves rather than a description of them, for the one caller
        /// that has to try several. Walking the stack a frame at a time enumerates it
        /// again for every frame, which is the cost this exists to avoid.
        /// </summary>
        public static List<IDebugStackFrame2> FrameObjects(IDebugThread2 thread, int count)
        {
            var frames = new List<IDebugStackFrame2>();
            foreach (var info in Enumerate(thread, count)) frames.Add(info.m_pFrame);
            return frames;
        }

        /// <summary>
        /// Whether anything can be evaluated in this frame. The row Visual Studio puts on
        /// top of a paused stack has no expression context, and neither does a frame the
        /// engine has no symbols for.
        /// </summary>
        public static bool CanEvaluate(IDebugStackFrame2 frame) =>
            frame != null && frame.GetExpressionContext(out var context) == VSConstants.S_OK && context != null;

        /// <summary>
        /// Whether this frame has source of its own. What separates a frame of the
        /// program being debugged from the system libraries under it, which have an
        /// expression context and no locals to read in it.
        /// </summary>
        public static bool HasSource(IDebugStackFrame2 frame) =>
            frame != null && frame.GetDocumentContext(out var context) == VSConstants.S_OK && context != null;

        /// <summary>
        /// The frame's function on its own, for a message that has to point at one. No
        /// document or code context is read, because a name is all such a message shows.
        /// </summary>
        public static string NameOf(IDebugStackFrame2 frame)
        {
            if (frame == null) return null;

            var info = new FRAMEINFO[1];
            if (frame.GetInfo(Flags, 10, info) != VSConstants.S_OK) return null;
            return info[0].m_bstrFuncName;
        }

        static IEnumerable<FRAMEINFO> Enumerate(IDebugThread2 thread, int count)
        {
            if (thread == null || count <= 0) yield break;

            if (thread.EnumFrameInfo(Flags, 10, out var enumerator) != VSConstants.S_OK || enumerator == null)
                yield break;

            var buffer = new FRAMEINFO[1];
            for (var produced = 0; produced < count; produced++)
            {
                uint fetched = 0;
                if (enumerator.Next(1, buffer, ref fetched) != VSConstants.S_OK || fetched != 1) yield break;
                yield return buffer[0];
            }
        }

        static Frame Describe(FRAMEINFO info, int index)
        {
            var frame = new Frame
            {
                Index = index,
                Function = info.m_bstrFuncName,
                Module = info.m_bstrModule,
                Language = info.m_bstrLanguage
            };

            if (info.m_pFrame != null)
            {
                ReadLocation(info.m_pFrame, frame);
                frame.Address = ReadAddress(info.m_pFrame);
            }
            return frame;
        }

        public static Frame Describe(IDebugStackFrame2 stackFrame, int index)
        {
            if (stackFrame == null) return null;

            var frame = new Frame { Index = index };

            var info = new FRAMEINFO[1];
            if (stackFrame.GetInfo(Flags, 10, info) == VSConstants.S_OK)
            {
                frame.Function = info[0].m_bstrFuncName;
                frame.Module = info[0].m_bstrModule;
                frame.Language = info[0].m_bstrLanguage;
            }

            ReadLocation(stackFrame, frame);
            frame.Address = ReadAddress(stackFrame);
            return frame;
        }

        static void ReadLocation(IDebugStackFrame2 stackFrame, Frame frame)
        {
            if (stackFrame.GetDocumentContext(out var context) != VSConstants.S_OK || context == null) return;

            if (context.GetName(enum_GETNAME_TYPE.GN_FILENAME, out var name) == VSConstants.S_OK)
                frame.File = name;

            var begin = new TEXT_POSITION[1];
            var end = new TEXT_POSITION[1];
            if (context.GetStatementRange(begin, end) == VSConstants.S_OK)
                frame.Line = (int)begin[0].dwLine + 1; // the engine counts from zero
        }

        static string ReadAddress(IDebugStackFrame2 stackFrame)
        {
            if (stackFrame.GetCodeContext(out var context) != VSConstants.S_OK || context == null) return null;

            var info = new CONTEXT_INFO[1];
            if (context.GetInfo(enum_CONTEXT_INFO_FIELDS.CIF_ADDRESS, info) != VSConstants.S_OK) return null;
            return info[0].bstrAddress;
        }
    }
}
