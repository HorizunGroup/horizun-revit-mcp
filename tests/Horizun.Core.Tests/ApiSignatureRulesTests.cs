using System;
using System.Collections.Generic;
using System.Linq;
using Horizun.Revit.Core;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Horizun.Core.Tests
{
    public class ApiSignatureRulesTests
    {
        public class ElementId
        {
            public static int ConstructorCalls;
            public ElementId(long value) { ConstructorCalls++; throw new InvalidOperationException("Must not invoke"); }
        }
        public class Cases
        {
            public void Numeric(int value) { }
            public void Numeric(long value) { }
            public void Wide(long value) { }
            public void Narrow(int value) { }
            public void Collection(ICollection<ElementId> values) { }
            public void Generic<T>(T value) { }
            public void Reference(object value) { }
            public void Nullable(long? value) { }
            public void Optional(int value = 7) { }
            public void Required(int value) { }
            public void Params(params int[] values) { }
            public void ByRef(ref int value) { }
            public void Matrix(int[,] values) { }
            public void Enum(DayOfWeek value) { }
            public void ValueType(ValueType value) { }
            public void Interface(IComparable value) { }
            public static int GetterCalls;
            public int Getter { get { GetterCalls++; throw new InvalidOperationException("Must not read"); } }
        }
        public class Derived : Cases { public void Own() { } }
        public class StaticBase { public static void InheritedStatic(int value) { } }
        public class StaticDerived : StaticBase { }
        public interface IBase { void InheritedInterface(int value); }
        public interface IDerived : IBase { void OwnInterface(); }
        public class One { public class Clash { } }
        public class Two { public class Clash { } }
        private class Hidden { }
        public class Convertible
        {
            public static int OperatorCalls;
            public static implicit operator int(Convertible value) { OperatorCalls++; throw new Exception("Never invoke"); }
        }

        private static IEnumerable<Type> Universe => new[] { typeof(Cases), typeof(Derived), typeof(ElementId),
            typeof(int), typeof(long), typeof(double), typeof(float), typeof(byte), typeof(char), typeof(decimal), typeof(bool),
            typeof(string), typeof(object), typeof(ValueType), typeof(IComparable), typeof(DayOfWeek), typeof(Convertible),
            typeof(List<>), typeof(ICollection<>), typeof(IEnumerable<>), typeof(Dictionary<,>), typeof(Nullable<>),
            typeof(One.Clash), typeof(Two.Clash), typeof(Hidden) };

        private static JObject Check(string member, params string[] arguments) => ApiSignatureRules.Check(typeof(Cases), member, arguments, Universe);
        private static JArray Matches(JObject result) => (JArray)result["matches"];
        private static string Conversion(JObject result) => (string)result["matches"][0]["conversions_required"][0]["conversion"];

        [Fact]
        public void ArrayRanksAreResolvedAndRenderedWithoutPretendingMatrixIsVector()
        {
            JObject exact = Check("Matrix", "System.Int32[,]");
            Assert.Single(Matches(exact));
            Assert.True((bool)exact["matches"][0]["exact_match"]);
            Assert.Contains("System.Int32[,]", (string)exact["matches"][0]["signature"]);
            JObject vector = Check("Matrix", "System.Int32[]");
            Assert.Empty(Matches(vector));
            Assert.Equal("System.Int32[,]", (string)vector["overloads"][0]["reasons"][0]["to"]);
        }

        [Theory]
        [InlineData(typeof(StaticDerived), "InheritedStatic")]
        [InlineData(typeof(IDerived), "InheritedInterface")]
        public void ExplicitInheritedCheckCoversStaticBaseAndParentInterface(Type target, string member)
        {
            var types = Universe.Concat(new[] { typeof(StaticBase), typeof(StaticDerived), typeof(IBase), typeof(IDerived) });
            JObject declared = ApiSignatureRules.Check(target, member, new[] { "System.Int32" }, types);
            Assert.Equal(0, (int)declared["candidate_count"]);
            JObject inherited = ApiSignatureRules.Check(target, member, new[] { "System.Int32" }, types, true);
            Assert.Equal(1, (int)inherited["candidate_count"]);
            Assert.Single(Matches(inherited));
            Assert.True((bool)inherited["coverage_complete"]);
        }

        [Fact]
        public void FindsEveryOverloadAndNamesNumericConversionWithoutChoosingBinder()
        {
            JObject result = Check("Numeric", "System.Int32");
            Assert.Equal(2, (int)result["candidate_count"]);
            Assert.Equal(2, Matches(result).Count);
            Assert.True((bool)result["ambiguous"]);
            Assert.Single(Matches(result), row => (bool)row["exact_match"]);
            Assert.Contains(Matches(result), row => row["conversions_required"].Any(c => (string)c["conversion"] == "implicit_numeric"));
            Assert.True((bool)result["coverage_complete"]);
        }

        [Theory]
        [InlineData("System.Int32")]
        [InlineData("Int32")]
        [InlineData("System.Byte")]
        [InlineData("System.Char")]
        public void SupportsWideningIntegralConversions(string argument)
        {
            JObject result = Check("Wide", argument);
            Assert.Single(Matches(result));
            Assert.Equal("implicit_numeric", Conversion(result));
        }

        [Theory]
        [InlineData("System.Int64")]
        [InlineData("System.Double")]
        [InlineData("System.Decimal")]
        [InlineData("System.Boolean")]
        [InlineData("System.String")]
        public void RejectsNarrowingAndUnrelatedTypesWithEachOverloadReason(string argument)
        {
            JObject result = Check("Narrow", argument);
            Assert.Empty(Matches(result));
            Assert.Equal("no_implicit_conversion", (string)result["overloads"][0]["reasons"][0]["code"]);
        }

        [Fact]
        public void DoesNotInferEnumConversionFromAnIntegerType()
        {
            Assert.Empty(Matches(Check("Enum", "System.Int32")));
            Assert.True((bool)Matches(Check("Enum", "System.DayOfWeek"))[0]["exact_match"]);
        }

        [Theory]
        [InlineData("List[ElementId]")]
        [InlineData("System.Collections.Generic.List`1[ElementId]")]
        [InlineData("System.Collections.Generic.List[ElementId]")]
        public void ClosedGenericListIsAReferenceConversionToCollection(string argument)
        {
            JObject result = Check("Collection", argument);
            Assert.Single(Matches(result));
            Assert.Equal("implicit_reference", Conversion(result));
            Assert.Contains("List[ElementId]", result["ironpython_suggestions"].ToString());
        }

        [Fact]
        public void ResolvesNestedGenericsAndArraysOnlyFromAllowedDefinitions()
        {
            string reason;
            Assert.Equal(typeof(Dictionary<string, List<int[]>>), ApiSignatureRules.ResolveType(
                "Dictionary[System.String, List[System.Int32[]]]", Universe, out reason));
            Assert.Null(reason);
            Assert.Null(ApiSignatureRules.ResolveType("HashSet[System.Int32]", Universe, out reason));
            Assert.Contains("permitted", reason);
            Assert.Null(ApiSignatureRules.ResolveType("Nullable[System.String]", Universe, out reason));
            Assert.Contains("constraints", reason);
        }

        [Theory]
        [InlineData("list")]
        [InlineData("int")]
        [InlineData("null")]
        [InlineData("None")]
        [InlineData("[1,2]")]
        [InlineData("System.Int64(5)")]
        [InlineData("System.Int32; throw")]
        [InlineData("System.Int32, mscorlib")]
        [InlineData("List[System.Int32")]
        [InlineData("List[]]")]
        [InlineData("List[System.Int32,,]")]
        public void RejectsPythonValuesCodeAndUnresolvedTypeNames(string argument)
        {
            JObject result = Check("Collection", argument);
            Assert.Empty(Matches(result));
            Assert.False((bool)result["coverage_complete"]);
            Assert.Single((JArray)result["unresolved_types"]);
            Assert.Contains("List[ElementId]", result["ironpython_suggestions"].ToString());
        }

        [Fact]
        public void AmbiguousAndHiddenNamesNeverResolve()
        {
            string reason;
            Assert.Null(ApiSignatureRules.ResolveType("Clash", Universe, out reason));
            Assert.Contains("Ambiguous", reason);
            Assert.Equal(typeof(One.Clash), ApiSignatureRules.ResolveType(typeof(One.Clash).FullName, Universe, out reason));
            Assert.Null(ApiSignatureRules.ResolveType("Hidden", Universe, out reason));
        }

        [Fact]
        public void ChecksConstructorMetadataWithoutRunningConstructorOrGetter()
        {
            ElementId.ConstructorCalls = 0;
            JObject result = ApiSignatureRules.Check(typeof(ElementId), ".ctor", new[] { "System.Int64" }, Universe, revitYear: "2025");
            Assert.Equal(0, ElementId.ConstructorCalls);
            Assert.Single(Matches(result));
            Assert.True((bool)Matches(result)[0]["exact_match"]);
            Assert.Equal("2025", (string)result["revit_year"]);
            Assert.Contains("ElementId(Int64(value))", result["ironpython_suggestions"].ToString());
            Assert.Equal(0, (int)Check("Getter")["candidate_count"]);
            Cases.GetterCalls = 0;
            JObject getter = Check("get_Getter");
            Assert.Equal(1, (int)getter["candidate_count"]);
            Assert.True((bool)Matches(getter)[0]["exact_match"]);
            Assert.True((bool)getter["coverage_complete"]);
            Assert.Equal(0, Cases.GetterCalls);
        }

        [Fact]
        public void OptionalMetadataDefaultIsReportedAsAdaptationNotExact()
        {
            JObject result = Check("Optional");
            Assert.Single(Matches(result));
            Assert.Equal("optional_parameter_default", Conversion(result));
            Assert.False((bool)Matches(result)[0]["exact_match"]);
            Assert.True((bool)Matches(Check("Optional", "System.Int32"))[0]["exact_match"]);
            Assert.Empty(Matches(Check("Required")));
            Assert.Empty(Matches(Check("Required", "System.Int32", "System.Int32")));
        }

        [Fact]
        public void SupportsReferenceBoxingAndNullableWidening()
        {
            Assert.Equal("implicit_reference", Conversion(Check("Reference", "System.String")));
            Assert.Equal("boxing", Conversion(Check("Reference", "System.Int32")));
            Assert.Equal("boxing", Conversion(Check("ValueType", "System.Int32")));
            Assert.Equal("boxing", Conversion(Check("Interface", "System.Int32")));
            Assert.Equal("implicit_nullable", Conversion(Check("Nullable", "System.Int32")));
            Assert.Equal("lifted_nullable", Conversion(Check("Nullable", "Nullable[System.Int32]")));
            Assert.Empty(Matches(Check("Wide", "Nullable[System.Int32]")));
            Assert.True((bool)Matches(Check("Nullable", "Nullable[System.Int64]"))[0]["exact_match"]);
        }

        [Fact]
        public void OpenGenericAndByRefSignaturesArePartialNeverClaimedCompatible()
        {
            foreach (string member in new[] { "Generic", "ByRef" })
            {
                JObject result = Check(member, "System.Int32");
                Assert.Empty(Matches(result));
                Assert.False((bool)result["coverage_complete"]);
                Assert.Equal("unsupported_signature", (string)result["overloads"][0]["reasons"][0]["code"]);
            }
        }

        [Fact]
        public void ParamsDeclaredArrayIsSupportedButExpansionIsPartial()
        {
            Assert.True((bool)Matches(Check("Params", "System.Int32[]"))[0]["exact_match"]);
            foreach (string[] arguments in new[] { new string[0], new[] { "System.Int32" }, new[] { "System.Int32", "System.Int32" } })
            {
                JObject result = Check("Params", arguments);
                Assert.Empty(Matches(result));
                Assert.False((bool)result["coverage_complete"]);
                Assert.Contains("params_expansion_not_analyzed", result["overloads"].ToString());
            }
        }

        [Fact]
        public void InheritedMethodsRequireOptInAndNamesAreExact()
        {
            Assert.Equal(0, (int)ApiSignatureRules.Check(typeof(Derived), "Wide", new[] { "System.Int32" }, Universe)["candidate_count"]);
            Assert.Equal(1, (int)ApiSignatureRules.Check(typeof(Derived), "Wide", new[] { "System.Int32" }, Universe, true)["candidate_count"]);
            Assert.Equal(0, (int)Check("wide", "System.Int32")["candidate_count"]);
            Assert.Equal(0, (int)Check("Wid", "System.Int32")["candidate_count"]);
        }

        [Fact]
        public void BoundsInputAndRejectsTargetOutsideUniverse()
        {
            Assert.Throws<ArgumentException>(() => ApiSignatureRules.Check(typeof(Cases), ".ctor", null, Universe));
            Assert.Throws<ArgumentException>(() => Check("Required", Enumerable.Repeat("System.Int32", 33).ToArray()));
            Assert.Throws<ArgumentException>(() => Check(".ctor()"));
            Assert.Throws<ArgumentException>(() => ApiSignatureRules.Check(typeof(Uri), ".ctor", new string[0], Universe));
            string reason;
            Assert.Null(ApiSignatureRules.ResolveType(new string('a', 513), Universe, out reason));
        }

        [Fact]
        public void UserDefinedOperatorsAreNeverInvoked()
        {
            Convertible.OperatorCalls = 0;
            JObject result = Check("Narrow", "Convertible");
            Assert.Empty(Matches(result));
            Assert.False((bool)result["coverage_complete"]);
            Assert.Contains("user_defined_conversion_not_analyzed", result["overloads"].ToString());
            Assert.Equal(0, Convertible.OperatorCalls);
        }

        [Fact]
        public void ExactSpecialNameOperatorIsCheckedAsMetadataWithoutExecutingIt()
        {
            Convertible.OperatorCalls = 0;
            JObject result = ApiSignatureRules.Check(typeof(Convertible), "op_Implicit", new[] { "Convertible" }, Universe);
            Assert.Equal(1, (int)result["candidate_count"]);
            Assert.True((bool)Matches(result)[0]["exact_match"]);
            Assert.True((bool)result["coverage_complete"]);
            Assert.Equal(0, Convertible.OperatorCalls);
        }
    }
}
