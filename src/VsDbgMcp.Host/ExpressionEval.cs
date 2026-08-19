using System;
using System.Collections.Generic;
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

            var text = Decorate(options.Expression, options.Format, options.Raw);
            if (context.ParseText(text, enum_PARSEFLAGS.PARSE_EXPRESSION, 10,
                    out var expression, out var parseError, out _) != VSConstants.S_OK || expression == null)
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

        public static List<VarNode> Scope(IDebugStackFrame2 frame, string scope, int depth, string filter)
        {
            var nodes = new List<VarNode>();
            if (frame == null) return nodes;

            var guid = ScopeFilter(scope);
            if (frame.EnumProperties(PropertyFields, 10, ref guid, 5000, out _, out var enumerator)
                    != VSConstants.S_OK || enumerator == null)
            {
                return nodes;
            }

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
            }

            return nodes;
        }

        /// <summary>
        /// Re-evaluates the full name a previous reply returned. Keeping expansion
        /// stateless means there is no handle table to grow, invalidate across stops,
        /// or leak.
        /// </summary>
        public static List<VarNode> Expand(IDebugStackFrame2 frame, string reference, int depth)
        {
            var nodes = new List<VarNode>();
            if (frame == null) return nodes;

            if (frame.GetExpressionContext(out var context) != VSConstants.S_OK || context == null) return nodes;

            if (context.ParseText(reference, enum_PARSEFLAGS.PARSE_EXPRESSION, 10, out var expression, out _, out _)
                    != VSConstants.S_OK || expression == null)
            {
                return nodes;
            }

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
            Ref = info.bstrFullName
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
