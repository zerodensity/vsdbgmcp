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

        // ------------------------------------------------- a module that is not loaded

        static readonly string[] Loaded =
        {
            "UnrealEditor.exe", "nos.sys.vulkan.dll", "UnrealEditor-Engine.dll", "ntdll.dll"
        };

        [Fact]
        public void A_module_that_is_loaded_is_used()
        {
            Assert.Null(ModuleQualifier.NotLoaded("nos.sys.vulkan.dll", Loaded));
        }

        [Theory]
        [InlineData("NOS.SYS.VULKAN.DLL")]
        [InlineData("nos.sys.vulkan")]
        [InlineData(@"C:\build\Cache\nos.sys.vulkan.dll")]
        [InlineData("{,,nos.sys.vulkan.dll}")]
        public void A_loaded_module_is_recognised_however_it_is_written(string module)
        {
            Assert.Null(ModuleQualifier.NotLoaded(module, Loaded));
        }

        [Fact]
        public void A_module_that_is_not_loaded_is_refused_with_what_is()
        {
            var text = ModuleQualifier.NotLoaded("nosSysVulkan.dll", Loaded);

            Assert.Contains("No loaded module is named 'nosSysVulkan.dll'", text);
            Assert.Contains("4 modules are loaded", text);
            Assert.Contains("nos.sys.vulkan.dll", text);

            // Why it matters: dropping the qualifier is what turns a cast into a value
            // that reads as data.
            Assert.Contains("resolve the type in the frame's own module", text);
        }

        [Fact]
        public void The_wrong_extension_is_the_wrong_module()
        {
            // {,,UnrealEditor.dll} names nothing when UnrealEditor.exe is what loaded.
            Assert.Contains("UnrealEditor.exe", ModuleQualifier.NotLoaded("UnrealEditor.dll", Loaded));
        }

        [Fact]
        public void Half_a_name_finds_the_whole_of_it()
        {
            Assert.Contains("UnrealEditor-Engine.dll", ModuleQualifier.NotLoaded("Engine.dll", Loaded));
        }

        [Fact]
        public void A_short_loaded_name_buried_in_a_long_one_is_not_a_near_miss()
        {
            // "user32" sits inside "MyUser32Wrapper" and has nothing to do with it.
            var text = ModuleQualifier.NotLoaded("MyUser32Wrapper.dll", new[] { "user32.dll", "ntdll.dll" });

            Assert.Contains("none of their names is close to it", text);
        }

        [Fact]
        public void A_name_like_nothing_loaded_says_there_was_no_near_miss()
        {
            var text = ModuleQualifier.NotLoaded("Zork.dll", Loaded);

            Assert.Contains("none of their names is close to it", text);
        }

        [Fact]
        public void No_module_list_is_not_a_reason_to_refuse()
        {
            // An empty list is what a module list nobody could read looks like as well,
            // and refusing on it would be a claim about something never looked at.
            Assert.Null(ModuleQualifier.NotLoaded("M.dll", new string[0]));
            Assert.Null(ModuleQualifier.NotLoaded("M.dll", null));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Naming_no_module_is_nothing_to_check(string module)
        {
            Assert.Null(ModuleQualifier.NotLoaded(module, Loaded));
        }
    }
}
