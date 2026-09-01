using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Debugger.Interop;
using VsDbgMcp.Contracts;

namespace VsDbgMcp.Host
{
    /// <summary>
    /// Expression evaluation through the debug engine rather than the automation model.
    ///
    /// The automation model always permits function evaluation, which means inspecting
    /// something as ordinary as v.size() really runs code inside the program being
    /// debugged. Going through the engine lets the caller ask for that explicitly and
    /// refuse it by default.
    ///
    /// These interfaces report failure through HRESULTs, so each step checks its return
    /// code and turns a failure into a message the caller can act on.
    /// </summary>
    static class ExpressionEval
    {
        // Scope filters understood by IDebugStackFrame2.EnumProperties.
        static Guid FilterLocals = new Guid("b200f725-e725-4c53-b36a-1ec27aef12ef");
        static Guid FilterArgs = new Guid("804bccea-0475-4ae7-8a46-1862688ab863");
        static Guid FilterLocalsPlusArgs = new Guid("e74721bb-10c0-40f5-807f-920d37f95419");
        static Guid FilterAllLocalsPlusArgs = new Guid("939729a8-4cb0-4647-9831-7ff465240d5f");

        const enum_DEBUGPROP_INFO_FLAGS PropertyFields =
            enum_DEBUGPROP_INFO_FLAGS.DEBUGPROP_INFO_NAME |
            enum_DEBUGPROP_INFO_FLAGS.DEBUGPROP_INFO_VALUE |
            enum_DEBUGPROP_INFO_FLAGS.DEBUGPROP_INFO_TYPE |
            enum_DEBUGPROP_INFO_FLAGS.DEBUGPROP_INFO_FULLNAME |
            enum_DEBUGPROP_INFO_FLAGS.DEBUGPROP_INFO_ATTRIB |

            // The property object itself, needed to expand a row and to ask the engine
            // where the value lives. Some engines fill it either way; asking is free.
            enum_DEBUGPROP_INFO_FLAGS.DEBUGPROP_INFO_PROP |

            // Without this a struct reads back as "{...}" and the visualizer summary -
            // the whole reason natvis exists - never reaches the caller.
            enum_DEBUGPROP_INFO_FLAGS.DEBUGPROP_INFO_VALUE_AUTOEXPAND;

        public static EvalResult Evaluate(IDebugStackFrame2 frame, EvalOptions options)
        {
            var result = new EvalResult { Expression = options.Expression };

            if (frame == null)
            {
                result.Error = "no stack frame: the debugger is not stopped";
                return result;
            }

            if (frame.GetExpressionContext(out var context) != VSConstants.S_OK || context == null)
            {
                result.Error = FrameChoice.NoContext;
                return result;
            }

            var expression = Parse(context, options.Expression, options.TypeModule,
                options.Format, options.Raw, out var parseError);
            if (expression == null)
            {
                result.Error = string.IsNullOrEmpty(parseError) ? "could not parse the expression" : parseError;
                return result;
            }

            var flags = enum_EVALFLAGS.EVAL_RETURNVALUE;
            if (!options.AllowSideEffects)
            {
                // Both, because engines differ in which one they honour.
                flags |= enum_EVALFLAGS.EVAL_NOSIDEEFFECTS | enum_EVALFLAGS.EVAL_NOFUNCEVAL;
            }

            var timeout = (uint)Math.Max(200, options.TimeoutMs);
            var evaluated = expression.EvaluateSync(flags, timeout, null, out var property);
            if (evaluated != VSConstants.S_OK || property == null)
            {
                result.Error = WhyNothingCameBack(evaluated, property);
                if (!options.AllowSideEffects)
                    result.Error += ". If it needs to call a function, set allowSideEffects";
                return result;
            }

            if (!ReadInfo(property, out var info))
            {
                // The engine evaluated something and then would not say what. Left
                // unchecked this reads back as an empty value the program really holds.
                result.Error = "the engine evaluated this and then would not describe the result";
                return result;
            }

            result.Value = info.bstrValue;
            result.Type = info.bstrType;
            result.IsValid = (info.dwAttrib & enum_DBG_ATTRIB_FLAGS.DBG_ATTRIB_VALUE_ERROR) == 0;
            result.HasChildren = (info.dwAttrib & enum_DBG_ATTRIB_FLAGS.DBG_ATTRIB_OBJ_IS_EXPANDABLE) != 0;
            result.Ref = string.IsNullOrEmpty(info.bstrFullName) ? options.Expression : info.bstrFullName;

            if (!result.IsValid) result.Error = info.bstrValue;
            return result;
        }

