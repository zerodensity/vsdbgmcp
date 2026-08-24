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
            if (expression.EvaluateSync(flags, timeout, null, out var property) != VSConstants.S_OK || property == null)
            {
                result.Error = "evaluation failed" +
                               (options.AllowSideEffects ? "" : ". If it needs to call a function, set allowSideEffects");
                return result;
            }

            var info = ReadInfo(property);
            result.Value = info.bstrValue;
            result.Type = info.bstrType;
            result.IsValid = (info.dwAttrib & enum_DBG_ATTRIB_FLAGS.DBG_ATTRIB_VALUE_ERROR) == 0;
            result.HasChildren = (info.dwAttrib & enum_DBG_ATTRIB_FLAGS.DBG_ATTRIB_OBJ_IS_EXPANDABLE) != 0;
            result.Ref = string.IsNullOrEmpty(info.bstrFullName) ? options.Expression : info.bstrFullName;

            if (!result.IsValid) result.Error = info.bstrValue;
            return result;
        }

        /// <summary>
        /// Parses the expression, trying each way of naming the module in turn. The native
        /// parser resolves identifiers, so a type the module does not have fails here
        /// rather than during evaluation, which is what makes trying more than one form
        /// both cheap and honest. The error left behind belongs to the last form, the one
        /// the caller actually wrote.
        /// </summary>
        static IDebugExpression2 Parse(IDebugExpressionContext2 context, string expression,
            string typeModule, string format, bool raw, out string error)
        {
            error = null;
            foreach (var form in ModuleQualifier.Forms(expression, typeModule))
            {
                var text = Decorate(form, format, raw);
                if (context.ParseText(text, enum_PARSEFLAGS.PARSE_EXPRESSION, 10,
                        out var parsed, out error, out _) == VSConstants.S_OK && parsed != null)
                {
                    return parsed;
                }
            }
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
                return result;
            }

            var guid = ScopeFilter(scope);
            if (frame.EnumProperties(PropertyFields, 10, ref guid, 5000, out _, out var enumerator)
                    != VSConstants.S_OK || enumerator == null)
            {
                // Not the same as a frame with no locals, and the difference is the whole
                // reason to say anything: an empty list here would read as one.
                result.Message = "the engine would not list variables in this frame";
                return result;
            }

            var nodes = result.Nodes;
            var properties = new List<IDebugProperty2>();
            var inScope = 0;

            foreach (var info in Drain(enumerator, 500))
            {
                inScope++;
                if (!string.IsNullOrEmpty(filter) &&
                    (info.bstrName == null ||
                     info.bstrName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0))
                {
                    continue;
                }

                var node = ToNode(info);
                if (depth > 1 && node.HasChildren) node.Children = Children(info.pProperty, depth - 1);
                nodes.Add(node);
                properties.Add(info.pProperty);
            }

            if (nodes.Count == 0 && inScope > 0 && !string.IsNullOrEmpty(filter))
            {
                result.Message = "No variable's name contains '" + filter + "'. " + inScope +
                                 " were read in this frame; drop the filter to see them.";
            }

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
                return result;
            }

            if (frame.GetExpressionContext(out var context) != VSConstants.S_OK || context == null)
            {
                result.Message = FrameChoice.NoContext;
                return result;
            }

            var expression = Parse(context, reference, typeModule, null, false, out var parseError);
            if (expression == null)
            {
                result.Message = string.IsNullOrEmpty(parseError)
                    ? "could not parse '" + reference + "'"
                    : parseError;
                return result;
            }

            if (expression.EvaluateSync(enum_EVALFLAGS.EVAL_NOSIDEEFFECTS | enum_EVALFLAGS.EVAL_NOFUNCEVAL,
                    5000, null, out var property) != VSConstants.S_OK || property == null)
            {
                result.Message = "'" + reference + "' could not be evaluated in this frame";
                return result;
            }

            if (index == null && key == null)
            {
                result.Nodes = Children(property, depth);
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
            var rows = Children(property, key == null ? 1 : 2);
            var elements = ContainerElement.In(rows);

            if (elements.Count == 0)
                return new VarsResult { Message = ContainerElement.NotAContainer(reference, rows) };

            var chosen = index.HasValue
                ? ContainerElement.At(elements, index.Value)
                : ContainerElement.WithKey(elements, key);

            if (chosen == null)
            {
                return new VarsResult
                {
                    Message = ContainerElement.NotFound(reference, elements, index, key, MaxChildren)
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

        static List<VarNode> Children(IDebugProperty2 property, int depth)
        {
            var nodes = new List<VarNode>();
            if (property == null || depth <= 0) return nodes;

            var guid = Guid.Empty;
            if (property.EnumChildren(PropertyFields, 10, ref guid, enum_DBG_ATTRIB_FLAGS.DBG_ATTRIB_ALL,
                    null, 5000, out var enumerator) != VSConstants.S_OK || enumerator == null)
            {
                return nodes;
            }

            foreach (var info in Drain(enumerator, MaxChildren))
            {
                var node = ToNode(info);
                if (depth > 1 && node.HasChildren) node.Children = Children(info.pProperty, depth - 1);
                nodes.Add(node);
            }

            return nodes;
        }

        static IEnumerable<DEBUG_PROPERTY_INFO> Drain(IEnumDebugPropertyInfo2 enumerator, int limit)
        {
            var buffer = new DEBUG_PROPERTY_INFO[1];
            for (var count = 0; count < limit; count++)
            {
                if (enumerator.Next(1, buffer, out var fetched) != VSConstants.S_OK || fetched != 1) yield break;
                yield return buffer[0];
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

        static DEBUG_PROPERTY_INFO ReadInfo(IDebugProperty2 property)
        {
            var info = new DEBUG_PROPERTY_INFO[1];
            property.GetPropertyInfo(PropertyFields, 10, 5000, null, 0, info);
            return info[0];
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
