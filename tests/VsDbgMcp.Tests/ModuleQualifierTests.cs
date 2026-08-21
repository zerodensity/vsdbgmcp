using Xunit;

namespace VsDbgMcp.Tests
{
    public class ModuleQualifierTests
    {
        const string Module = "UnrealEditor-NOSSceneTreeManager.dll";

        [Fact]
        public void A_cast_through_an_arrow_is_rewritten_around_the_dereference()
        {
            // The shape from the report: written straight, the qualifier never reaches the
            // type inside the cast and the engine says NOSProperty is undefined.
            var forms = ModuleQualifier.Forms("((NOSProperty*)0x1b9993de700)->IsOrphan", Module);

            Assert.Equal(
                "({,,UnrealEditor-NOSSceneTreeManager.dll}*(NOSProperty*)0x1b9993de700).IsOrphan",
                forms[0]);
            Assert.Equal(
                "{,,UnrealEditor-NOSSceneTreeManager.dll}((NOSProperty*)0x1b9993de700)->IsOrphan",
                forms[1]);
            Assert.Equal(2, forms.Count);
        }

        [Fact]
        public void A_dereference_the_caller_already_wrote_is_qualified_in_place()
        {
            var forms = ModuleQualifier.Forms("(*(NOSProperty*)0x1b9993de700).IsOrphan", Module);

            Assert.Equal(
                "({,,UnrealEditor-NOSSceneTreeManager.dll}*(NOSProperty*)0x1b9993de700).IsOrphan",
                forms[0]);
        }

        [Theory]
        [InlineData("((T*)ptr)->a->b", "({,,M.dll}*(T*)ptr).a->b")]
        [InlineData("((T*)&obj)->a", "({,,M.dll}*(T*)&obj).a")]
        [InlineData("((NS::T*)this)->count", "({,,M.dll}*(NS::T*)this).count")]
        [InlineData("((const T*)0x10)->a[2]", "({,,M.dll}*(const T*)0x10).a[2]")]
        [InlineData("  ( (T*)p ) -> a  ", "({,,M.dll}*(T*)p).a")]
        [InlineData("((T*)p)->count + 1", "({,,M.dll}*(T*)p).count + 1")]
        public void Shapes_that_rewrite(string expression, string expected)
        {
            Assert.Equal(expected, ModuleQualifier.Forms(expression, "M.dll")[0]);
        }

        [Theory]
        // Nothing to hang a dereference on.
        [InlineData("g_thing")]
        [InlineData("thing->member")]
        [InlineData("((T*)p)")]
        [InlineData("(T*)0x10")]
        [InlineData("!((T*)p)->flag")]
        // Dereferencing the front of this would bind to the wrong half of the sum.
        [InlineData("((T*)p + 1)->m")]
        [InlineData("((T*)arr[2])->m")]
        [InlineData("((T*)f(p))->m")]
        public void Shapes_that_are_left_alone(string expression)
        {
            var forms = ModuleQualifier.Forms(expression, "M.dll");

            // One form, qualified as written, so the engine's own error is what the caller
            // is told rather than something this guessed at.
            Assert.Single(forms);
            Assert.Equal("{,,M.dll}" + expression, forms[0]);
        }

        [Fact]
        public void Only_the_leading_cast_is_rewritten()
        {
            // Both forms mean the same read; if the second cast needs the module too,
            // neither parses and the caller sees why.
            var forms = ModuleQualifier.Forms("((A*)a)->x == ((B*)b)->y", "M.dll");

            Assert.Equal("({,,M.dll}*(A*)a).x == ((B*)b)->y", forms[0]);
            Assert.Equal("{,,M.dll}((A*)a)->x == ((B*)b)->y", forms[1]);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Without_a_module_the_expression_is_untouched(string module)
        {
            var forms = ModuleQualifier.Forms("((T*)p)->m", module);

            Assert.Single(forms);
            Assert.Equal("((T*)p)->m", forms[0]);
        }

        [Theory]
        [InlineData("M.dll")]
        [InlineData("  M.dll  ")]
        [InlineData("{,,M.dll}")]
        public void The_module_is_accepted_however_it_is_written(string module)
        {
            Assert.Equal("({,,M.dll}*(T*)p).m", ModuleQualifier.Forms("((T*)p)->m", module)[0]);
        }

        [Fact]
        public void An_expression_that_already_names_a_module_keeps_the_one_it_names()
        {
            var forms = ModuleQualifier.Forms("{,,Other.dll}g_thing", "M.dll");

            Assert.Single(forms);
            Assert.Equal("{,,Other.dll}g_thing", forms[0]);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("  ")]
        public void Nothing_to_evaluate_produces_no_forms(string expression)
        {
            Assert.Empty(ModuleQualifier.Forms(expression, "M.dll"));
        }
    }
}