        /// <summary>
        /// Why the engine returned no value. It often hands back a property holding its
        /// own account of the failure even while failing, and that text is worth far more
        /// than the code beside it, so this reads it before falling back to the code.
        /// </summary>
        static string WhyNothingCameBack(int hr, IDebugProperty2 property)
        {
            var said = property != null && ReadInfo(property, out var info) ? info.bstrValue : null;

            var text = "evaluation failed";
            if (!string.IsNullOrWhiteSpace(said)) text += ": " + said.Trim();
            return text + " (" + Code(hr) + ")";
        }

        /// <summary>The engine's own return code, for a failure it has no words for.</summary>
        static string Code(int hr) => "HRESULT 0x" + hr.ToString("X8");

        /// <summary>
        /// Parses the expression, trying each way of naming the module in turn. The native
        /// parser resolves identifiers, so a type the module does not have fails here
        /// rather than during evaluation, which is what makes trying more than one form
        /// both cheap and honest. The complaint left behind belongs to the last form
        /// tried, which is not what the caller wrote once a module qualifier or a format
        /// specifier has been added to it, so the text it is about comes with it.
        /// </summary>
        static IDebugExpression2 Parse(IDebugExpressionContext2 context, string expression,
            string typeModule, string format, bool raw, out string error)
        {
            error = null;
            string tried = null;

            foreach (var form in ModuleQualifier.Forms(expression, typeModule))
            {
                var text = Decorate(form, format, raw);
                if (context.ParseText(text, enum_PARSEFLAGS.PARSE_EXPRESSION, 10,
                        out var parsed, out var complaint, out _) == VSConstants.S_OK && parsed != null)
                {
                    return parsed;
                }

                tried = text;
                error = complaint;
            }

            if (!string.IsNullOrEmpty(error) && tried != expression)
                error += "  -- as it was written for the engine: " + tried;
            return null;
        }

        /// <summary>
        /// Format specifiers are appended here rather than being spliced into the
        /// expression by the caller, so a model never has to know the syntax.
        /// </summary>
        static string Decorate(string expression, string format, bool raw)
        {
            if (string.IsNullOrWhiteSpace(expression)) return expression;

            var text = expression;
            if (raw) text += ",!";
            if (!string.IsNullOrWhiteSpace(format))
            {
                var trimmed = format.Trim().TrimStart(',');
                if (trimmed.Length > 0) text += "," + trimmed;
            }
            return text;
        }

