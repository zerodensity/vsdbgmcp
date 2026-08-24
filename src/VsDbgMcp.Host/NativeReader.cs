using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Debugger.Interop;
using VsDbgMcp.Contracts;

namespace VsDbgMcp.Host
{
    /// <summary>
    /// The parts of debugging that only exist for native code: raw memory, CPU
    /// registers, disassembly, and which modules actually have symbols.
    ///
    /// The debug interfaces report failure through HRESULTs, so every step checks its
    /// return code and turns a failure into a message the caller can act on.
    /// </summary>
    static class NativeReader
    {
        public static MemoryResult ReadMemory(IDebugStackFrame2 frame, string expression, int size)
        {
            var result = new MemoryResult { Address = expression, Length = size };

            if (frame == null)
            {
                result.Error = "the debugger is not stopped";
                return result;
            }

            if (frame.GetExpressionContext(out var context) != VSConstants.S_OK || context == null)
            {
                result.Error = FrameChoice.NoContext;
                return result;
            }

            if (context.ParseText(expression, enum_PARSEFLAGS.PARSE_EXPRESSION, 16,
                    out var parsed, out var parseError, out _) != VSConstants.S_OK || parsed == null)
            {
                result.Error = string.IsNullOrEmpty(parseError) ? "could not parse the address" : parseError;
                return result;
            }

            if (parsed.EvaluateSync(enum_EVALFLAGS.EVAL_NOSIDEEFFECTS | enum_EVALFLAGS.EVAL_NOFUNCEVAL,
                    3000, null, out var property) != VSConstants.S_OK || property == null)
            {
                result.Error = "could not evaluate the address";
                return result;
            }

            if (property.GetMemoryContext(out var memoryContext) != VSConstants.S_OK || memoryContext == null)
            {
                result.Error = "that expression does not have an address";
                return result;
            }

            if (property.GetMemoryBytes(out var bytes) != VSConstants.S_OK || bytes == null)
            {
                result.Error = "memory is not readable here";
                return result;
            }

            var buffer = new byte[size];
            uint unreadable = 0;
            if (bytes.ReadAt(memoryContext, (uint)size, buffer, out var read, ref unreadable) != VSConstants.S_OK)
            {
                result.Error = "the read failed";
                return result;
            }

            if (memoryContext.GetName(out var name) == VSConstants.S_OK && !string.IsNullOrEmpty(name))
                result.Address = name;

            result.Length = (int)read;
            result.Hex = Hex(buffer, (int)read);
            result.Ascii = Ascii(buffer, (int)read);
            if (unreadable > 0) result.Error = unreadable + " bytes were not readable";
            return result;
        }

