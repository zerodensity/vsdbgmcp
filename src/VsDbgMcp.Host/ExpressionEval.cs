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
                result.Error = "this frame has no expression context";
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

        public static List<VarNode> Scope(IDebugStackFrame2 frame, string scope, int depth, string filter,
            bool sharedAddresses)
        {
            var nodes = new List<VarNode>();
            if (frame == null) return nodes;

            var guid = ScopeFilter(scope);
            if (frame.EnumProperties(PropertyFields, 10, ref guid, 5000, out _, out var enumerator)
                    != VSConstants.S_OK || enumerator == null)
            {
                return nodes;
            }

            var properties = new List<IDebugProperty2>();
            foreach (var info in Drain(enumerator, 500))
            {
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

            if (sharedAddresses) MarkSharedAddresses(nodes, properties);
            return nodes;
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
        /// Re-evaluates the full name a previous reply returned. Keeping expansion
        /// stateless means there is no handle table to grow, invalidate across stops,
        /// or leak.
        /// </summary>
        public static List<VarNode> Expand(IDebugStackFrame2 frame, string reference, int depth, string typeModule)
        {
            var nodes = new List<VarNode>();
            if (frame == null) return nodes;

            if (frame.GetExpressionContext(out var context) != VSConstants.S_OK || context == null) return nodes;

            var expression = Parse(context, reference, typeModule, null, false, out _);
            if (expression == null) return nodes;

            if (expression.EvaluateSync(enum_EVALFLAGS.EVAL_NOSIDEEFFECTS | enum_EVALFLAGS.EVAL_NOFUNCEVAL,
                    5000, null, out var property) != VSConstants.S_OK || property == null)
            {
                return nodes;
            }

            return Children(property, depth);
        }

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

            foreach (var info in Drain(enumerator, 200))
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