        public static VarsResult Scope(IDebugStackFrame2 frame, string scope, int depth, string filter,
            bool sharedAddresses)
        {
            var result = new VarsResult();
            if (frame == null)
            {
                result.Message = "there is no frame to list variables in";
                result.Failed = true;
                return result;
            }

            var guid = ScopeFilter(scope);
            if (frame.EnumProperties(PropertyFields, 10, ref guid, 5000, out _, out var enumerator)
                    != VSConstants.S_OK || enumerator == null)
            {
                // Not the same as a frame with no locals, and the difference is the whole
                // reason to say anything: an empty list here would read as one.
                result.Message = "the engine would not list variables in this frame";
                result.Failed = true;
                return result;
            }

            var nodes = result.Nodes;
            var properties = new List<IDebugProperty2>();
            var inScope = 0;

            var rows = Drain(enumerator, 500, out var listing);
            foreach (var info in rows)
            {
                inScope++;
                if (!string.IsNullOrEmpty(filter) &&
                    (info.bstrName == null ||
                     info.bstrName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0))
                {
                    continue;
                }

                var node = ToNode(info);
                if (depth > 1 && node.HasChildren)
                {
                    node.Children = Children(info.pProperty, depth - 1, out var deeper);
                    node.Note = deeper;
                }
                nodes.Add(node);
                properties.Add(info.pProperty);
            }

            if (nodes.Count == 0 && inScope > 0 && !string.IsNullOrEmpty(filter))
            {
                result.Message = "No variable's name contains '" + filter + "'. " + inScope +
                                 " were read in this frame; drop the filter to see them.";
            }

            result.Message = Also(result.Message, listing);

            if (sharedAddresses) MarkSharedAddresses(nodes, properties);
            return result;
        }

        /// <summary>
        /// Names in this frame that read the same address, marked on each other. An
        /// optimized build gives several variables one slot, and a value that is really
        /// another variable's is otherwise indistinguishable from this one's.
        ///
        /// An extra engine call per variable, which is why the caller can turn it off.
        /// </summary>
        static void MarkSharedAddresses(List<VarNode> nodes, List<IDebugProperty2> properties)
        {
            var byAddress = new Dictionary<string, List<VarNode>>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < nodes.Count; i++)
            {
                var address = AddressOf(properties[i]);
                if (string.IsNullOrEmpty(address)) continue;

                if (!byAddress.TryGetValue(address, out var sharing))
                {
                    sharing = new List<VarNode>();
                    byAddress[address] = sharing;
                }
                sharing.Add(nodes[i]);
            }

            foreach (var sharing in byAddress.Values)
            {
                if (sharing.Count < 2) continue;

                foreach (var node in sharing)
                {
                    node.SameAddressAs = new List<string>();
                    foreach (var other in sharing)
                    {
                        if (!ReferenceEquals(other, node)) node.SameAddressAs.Add(other.Name);
                    }
                }
            }
        }

        /// <summary>
        /// The address this value refers to, or null when it refers to none. Scalars held
        /// in a register have no address, so they simply do not take part.
        /// </summary>
        static string AddressOf(IDebugProperty2 property)
        {
            if (property == null) return null;

            try
            {
                if (property.GetMemoryContext(out var context) != VSConstants.S_OK || context == null) return null;

                var info = new CONTEXT_INFO[1];
                if (context.GetInfo(enum_CONTEXT_INFO_FIELDS.CIF_ADDRESSABSOLUTE, info) != VSConstants.S_OK)
                    return null;

                return info[0].bstrAddressAbsolute;
            }
            catch (COMException)
            {
                // Engines differ on what a property without an address does; none of them
                // are worth failing the whole frame over.
                return null;
            }
        }

        /// <summary>
        /// Re-evaluates the full name a previous reply returned, and can pick one element
        /// out of what a container's visualizer showed.
        ///
        /// Keeping expansion stateless means there is no handle table to grow, invalidate
        /// across stops, or leak. The price is that a reference into a std container is a
        /// two hundred character expression, and a typo in the middle of one is invisible.
        /// Picking an element is what removes the need to carry it: the element's own
        /// reference comes back with the reply, so the caller never types it.
        /// </summary>
        public static VarsResult Expand(IDebugStackFrame2 frame, string reference, int depth, string typeModule,
            int? index, string key)
        {
            var result = new VarsResult();
            if (frame == null)
            {
                result.Message = "there is no frame to expand this in";
                result.Failed = true;
                return result;
            }

            if (frame.GetExpressionContext(out var context) != VSConstants.S_OK || context == null)
            {
                result.Message = FrameChoice.NoContext;
                result.Failed = true;
                return result;
            }

            var expression = Parse(context, reference, typeModule, null, false, out var parseError);
            if (expression == null)
            {
                result.Message = string.IsNullOrEmpty(parseError)
                    ? "could not parse '" + reference + "'"
                    : parseError;
                result.Failed = true;
                return result;
            }

            var evaluated = expression.EvaluateSync(enum_EVALFLAGS.EVAL_NOSIDEEFFECTS | enum_EVALFLAGS.EVAL_NOFUNCEVAL,
                5000, null, out var property);
            if (evaluated != VSConstants.S_OK || property == null)
            {
                result.Message = "'" + reference + "' could not be evaluated in this frame: " +
                                 WhyNothingCameBack(evaluated, property);
                result.Failed = true;
                return result;
            }

            if (index == null && key == null)
            {
                result.Nodes = Children(property, depth, out var note);
                result.Message = note;
                return result;
            }

            return Element(frame, property, reference, depth, typeModule, index, key);
        }