        static string Hex(byte[] buffer, int length)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < length; i++)
            {
                if (i > 0 && i % 16 == 0) sb.AppendLine();
                else if (i > 0) sb.Append(' ');
                sb.Append(buffer[i].ToString("x2"));
            }
            return sb.ToString();
        }

        static string Ascii(byte[] buffer, int length)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < length; i++)
            {
                var c = (char)buffer[i];
                sb.Append(c >= ' ' && c < (char)127 ? c : '.');
            }
            return sb.ToString();
        }

        static readonly string[] General64 =
        {
            "rip", "rsp", "rbp", "rax", "rbx", "rcx", "rdx", "rsi", "rdi",
            "r8", "r9", "r10", "r11", "r12", "r13", "r14", "r15"
        };

        static readonly string[] General32 = { "eip", "esp", "ebp", "eax", "ebx", "ecx", "edx", "esi", "edi" };

        static readonly string[] Flags = { "eflags" };

        /// <summary>
        /// Registers, read as pseudo-variables.
        ///
        /// The property enumeration that is meant to expose register groups returns
        /// nothing from the native engine, so these are read the way the watch window
        /// reads them: evaluate "@rax" and friends. Whichever set parses is the
        /// architecture the debuggee is running.
        /// </summary>
        public static List<RegisterInfo> ReadRegisters(IDebugStackFrame2 frame, string group)
        {
            var registers = new List<RegisterInfo>();
            if (frame == null) return registers;

            var wantGeneral = string.IsNullOrEmpty(group) ||
                              group.IndexOf("general", StringComparison.OrdinalIgnoreCase) >= 0;
            var wantFlags = string.IsNullOrEmpty(group) ||
                            group.IndexOf("flag", StringComparison.OrdinalIgnoreCase) >= 0;

            if (wantGeneral)
            {
                Read(frame, General64, "general", registers);
                if (registers.Count == 0) Read(frame, General32, "general", registers);
            }

            if (wantFlags) Read(frame, Flags, "flags", registers);
            return registers;
        }

        static void Read(IDebugStackFrame2 frame, string[] names, string group, List<RegisterInfo> into)
        {
            foreach (var name in names)
            {
                var result = ExpressionEval.Evaluate(frame, new EvalOptions
                {
                    Expression = "@" + name,
                    Format = "x",
                    TimeoutMs = 500
                });

                if (!result.IsValid || string.IsNullOrEmpty(result.Value)) continue;
                into.Add(new RegisterInfo { Name = name, Value = result.Value, Group = group });
            }
        }

        public static List<DisasmLine> Disassemble(IDebugProgram2 program, IDebugStackFrame2 frame, int count)
        {
            var lines = new List<DisasmLine>();
            if (program == null || frame == null) return lines;

            if (frame.GetCodeContext(out var codeContext) != VSConstants.S_OK || codeContext == null)
                return lines;

            if (program.GetDisassemblyStream(enum_DISASSEMBLY_STREAM_SCOPE.DSS_FUNCTION, codeContext, out var stream)
                    != VSConstants.S_OK || stream == null)
            {
                return lines;
            }

            const enum_DISASSEMBLY_STREAM_FIELDS fields =
                enum_DISASSEMBLY_STREAM_FIELDS.DSF_ADDRESS |
                enum_DISASSEMBLY_STREAM_FIELDS.DSF_CODEBYTES |
                enum_DISASSEMBLY_STREAM_FIELDS.DSF_OPCODE |
                enum_DISASSEMBLY_STREAM_FIELDS.DSF_OPERANDS |
                enum_DISASSEMBLY_STREAM_FIELDS.DSF_DOCUMENTURL |
                enum_DISASSEMBLY_STREAM_FIELDS.DSF_POSITION;

            var data = new DisassemblyData[count];
            if (stream.Read((uint)count, fields, out var read, data) != VSConstants.S_OK) return lines;

            for (var i = 0; i < read; i++)
            {
                lines.Add(new DisasmLine
                {
                    Address = data[i].bstrAddress,
                    Bytes = data[i].bstrCodeBytes,
                    Text = (data[i].bstrOpcode + " " + data[i].bstrOperands).Trim(),
                    File = data[i].bstrDocumentUrl,
                    Line = (int)data[i].posBeg.dwLine + 1
                });
            }
            return lines;
        }

        /// <summary>
        /// Every module the program has loaded. Filtering is left to the caller, which
        /// needs the full count to say how much a filtered answer left out.
        /// </summary>
        public static List<ModuleInfo> ReadModules(IDebugProgram2 program)
        {
            var modules = new List<ModuleInfo>();
            var order = 0;

            foreach (var module in Enumerate(program))
            {
                var info = Describe(module);
                if (info == null) continue;

                info.Order = order++;
                modules.Add(info);
            }

            return modules;
        }

        /// <summary>
        /// The engine's own module objects, one at a time. Separate from the reading
        /// below because loading symbols needs the object itself and not a snapshot of
        /// what it said a moment ago.
        /// </summary>
        public static IEnumerable<IDebugModule2> Enumerate(IDebugProgram2 program)
        {
            if (program == null) yield break;

            if (program.EnumModules(out var enumerator) != VSConstants.S_OK || enumerator == null)
                yield break;

            var buffer = new IDebugModule2[1];
            uint fetched = 0;

            while (enumerator.Next(1, buffer, ref fetched) == VSConstants.S_OK && fetched == 1)
                yield return buffer[0];
        }

        /// <summary>The module's name on its own, for matching a query without paying for the rest.</summary>
        public static string NameOf(IDebugModule2 module)
        {
            if (module == null) return null;

            var info = new MODULE_INFO[1];
            if (module.GetInfo(enum_MODULE_INFO_FIELDS.MIF_NAME, info) != VSConstants.S_OK) return null;

            return info[0].m_bstrName;
        }

        const enum_MODULE_INFO_FIELDS Wanted =
            enum_MODULE_INFO_FIELDS.MIF_NAME |
            enum_MODULE_INFO_FIELDS.MIF_URL |
            enum_MODULE_INFO_FIELDS.MIF_VERSION |
            enum_MODULE_INFO_FIELDS.MIF_DEBUGMESSAGE |
            enum_MODULE_INFO_FIELDS.MIF_LOADADDRESS |
            enum_MODULE_INFO_FIELDS.MIF_SIZE |
            enum_MODULE_INFO_FIELDS.MIF_TIMESTAMP |
            enum_MODULE_INFO_FIELDS.MIF_FLAGS |
            enum_MODULE_INFO_FIELDS.MIF_URLSYMBOLLOCATION;

        /// <summary>
        /// What the engine will say about one module, read fresh. The engine fills only
        /// the fields it has, and says which those were in dwValidFields, so each one
        /// is taken only when it was actually answered.
        /// </summary>
        public static ModuleInfo Describe(IDebugModule2 module)
        {
            if (module == null) return null;

            var info = new MODULE_INFO[1];
            if (module.GetInfo(Wanted, info) != VSConstants.S_OK) return null;

            var got = info[0].dwValidFields;
            var symbolsLoaded = (info[0].m_dwModuleFlags & enum_MODULE_FLAGS.MODULE_FLAG_SYMBOLS) != 0;
            var size = (got & enum_MODULE_INFO_FIELDS.MIF_SIZE) == 0 ? 0 : info[0].m_dwSize;

            return new ModuleInfo
            {
                Name = info[0].m_bstrName,
                Path = info[0].m_bstrUrl,
                Built = SourceFreshness.Show(SourceFreshness.LastWritten(info[0].m_bstrUrl)),
                ImageBuilt = ModuleIdentity.ImageTime(Stamp(info[0])),
                Size = ModuleIdentity.Size(size),
                Version = info[0].m_bstrVersion,
                Address = "0x" + info[0].m_addrLoadAddress.ToString("x"),
                SymbolsLoaded = symbolsLoaded,
                SymbolStatus = symbolsLoaded ? null : NormalizeMessage(info[0].m_bstrDebugMessage),
                SymbolPath = info[0].m_bstrUrlSymbolLocation,
                IsUserCode = UserCode(module)
            };
        }

        /// <summary>
        /// The image's own time stamp. The engine hands it over as a file time, and a
        /// module that carries none reports a value the conversion refuses outright.
        /// </summary>
        static DateTime? Stamp(MODULE_INFO info)
        {
            if ((info.dwValidFields & enum_MODULE_INFO_FIELDS.MIF_TIMESTAMP) == 0) return null;

            var fileTime = ((long)info.m_TimeStamp.dwHighDateTime << 32) | info.m_TimeStamp.dwLowDateTime;
            if (fileTime <= 0) return null;

            try { return DateTime.FromFileTimeUtc(fileTime); }
            catch (ArgumentOutOfRangeException) { return null; }
        }

        /// <summary>
        /// Whether Just My Code counts this module as the caller's own. Only a yes is
        /// ever recorded: an engine that will not answer must not read as a no.
        /// </summary>
        static bool UserCode(IDebugModule2 module)
        {
            var module3 = module as IDebugModule3;
            if (module3 == null) return false;

            return module3.IsUserCode(out var user) == VSConstants.S_OK && user != 0;
        }

        static string NormalizeMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return "no symbols loaded";
            return message.Replace("\r", " ").Replace("\n", " ").Trim();
        }
    }
}
