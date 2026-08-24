using VsDbgMcp.Contracts;
using VsDbgMcp.Shim;
using Xunit;

namespace VsDbgMcp.Tests
{
    /// <summary>
    /// Two refusals that come from the native expression evaluator rather than from this
    /// server, and read as though they came from it. The engine's own text is what is
    /// matched on, and anything it does not recognise has to reach the caller untouched.
    /// </summary>
    public class EngineRefusalTests
    {
        [Fact]
        public void A_nested_call_says_whose_limit_it_is_and_what_to_do()
        {
            var text = EngineRefusal.Explain("Nested function evaluation not supported.");

            Assert.StartsWith("Nested function evaluation not supported.", text);
            Assert.Contains("native expression evaluator's, not this server's", text);
            Assert.Contains("Evaluate the inner call on its own", text);
        }

        [Fact]
        public void An_argument_that_will_not_bind_to_a_reference_points_at_the_way_through()
        {
            var text = EngineRefusal.Explain(
                "a reference of type \"ULWord &\" (not const-qualified) cannot be initialized " +
                "with a value of type \"unsigned long\"");

            Assert.Contains("no storage to bind a reference parameter to", text);
            Assert.Contains("yours to free", text);
        }

        [Fact]
        public void The_compilers_wording_for_the_same_refusal_is_recognised_too()
        {
            Assert.NotNull(EngineRefusal.Advice(
                "cannot convert argument 2 from 'unsigned long' to 'unsigned long &'"));
        }

        [Fact]
        public void An_ordinary_conversion_complaint_is_not_mistaken_for_it()
        {
            Assert.Null(EngineRefusal.Advice("cannot convert argument 1 from 'const char *' to 'int'"));
        }

        [Fact]
        public void Anything_unrecognised_reaches_the_caller_as_the_engine_wrote_it()
        {
            Assert.Equal("identifier \"nosuchvar\" is undefined",
                EngineRefusal.Explain("identifier \"nosuchvar\" is undefined"));
            Assert.Null(EngineRefusal.Advice(null));
            Assert.Null(EngineRefusal.Advice(""));
        }

        // ------------------------------------------------------------------ rendering

        [Fact]
        public void Eval_prints_the_engines_words_and_then_the_way_round_it()
        {
            var text = Render.Evals(new[]
            {
                new EvalResult
                {
                    Expression = "IsExternallySynced(*GetDevice())",
                    IsValid = false,
                    Error = "Nested function evaluation not supported."
                }
            });

            Assert.Contains("IsExternallySynced(*GetDevice()) -- Nested function evaluation not supported.", text);
            Assert.Contains("Evaluate the inner call on its own", text);
        }

        [Fact]
        public void Across_threads_the_advice_is_given_once_rather_than_per_group()
        {
            var text = Render.Evals(new[]
            {
                new EvalResult { Expression = "f(*g())", IsValid = false, Error = "Nested function evaluation not supported.", ThreadId = 11 },
                new EvalResult { Expression = "f(*g())", IsValid = false, Error = "Nested function evaluation not supported.", ThreadId = 12 }
            });

            Assert.Equal(1, Occurrences(text, "Evaluate the inner call on its own"));
        }

        static int Occurrences(string text, string part)
        {
            var count = 0;
            for (var i = text.IndexOf(part); i >= 0; i = text.IndexOf(part, i + part.Length)) count++;
            return count;
        }

        /// <summary>
        /// The engine's text does not end in a period, so joined with a space the advice
        /// ran on into it and the whole thing read as the evaluator's own words.
        /// </summary>
        [Fact]
        public void The_advice_does_not_run_on_from_the_engines_own_words()
        {
            var engine = "a reference of type \"Mesh &\" (not const-qualified) cannot be " +
                         "initialized with a value of type \"int\"";

            var text = EngineRefusal.Explain(engine);

            Assert.Equal(engine, text.Split('\n')[0]);
            Assert.Contains("\n", text);
        }
    }
}