        /// <summary>
        /// One element out of a container, chosen from the rows the visualizer already
        /// produces. Each row's full name is the expression that reaches that element, so
        /// this picks a row rather than writing any C++ of its own.
        /// </summary>
        static VarsResult Element(IDebugStackFrame2 frame, IDebugProperty2 property, string reference,
            int depth, string typeModule, int? index, string key)
        {
            // A key is compared against the element's own key, which for a map is its
            // "first" child, so a key search has to read one level deeper than an index.
            var rows = Children(property, key == null ? 1 : 2, out var note);
            var elements = ContainerElement.In(rows);

            if (elements.Count == 0)
            {
                return new VarsResult
                {
                    Message = Also(ContainerElement.NotAContainer(reference, rows), note),
                    Failed = true
                };
            }

            var chosen = index.HasValue
                ? ContainerElement.At(elements, index.Value)
                : ContainerElement.WithKey(elements, key);

            if (chosen == null)
            {
                return new VarsResult
                {
                    Message = Also(ContainerElement.NotFound(reference, elements, index, key, MaxChildren), note),
                    Failed = true
                };
            }

            if (string.IsNullOrEmpty(chosen.Ref))
            {
                return new VarsResult
                {
                    Nodes = new List<VarNode> { chosen },
                    Message = "The visualizer gave this element no expression of its own, so there is " +
                              "nothing to hand back for a further call."
                };
            }

            // Expanded through the reference the caller is being given, which is also what
            // shows that the reference works.
            var deeper = Expand(frame, chosen.Ref, depth, typeModule, null, null);
            chosen.Children = deeper.Nodes;

            return new VarsResult
            {
                Ref = chosen.Ref,
                Nodes = new List<VarNode> { chosen },
                Message = deeper.Message
            };
        }

        /// <summary>
        /// How many children one expansion reads. A key search sees these and no more,
        /// and says so when it finds nothing.
        /// </summary>
        const int MaxChildren = 200;

        /// <summary>Both, when there are both.</summary>
        static string Also(string message, string note)
        {
            if (string.IsNullOrEmpty(note)) return message;
            return string.IsNullOrEmpty(message) ? note : message + " " + note;
        }

        /// <summary>
        /// What is inside a value, and a note when that is not all of it. An empty list
        /// is the same shape whether the value holds nothing or the engine refused to
        /// say, and the two call for opposite next moves, so the refusal is named.
        /// </summary>
        static List<VarNode> Children(IDebugProperty2 property, int depth, out string note)
        {
            note = null;
            var nodes = new List<VarNode>();
            if (property == null || depth <= 0) return nodes;

            var guid = Guid.Empty;
            var hr = property.EnumChildren(PropertyFields, 10, ref guid, enum_DBG_ATTRIB_FLAGS.DBG_ATTRIB_ALL,
                null, 5000, out var enumerator);
            if (hr < 0)
            {
                note = "The engine would not list what is inside this (" + Code(hr) + ").";
                return nodes;
            }

            if (enumerator == null)
            {
                note = "The engine gave nothing to read the contents from.";
                return nodes;
            }

            foreach (var info in Drain(enumerator, MaxChildren, out note))
            {
                var node = ToNode(info);
                if (depth > 1 && node.HasChildren)
                {
                    node.Children = Children(info.pProperty, depth - 1, out var deeper);
                    node.Note = deeper;
                }
                nodes.Add(node);
            }

            return nodes;
        }

        /// <summary>
        /// Up to <paramref name="limit"/> rows, and a note when they are not all there
        /// was. A list that simply stops shows neither the engine giving up partway nor
        /// the limit here, and both leave a caller certain it has seen everything.
        /// </summary>
        static List<DEBUG_PROPERTY_INFO> Drain(IEnumDebugPropertyInfo2 enumerator, int limit, out string note)
        {
            note = null;
            var rows = new List<DEBUG_PROPERTY_INFO>();
            var buffer = new DEBUG_PROPERTY_INFO[1];

            while (rows.Count < limit)
            {
                var hr = enumerator.Next(1, buffer, out var fetched);
                if (hr < 0)
                {
                    note = "The engine stopped after " + rows.Count + " of these (" + Code(hr) +
                           "), so this is not all of them.";
                    return rows;
                }

                // S_FALSE and a short fetch are how an enumeration ends.
                if (hr != VSConstants.S_OK || fetched != 1) return rows;
                rows.Add(buffer[0]);
            }

            var total = Total(enumerator);
            if (total > rows.Count) note = "These are the first " + rows.Count + " of " + total + ".";
            else if (total < 0)
                note = "These are the first " + rows.Count + ", which is as many as one read returns.";
            return rows;
        }

        /// <summary>How many rows there are altogether, or -1 when the engine will not say.</summary>
        static int Total(IEnumDebugPropertyInfo2 enumerator)
        {
            try
            {
                return enumerator.GetCount(out var count) == VSConstants.S_OK ? (int)count : -1;
            }
            catch (COMException)
            {
                return -1;
            }
        }

        static VarNode ToNode(DEBUG_PROPERTY_INFO info) => new VarNode
        {
            Name = info.bstrName,
            Value = info.bstrValue,
            Type = info.bstrType,
            HasChildren = (info.dwAttrib & enum_DBG_ATTRIB_FLAGS.DBG_ATTRIB_OBJ_IS_EXPANDABLE) != 0,
            Ref = info.bstrFullName,

            // The engine says so when it has no value to give - optimized away, or not in
            // scope yet. Dropping that leaves the reason sitting in the value field
            // looking like one.
            Readable = (info.dwAttrib & enum_DBG_ATTRIB_FLAGS.DBG_ATTRIB_VALUE_ERROR) == 0
        };

        /// <summary>
        /// The engine's description of a value, or false when it would not give one.
        ///
        /// A zeroed struct has no value, no type and no attributes, which is exactly what
        /// a successful read of an empty string looks like: taken for a description it
        /// turns a failure into a value the program appears to hold. Anything the engine
        /// does not call a failure is kept, because some of them fill the struct and
        /// return S_FALSE.
        /// </summary>
        static bool ReadInfo(IDebugProperty2 property, out DEBUG_PROPERTY_INFO info)
        {
            var buffer = new DEBUG_PROPERTY_INFO[1];
            var hr = property.GetPropertyInfo(PropertyFields, 10, 5000, null, 0, buffer);
            info = buffer[0];
            return hr >= 0;
        }

        static Guid ScopeFilter(string scope)
        {
            switch ((scope ?? "locals").Trim().ToLowerInvariant())
            {
                case "args": return FilterArgs;
                case "autos":
                case "all": return FilterAllLocalsPlusArgs;
                case "localsandargs":
                case "locals+args": return FilterLocalsPlusArgs;
                default: return FilterLocals;
            }
        }
    }
}
